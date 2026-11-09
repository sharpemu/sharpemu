// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.GnmMode.Libraries.VideoOut;

namespace SharpEmu.GnmMode.Shaders;

// GCN T# (8 dwords) and RDNA2/AGC T# have different image format, width,
// pitch and array fields. Convert only layouts the existing AGC image cache
// can read at the same physical addresses; never reinterpret a GNM tile index
// as an RDNA tile size.
public static class GcnImageDescriptor
{
    private const int MaxTraces = 192;
    internal const uint NativeStorageMarker = 1u << 31;
    internal const uint VolumeImageType = 10;
    private static readonly uint[] UnifiedFormats = BuildFormats();
    private static readonly ConcurrentDictionary<ulong, byte> TracedLayouts = new();
    private static readonly ConcurrentDictionary<ulong, byte> WarnedLayouts = new();
    private static int _traceCount;
    private static int _warningCount;
    private readonly record struct ColorLayout(uint Pitch, uint Height, uint GcnTile, GuestTileMode HostTile,
        ulong HostAddress, uint HostHeight, ColorTargetWords Words);
    private static readonly ConcurrentDictionary<ulong, ColorLayout> ColorTargets = new();
    private readonly record struct LinearLayout(uint Pitch, uint Width, uint Height, GuestPixelFormat Format);
    private static readonly ConcurrentDictionary<ulong, LinearLayout> LinearPitches = new();
    private readonly record struct DepthLayout(ulong HostAddress, uint Width, uint Height, uint Layers, GuestPixelFormat Format,
        ulong GuestSize, ulong HostSize);
    private static readonly ConcurrentDictionary<ulong, DepthLayout> DepthTargets = new();
    private static readonly ConcurrentDictionary<string, byte> ReportedDepthCopies = new();
    private static long _normalizationVersion;

    internal static long NormalizationVersion => Volatile.Read(ref _normalizationVersion);

    internal static void InvalidateNormalizations(NormalizationInvalidation reason)
    {
        Interlocked.Increment(ref _normalizationVersion);
        GnmImageProfile.NormalizationInvalidated(reason);
    }

    internal static bool IsGpuDepthAddress(ulong address) => DepthTargets.ContainsKey(address);

    internal static bool TryGetDepthShadow(ulong address, ulong size, out ulong hostAddress)
    {
        hostAddress = 0;
        if (!DepthTargets.TryGetValue(address, out var depth) || depth.HostAddress == address || depth.HostSize < size)
            return false;
        hostAddress = depth.HostAddress;
        return true;
    }

    private static bool IsDepthTarget(ReadOnlySpan<uint> words)
    {
        var address = ((ulong)words[0] | ((ulong)(words[1] & 63) << 32)) << 8;
        var format = UnifiedFormats[((words[1] >> 20) & 63) * 16 + ((words[1] >> 26) & 15)];
        return DepthTargets.TryGetValue(address, out var target) && words[3] >> 28 is 9 or 13 &&
            format == (uint)target.Format && (words[2] & 0x3FFF) < target.Width &&
            ((words[2] >> 14) & 0x3FFF) < target.Height && ((words[5] >> 13) & 0x1FFF) < target.Layers;
    }

    internal static void ResetLayouts()
    {
        ColorTargets.Clear();
        LinearPitches.Clear();
        DepthTargets.Clear();
        ReportedDepthCopies.Clear();
        InvalidateNormalizations(NormalizationInvalidation.Reset);
    }

    internal static void ObserveDepthTarget(ulong guestAddress, ulong hostAddress, uint width, uint height, uint layers,
        GuestPixelFormat format, ulong guestSize, ulong hostSize)
    {
        var layout = new DepthLayout(hostAddress, width, height, layers, format, guestSize, hostSize);
        if (DepthTargets.TryGetValue(guestAddress, out var previous) && previous == layout)
            return;
        if (GnmDiagnostics.Log && previous == default)
            Console.Error.WriteLine($"[GNMMODE][TRACE] GPU depth target address=0x{guestAddress:X} host=0x{hostAddress:X} extent={width}x{height} layers={layers} format={format} guestSize=0x{guestSize:X} hostSize=0x{hostSize:X}.");
        DepthTargets[guestAddress] = layout;
        InvalidateNormalizations(NormalizationInvalidation.Depth);
    }

