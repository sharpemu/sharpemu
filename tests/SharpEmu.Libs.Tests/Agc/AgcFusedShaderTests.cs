// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// sceAgcGetFusedShaderSize (dolOmWH+huQ) and sceAgcFuseShaderHalves (fd5Bp5tGTgo)
// join a GS or HS front/back shader half pair into one shader: the fused header
// is the back half retyped, the back half's SH registers become the fused
// register image, and the front half contributes its program address
// (SPI_SHADER_PGM_LO/HI_ES) and checksum registers.
public sealed class AgcFusedShaderTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x4000;

    private const ulong FrontShader = BaseAddress + 0x0000;
    private const ulong BackShader = BaseAddress + 0x0100;
    private const ulong FusedShader = BaseAddress + 0x0200;
    private const ulong FrontRegisters = BaseAddress + 0x0300;
    private const ulong BackRegisters = BaseAddress + 0x0400;
    private const ulong FrontSpecials = BaseAddress + 0x0500;
    private const ulong BackSpecials = BaseAddress + 0x0600;
    private const ulong Scratch = BaseAddress + 0x0700;
    private const ulong SizeResult = BaseAddress + 0x0800;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;

    private const byte GsFront = 4;
    private const byte HsFront = 5;
    private const byte GsBack = 6;
    private const byte HsBack = 7;

    private const ulong FrontCode = 0x0000_1234_5678_9A00;

    [Fact]
    public void GetFusedShaderSize_GsPair_ReportsBackRegisterBytes()
    {
        var (memory, ctx) = CreateGsPair();

        ctx[CpuRegister.Rdi] = SizeResult;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        var result = AgcExports.GetFusedShaderSize(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(5UL * 8UL, ReadUInt64(memory, SizeResult));
        Assert.Equal(4UL, ReadUInt64(memory, SizeResult + 8));
    }

    [Fact]
    public void GetFusedShaderSize_MismatchedHalves_Rejects()
    {
        var (memory, ctx) = CreateGsPair();
        WriteByte(memory, BackShader + ShaderTypeOffset, HsBack);

        ctx[CpuRegister.Rdi] = SizeResult;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        var result = AgcExports.GetFusedShaderSize(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(0UL, ReadUInt64(memory, SizeResult));
    }

    [Fact]
    public void FuseShaderHalves_GsPairWithScratch_BuildsFusedShader()
    {
        var (memory, ctx) = CreateGsPair();

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);

        // Fused header is the back half with type kGs, cleared user data, and
        // registers relocated to the scratch image.
        Assert.Equal(2, ReadByte(memory, FusedShader + ShaderTypeOffset));
        Assert.Equal(0UL, ReadUInt64(memory, FusedShader + ShaderUserDataOffset));
        Assert.Equal(Scratch, ReadUInt64(memory, FusedShader + ShaderShRegistersOffset));
        Assert.Equal(5, ReadByte(memory, FusedShader + ShaderNumShRegistersOffset));
        Assert.Equal(
            ReadUInt64(memory, BackShader + ShaderCodeOffset),
            ReadUInt64(memory, FusedShader + ShaderCodeOffset));

        // The back half's own register image is untouched.
        Assert.Equal(0x1111_1111u, ReadUInt32(memory, BackRegisters + 4));

        // Scratch image: LO_ES points at the front code, HI_ES keeps its upper
        // bits, both checksum occurrences carry the front half's values.
        Assert.Equal(0xC8u, ReadUInt32(memory, Scratch + 0));
        Assert.Equal(0x3456_789Au, ReadUInt32(memory, Scratch + 4));
        Assert.Equal(0xC9u, ReadUInt32(memory, Scratch + 8));
        Assert.Equal(0xAABB_CC12u, ReadUInt32(memory, Scratch + 12));
        Assert.Equal(0xAAAA_0001u, ReadUInt32(memory, Scratch + 20));
        Assert.Equal(0xBBBB_0002u, ReadUInt32(memory, Scratch + 28));
        Assert.Equal(0x5555_5555u, ReadUInt32(memory, Scratch + 36));
    }

    [Fact]
    public void FuseShaderHalves_NoScratch_PatchesBackRegistersInPlace()
    {
        var (memory, ctx) = CreateGsPair();

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = 0;
        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(BackRegisters, ReadUInt64(memory, FusedShader + ShaderShRegistersOffset));
        Assert.Equal(0x3456_789Au, ReadUInt32(memory, BackRegisters + 4));
        Assert.Equal(0xAABB_CC12u, ReadUInt32(memory, BackRegisters + 12));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FuseShaderHalvesLegacy_MergesAllocationsAndPreservesFrontUserData(bool hull, bool scratch)
    {
        var (memory, ctx) = CreateGsPair();
        var rsrc1Offset = hull ? 0x10Au : 0x8Au;
        var checksumOffset = hull ? 0x100u : 0x80u;
        var componentShift = hull ? 28 : 29;
        WriteByte(memory, FrontShader + ShaderTypeOffset, hull ? HsFront : GsFront);
        WriteByte(memory, BackShader + ShaderTypeOffset, hull ? HsBack : GsBack);
        WriteUInt64(memory, FrontShader + ShaderUserDataOffset, SizeResult);
        WriteByte(memory, BackShader + ShaderNumShRegistersOffset, 6);
        WriteRegister(memory, FrontRegisters, 0, rsrc1Offset, (3u << componentShift) | 31u);
        WriteRegister(memory, FrontRegisters, 1, rsrc1Offset + 1, 0xA806_000Cu);
        WriteRegister(memory, FrontRegisters, 2, checksumOffset, 0xAAAA_0001u);
        WriteRegister(memory, FrontRegisters, 3, checksumOffset, 0xBBBB_0002u);
        WriteRegister(memory, BackRegisters, 0, hull ? 0x148u : 0xC8u, 0x1111_1111u);
        WriteRegister(memory, BackRegisters, 1, hull ? 0x149u : 0xC9u, 0xAABB_CC77u);
        WriteRegister(memory, BackRegisters, 2, checksumOffset, 0x1111_0001u);
        WriteRegister(memory, BackRegisters, 3, checksumOffset, 0x1111_0002u);
        WriteRegister(memory, BackRegisters, 4, rsrc1Offset, 0x8000_000Fu | (1u << componentShift));
        WriteRegister(memory, BackRegisters, 5, rsrc1Offset + 1, 0x5003_8028u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = scratch ? Scratch : 0;
        Assert.Equal(0, AgcExports.FuseShaderHalvesLegacy(ctx));

        var registers = scratch ? Scratch : BackRegisters;
        Assert.Equal(hull ? 3 : 2, ReadByte(memory, FusedShader + ShaderTypeOffset));
        Assert.Equal(SizeResult, ReadUInt64(memory, FusedShader + ShaderUserDataOffset));
        Assert.Equal(registers, ReadUInt64(memory, FusedShader + ShaderShRegistersOffset));
        Assert.Equal(0x3456_789Au, ReadUInt32(memory, registers + 4));
        Assert.Equal(0xAABB_CC12u, ReadUInt32(memory, registers + 12));
        Assert.Equal(0xAAAA_0001u, ReadUInt32(memory, registers + 20));
        Assert.Equal(0xBBBB_0002u, ReadUInt32(memory, registers + 28));
        Assert.Equal(hull ? 0xB000_001Fu : 0xE000_001Fu, ReadUInt32(memory, registers + 36));
        Assert.Equal(hull ? 0xA803_800Cu : 0xA807_800Cu, ReadUInt32(memory, registers + 44));
        if (scratch)
        {
            Assert.Equal(0x1111_1111u, ReadUInt32(memory, BackRegisters + 4));
            Assert.Equal(0x5003_8028u, ReadUInt32(memory, BackRegisters + 44));
        }
    }

    [Fact]
    public void FuseShaderHalves_WaveSizeMismatch_Rejects()
    {
        var (memory, ctx) = CreateGsPair();
        WriteUInt32(memory, BackSpecials + 0x08 + 4, 0u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(0, ReadByte(memory, FusedShader + ShaderTypeOffset));
    }

    [Fact]
    public void FuseShaderHalves_HsPair_PatchesLoLs()
    {
        var (memory, ctx) = CreateGsPair();
        WriteByte(memory, FrontShader + ShaderTypeOffset, HsFront);
        WriteByte(memory, BackShader + ShaderTypeOffset, HsBack);
        WriteUInt32(memory, BackRegisters + 0, 0x148u);
        WriteUInt32(memory, BackRegisters + 8, 0x149u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(3, ReadByte(memory, FusedShader + ShaderTypeOffset));
        Assert.Equal(0x3456_789Au, ReadUInt32(memory, Scratch + 4));
        // Checksum grafting is a geometry-pair behavior; the HS image keeps its own values.
        Assert.Equal(0x1111_0001u, ReadUInt32(memory, Scratch + 20));
    }

    [Fact]
    public void FuseShaderHalves_MissingSpecials_SkipsWaveSizeGate()
    {
        var (memory, ctx) = CreateGsPair();
        // The divergence the mismatch test rejects passes when a half lacks specials.
        WriteUInt64(memory, FrontShader + ShaderSpecialsOffset, 0);
        WriteUInt32(memory, BackSpecials + 0x08 + 4, 0u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(2, ReadByte(memory, FusedShader + ShaderTypeOffset));
    }

    [Fact]
    public void FuseShaderHalves_ProgramRegisterAbsent_LeavesImageUntouched()
    {
        var (memory, ctx) = CreateGsPair();
        WriteByte(memory, FrontShader + ShaderTypeOffset, HsFront);
        WriteByte(memory, BackShader + ShaderTypeOffset, HsBack);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        // No LO_LS entry in the back image: the fuse still succeeds and the
        // scratch copy stays verbatim.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(3, ReadByte(memory, FusedShader + ShaderTypeOffset));
        Assert.Equal(0x1111_1111u, ReadUInt32(memory, Scratch + 4));
        Assert.Equal(0xAABB_CC77u, ReadUInt32(memory, Scratch + 12));
    }

    [Fact]
    public void FuseShaderHalves_UnpairedProgramRegister_LeavesImageUntouched()
    {
        var (memory, ctx) = CreateGsPair();
        WriteByte(memory, FrontShader + ShaderTypeOffset, HsFront);
        WriteByte(memory, BackShader + ShaderTypeOffset, HsBack);
        WriteUInt32(memory, BackRegisters + 0, 0x148u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        // LO_LS is present but the next entry is not HI_LS, so the patch is skipped.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x1111_1111u, ReadUInt32(memory, Scratch + 4));
    }

    [Fact]
    public void FuseShaderHalves_ProgramRegisterAtImageEnd_LeavesImageUntouched()
    {
        var (memory, ctx) = CreateGsPair();
        WriteByte(memory, FrontShader + ShaderTypeOffset, HsFront);
        WriteByte(memory, BackShader + ShaderTypeOffset, HsBack);
        WriteUInt32(memory, BackRegisters + 4 * 8, 0x148u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        var result = AgcExports.FuseShaderHalves(ctx);

        // The hi half of the pair would sit past the register image.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x5555_5555u, ReadUInt32(memory, Scratch + 36));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FuseShaderHalves_RegistersBothProgramSegmentsAcrossThreadWrappers(bool legacy)
    {
        var (memory, _) = CreateGsPair();
        var ctx = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        var entryCode = BaseAddress + 0x1000;
        var continuationCode = BaseAddress + 0x1100;
        WriteUInt64(memory, FrontShader + ShaderCodeOffset, entryCode);
        WriteUInt64(memory, BackShader + ShaderCodeOffset, continuationCode);
        WriteUInt32(memory, FrontShader + 0x44, 2 * sizeof(uint));
        WriteUInt32(memory, BackShader + 0x44, 2 * sizeof(uint));
        WriteWords(memory, entryCode, 0xBF800000u, 0xBE802000u);
        WriteWords(memory, continuationCode, 0xBF800000u, 0xBF810000u);

        ctx[CpuRegister.Rdi] = FusedShader;
        ctx[CpuRegister.Rsi] = FrontShader;
        ctx[CpuRegister.Rdx] = BackShader;
        ctx[CpuRegister.Rcx] = Scratch;
        Assert.Equal(0, legacy ? AgcExports.FuseShaderHalvesLegacy(ctx) : AgcExports.FuseShaderHalves(ctx));

        var renderContext = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryGetFusedProgramParts(renderContext, entryCode, out var continuation, out _));
        Assert.Equal(continuationCode, continuation);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                renderContext,
                entryCode,
                out var program,
                out var error),
            error);
        Assert.Equal(
            ["SNop", "SNop", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
    }

    [Fact]
    public void EmbeddedFusedProgram_RegistersValidatedContinuationDescriptor()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1_0000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var entryHeader = BaseAddress + 0x100;
        var entryCode = BaseAddress + 0x1000;
        var continuationHeader = entryCode + 0x80;
        var continuationCode = entryCode + 0x200;

        WriteUInt32(memory, entryHeader, 0x34333231u);
        WriteUInt32(memory, entryHeader + sizeof(uint), 0x18u);
        WriteUInt64(memory, entryHeader + ShaderCodeOffset, entryCode);
        WriteUInt32(memory, entryHeader + 0x44, 2 * sizeof(uint));
        WriteByte(memory, entryHeader + ShaderTypeOffset, GsFront);
        WriteWords(memory, entryCode, 0xBF800000u, 0xBE802000u);

        WriteUInt32(memory, continuationHeader, 0x34333231u);
        WriteUInt32(memory, continuationHeader + sizeof(uint), 0x18u);
        WriteUInt64(memory, continuationHeader + ShaderCodeOffset, continuationCode);
        WriteUInt32(memory, continuationHeader + 0x44, 2 * sizeof(uint));
        WriteByte(memory, continuationHeader + ShaderTypeOffset, GsBack);
        WriteWords(memory, continuationCode, 0xBF800000u, 0xBF810000u);

        Assert.True(
            AgcExports.TryRegisterEmbeddedFusedProgram(ctx, entryCode, entryHeader));
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, entryCode, out var program, out var error),
            error);
        Assert.Equal(
            ["SNop", "SNop", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
    }

    private static (FakeCpuMemory Memory, CpuContext Ctx) CreateGsPair()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        WriteByte(memory, FrontShader + ShaderTypeOffset, GsFront);
        WriteUInt64(memory, FrontShader + ShaderCodeOffset, FrontCode);
        WriteUInt64(memory, FrontShader + ShaderShRegistersOffset, FrontRegisters);
        WriteUInt64(memory, FrontShader + ShaderSpecialsOffset, FrontSpecials);
        WriteByte(memory, FrontShader + ShaderNumShRegistersOffset, 4);

        WriteByte(memory, BackShader + ShaderTypeOffset, GsBack);
        WriteUInt64(memory, BackShader + ShaderCodeOffset, 0x0000_0BAD_F00D_BE00);
        WriteUInt64(memory, BackShader + ShaderShRegistersOffset, BackRegisters);
        WriteUInt64(memory, BackShader + ShaderSpecialsOffset, BackSpecials);
        WriteUInt64(memory, BackShader + ShaderUserDataOffset, 0xDEAD_BEEF);
        WriteByte(memory, BackShader + ShaderNumShRegistersOffset, 5);

        // Back image: ES program address pair, two checksum slots, one bystander.
        WriteRegister(memory, BackRegisters, 0, 0xC8u, 0x1111_1111u);
        WriteRegister(memory, BackRegisters, 1, 0xC9u, 0xAABB_CC77u);
        WriteRegister(memory, BackRegisters, 2, 0x80u, 0x1111_0001u);
        WriteRegister(memory, BackRegisters, 3, 0x80u, 0x1111_0002u);
        WriteRegister(memory, BackRegisters, 4, 0x10u, 0x5555_5555u);

        // Front image: GS RSRC pair as shipped, then the checksum values to graft.
        WriteRegister(memory, FrontRegisters, 0, 0x8Au, 0x0123_4567u);
        WriteRegister(memory, FrontRegisters, 1, 0x8Bu, 0x89AB_CDEFu);
        WriteRegister(memory, FrontRegisters, 2, 0x80u, 0xAAAA_0001u);
        WriteRegister(memory, FrontRegisters, 3, 0x80u, 0xBBBB_0002u);

        // VGT_SHADER_STAGES_EN register pairs with the GS wave32 enable bit set on both halves.
        WriteUInt32(memory, FrontSpecials + 0x08, 0x1F1u);
        WriteUInt32(memory, FrontSpecials + 0x08 + 4, 1u << 22);
        WriteUInt32(memory, BackSpecials + 0x08, 0x1F1u);
        WriteUInt32(memory, BackSpecials + 0x08 + 4, 1u << 22);

        return (memory, ctx);
    }

    private static void WriteRegister(FakeCpuMemory memory, ulong array, int index, uint offset, uint value)
    {
        WriteUInt32(memory, array + (ulong)index * 8, offset);
        WriteUInt32(memory, array + (ulong)index * 8 + 4, value);
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value)
    {
        Span<byte> buffer = [value];
        Assert.True(memory.TryWrite(address, buffer));
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

    private static void WriteWords(FakeCpuMemory memory, ulong address, params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(address, bytes));
    }

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[1];
        Assert.True(memory.TryRead(address, buffer));
        return buffer[0];
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }
}
