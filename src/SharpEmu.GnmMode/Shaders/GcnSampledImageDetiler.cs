// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Numerics;
using SharpEmu.GnmMode.Gpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.GnmMode.Shaders;

// A PS4-only bridge for GCN thin-1D microtiles. AGC's linear texture cache
// must see the result in guest-backed memory, not an ordinary private mapping.
internal sealed class GcnSampledImageDetiler(ICpuMemory memory, Func<ulong, ulong?> allocate)
{
    private const int MaxTextureBytes = 64 * 1024 * 1024;
    private const ulong MaxScratchBytes = 1024UL * 1024 * 1024;
    private const int MaxSynchronizationTraces = 32;
    private const long SynchronizationSummaryInterval = 1000;
    private const int SampleCount = 16;
    private const int SampleBytes = 64;
    private const int MaxVerifyInterval = 16;
    private static readonly ConcurrentDictionary<ulong, byte> SynchronizedSources = new();
    private static long _synchronized;
    private static long _epoch;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ShadowKey, Shadow> _shadows = new();
    private readonly ConcurrentDictionary<ShadowKey, GpuShadow> _gpuShadows = new();
    private readonly ConcurrentDictionary<ulong, ShadowKey> _shadowKeys = new();
    private readonly ConcurrentDictionary<ShadowKey, ulong> _preparedShadows = new();
    private readonly ConcurrentDictionary<ulong, ShadowKey> _preparedKeys = new();
    private ulong _reservedBytes;

    private readonly record struct ShadowKey(ulong Source, uint Width, uint Height, uint Pitch, uint Layers, uint SourceLayers, uint Levels,
        bool Pow2, GuestPixelFormat Format, bool Thick);

    private readonly record struct Shadow(ulong Address, ulong Sample, ulong Content, long Epoch, long Verified, int Interval);

    private readonly record struct GpuShadow(ulong Address, Geometry Geometry, GuestBufferCache.ContentMark Source);

    internal static void AdvanceEpoch() => Interlocked.Increment(ref _epoch);

    public bool TryDetile(ReadOnlySpan<uint> words, uint width, uint height, uint pitch, uint layers, uint levels,
        bool pow2, GuestPixelFormat format, out ulong destination, out string failure, bool deferPreparation = false,
        bool allowPreparation = true, uint sourceLayers = 0)
    {
        destination = 0;
        failure = "";
        // A GCN T# base is encoded in 256-byte units across W0 and W1[5:0].
        var baseAddress = ((ulong)words[0] | ((ulong)words[1] & 63) << 32) << 8;
        if (baseAddress == 0)
        {
            failure = $"gcn-tile13-invalid-footprint:width={width},pitch={pitch},layers={layers},levels={levels}";
            return false;
        }
        var key = new ShadowKey(baseAddress, width, height, pitch, layers, sourceLayers == 0 ? layers : sourceLayers, levels, pow2, format,
            ((words[3] >> 20) & 31) == 19);
        var geometry = GeometryOf(key);
        if (geometry.Failure is { } invalid)
        {
            failure = invalid;
            return false;
        }
        return deferPreparation
            ? Reserve(key, geometry, allowPreparation, out destination, out failure)
            : Resolve(key, geometry, out destination, out failure);
    }

    private bool Reserve(ShadowKey key, Geometry geometry, bool allowPreparation, out ulong destination, out string failure)
    {
        destination = 0;
        failure = "";
        if (_preparedShadows.TryGetValue(key, out destination))
            return true;
        if (!allowPreparation)
            throw new SerialResolveRequiredException("gcn image backing allocation");
        Span<byte> probe = stackalloc byte[1];
        if (!memory.CanRead(key.Source, geometry.SourceBytes) &&
            (!memory.TryRead(key.Source, probe) || !memory.TryRead(key.Source + geometry.SourceBytes - 1, probe)))
        {
            failure = $"gcn-tile13-source-unreadable:0x{key.Source:X16}+0x{geometry.SourceBytes:X}";
            return false;
        }
        lock (_gate)
        {
            if (_preparedShadows.TryGetValue(key, out destination))
                return true;
            var size = (geometry.DestinationBytes + 0x3FFFUL) & ~0x3FFFUL;
            if (_reservedBytes + size > MaxScratchBytes || allocate(size) is not { } address || address == 0 || (address & 0xFF) != 0)
            {
                failure = $"gcn-tile13-backing-unavailable:0x{size:X}";
                return false;
            }
            _reservedBytes += size;
            _preparedKeys[address] = key;
            _preparedShadows[key] = destination = address;
            return true;
        }
    }

