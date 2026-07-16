// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong LayoutAddress = Base + 0x100;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    // SceFontHorizontalLayout is three floats; the sentinel directly after
    // them must survive the call.
    [Fact]
    public void GetHorizontalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }
}
