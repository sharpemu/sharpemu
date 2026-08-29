// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// Coverage for the graphics context-register path in the PM4 parser. Draw
/// translation reads render state out of this dictionary (CB_TARGET_MASK
/// decides whether a draw writes alpha, CB_COLOR_CONTROL decides what the draw
/// means), so a write that lands under the wrong key, or fails to overwrite an
/// earlier one, silently changes what every later draw does. These drive real
/// PM4 packets through the public submit export and assert what the parser
/// retained.
/// </summary>
public sealed class AgcContextRegisterTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const ulong SubmitPacketAddress = BaseAddress + 0x40;
    private const ulong CommandAddress = BaseAddress + 0x200;
    private const ulong IndirectTableAddress = BaseAddress + 0x600;

    private const uint ItNop = 0x10;
    private const uint ItSetContextReg = 0x69;
    private const uint RCxRegsIndirect = 0x12;
    private const uint CbTargetMask = 0x8E;
    private const uint CbColorControl = 0x202;
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbZInfo = 0x010;

    // PM4 type-3 header: 0xC0000000 | ((dwords - 2) << 16) | (opcode << 8), with the
    // NOP sub-register in bits 2..7 — the parser reads it as (header >> 2) & 0x3F.
    private static uint Pm4Header(uint dwords, uint opcode, uint register = 0) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8) | ((register & 0x3Fu) << 2);

    [Fact]
    public void SetContextRegRetainsTargetMask()
    {
        var ctx = CreateContext(out var memory);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(3, ItSetContextReg),
            CbTargetMask,
            0x0000_0007u);
        Submit(ctx, memory, dwordCount: 3);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out var value));
        Assert.Equal(0x0000_0007u, value);
    }

    /// <summary>
    /// The indirect form carries (offset, value) pairs out of guest memory
    /// rather than inline dwords, so an offset-encoding mismatch here would
    /// store the register under a key no reader looks at.
    /// </summary>
    [Fact]
    public void IndirectRegisterWriteRetainsTargetMask()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectRegisterCommand(memory, (CbTargetMask, 0xFFFF_FFFFu));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out var value));
        Assert.Equal(0xFFFF_FFFFu, value);
    }

    /// <summary>
    /// Context registers persist across submissions on hardware until something
    /// clears them, so a mask written in one submission has to still be there
    /// for a draw in the next.
    /// </summary>
    [Fact]
    public void TargetMaskSurvivesASecondSubmission()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectRegisterCommand(memory, (CbTargetMask, 0x8888_8888u));
        Submit(ctx, memory, dwordCount: 4);

        // A second, unrelated submission: a bare NOP that touches no registers.
        WriteDwords(memory, CommandAddress, Pm4Header(2, ItNop), 0);
        Submit(ctx, memory, dwordCount: 2);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out var value));
        Assert.Equal(0x8888_8888u, value);
    }

    /// <summary>
    /// Both encodings must land on the same key, or a title that sets the
    /// register one way and a reader that expects the other silently disagree.
    /// </summary>
    [Fact]
    public void DirectAndIndirectWritesShareOneKey()
    {
        var ctx = CreateContext(out var memory);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(3, ItSetContextReg),
            CbTargetMask,
            0x0000_0007u);
        Submit(ctx, memory, dwordCount: 3);

        WriteIndirectRegisterCommand(memory, (CbTargetMask, 0x0000_000Fu));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out var value));
        Assert.Equal(0x0000_000Fu, value);
    }

    /// <summary>
    /// CB_COLOR_CONTROL (0x202) MODE bits [6:4] give Normal=1,
    /// EliminateFastClear=2, Resolve=3, FmaskDecompress=5, DccDecompress=6. The
    /// value has to survive the parser intact, ROP3 bits and all, because the
    /// mode decides whether a draw shades or resolves.
    /// </summary>
    [Theory]
    [InlineData(0x0000_0010u, 1u)] // Normal
    [InlineData(0x0000_0020u, 2u)] // EliminateFastClear
    [InlineData(0x00CC_0060u, 6u)] // DccDecompress, with ROP3=0xCC alongside
    public void ColorControlRetainsMode(uint written, uint expectedMode)
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectRegisterCommand(memory, (CbColorControl, written));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbColorControl, out var value));
        Assert.Equal(written, value);
        Assert.Equal(expectedMode, (value >> 4) & 0x7u);
    }

    /// <summary>
    /// A later write must win. If the parser kept the first value, a draw that
    /// sets EliminateFastClear after an earlier Normal would still read Normal
    /// and the clear would be silently dropped.
    /// </summary>
    [Fact]
    public void ColorControlLaterWriteOverwritesEarlier()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectRegisterCommand(memory, (CbColorControl, 0x00CC_0010u));
        Submit(ctx, memory, dwordCount: 4);

        WriteIndirectRegisterCommand(memory, (CbColorControl, 0x00CC_0020u));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbColorControl, out var value));
        Assert.Equal(0x00CC_0020u, value);
        Assert.Equal(2u, (value >> 4) & 0x7u);
    }

    [Fact]
    public void CompositeDepthTargetRetainsTrailingExtent()
    {
        var ctx = CreateContext(out var memory);
        var sizeXy = 1919u | (1079u << 16);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(10, ItSetContextReg), DbZInfo,
            3, 0, 0x10000, 0, 0x10000, 0, 0, 0,
            Pm4Header(3, ItSetContextReg), 0x00F, 0,
            Pm4Header(3, ItSetContextReg), 0x002, 0,
            Pm4Header(3, ItSetContextReg), 0x005, 0,
            Pm4Header(3, ItSetContextReg), 0x2AF, 0,
            Pm4Header(2, ItNop), sizeXy);
        Submit(ctx, memory, dwordCount: 24);

        Assert.True(AgcExports.TryGetGraphicsCompositeDepthSizeForTests(ctx, out var value));
        Assert.Equal(sizeXy, value);
    }

    [Fact]
    public void StandaloneDepthSizeSupersedesCompositeExtent()
    {
        var ctx = CreateContext(out var memory);
        var sizeXy = 1919u | (1079u << 16);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(10, ItSetContextReg), DbZInfo,
            3, 0, 0x10000, 0, 0x10000, 0, 0, 0,
            Pm4Header(3, ItSetContextReg), 0x00F, 0,
            Pm4Header(3, ItSetContextReg), 0x002, 0,
            Pm4Header(3, ItSetContextReg), 0x005, 0,
            Pm4Header(3, ItSetContextReg), 0x2AF, 0,
            Pm4Header(2, ItNop), sizeXy);
        Submit(ctx, memory, dwordCount: 24);

        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(3, ItSetContextReg),
            DbDepthSizeXy,
            7u);
        Submit(ctx, memory, dwordCount: 3);

        Assert.False(AgcExports.TryGetGraphicsCompositeDepthSizeForTests(ctx, out _));
        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, DbDepthSizeXy, out var value));
        Assert.Equal(7u, value);
    }

    private static void WriteIndirectRegisterCommand(
        FakeCpuMemory memory,
        params (uint Offset, uint Value)[] registers)
    {
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(4, ItNop, RCxRegsIndirect),
            (uint)registers.Length,
            (uint)(IndirectTableAddress & 0xFFFF_FFFFu),
            (uint)(IndirectTableAddress >> 32));

        for (var index = 0; index < registers.Length; index++)
        {
            var entry = IndirectTableAddress + ((ulong)index * 8);
            WriteUInt32(memory, entry, registers[index].Offset);
            WriteUInt32(memory, entry + 4, registers[index].Value);
        }
    }

    private static void Submit(CpuContext ctx, FakeCpuMemory memory, uint dwordCount)
    {
        WriteUInt64(memory, SubmitPacketAddress, CommandAddress);
        WriteUInt32(memory, SubmitPacketAddress + 8, dwordCount);
        ctx[CpuRegister.Rdi] = SubmitPacketAddress;
        AgcExports.DriverSubmitDcb(ctx);
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void WriteDwords(FakeCpuMemory memory, ulong address, params uint[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            WriteUInt32(memory, address + ((ulong)index * sizeof(uint)), values[index]);
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