    internal bool Prepare(ReadOnlySpan<uint> words)
    {
        if (words.Length < 2)
            return true;
        var address = ((ulong)words[0] | ((ulong)words[1] & 63) << 32) << 8;
        if (_preparedKeys.TryGetValue(address, out var prepared))
        {
            if (_gpuShadows.TryGetValue(prepared, out var existing) && existing.Address == address && existing.Source.Buffer is not null)
            {
                if (GnmGpuWrites.Buffers is not { } buffers)
                    return false;
                return !buffers.ContentsChangedSince(prepared.Source, existing.Geometry.SourceBytes, existing.Source) ||
                    TryDetileOnGpu(prepared, existing.Geometry, out _, address);
            }
            return Resolve(prepared, GeometryOf(prepared), out var destination, out _, address) && destination == address;
        }
        if (!_shadowKeys.TryGetValue(address, out var key))
            return true;
        var gpu = _gpuShadows.TryGetValue(key, out var gpuShadow) && gpuShadow.Address == address;
        if (gpu)
        {
            if (GnmGpuWrites.Buffers is not { } buffers || gpuShadow.Source.Buffer is not null &&
                !buffers.ContentsChangedSince(key.Source, gpuShadow.Geometry.SourceBytes, gpuShadow.Source))
                return true;
            return TryDetileOnGpu(key, gpuShadow.Geometry, out _);
        }
        if (!Resolve(key, GeometryOf(key), out var resolved, out _) || resolved != address)
        {
            GcnImageDescriptor.InvalidateNormalizations(NormalizationInvalidation.DetileMiss);
            return false;
        }
        return true;
    }

    private static int NextVerifyInterval(in ShadowKey key, int interval) =>
        interval * 2 < MaxVerifyInterval ? interval * 2 : MaxVerifyInterval / 2 + (int)((uint)key.GetHashCode() % MaxVerifyInterval);

    internal bool IsPrepared(ReadOnlySpan<uint> words, bool ordered, out ulong source, out ulong sourceBytes)
    {
        var preparation = CheckPrepared(words, ordered, out source, out sourceBytes);
        if (preparation == TexturePreparation.Ready)
            return true;
        if (!ordered)
            GnmImageProfile.Unprepared(preparation);
        return false;
    }

    private TexturePreparation CheckPrepared(ReadOnlySpan<uint> words, bool ordered, out ulong source, out ulong sourceBytes)
    {
        source = sourceBytes = 0;
        if (words.Length < 2)
            return TexturePreparation.Ready;
        var address = ((ulong)words[0] | ((ulong)words[1] & 63) << 32) << 8;
        var prepared = _preparedKeys.TryGetValue(address, out var key);
        if (!prepared && !_shadowKeys.TryGetValue(address, out key))
            return TexturePreparation.Ready;
        var geometry = GeometryOf(key);
        source = key.Source;
        sourceBytes = geometry.SourceBytes;
        if (geometry.Failure is not null)
            return TexturePreparation.Geometry;
        if (_gpuShadows.TryGetValue(key, out var gpuShadow) && gpuShadow.Address == address)
        {
            if (GnmGpuWrites.Buffers is not { } buffers)
                return prepared ? TexturePreparation.GpuShadowChanged : TexturePreparation.Ready;
            if (gpuShadow.Source.Buffer is not { } owner)
                return TexturePreparation.GpuShadowChanged;
            var unchanged = ordered
                ? !buffers.ContentsChangedSince(key.Source, gpuShadow.Geometry.SourceBytes, gpuShadow.Source)
                : owner.GpuWrites == gpuShadow.Source.Writes;
            return unchanged ? TexturePreparation.Ready : TexturePreparation.GpuShadowChanged;
        }
        if (GnmGpuWrites.Overlap(key.Source, geometry.SourceBytes))
            return TexturePreparation.GpuWritten;
        if (!_shadows.TryGetValue(key, out var current) || current.Address != address)
            return TexturePreparation.NoShadow;
        var epoch = Volatile.Read(ref _epoch);
        if (current.Epoch == epoch)
            return TexturePreparation.Ready;
        if (TrySample(key.Source, geometry.SourceBytes, out var sample) && current.Sample == sample &&
            epoch - current.Verified < current.Interval && _shadows.TryUpdate(key, current with { Epoch = epoch }, current))
            return TexturePreparation.Ready;
        return TexturePreparation.StaleEpoch;
    }