    internal static void ObserveColorTarget(ulong guestAddress, in ColorTargetWords target, uint pitch, uint height, uint tile)
    {
        if (guestAddress != 0 && target.Layout != 0 && target.SamplesLog2 == 0)
        {
            if (GnmDiagnostics.Log && !ColorTargets.ContainsKey(guestAddress))
                Console.Error.WriteLine($"[GNMMODE][TRACE] GPU target address=0x{guestAddress:X} host=0x{target.BaseAddress:X} pitch={pitch} height={height} tile={tile} hostTile={target.TileMode} info=0x{target.Info:X8}.");
            var layout = new ColorLayout(pitch, height, tile, target.TileMode, target.BaseAddress, target.Height + 1, target);
            var changed = StoreColorTarget(guestAddress, layout);
            if (target.BaseAddress != guestAddress)
                changed |= StoreColorTarget(target.BaseAddress, layout);
            if (changed)
                InvalidateNormalizations(NormalizationInvalidation.Color);
        }
    }

    private static bool StoreColorTarget(ulong address, in ColorLayout layout)
    {
        var changed = !ColorTargets.TryGetValue(address, out var previous) || previous with { Words = layout.Words } != layout;
        ColorTargets[address] = layout;
        return changed;
    }

    internal static bool TryResolveColorCopy(ulong source, ulong destination, ulong size,
        out ImageRequest sourceRequest, out ImageRequest destinationRequest)
    {
        sourceRequest = destinationRequest = default;
        if (!ColorTargets.TryGetValue(source, out var sourceLayout) || sourceLayout.GcnTile != 14 ||
            sourceLayout.Words.Dimension != 1 || sourceLayout.Words.MaxMip != 0 || sourceLayout.Words.SliceStart != 0 ||
            sourceLayout.Words.SliceMax != 0 || sourceLayout.Words.SamplesLog2 != 0 ||
            ImageRequestBuilders.ColorTarget(sourceLayout.Words, 15, 0, false) is not { } resolved ||
            size != (ulong)sourceLayout.Pitch * sourceLayout.Height * resolved.Request.Description.BytesPerBlock)
            return false;

        var to = sourceLayout with { HostAddress = destination, Words = sourceLayout.Words with { BaseAddress = destination } };
        if (ColorTargets.TryGetValue(destination, out var known))
        {
            if (known.Pitch != sourceLayout.Pitch || known.Height != sourceLayout.Height || known.GcnTile != sourceLayout.GcnTile ||
                known.HostHeight != sourceLayout.HostHeight || known.Words.Info != sourceLayout.Words.Info)
                return false;
            to = known;
        }
        if (ImageRequestBuilders.ColorTarget(to.Words, 15, 0, false) is not { } target)
            return false;
        sourceRequest = resolved.Request;
        destinationRequest = target.Request;
        if (ColorTargets.TryAdd(destination, to))
            InvalidateNormalizations(NormalizationInvalidation.ColorCopy);
        return true;
    }

    internal static bool TryResolveDepthCopy(ulong source, ulong destination, ulong size, Func<ulong, ulong?>? allocate,
        out ImageRequest sourceRequest, out ImageRequest destinationRequest)
    {
        sourceRequest = destinationRequest = default;
        var sourceKnown = DepthTargets.TryGetValue(source, out var depth);
        if (GnmDiagnostics.Log && ReportedDepthCopies.Count < 64)
        {
            DepthTargets.TryGetValue(destination, out var observedDestination);
            var details = $"from=0x{source:X} to=0x{destination:X} bytes=0x{size:X} sourceKnown={sourceKnown} source={depth} destination={observedDestination}";
            if (ReportedDepthCopies.TryAdd(details, 0))
                Console.Error.WriteLine($"[GNMMODE][TRACE] Depth copy lookup {details}.");
        }
        if (!sourceKnown || depth.GuestSize == 0 || size < depth.GuestSize || depth.HostSize == 0)
            return false;

        var target = depth with { HostAddress = destination };
        if (DepthTargets.TryGetValue(destination, out var known))
        {
            if (known with { HostAddress = depth.HostAddress } != depth)
                return false;
            target = known;
        }
        else if ((destination & 0xFFFF) != 0 || depth.HostSize > size)
        {
            if (allocate?.Invoke(depth.HostSize) is not { } shadow || shadow == 0 || (shadow & 0xFFFF) != 0)
                return false;
            target = target with { HostAddress = shadow };
        }

        sourceRequest = DepthCopyRequest(depth.HostAddress, depth.HostSize);
        destinationRequest = DepthCopyRequest(target.HostAddress, target.HostSize);
        if (DepthTargets.TryAdd(destination, target))
            InvalidateNormalizations(NormalizationInvalidation.DepthCopy);
        return true;
    }

