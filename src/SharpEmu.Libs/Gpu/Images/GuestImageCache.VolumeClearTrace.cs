// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private int _volumeClearTraceCount;
    private readonly Dictionary<string, string> _volumeClearTraceStates = new();

    internal void TraceVolumeClear(string phase, in ImageRequest request, CachedImage? image, string decision = "")
    {
        if (!ImageClearTrace.Enabled || !(image?.Description.IsVolume ?? request.Description.IsVolume)) return;
        if (_volumeClearTraceCount >= 2048) return;
        var description = image?.Description ?? request.Description;
        var cachedMetadata = image?.Description.Metadata.Range.Address ?? 0;
        var message = new StringBuilder();
        message.Append($"phase={phase} image=0x{description.Data.Address:X16} ownerResolved={image is not null} host=0x{image?.Backing.Handle.Handle ?? 0:X} ");
        message.Append($"role={request.Role} extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} format={description.PixelFormat} view={request.View} ");
        message.Append($"gpuModified={image?.IsGpuModified} descriptor={request.TraceTextureDescriptor ?? "none"} ");
        message.Append($"metadataCompress={request.TraceMetadataCompress} writeCompress={request.TraceWriteCompress} ");
        message.Append($"descriptorMetadata=0x{request.TraceTextureMetadataAddress:X16} cachedMetadata=0x{cachedMetadata:X16} ");
        message.Append($"decision={decision} synchronization=none ");
        // Derive a diagnostic candidate without registering metadata or changing the request.
        var candidateDescription = description;
        candidateDescription.Metadata.Kind = MetadataKind.Dcc;
        var sliceSize = candidateDescription.DccSliceSize;
        message.Append($"candidateSliceSize=0x{sliceSize:X} depth={description.Extent.Depth} ");
        Span<byte> bytes = stackalloc byte[4096];
        foreach (var address in new[] { cachedMetadata, request.TraceTextureMetadataAddress }.Distinct())
        {
            message.Append($"range=0x{address:X16}[");
            using (var held = _lock.Hold())
            {
                _surfaceMetadata.TryGetValue(address, out var registration);
                message.Append($"registration={registration?.Kind.ToString() ?? "none"},invalidated={registration?.Invalidated},fillSize={registration?.FillSize},fill={registration?.FillValue:X8};");
            }
            if (address == 0 || sliceSize == 0 || description.Extent.Depth == 0 ||
                sliceSize > (ulong.MaxValue - address) / description.Extent.Depth ||
                sliceSize > (8UL * 1024 * 1024) / description.Extent.Depth)
            {
                message.Append("unavailable-invalid-or-over-8MiB]");
                continue;
            }
            for (uint slice = 0; slice < description.Extent.Depth; slice++)
            {
                var start = address + slice * sliceSize;
                var dirtyBefore = _bufferCache.HasGpuDirtyBytes(start, sliceSize);
                var uniform = true;
                var readable = true;
                byte code = 0;
                ulong scanned = 0;
                for (ulong offset = 0; offset < sliceSize;)
                {
                    var chunk = bytes[..(int)Math.Min((ulong)bytes.Length, sliceSize - offset)];
                    if (!_backing.TryReadBacking(start + offset, chunk))
                    {
                        readable = false;
                        break;
                    }
                    if (offset == 0) code = chunk[0];
                    uniform &= chunk.IndexOfAnyExcept(code) < 0;
                    offset += (ulong)chunk.Length;
                    scanned = offset;
                }
                var dirtyAfter = _bufferCache.HasGpuDirtyBytes(start, sliceSize);
                var tracked = IsMetadataCleared(address, slice, out var fill);
                message.Append($"slice={slice},address=0x{start:X},bytes={scanned}/{sliceSize},code={code:X2},uniform={uniform},readable={readable},");
                message.Append($"gpuDirty={dirtyBefore}/{dirtyAfter},tracked={tracked},fill={fill:X8},");
                message.Append($"candidate={readable && uniform && !dirtyBefore && !dirtyAfter && IsDccClearCode(code)};");
            }
            message.Append(']');
        }
        var key = $"{description.Data.Address:X}:{request.Role}:{phase}";
        var text = message.ToString();
        if (_volumeClearTraceStates.TryGetValue(key, out var previous) && previous == text) return;
        if (_volumeClearTraceStates.Count >= 256 && !_volumeClearTraceStates.ContainsKey(key))
        {
            Console.Error.WriteLine("[GPU][TRACE] VolumeClear key limit=256. Further volume records are omitted.");
            _volumeClearTraceCount = 2048;
            return;
        }
        _volumeClearTraceStates[key] = text;
        Console.Error.WriteLine($"[GPU][TRACE] VolumeClear sequence={++_volumeClearTraceCount} tick={_scheduler.CurrentTick} queue={_metadataQueueId} submit={_metadataSubmissionId} {text}");
        if (_volumeClearTraceCount == 2048)
            Console.Error.WriteLine("[GPU][TRACE] VolumeClear limit=2048. Further volume records are omitted.");
    }
}
