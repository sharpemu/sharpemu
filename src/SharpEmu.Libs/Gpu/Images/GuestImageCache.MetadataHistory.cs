// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private readonly MetadataAccessHistory _metadataHistory = new();
    private ulong _metadataHistorySequence;
    private ulong _metadataSubmissionId;
    private int _metadataQueueId = -1;
    private readonly Dictionary<(ulong Image, ulong Metadata, bool GpuDirty), int> _metadataSampleCounts = new();
    private int _metadataSampleCount;
    private readonly Dictionary<(ulong Image, ulong Metadata), int> _textureMetadataSamples = new();

    internal void TraceTextureMetadata(in ImageRequest request)
    {
        var address = request.TraceTextureMetadataAddress;
        const int sampleSize = 256;
        if (!ImageClearTrace.Enabled || address == 0 || address > ulong.MaxValue - sampleSize) return;
        var key = (request.Description.Data.Address, address);
        using (var held = _lock.Hold())
        {
            if (!_textureMetadataSamples.TryGetValue(key, out var count) && _textureMetadataSamples.Count >= 128) return;
            if (count >= 4) return;
            _textureMetadataSamples[key] = count + 1;
        }
        var gpuDirty = _bufferCache.HasGpuDirtyBytes(address, sampleSize);
        // Complete native writes before sampling. Readback must run outside the image-cache lock.
        if (gpuDirty) _bufferCache.ReadMemory(address, sampleSize);
        Span<byte> bytes = stackalloc byte[sampleSize];
        var readable = _backing.TryReadBacking(address, bytes);
        ImageClearTrace.Write($"texture-metadata image=0x{key.Item1:X16} metadata=0x{address:X16} " +
            $"gpuDirty={gpuDirty} readable={readable} sampleSize={sampleSize} bytes={(readable ? Convert.ToHexString(bytes) : "unavailable")}");
        TraceColorClearState(address);
    }

    internal void TraceNativeColorMetadata(in ImageRequest request)
    {
        if (!ImageClearTrace.Enabled || request.Role != ImageRole.ColorTarget ||
            request.Description.Metadata.Kind != MetadataKind.Dcc) return;

        const int sampleSize = 64;
        var address = request.Description.Metadata.Range.Address;
        if (address == 0 || address > ulong.MaxValue - sampleSize) return;
        var gpuDirty = _bufferCache.HasGpuDirtyBytes(address, sampleSize);
        var key = (request.Description.Data.Address, address, gpuDirty);
        using (var held = _lock.Hold())
        {
            if (_metadataSampleCount >= 2048) return;
            _metadataSampleCounts.TryGetValue(key, out var count);
            if (count >= (gpuDirty ? 4 : 1)) return;
            _metadataSampleCounts[key] = count + 1;
            _metadataSampleCount++;
        }

        // Readback can submit the current batch. Do not hold the image-cache lock across it.
        if (gpuDirty) _bufferCache.ReadMemory(address, sampleSize);
        Span<byte> bytes = stackalloc byte[sampleSize];
        var readable = _backing.TryReadBacking(address, bytes);
        ImageClearTrace.Write($"native image=0x{request.Description.Data.Address:X16} metadata=0x{address:X16} " +
            $"sampleSize={sampleSize} gpuDirty={gpuDirty} readable={readable} " +
            $"bytes={(readable ? Convert.ToHexString(bytes) : "unavailable")}");
        if (_metadataSampleCount == 2048)
        {
            ImageClearTrace.Write("native sample limit=2048. Further readbacks are omitted.");
        }
    }

    public void TraceMetadataFill(ulong address, ulong size, uint value)
    {
        if (!ImageClearTrace.Enabled) return;
        using var held = _lock.Hold();
        _metadataHistory.RecordRange(CreateMetadataEvent("buffer-fill", address, size, value: value));
        RecordResourceHistory("buffer-fill", address, size);
    }

    public void SetMetadataTraceSubmission(int queueId, ulong submissionId)
    {
        if (!ImageClearTrace.Enabled) return;
        using var held = _lock.Hold();
        _metadataQueueId = queueId;
        _metadataSubmissionId = submissionId;
    }

    private MetadataAccessEntry CreateMetadataEvent(string operation, ulong address, ulong size,
        ulong imageAddress = 0, string kind = "unknown", uint value = 0, bool guestCpuWrite = false) =>
        new(++_metadataHistorySequence, _scheduler.CurrentTick,
            guestCpuWrite ? -1 : _metadataQueueId, guestCpuWrite ? 0 : _metadataSubmissionId,
            operation, address, size, imageAddress, kind, value);

    private void RecordMetadataEvent(string operation, ulong address, ulong size = 0, uint value = 0)
    {
        if (!ImageClearTrace.Enabled) return;
        var kind = _surfaceMetadata.TryGetValue(address, out var metadata) ? metadata.Kind.ToString() : "unregistered";
        _metadataHistory.Record(address, size, CreateMetadataEvent(operation, address, size, kind: kind, value: value));
        if (ImageClearTrace.Enabled)
        {
            ImageClearTrace.Write($"metadata operation={operation} address=0x{address:X16} size=0x{size:X} " +
                $"kind={kind} value=0x{value:X8} previousFill=0x{metadata?.FillValue ?? 0:X8} previousMask=0x{metadata?.ClearMask ?? 0:X8}");
        }
    }

    internal void TraceColorClearState(ulong address)
    {
        if (!ImageClearTrace.Enabled) return;
        using var held = _lock.Hold();
        _surfaceMetadata.TryGetValue(address, out var metadata);
        ImageClearTrace.Write($"state address=0x{address:X16} kind={metadata?.Kind.ToString() ?? "unregistered"} " +
            $"fill=0x{metadata?.FillValue ?? 0:X8} mask=0x{metadata?.ClearMask ?? 0:X8} fillSize=0x{metadata?.FillSize ?? 0:X}");
        foreach (var entry in _metadataHistory.Read(address))
        {
            ImageClearTrace.Write($"history operation={entry.Operation} address=0x{entry.Address:X16} size=0x{entry.Size:X} " +
                $"image=0x{entry.TrackedImageBaseAddress:X16} kind={entry.Kind} value=0x{entry.Value:X8}");
        }
    }

    private void PrintMetadataHistory(ulong address)
    {
        Console.Error.WriteLine("[GPU][ERROR] Metadata history: last 32 transitions for up to 256 addresses; repeated events are coalesced. CPU writes include notified ranges only.");
        foreach (var entry in _metadataHistory.Read(address))
        {
            Console.Error.WriteLine($"[GPU][ERROR] MetadataHistory sequence={entry.Sequence} tick={entry.Tick} " +
                $"queue={entry.QueueId} submit={entry.SubmissionId} operation={entry.Operation} " +
                $"address=0x{entry.Address:X16} size=0x{entry.Size:X} image=0x{entry.TrackedImageBaseAddress:X16} " +
                $"kind={entry.Kind} value=0x{entry.Value:X8}");
        }
    }
}
