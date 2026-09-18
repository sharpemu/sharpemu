// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

internal readonly record struct BufferDownloadPlacement(
    ulong SourceOffset, ulong TransferSize, ulong DestinationOffset, ulong DataOffset, ulong DataSize);

// Plans aligned transfers without recording commands or changing memory ownership.
internal struct BufferDownloadBatchPlanner
{
    public const ulong Alignment = 64;
    private readonly ulong _capacity;

    public BufferDownloadBatchPlanner(ulong capacity)
    {
        if (capacity == 0 || capacity % Alignment != 0)
            throw SubmissionScheduler.Fatal("The download batch capacity is invalid.");
        _capacity = capacity;
        PackedSize = 0;
    }

    public ulong PackedSize { get; private set; }
    public bool IsFull => PackedSize == _capacity;

    public void Reset() => PackedSize = 0;

    public BufferDownloadPlacement Append(ulong sourceOffset, ulong remainingSize, ulong bufferSize)
    {
        if (_capacity == 0 || IsFull)
            throw SubmissionScheduler.Fatal("The download batch has no space.");

        var prefix = sourceOffset & 3;
        var dataSize = Math.Min(remainingSize, _capacity - PackedSize - prefix);
        if (dataSize == 0 || sourceOffset > bufferSize || dataSize > bufferSize - sourceOffset)
            throw SubmissionScheduler.Fatal("The download copy range is invalid.");
        if (sourceOffset > ulong.MaxValue - dataSize || sourceOffset + dataSize > ulong.MaxValue - 3)
            throw SubmissionScheduler.Fatal("The aligned download range exceeds the address limit.");

        var sourceBegin = sourceOffset & ~3UL;
        var sourceEnd = (sourceOffset + dataSize + 3) & ~3UL;
        if (sourceEnd > bufferSize)
            throw SubmissionScheduler.Fatal("The aligned download range exceeds its buffer.");

        var transferSize = sourceEnd - sourceBegin;
        var placement = new BufferDownloadPlacement(sourceBegin, transferSize, PackedSize, PackedSize + prefix, dataSize);
        PackedSize += (transferSize + Alignment - 1) & ~(Alignment - 1);
        return placement;
    }
}
