// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// Coverage for the SH-register path in the PM4 parser. A draw resolves its
/// vertex stage from SPI_SHADER_PGM_LO_ES/HI_ES and its pixel stage from
/// SPI_SHADER_PGM_LO_PS/HI_PS, both out of this dictionary, so a key that is
/// dropped or written under a different encoding pairs a current pixel shader
/// with a stale vertex shader — a failure that produces plausible-looking
/// garbage rather than an error. These drive real PM4 packets through the
/// public submit export and assert what the parser retained.
/// </summary>
public sealed class AgcShaderStageRegisterTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const ulong SubmitPacketAddress = BaseAddress + 0x40;
    private const ulong CommandAddress = BaseAddress + 0x200;
    private const ulong IndirectTableAddress = BaseAddress + 0x600;

    private const uint ItNop = 0x10;
    private const uint ItSetShReg = 0x76;
    private const uint RShRegsIndirect = 0x11;

    // SH register offsets. ES is the vertex stage on GFX10 — the standalone
    // PGM_LO/HI_GS pair is dead post-GCN and the merged ES/GS stage is addressed
    // through ES.
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;

    private static uint Pm4Header(uint dwords, uint opcode, uint register = 0) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8) | ((register & 0x3Fu) << 2);

    /// <summary>
    /// The baseline: a direct SET_SH_REG write of the vertex stage address has
    /// to be readable afterwards. If this fails, nothing downstream can pair
    /// shaders correctly.
    /// </summary>
    [Fact]
    public void SetShRegRetainsExportShaderAddress()
    {
        var ctx = CreateContext(out var memory);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(3, ItSetShReg),
            SpiShaderPgmLoEs,
            0x0044_8582u);
        Submit(ctx, memory, dwordCount: 3);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var value));
        Assert.Equal(0x0044_8582u, value);
    }

    /// <summary>
    /// The indirect encoding must land on the same keys as the direct one. A
    /// mismatch would store the stage address where the draw never reads it,
    /// leaving the draw to see whatever a previous submission left behind.
    /// </summary>
    [Fact]
    public void IndirectShRegisterWriteRetainsExportShaderAddress()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectShRegisterCommand(memory, (SpiShaderPgmLoEs, 0x0044_8DD1u));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var value));
        Assert.Equal(0x0044_8DD1u, value);
    }

    /// <summary>
    /// Both stages written in one submission must both read back as written. If
    /// the vertex stage kept an older value while the pixel stage updated, every
    /// draw after it would be mis-paired.
    /// </summary>
    [Fact]
    public void BothStagesUpdateTogetherWithinOneSubmission()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectShRegisterCommand(
            memory,
            (SpiShaderPgmLoEs, 0x0080_2933u),
            (SpiShaderPgmLoPs, 0x0044_858Au));
        Submit(ctx, memory, dwordCount: 4);

        WriteIndirectShRegisterCommand(
            memory,
            (SpiShaderPgmLoEs, 0x0044_8581u),
            (SpiShaderPgmLoPs, 0x0044_8719u));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var es));
        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoPs, out var ps));
        Assert.Equal(0x0044_8581u, es);
        Assert.Equal(0x0044_8719u, ps);
    }

    /// <summary>
    /// Updating only the pixel stage must leave the vertex stage at its previous
    /// value rather than dropping the key, or the draw falls back to whatever
    /// default the resolver finds.
    /// </summary>
    [Fact]
    public void PixelStageUpdateLeavesExportStageIntact()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectShRegisterCommand(memory, (SpiShaderPgmLoEs, 0x0044_8582u));
        Submit(ctx, memory, dwordCount: 4);

        WriteIndirectShRegisterCommand(memory, (SpiShaderPgmLoPs, 0x0044_858Au));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var es));
        Assert.Equal(0x0044_8582u, es);
    }

    /// <summary>
    /// Stage addresses are 64-bit: LO carries bits 39:8 and HI the top bits, and
    /// the draw combines them. A HI retained from an earlier shader while LO
    /// updates resolves to a splice of two different programs.
    /// </summary>
    [Fact]
    public void HighAndLowHalvesUpdateTogether()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectShRegisterCommand(
            memory,
            (SpiShaderPgmLoEs, 0x0080_2933u),
            (SpiShaderPgmHiEs, 0x0000_0008u));
        Submit(ctx, memory, dwordCount: 4);

        WriteIndirectShRegisterCommand(
            memory,
            (SpiShaderPgmLoEs, 0x0044_8582u),
            (SpiShaderPgmHiEs, 0x0000_0004u));
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var lo));
        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmHiEs, out var hi));
        Assert.Equal(0x0044_8582u, lo);
        Assert.Equal(0x0000_0004u, hi);
    }

    /// <summary>
    /// SH registers persist across submissions on hardware. A stage address set
    /// in one submission must still be there for a draw in the next.
    /// </summary>
    [Fact]
    public void ExportShaderAddressSurvivesASecondSubmission()
    {
        var ctx = CreateContext(out var memory);
        WriteIndirectShRegisterCommand(memory, (SpiShaderPgmLoEs, 0x0044_8583u));
        Submit(ctx, memory, dwordCount: 4);

        WriteDwords(memory, CommandAddress, Pm4Header(2, ItNop), 0);
        Submit(ctx, memory, dwordCount: 2);

        Assert.True(
            AgcExports.TryGetGraphicsShRegisterForTests(ctx, SpiShaderPgmLoEs, out var value));
        Assert.Equal(0x0044_8583u, value);
    }

    private static void WriteIndirectShRegisterCommand(
        FakeCpuMemory memory,
        params (uint Offset, uint Value)[] registers)
    {
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(4, ItNop, RShRegsIndirect),
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
