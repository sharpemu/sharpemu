// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using Xunit;

namespace SharpEmu.Libs.Tests.Ime;

public sealed class ImeExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong OutAddress = Base + 0x100;
    private const int InvalidAddress = unchecked((int)0x80BC0031);
    // sizeof(SceImeKeyboardInfo): six uint32 fields plus
    // int8_t reserved[12].
    private const int KeyboardInfoSize = 0x24;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public ImeExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private void Paint(int length, byte value)
    {
        var paint = new byte[length];
        paint.AsSpan().Fill(value);
        Assert.True(_memory.TryWrite(OutAddress, paint));
    }

    [Fact]
    public void KeyboardGetInfo_WritesExactlySizeofKeyboardInfo()
    {
        Paint(KeyboardInfoSize + 0x10, 0x6B);
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, ImeExports.ImeKeyboardGetInfo(_ctx));

        Span<byte> info = stackalloc byte[KeyboardInfoSize + 0x10];
        Assert.True(_memory.TryRead(OutAddress, info));
        Assert.Equal(0x10000000, BinaryPrimitives.ReadInt32LittleEndian(info));
        // device = SCE_IME_KEYBOARD_DEVICE_TYPE_OSK.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(info[4..]));
        // status = SCE_IME_KEYBOARD_STATE_DISCONNECTED.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[0x14..]));
        // Nothing past the struct may be touched.
        Assert.Equal(0x6B, info[KeyboardInfoSize]);
        Assert.Equal(0x6B, info[KeyboardInfoSize + 0x0F]);
    }

    [Fact]
    public void KeyboardGetInfo_RejectsANullInfoPointer()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(InvalidAddress, ImeExports.ImeKeyboardGetInfo(_ctx));
    }

    [Fact]
    public void KeyboardGetResourceId_PublishesAnEmptyIdArray()
    {
        Paint(0x30, 0x4D);
        _ctx[CpuRegister.Rdi] = 0x10000000;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, ImeExports.ImeKeyboardGetResourceId(_ctx));

        Span<byte> array = stackalloc byte[0x30];
        Assert.True(_memory.TryRead(OutAddress, array));
        Assert.Equal(0x10000000, BinaryPrimitives.ReadInt32LittleEndian(array));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(array[(4 + (i * 4))..]));
        }

        // sizeof(SceImeKeyboardResourceIdArray) = 4 + 5 * 4 = 0x18.
        Assert.Equal(0x4D, array[0x18]);
    }
}
