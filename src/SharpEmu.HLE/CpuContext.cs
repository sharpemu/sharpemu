// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.HLE;

public sealed class CpuContext(ICpuMemory memory, Generation generation)
{
    private readonly ulong[] _registers = new ulong[16];
    private readonly ulong[] _xmmRegisters = new ulong[32];
    private readonly ulong[] _ymmUpperRegisters = new ulong[32];
    private bool _raxWritten;

    public ICpuMemory Memory { get; } = memory ?? throw new ArgumentNullException(nameof(memory));

    public Generation TargetGeneration { get; } = generation;

    public ulong Rip { get; set; }

    public ulong Rflags { get; set; }

    public ulong FsBase { get; set; }

    public ulong GsBase { get; set; }

    public ulong this[CpuRegister register]
    {
        get => _registers[(int)register];
        set
        {
            _registers[(int)register] = value;
            if (register == CpuRegister.Rax)
            {
                _raxWritten = true;
            }
        }
    }

    public void ClearRaxWriteFlag()
    {
        _raxWritten = false;
    }

    public bool WasRaxWritten => _raxWritten;

    public void GetXmmRegister(int registerIndex, out ulong low, out ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        low = _xmmRegisters[offset];
        high = _xmmRegisters[offset + 1];
    }

    public void SetXmmRegister(int registerIndex, ulong low, ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        _xmmRegisters[offset] = low;
        _xmmRegisters[offset + 1] = high;
    }

    public void GetYmmUpper(int registerIndex, out ulong low, out ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        low = _ymmUpperRegisters[offset];
        high = _ymmUpperRegisters[offset + 1];
    }

    public void SetYmmUpper(int registerIndex, ulong low, ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        _ymmUpperRegisters[offset] = low;
        _ymmUpperRegisters[offset + 1] = high;
    }

    public void ClearYmmUpper(int registerIndex)
    {
        SetYmmUpper(registerIndex, 0, 0);
    }

    public void ClearAllYmmUpper()
    {
        Array.Clear(_ymmUpperRegisters);
    }

    public void GetYmmRegister(
        int registerIndex,
        out ulong lowLow,
        out ulong lowHigh,
        out ulong highLow,
        out ulong highHigh)
    {
        GetXmmRegister(registerIndex, out lowLow, out lowHigh);
        GetYmmUpper(registerIndex, out highLow, out highHigh);
    }

    public void SetYmmRegister(
        int registerIndex,
        ulong lowLow,
        ulong lowHigh,
        ulong highLow,
        ulong highHigh)
    {
        SetXmmRegister(registerIndex, lowLow, lowHigh);
        SetYmmUpper(registerIndex, highLow, highHigh);
    }

    public bool TryReadByte(ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    public bool TryWriteUInt32(ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryReadUInt64(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    public bool TryWriteUInt64(ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryReadNullTerminatedUtf8(ulong address, int capacity, out string value)
    {
        value = string.Empty;
        if (address == 0 || capacity <= 0)
        {
            return false;
        }

        var bytes = new byte[capacity];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!Memory.TryRead(address + (ulong)index, bytes.AsSpan(index, 1)))
            {
                return false;
            }

            if (bytes[index] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    public bool PushUInt64(ulong value)
    {
        var rsp = this[CpuRegister.Rsp];
        rsp -= sizeof(ulong);
        this[CpuRegister.Rsp] = rsp;
        return TryWriteUInt64(rsp, value);
    }

    public bool PopUInt64(out ulong value)
    {
        var rsp = this[CpuRegister.Rsp];
        if (!TryReadUInt64(rsp, out value))
        {
            return false;
        }

        this[CpuRegister.Rsp] = rsp + sizeof(ulong);
        return true;
    }
}
