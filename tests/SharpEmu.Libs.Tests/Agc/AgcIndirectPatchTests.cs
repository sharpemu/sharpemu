// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectPatchTests
{
    private const ulong BaseAddress = 0x2_2000_0000;
    private const ulong CommandAddress = BaseAddress + 0x100;
    private const uint SetContextRegIndirectHeader = 0xC003_9F00u;
    private const uint SetShaderRegIndirectHeader = 0xC003_6300u;
    private const uint SetUserConfigRegIndirectHeader = 0xC003_6400u;

    [Fact]
    public void SetCxRegisterCount_ReplacesCountAndPreservesOtherFields()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt64(memory, CommandAddress + 4, 0x5566_7788_99AA_BBCC);
        WriteUInt32(memory, CommandAddress + 12, 0x8000_0000u);
        WriteUInt32(memory, CommandAddress + 16, 0xCAFE_0003u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x4C;

        var result = AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(SetContextRegIndirectHeader, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x5566_7788_99AA_BBCCUL, ReadUInt64(memory, CommandAddress + 4));
        Assert.Equal(0x8000_0000u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0xCAFE_004Cu, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetCxRegisterCount_AllowsZeroAndAddRegistersUsesNewCount()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 9);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 16));

        ctx[CpuRegister.Rsi] = 7;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(7u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetShRegisterCount_ExposesTrailingComputeDimensions()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetShaderRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 0xCAFE_0007u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 10;

        var result = AgcExports.SetShRegIndirectPatchSetNumRegisters(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        // A compute header commonly places NUM_THREAD_X/Y/Z at entries 7-9.
        // Keeping the old count at seven binds the shader but leaves stale dimensions.
        Assert.Equal(0xCAFE_000Au, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetShRegisterCount_MakesTheWholeComputeHeaderVisibleToTheCommandProcessor()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(StreamRunner.TableAddress,
        [
            ShaderRegisterOffset.ComputePgmLo, 0x200B_1E00u,
            ShaderRegisterOffset.ComputePgmHi, 0u,
            ShaderRegisterOffset.ComputeShaderChecksum, 0x2800_0940u,
            ShaderRegisterOffset.ComputeShaderChecksum, 0x4058_F2C2u,
            ShaderRegisterOffset.ComputePgmRsrc1, 0x402C_0187u,
            ShaderRegisterOffset.ComputePgmRsrc2, 0x0000_899Cu,
            ShaderRegisterOffset.ComputeResourceLimits, 0u,
            ShaderRegisterOffset.ComputeNumThreadX, 8u,
            ShaderRegisterOffset.ComputeNumThreadY, 8u,
            ShaderRegisterOffset.ComputeNumThreadZ, 1u,
        ]);
        runner.Load(StreamRunner.Packet(
            PacketOpcode.SetShaderRegisterIndirect,
            StreamRunner.Low(StreamRunner.TableAddress),
            StreamRunner.High(StreamRunner.TableAddress),
            0x8000_0000u,
            7u));
        var ctx = new CpuContext(runner.Host.GuestMemory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = StreamRunner.CommandAddress,
            [CpuRegister.Rsi] = 10,
        };

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetShRegIndirectPatchSetNumRegisters(ctx));
        runner.Run();

        var compute = runner.Interpreter.TypedRegisters.Shader.Compute;
        Assert.Equal(0x0000_0020_0B1E_0000UL, compute.Address);
        Assert.Equal((8u, 8u, 1u), (compute.ThreadsX, compute.ThreadsY, compute.ThreadsZ));
    }

    [Fact]
    public void SetUcRegisterCount_UsesTheNativeIndirectPacketLayout()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetUserConfigRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 0x8000_0002u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 6;

        var result = AgcExports.SetUcRegIndirectPatchSetNumRegisters(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x8000_0006u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Theory]
    [InlineData("nCUgItdN2ms", "sceAgcSetShRegIndirectPatchSetNumRegisters")]
    [InlineData("fRG-JOH5+sI", "sceAgcSetUcRegIndirectPatchSetNumRegisters")]
    public void IndirectCountPatches_AreRegistered(string nid, string expectedName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(expectedName, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
    }

    [Fact]
    public void SetCxAddress_PatchesNativeAddressAndPreservesLowControlBits()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 4, 3u);
        WriteUInt32(memory, CommandAddress + 8, 0u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x0000_0002_3456_789CUL;

        var result = AgcExports.SetCxRegIndirectPatchSetAddress(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x0000_0002_3456_789FUL, ReadUInt64(memory, CommandAddress + 4));
    }

    [Fact]
    public void SetCxRegisterCount_RejectsNullCommandAddress()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
    }

    [Fact]
    public void SetCxRegisterCount_ReturnsMemoryFaultForInvalidGuestMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x1000;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
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
