// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.Rendering;

// Owns the submitted bytes and layout until the matching draw has copied them.
internal sealed class SubmittedVertexData
{
    private readonly VertexInputBuffer[] _buffers;
    private readonly VertexAttributeResource[] _attributes;
    private readonly byte[][] _data;

    private SubmittedVertexData(VertexInputInfo input, byte[][] data, long byteCount)
    {
        _buffers = (VertexInputBuffer[])input.Buffers.Clone();
        _attributes = (VertexAttributeResource[])input.Attributes.Clone();
        _data = data;
        ByteCount = byteCount;
    }

    public long ByteCount { get; }

    public static bool TryCapture(ICpuMemory memory, VertexInputInfo input, long maximumBytes, out SubmittedVertexData? snapshot)
    {
        snapshot = null;
        if (input.Buffers.Length == 0 || maximumBytes <= 0)
        {
            return false;
        }

        // Validate the full allocation before reading any part of the snapshot.
        long byteCount = 0;
        var uniqueRanges = new Dictionary<(ulong Address, ulong Size), byte[]>();
        for (var index = 0; index < input.Buffers.Length; index++)
        {
            var buffer = input.Buffers[index];
            var size = input.BufferSize(index);
            if (size > int.MaxValue || size > ulong.MaxValue - buffer.Address ||
                (size != 0 && buffer.Address == 0))
            {
                return false;
            }

            if (!uniqueRanges.TryAdd((buffer.Address, size), [])) continue;
            if (size > (ulong)(maximumBytes - byteCount)) return false;
            byteCount += (long)size;
        }

        if (byteCount == 0) return false;
        var data = new byte[input.Buffers.Length][];
        for (var index = 0; index < input.Buffers.Length; index++)
        {
            var buffer = input.Buffers[index];
            var size = input.BufferSize(index);
            var key = (buffer.Address, size);
            var bytes = uniqueRanges[key];
            if (bytes.Length == 0 && size != 0)
            {
                bytes = new byte[(int)size];
                if (!memory.TryRead(buffer.Address, bytes)) return false;
                uniqueRanges[key] = bytes;
            }

            data[index] = bytes;
        }

        snapshot = new SubmittedVertexData(input, data, byteCount);
        return true;
    }

    public bool Matches(VertexInputInfo input) =>
        _buffers.AsSpan().SequenceEqual(input.Buffers) &&
        _attributes.AsSpan().SequenceEqual(input.Attributes);

    // Draw records own their copies; a replay cannot modify the submission snapshot.
    public byte[] CopyBuffer(int index) => (byte[])_data[index].Clone();
}