    private Geometry GeometryOf(in ShadowKey key) => _geometries.GetOrAdd(
        new GeometryKey(key.Width, key.Height, key.Pitch, key.Layers, key.SourceLayers, key.Levels, key.Pow2, key.Format, key.Thick), BuildGeometry);

    private bool Resolve(ShadowKey key, Geometry geometry, out ulong destination, out string failure, ulong preparedAddress = 0)
    {
        destination = 0;
        failure = "";
        var baseAddress = key.Source;
        var sourceBytes = geometry.SourceBytes;
        var destBytes = geometry.DestinationBytes;
        var elementBytes = geometry.ElementBytes;
        var gpuWritten = GnmGpuWrites.Overlap(baseAddress, sourceBytes);
        if (gpuWritten)
        {
            if (_gpuShadows.TryGetValue(key, out var shadow) && shadow.Source.Buffer is not null &&
                (preparedAddress == 0 || shadow.Address == preparedAddress))
            {
                destination = shadow.Address;
                GnmImageProfile.GpuReused();
                return true;
            }
            if (SubmissionScheduler.ThrowsOnFatal)
            {
                GnmImageProfile.Abandoned();
                throw new SubmissionScheduler.AbandonedWorkException(
                    $"A GCN thin-1D texture reads GPU-written memory: 0x{baseAddress:X16}+0x{sourceBytes:X}.");
            }
            if (TryDetileOnGpu(key, geometry, out destination, preparedAddress))
                return true;
            if (GnmGpuWrites.Buffers?.TrySynchronizeCpuRead(baseAddress, sourceBytes) != true)
            {
                failure = $"gcn-tile13-source-sync-failed:0x{baseAddress:X16}+0x{sourceBytes:X}";
                return false;
            }
            if (GnmDiagnostics.LogImages)
            {
                var synchronized = Interlocked.Increment(ref _synchronized);
                if (SynchronizedSources.TryAdd(baseAddress, 0) && SynchronizedSources.Count <= MaxSynchronizationTraces)
                    Console.Error.WriteLine($"[GNMMODE][TRACE] GCN thin-1D texture 0x{baseAddress:X16}+0x{sourceBytes:X} " +
                        $"{key.Width}x{key.Height} {key.Format} is read back from GPU writes before detiling.");
                if (synchronized % SynchronizationSummaryInterval == 0)
                    Console.Error.WriteLine($"[GNMMODE][TRACE] GCN thin-1D readbacks: {synchronized} from " +
                        $"{SynchronizedSources.Count} textures; latest 0x{baseAddress:X16}.");
            }
        }

        var epoch = Volatile.Read(ref _epoch);
        // Published entries are immutable; ordinary cache reads need no allocation gate.
        if (!gpuWritten && _shadows.TryGetValue(key, out var current) && current.Epoch == epoch &&
            (preparedAddress == 0 || current.Address == preparedAddress))
        {
            destination = current.Address;
            GnmImageProfile.DetileHit();
            return true;
        }
        if (!TrySample(baseAddress, sourceBytes, out var sample))
        {
            failure = $"gcn-tile13-source-unreadable:0x{baseAddress:X16}+0x{sourceBytes:X}";
            return false;
        }
        if (!gpuWritten && _shadows.TryGetValue(key, out var cached) && (preparedAddress == 0 || cached.Address == preparedAddress) && cached.Sample == sample &&
            epoch - cached.Verified < cached.Interval && _shadows.TryUpdate(key, cached with { Epoch = epoch }, cached))
        {
            destination = cached.Address;
            GnmImageProfile.DetileHit();
            return true;
        }

        var hashStart = GnmImageProfile.Start();
        using var sourceOwner = MemoryPool<byte>.Shared.Rent((int)sourceBytes);
        var source = sourceOwner.Memory.Span[..(int)sourceBytes];
        if (!memory.TryRead(baseAddress, source))
        {
            failure = $"gcn-tile13-source-unreadable:0x{baseAddress:X16}+0x{sourceBytes:X}";
            return false;
        }
        var content = XxHash3.HashToUInt64(source);
        GnmImageProfile.Hashed(hashStart, sourceBytes);
        if (_shadows.TryGetValue(key, out cached) && (preparedAddress == 0 || cached.Address == preparedAddress) && cached.Content == content &&
            _shadows.TryUpdate(key, cached with
            {
                Sample = sample, Epoch = epoch, Verified = epoch,
                Interval = NextVerifyInterval(key, cached.Interval),
            }, cached))
        {
            destination = cached.Address;
            return true;
        }

        var detileStart = GnmImageProfile.Start();
        var linear = new byte[(int)destBytes];
        var tileElements = key.Thick ? 256u : 64u;
        foreach (var transfer in geometry.Transfers)
        {
            // BC addressing counts 4x4 blocks, not individual texels. Thin microtiles
            // contain 8x8 elements in Morton order, independently of element size.
            for (var y = 0u; y < transfer.Height; y++)
            for (var x = 0u; x < transfer.Width; x++)
            {
                var morton = (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) |
                    ((y & 2) << 2) | ((x & 4) << 2) | ((y & 4) << 3);
                if (key.Thick)
                {
                    var z = transfer.SurfaceZ;
                    var low = elementBytes switch
                    {
                        1 or 2 => (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) | ((y & 2) << 2) | ((z & 1) << 4),
                        4 => (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) | ((z & 1) << 3) | ((y & 2) << 3),
                        _ => (x & 1) | ((y & 1) << 1) | ((z & 1) << 2) | ((x & 2) << 2) | ((y & 2) << 3),
                    };
                    morton = low | ((z & 2) << 4) | ((x & 4) << 4) | ((y & 4) << 5);
                }
                var tile = (ulong)(y >> 3) * (transfer.TiledWidth >> 3) + (x >> 3);
                var from = (int)(transfer.TiledOffset + (tile * tileElements + morton) * elementBytes);
                var to = (int)(transfer.LinearOffset + ((ulong)y * transfer.Pitch + x) * elementBytes);
                source.Slice(from, (int)elementBytes).CopyTo(linear.AsSpan(to, (int)elementBytes));
            }
        }
        GnmImageProfile.CpuDetiled(detileStart);

        return StoreShadow(key, sample, content, epoch, destBytes, linear, out destination, out failure, preparedAddress);
    }