    private static ImageRequest DepthCopyRequest(ulong address, ulong size)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, size);
        return new ImageRequest(description, default, ImageRole.DepthTarget);
    }

    internal static TextureRequestResolution ResolveTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape,
        ulong shaderHash, int imageIndex)
    {
        var descriptor = new TextureDescriptorWords(words);
        if (descriptor.TileMode == GuestTileMode.Linear &&
            LinearPitches.TryGetValue(descriptor.BaseAddress, out var linear) &&
            linear.Width == descriptor.Width + 1 && linear.Height == descriptor.Height + 1 &&
            linear.Format == descriptor.Format && TileGeometry.TryGetElementLayout(linear.Format, out var element))
        {
            var pitched = ImageRequestBuilders.Texture(words, shape, shaderHash, imageIndex);
            var pitchedRequest = pitched.Request;
            var size = (linear.Pitch + (ulong)element.TexelWidth - 1) / element.TexelWidth *
                ((linear.Height + (ulong)element.TexelHeight - 1) / element.TexelHeight) * element.Bytes;
            pitchedRequest.Description.Pitch = linear.Pitch;
            pitchedRequest.Description.Data = new GuestSpan(descriptor.BaseAddress, size);
            pitchedRequest.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = linear.Pitch, Height = linear.Height };
            return pitched with { Request = pitchedRequest };
        }
        if (descriptor.TileMode != GuestTileMode.RenderTarget)
            return ImageRequestBuilders.Texture(words, shape, shaderHash, imageIndex);

        // GNM placements can be 32 KiB aligned. Only known GPU attachments use the
        // shared host layout; CPU-resident GCN tiles still require a real detiler.
        var aligned = words.ToArray();
        var address = (descriptor.BaseAddress + 0xFFFFUL) & ~0xFFFFUL;
        aligned[0] = (uint)(address >> 8);
        aligned[1] = (aligned[1] & ~63u) | (uint)(address >> 40);
        var resolution = ImageRequestBuilders.Texture(aligned, shape, shaderHash, imageIndex);
        var request = resolution.Request;
        request.Description.Data = request.Description.Data with { Address = descriptor.BaseAddress };
        return resolution with { Request = request };
    }

    public static uint[] Normalize(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash, string source)
        => Normalize(words, stage, shaderHash, source, null);

    internal static uint[] Normalize(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash,
        string source, GcnSampledImageDetiler? detiler, bool deferPreparation = false, bool allowPreparation = true)
    {
        var start = GnmImageProfile.Start();
        var normalized = NormalizeCore(words, stage, shaderHash, source, detiler, deferPreparation, allowPreparation);
        GnmImageProfile.Normalized(start);
        return normalized;
    }

    private static uint[] NormalizeCore(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash,
        string source, GcnSampledImageDetiler? detiler, bool deferPreparation, bool allowPreparation)
    {
        if (words.Length is not (4 or 8))
            return Trace(words, stage, shaderHash, source, "invalid-descriptor-length", []);

        if (words[0] == 0 && (words[1] & 63) == 0)
            return new uint[8];

        if (words.Length < 8)
            return Trace(words, stage, shaderHash, source, "missing-gcn-extended-fields", []);

        if ((words[5] & NativeStorageMarker) != 0)
        {
            var unmarked = words.ToArray();
            unmarked[5] &= ~NativeStorageMarker;
            return IsColorTarget(unmarked, out _) || IsDepthTarget(unmarked)
                ? NormalizeCore(unmarked, stage, shaderHash, source, detiler, deferPreparation, allowPreparation) : new uint[8];
        }

        var data = (words[1] >> 20) & 63;
        var number = (words[1] >> 26) & 15;
        var widthMinusOne = words[2] & 0x3FFF;
        var heightMinusOne = (words[2] >> 14) & 0x3FFF;
        var baseLevel = (words[3] >> 12) & 15;
        var lastLevel = (words[3] >> 16) & 15;
        var tile = (words[3] >> 20) & 31;
        var microTiled = tile is 13 or 19;
        var type = words[3] >> 28;
        var pitch = ((words[4] >> 13) & 0x3FFF) + 1;
        var baseArray = words[5] & 0x1FFF;
        var lastArray = (words[5] >> 13) & 0x1FFF;
        if (type == 11)
            lastArray = Math.Min(lastArray, ((words[4] & 0x1FFF) + 1) * 6 - 1);
        var format = UnifiedFormats[(data << 4) | number];
        var bytes = BytesPerTexel(data);
        var addressBase = ((ulong)words[0] | ((ulong)(words[1] & 63) << 32)) << 8;
        var colorTarget = IsColorTarget(words, out var target);
        var depthTarget = DepthTargets.TryGetValue(addressBase, out var depth) && !colorTarget &&
            type is 9 or 13 && format == (uint)depth.Format &&
            widthMinusOne < depth.Width && heightMinusOne < depth.Height && lastArray < depth.Layers;
        var rowTexels = bytes == 0 ? 0 : 256 / bytes * BlockWidth(data);
        var linearRepack = !colorTarget && !depthTarget && !microTiled && rowTexels != 0 &&
            pitch != (widthMinusOne + rowTexels) / rowTexels * rowTexels;

        string? failure = null;
        if (type is < 8 or > 13)
            failure = $"unsupported-gcn-image-type:{type}";
        else if (format == uint.MaxValue || bytes == 0)
            failure = $"unmapped-gcn-format:{data}/{number}";
        else if (!colorTarget && !depthTarget && (tile is not (8 or 13 or 19 or 31) || (microTiled && detiler is null)))
            failure = $"gcn-tiling-needs-detile:index-{tile}";
        else if (tile == 19 && type != VolumeImageType)
            failure = $"gcn-tile19-unsupported-shape:type={type}";
        else if (tile == 13 && (type is not (8 or 9 or 10 or 11 or 12 or 13) || (type is 8 or 12 && heightMinusOne != 0)))
            failure = $"gcn-tile13-unsupported-shape:type={type},height={heightMinusOne + 1},bytes={bytes}";
        else if ((baseLevel != 0 || lastLevel != 0) &&
            (tile != 13 || type is not (8 or 9 or 11 or 12 or 13) || colorTarget || depthTarget || baseLevel > lastLevel))
            failure = $"gcn-mip-layout-unimplemented:{baseLevel}-{lastLevel}";
        else if (((words[3] >> 25) & 1) != 0 && (!microTiled || colorTarget || depthTarget))
            failure = "gcn-pow2-padding-unimplemented";
        else if (((words[3] >> 27) & 1) != 0)
            failure = "gcn-atc-unimplemented";
        else if (((words[6] >> 21) & 1) != 0 && !depthTarget)
            failure = "gcn-compressed-metadata-unimplemented";
        else if (linearRepack && (type != 9 || baseArray != 0 || lastArray != 0))
            failure = $"gcn-linear-pitch-mismatch:{pitch}";
        else if (type is 11 or 12 or 13 && baseArray > lastArray)
            failure = $"gcn-invalid-array-range:{baseArray}-{lastArray}";
        else if (type is not (11 or 12 or 13) && (baseArray != 0 || lastArray != 0))
            failure = $"gcn-unexpected-array-range:{baseArray}-{lastArray}";

        if (failure is not null)
            return Trace(words, stage, shaderHash, source, failure, []);

        var tileAsLinear = tile == 13 && widthMinusOne == 0 && heightMinusOne == 0 && lastLevel == 0 &&
            bytes <= 4 && BlockWidth(data) == 1;
        ulong? detiledAddress = null;
        if (microTiled && !colorTarget && !tileAsLinear)
        {
            var slices = type == VolumeImageType ? (words[4] & 0x1FFF) + 1 : lastArray + 1;
            var sourceSlices = type == 11 ? ((words[4] & 0x1FFF) + 1) * 6
                : type is 12 or 13 ? Math.Max((words[4] & 0x1FFF) + 1, slices) : slices;
            if (((words[3] >> 25) & 1) != 0 && (type is 11 or 12 or 13 || tile == 19))
                sourceSlices = System.Numerics.BitOperations.RoundUpToPowerOf2(sourceSlices);
            if (!detiler!.TryDetile(words, widthMinusOne + 1, heightMinusOne + 1, pitch, slices, lastLevel + 1,
                    ((words[3] >> 25) & 1) != 0, (GuestPixelFormat)format,
                    out var destination, out var error, deferPreparation, allowPreparation, sourceSlices))
                return Trace(words, stage, shaderHash, source, error, []);
            detiledAddress = destination;
        }
        if (linearRepack)
        {
            var layout = new LinearLayout(pitch, widthMinusOne + 1, heightMinusOne + 1, (GuestPixelFormat)format);
            if (!LinearPitches.TryGetValue(addressBase, out var current) || current != layout)
            {
                if (!allowPreparation)
                    throw new SerialResolveRequiredException("gcn linear image layout");
                LinearPitches[addressBase] = layout;
                InvalidateNormalizations(NormalizationInvalidation.Linear);
            }
        }
        else if (tile != 13 && !colorTarget && !depthTarget)
        {
            if (LinearPitches.ContainsKey(addressBase))
            {
                if (!allowPreparation)
                    throw new SerialResolveRequiredException("gcn linear image layout");
                if (LinearPitches.TryRemove(addressBase, out _))
                    InvalidateNormalizations(NormalizationInvalidation.Linear);
            }
        }

        // GCN W2 WIDTH[13:0] is contiguous. AGC moves its low two bits to
        // W1[31:30] and the other twelve bits to W2[11:0].
        // GCN W5 carries array slices; AGC carries them in W4.
        var lastSlice = type is 11 or 12 or 13 ? lastArray : words[4] & 0x1FFF;
        var translatedAddress = detiledAddress ?? (colorTarget ? target.HostAddress : depthTarget ? depth.HostAddress : addressBase);
        // GCN color slices may allocate padded rows (e.g. 1088 for a 1080p
        // texture). Match full-width views to the GPU attachment's host extent
        // so sampling reuses its rendered image instead of uploading guest bytes.
        var textureHeight = heightMinusOne + 1;
        var fullColorSlice = colorTarget && widthMinusOne + 1 == target.Pitch &&
            target.Height - textureHeight < 64;
        var translatedHeight = fullColorSlice ? target.HostHeight - 1 : heightMinusOne;
        uint[] normalized =
        [
            (uint)(translatedAddress >> 8),
            (words[1] & 0x000FFF00) | (uint)((translatedAddress >> 40) & 63)
                | (format << 20) | ((widthMinusOne & 3) << 30),
            (widthMinusOne >> 2) | (translatedHeight << 14),
            (words[3] & 0xF00FFFFF) | (colorTarget ? (uint)target.HostTile << 20 : depthTarget ? (uint)GuestTileMode.Depth << 20 : 0),
            lastSlice | (baseArray << 16),
            (lastLevel << 4), // AGC MAX_MIP; other AGC-only fields stay zero
            0,
            0,
        ];
        if (!GnmDiagnostics.LogImages || !IsUntraced(words, shaderHash))
            return normalized;
        return Trace(words, stage, shaderHash, source,
            colorTarget ? "converted-gpu-color-target" :
                depthTarget ? "converted-gpu-depth-target" :
                tileAsLinear ? "converted-tile13-single-texel" :
                microTiled ? $"converted-tile{tile}-detiled:srcPitch={pitch},layers={lastArray + 1},levels={lastLevel + 1}" :
                linearRepack ? $"converted-linear-pitched:pitch={pitch}" : "converted-linear",
            normalized);
    }

    private static bool IsColorTarget(ReadOnlySpan<uint> words, out ColorLayout target)
    {
        var addressBase = ((ulong)words[0] | ((ulong)(words[1] & 63) << 32)) << 8;
        if (ColorTargets.TryGetValue(addressBase, out target) &&
            words[3] >> 28 == 9 && ((words[3] >> 20) & 31) == target.GcnTile &&
            ((words[4] >> 13) & 0x3FFF) + 1 == target.Pitch &&
            (words[2] & 0x3FFF) < target.Pitch && ((words[2] >> 14) & 0x3FFF) < target.Height)
            return true;
        var pitch = ((words[4] >> 13) & 0x3FFF) + 1;
        var height = ((words[2] >> 14) & 0x3FFF) + 1;
        if (words[3] >> 28 != 9 || ((words[3] >> 20) & 31) != 10 || (words[2] & 0x3FFF) >= pitch ||
            !GnmVideoOutExports.TryGetDisplayTarget(addressBase, pitch, height, out _, out var hostAddress, out var displayHeight))
            return false;
        target = new ColorLayout(pitch, height, 10, GuestTileMode.RenderTarget, hostAddress, displayHeight, default);
        return true;
    }

    private static bool IsUntraced(ReadOnlySpan<uint> original, ulong shaderHash) =>
        Volatile.Read(ref _traceCount) <= MaxTraces && !TracedLayouts.ContainsKey(Fingerprint(original, shaderHash));

    private static ulong Fingerprint(ReadOnlySpan<uint> original, ulong shaderHash)
    {
        // One entry per shader/descriptor layout. Address-only variations are omitted.
        ulong fingerprint = shaderHash ^ 14695981039346656037UL;
        for (var index = 1; index < original.Length; index++)
        {
            // W1[5:0] holds the upper address bits, not part of the format.
            var layoutWord = index == 1 ? original[index] & ~63u : original[index];
            fingerprint = unchecked((fingerprint ^ layoutWord) * 1099511628211UL);
        }
        return fingerprint;
    }

    private static uint[] Trace(ReadOnlySpan<uint> original, ShaderStage stage, ulong shaderHash,
        string source, string outcome, uint[] normalized)
    {
        var result = normalized.Length == 8 ? normalized : new uint[8];
        var converted = outcome.StartsWith("converted-", StringComparison.Ordinal);
        if (converted && !GnmDiagnostics.LogImages) return result;
        var layouts = converted ? TracedLayouts : WarnedLayouts;
        ref var count = ref (converted ? ref _traceCount : ref _warningCount);
        if (Volatile.Read(ref count) > MaxTraces) return result;
        var fingerprint = Fingerprint(original, shaderHash);
        if (layouts.ContainsKey(fingerprint) || !layouts.TryAdd(fingerprint, 0)) return result;
        var number = Interlocked.Increment(ref count);
        if (number > MaxTraces)
        {
            layouts.TryRemove(fingerprint, out _);
            if (number == MaxTraces + 1)
                Console.Error.WriteLine($"[GNMMODE][{(converted ? "GPU" : "WARN")}] GCN T# translation {(converted ? "trace" : "warning")} limit reached ({MaxTraces} distinct layouts).");
            return result;
        }

        var w0 = original.Length > 0 ? original[0] : 0;
        var w1 = original.Length > 1 ? original[1] : 0;
        var w2 = original.Length > 2 ? original[2] : 0;
        var w3 = original.Length > 3 ? original[3] : 0;
        var w4 = original.Length > 4 ? original[4] : 0;
        var w5 = original.Length > 5 ? original[5] : 0;
        var gcnTile = (w3 >> 20) & 31;
        if (!GnmDiagnostics.LogImages)
        {
            Console.Error.WriteLine($"[GNMMODE][WARN] GCN T# -> AGC stage={stage} shader=0x{shaderHash:X16} source={source} " +
                $"outcome={outcome} format={(w1 >> 20) & 63}/{(w1 >> 26) & 15} " +
                $"extent={(w2 & 0x3FFF) + 1}x{((w2 >> 14) & 0x3FFF) + 1} tile={gcnTile} type={w3 >> 28} " +
                $"raw=[{string.Join(" ", original.ToArray().Select(word => $"{word:X8}"))}].");
            return result;
        }
        var level = original.Length > 3 ? $"{(w3 >> 12) & 15}-{(w3 >> 16) & 15}" : "?";
        var before = string.Join(" ", original.ToArray().Select(word => $"{word:X8}"));
        var after = string.Join(" ", result.Select(word => $"{word:X8}"));
        var tag = converted ? "TRACE" : "WARN";
        var tileName = gcnTile switch { 8 => "linear-aligned", 13 => "thin-1d-microtiled", 31 => "linear-general", _ => "tiled-or-unknown" };
        var agcFormat = (result[1] >> 20) & 0x1FF;
        var agcFormatName = Enum.GetName((GuestPixelFormat)agcFormat) ?? "unknown";
        var agcWidth = (((result[1] >> 30) & 3) | ((result[2] & 0xFFF) << 2)) + 1;
        var agcHeight = ((result[2] >> 14) & 0x3FFF) + 1;
        Console.Error.WriteLine($"[GNMMODE][{tag}] GCN T# -> AGC stage={stage} shader=0x{shaderHash:X16} source={source} " +
            $"outcome={outcome} gcn.format={((w1 >> 20) & 63)}/{((w1 >> 26) & 15)} " +
            $"gcn.extent={(w2 & 0x3FFF) + 1}x{((w2 >> 14) & 0x3FFF) + 1} " +
            $"gcn.pitch={((w4 >> 13) & 0x3FFF) + 1} gcn.tile={gcnTile}({tileName}) " +
            $"gcn.type={w3 >> 28} gcn.mips={level} gcn.array={(w5 & 0x1FFF)}-{((w5 >> 13) & 0x1FFF)} " +
            $"agc.format={agcFormat}({agcFormatName}) agc.extent={agcWidth}x{agcHeight} agc.tile={(result[3] >> 20) & 31} " +
            $"gcn.base=0x{(((ulong)w0 | ((ulong)(w1 & 63) << 32)) << 8):X16} " +
            $"agc.base=0x{(((ulong)result[0] | ((ulong)(result[1] & 63) << 32)) << 8):X16} " +
            $"raw=[{before}] agc=[{after}]");
        return result;
    }

    private static uint BytesPerTexel(uint data) => data switch
    {
        1 => 1,
        2 or 3 => 2,
        4 or 5 or 6 or 7 or 8 or 9 or 10 => 4,
        11 or 12 => 8,
        13 => 12,
        14 => 16,
        35 or 38 => 8,
        36 or 37 or 39 or 40 or 41 => 16,
        _ => 0,
    };

    private static uint BlockWidth(uint data) => data is >= 35 and <= 41 ? 4u : 1u;

    private static uint[] BuildFormats()
    {
        var formats = new uint[64 * 16];
        Array.Fill(formats, uint.MaxValue);
        for (uint unified = 1; unified <= 154; unified++)
            if (Gfx10UnifiedFormat.TryDecode(unified, out var data, out var number) && data < 64 && number < 16)
            {
                // Prefer the legacy-compatible encoding when a later image-only
                // format aliases the same pair (e.g. R32_FLOAT: 22 vs 140).
                var index = (data << 4) | number;
                if (formats[index] == uint.MaxValue)
                    formats[index] = unified;
            }
        // GCN BC formats use DATA_FORMAT 35..41; RDNA uses image-only FORMATs.
        (uint Data, uint Number, GuestPixelFormat Format)[] compressed =
        [
            (35, 0, GuestPixelFormat.Bc1UNorm), (35, 9, GuestPixelFormat.Bc1Srgb),
            (36, 0, GuestPixelFormat.Bc2UNorm), (36, 9, GuestPixelFormat.Bc2Srgb),
            (37, 0, GuestPixelFormat.Bc3UNorm), (37, 9, GuestPixelFormat.Bc3Srgb),
            (38, 0, GuestPixelFormat.Bc4UNorm), (38, 1, GuestPixelFormat.Bc4SNorm),
            (39, 0, GuestPixelFormat.Bc5UNorm), (39, 1, GuestPixelFormat.Bc5SNorm),
            (40, 0, GuestPixelFormat.Bc6UFloat), (40, 1, GuestPixelFormat.Bc6SFloat),
            (41, 0, GuestPixelFormat.Bc7UNorm), (41, 9, GuestPixelFormat.Bc7Srgb),
        ];
        foreach (var (data, number, format) in compressed)
            formats[(data << 4) | number] = (uint)format;
        return formats;
    }
}
