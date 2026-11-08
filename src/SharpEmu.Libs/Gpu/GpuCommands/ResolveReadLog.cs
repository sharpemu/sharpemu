// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

// A read a worker cannot serve ahead of the stream: the draw resolves in stream order instead.
public sealed class SerialResolveRequiredException(string reason) : Exception(reason);

// The guest bytes a worker read while it resolved a draw ahead of the stream, so the stream
// position can check them before it records the draw. Adjacent reads merge into one range.
public sealed class ResolveReadLog
{
    private ulong[] _addresses = new ulong[64];
    private int[] _offsets = new int[64];
    private int[] _lengths = new int[64];
    private byte[] _bytes = new byte[2048];
    private int _byteCount;

    public int Count { get; private set; }

    // The GPU-modified version of the buffer cache when the reads began.
    public long Version;
    public long Publications;

    public void Add(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (_byteCount + bytes.Length > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _byteCount + bytes.Length));
        }

        bytes.CopyTo(_bytes.AsSpan(_byteCount));
        if (Count != 0 && _addresses[Count - 1] + (ulong)_lengths[Count - 1] == address && _offsets[Count - 1] + _lengths[Count - 1] == _byteCount)
        {
            _lengths[Count - 1] += bytes.Length;
            _byteCount += bytes.Length;
            return;
        }

        if (Count == _addresses.Length)
        {
            Array.Resize(ref _addresses, Count * 2);
            Array.Resize(ref _offsets, Count * 2);
            Array.Resize(ref _lengths, Count * 2);
        }

        _addresses[Count] = address;
        _offsets[Count] = _byteCount;
        _lengths[Count] = bytes.Length;
        _byteCount += bytes.Length;
        Count++;
    }

    public void Add(ulong address, uint word)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        Add(address, bytes);
    }

    public ReadOnlySpan<byte> Bytes(int index, out ulong address)
    {
        address = _addresses[index];
        return _bytes.AsSpan(_offsets[index], _lengths[index]);
    }

    public void Clear()
    {
        Count = 0;
        _byteCount = 0;
        if (_bytes.Length > 1 << 20)
        {
            _bytes = new byte[2048];
        }
    }
}