    private readonly record struct GeometryKey(uint Width, uint Height, uint Pitch, uint Layers, uint SourceLayers, uint Levels, bool Pow2,
        GuestPixelFormat Format, bool Thick);

    private sealed record Geometry(ulong SourceBytes, ulong DestinationBytes, TileTransfer[] Transfers, uint ElementBytes,
        string? Failure);

    private readonly ConcurrentDictionary<GeometryKey, Geometry> _geometries = new();

    private static Geometry Invalid(string failure) => new(0, 0, [], 0, failure);

    private static Geometry BuildGeometry(GeometryKey geometryKey)
    {
        var (width, height, pitch, layers, sourceLayers, levels, pow2, format, thick) = geometryKey;
        if (!TileGeometry.TryGetElementLayout(format, out var element))
            return Invalid($"gcn-tile13-unsupported-format:{format}");
        if (width == 0 || height == 0 || pitch < width || layers == 0 || sourceLayers < layers ||
            levels is 0 or > TiledSurfaceLayout.MaxLevels)
            return Invalid($"gcn-tile13-invalid-footprint:width={width},pitch={pitch},layers={layers},levels={levels}");
        if (thick && (levels != 1 || element.TexelWidth != 1 || element.TexelHeight != 1 || element.Bytes is not (1 or 2 or 4 or 8 or 16)))
            return Invalid($"gcn-tile19-unsupported-footprint:format={format},levels={levels}");
        if (thick)
            sourceLayers = (sourceLayers + 3) & ~3u;

        Span<ulong> sourceOffsets = stackalloc ulong[(int)levels];
        Span<uint> sourcePitches = stackalloc uint[(int)levels];
        Span<uint> sourceHeights = stackalloc uint[(int)levels];
        var sourceBytes = 0UL;
        for (var level = 0; level < levels; level++)
        {
            var levelPitch = Math.Max(((pitch >> level) + element.TexelWidth - 1) / element.TexelWidth, 1);
            var levelHeight = Math.Max(((height >> level) + element.TexelHeight - 1) / element.TexelHeight, 1);
            if (pow2)
            {
                levelPitch = BitOperations.RoundUpToPowerOf2(levelPitch);
                levelHeight = BitOperations.RoundUpToPowerOf2(levelHeight);
            }
            var alignedPitch = (levelPitch + 7) & ~7u;
            var alignedHeight = (levelHeight + 7) & ~7u;
            while ((ulong)alignedPitch * alignedHeight * element.Bytes * (thick ? 4u : 1u) % 256 != 0)
                alignedPitch += 8;
            sourceOffsets[level] = sourceBytes;
            sourcePitches[level] = alignedPitch;
            sourceHeights[level] = alignedHeight;
            sourceBytes += (ulong)alignedPitch * alignedHeight * element.Bytes * sourceLayers;
        }

        var spans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var padded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        if (!TileGeometry.TryGetTextureSize(format, width, height, levels, GuestTileMode.Linear, out var total, spans, padded))
            return Invalid($"gcn-tile13-linear-layout-unsupported:format={format}");
        var destBytes = (ulong)total.Size * layers;
        if (sourceBytes > MaxTextureBytes || destBytes > MaxTextureBytes)
            return Invalid($"gcn-tile13-invalid-footprint:width={width},pitch={pitch},layers={layers},levels={levels}");

        var elementsWidth = Math.Max((width + element.TexelWidth - 1) / element.TexelWidth, 1);
        var elementsHeight = Math.Max((height + element.TexelHeight - 1) / element.TexelHeight, 1);
        var transfers = new TileTransfer[levels * layers];
        for (var level = 0; level < levels; level++)
        {
            var levelWidth = Math.Max((elementsWidth + (1u << level) - 1) >> level, 1);
            var levelRows = Math.Max((elementsHeight + (1u << level) - 1) >> level, 1);
            var targetPitch = padded[level].Width / element.TexelWidth;
            if (targetPitch < levelWidth || (ulong)targetPitch * levelRows * element.Bytes != spans[level].Size)
                return Invalid($"gcn-tile13-linear-layout-mismatch:level={level},pitch={targetPitch},rows={levelRows},size={spans[level].Size}");
            var sliceBytes = (ulong)sourcePitches[level] * sourceHeights[level] * element.Bytes;
            for (var layer = 0u; layer < layers; layer++)
                transfers[level * layers + layer] = new TileTransfer
                {
                    Kind = TileBlockKind.GcnThin1D,
                    BytesPerElement = element.Bytes,
                    TiledOffset = sourceOffsets[level] + (thick ? layer & ~3u : layer) * sliceBytes,
                    TiledSize = sliceBytes * (thick ? 4u : 1u),
                    LinearOffset = spans[level].Offset + (ulong)layer * total.Size,
                    LinearSize = spans[level].Size,
                    Width = Math.Min(levelWidth, sourcePitches[level]),
                    Height = Math.Min(levelRows, sourceHeights[level]),
                    Depth = 1,
                    Pitch = targetPitch,
                    TiledWidth = sourcePitches[level],
                    TiledHeight = sourceHeights[level],
                    SurfaceZ = thick ? layer & 3u : 0,
                };
        }
        return new Geometry(sourceBytes, destBytes, transfers, element.Bytes, null);
    }

    private bool StoreShadow(ShadowKey key, ulong sample, ulong content, long epoch, ulong destBytes, byte[] linear,
        out ulong destination, out string failure, ulong preparedAddress)
    {
        destination = 0;
        failure = "";
        lock (_gate)
        {
            var hasShadow = _shadows.TryGetValue(key, out var shadow);
            if (hasShadow && shadow.Content == content && (preparedAddress == 0 || shadow.Address == preparedAddress))
            {
                _shadows[key] = shadow with { Sample = sample, Epoch = epoch, Verified = epoch };
                destination = shadow.Address;
                return true;
            }
            var allocationSize = (destBytes + 0x3FFFUL) & ~0x3FFFUL;
            var moved = false;
            if (preparedAddress != 0)
                shadow = new Shadow(preparedAddress, sample, content, epoch, epoch, 1);
            else if (!hasShadow || _reservedBytes + allocationSize <= MaxScratchBytes)
            {
                if (_reservedBytes + allocationSize > MaxScratchBytes)
                {
                    failure = $"gcn-tile13-scratch-limit:0x{_reservedBytes:X}+0x{allocationSize:X}";
                    return false;
                }
                var address = allocate(allocationSize);
                if (!address.HasValue || address == 0 || (address.Value & 0xFF) != 0)
                {
                    failure = $"gcn-tile13-direct-allocation-failed:0x{allocationSize:X}";
                    return false;
                }
                moved = hasShadow;
                shadow = new Shadow(address.Value, sample, content, epoch, epoch, 1);
                _reservedBytes += allocationSize;
                _shadowKeys[address.Value] = key;
            }
            if (!memory.TryWrite(shadow.Address, linear))
            {
                failure = $"gcn-tile13-shadow-unwritable:0x{shadow.Address:X16}+0x{destBytes:X}";
                return false;
            }
            _shadows[key] = shadow with { Sample = sample, Content = content, Epoch = epoch, Verified = epoch, Interval = 1 };
            destination = shadow.Address;
            if (moved)
                GcnImageDescriptor.InvalidateNormalizations(NormalizationInvalidation.DetileMoved);
            return true;
        }
    }

    private bool TryDetileOnGpu(ShadowKey key, Geometry geometry, out ulong destination, ulong preparedAddress = 0)
    {
        destination = 0;
        if (key.Thick || GnmGpuWrites.Buffers is not { ImageCache: { } images } buffers)
            return false;
        ulong address;
        lock (_gate)
        {
            if (preparedAddress != 0)
                address = preparedAddress;
            else if (_gpuShadows.TryGetValue(key, out var shadow))
                address = shadow.Address;
            else
            {
                var allocationSize = (geometry.DestinationBytes + 0x3FFFUL) & ~0x3FFFUL;
                if (_reservedBytes + allocationSize > MaxScratchBytes || allocate(allocationSize) is not { } allocated ||
                    allocated == 0 || (allocated & 0xFF) != 0)
                    return false;
                address = allocated;
                _reservedBytes += allocationSize;
                _shadowKeys[address] = key;
            }
            _gpuShadows[key] = new GpuShadow(address, geometry, default);
        }
        if (!images.TryDetileMemory(key.Source, geometry.SourceBytes, address, (geometry.DestinationBytes + 3) & ~3UL,
                geometry.Transfers))
            return false;
        lock (_gate)
            _gpuShadows[key] = new GpuShadow(address, geometry, buffers.MarkContents(key.Source, geometry.SourceBytes));
        GnmImageProfile.GpuDetiled();
        destination = address;
        return true;
    }

    private bool TrySample(ulong address, ulong size, out ulong sample)
    {
        sample = 0;
        Span<byte> bytes = stackalloc byte[SampleCount * SampleBytes];
        var span = Math.Max(size, SampleBytes) - SampleBytes;
        var filled = 0;
        for (var index = 0; index < SampleCount; index++)
        {
            var offset = span * (ulong)index / (SampleCount - 1) & ~3UL;
            var length = (int)Math.Min((ulong)SampleBytes, size - offset);
            if (!memory.TryRead(address + offset, bytes.Slice(filled, length)))
                return false;
            filled += length;
        }
        sample = XxHash3.HashToUInt64(bytes[..filled]);
        return true;
    }
}
