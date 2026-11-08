// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcGsOversubscriptionTests
{
    private const string Nid = "NKIzURsgV7I";
    private const ulong Table = StreamRunner.TableAddress;

    private static ModuleManager Manager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    [Fact]
    public void ZeroBudget_ReplacesStaleDescriptorsBeforeNativeRegisterSubmission()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, [0x191, 0, 0x192, 1]);
        var context = new CpuContext(runner.Host.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = Table;
        context[CpuRegister.Rsi] = ulong.MaxValue; // The zero-budget contract needs no shader read.
        context[CpuRegister.Rdx] = 0;
        // Yotei ignores the return value and submits both output descriptors.
        var result = Manager().Dispatch(Nid, context);
        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetUserConfigRegisterIndirect, StreamRunner.Low(Table), StreamRunner.High(Table), 0x8000_0000, 1),
            StreamRunner.Packet(PacketOpcode.SetShaderRegisterIndirect, StreamRunner.Low(Table + 8), StreamRunner.High(Table + 8), 0x8000_0000, 1));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0u, runner.Interpreter.Registers.Shader[0x81]);
        Assert.False(runner.Interpreter.Registers.Shader.ContainsKey(0x192));
    }

    [Fact]
    public void Export_IsRegisteredForTheDriverLibrary()
    {
        Assert.True(Manager().TryGetExport(Nid, out var export));
        Assert.Equal("sceAgcGetGsOversubscription", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(uint.MaxValue, 0x7FFu, 0x7F_0000u)]
    public void BudgetEndpoints_InitializeBothOffsetsAndValuesWithoutShaderReads(uint budget, uint pc, uint late)
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, [0x191, 0xDEAD, 0x192, 0xBEEF]);
        var context = Context(runner, budget);
        context[CpuRegister.Rsi] = ulong.MaxValue;
        context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(float.NaN), 0);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Manager().Dispatch(Nid, context));
        AssertOutput(context, pc, late);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0u, 65536u, 0.5f, 0x0040_0000u, 0x7FFu, 0x20_0000u)]
    [InlineData(0u, 65536u, 0f, 0x0040_0000u, 0u, 0u)]
    [InlineData(0x3Cu, 4096u, 0.5f, 0x0040_0000u, 0x3FFu, 0x7F_0000u)]
    [InlineData(0x3Cu, 4096u, 0.5f, 0u, 0u, 0u)]
    public void FiniteBudget_UsesShaderOccupancyAndWaveSize(uint vertexOutput, uint budget, float factor, uint stages, uint pc, uint late)
    {
        var runner = new StreamRunner();
        var context = Context(runner, budget);
        WriteShader(runner, context, vertexOutput, stages);
        context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(factor), 0);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Manager().Dispatch(Nid, context));
        AssertOutput(context, pc, late);
    }

    [Fact]
    public void InvalidShaderAndFactor_DoNotPartiallyOverwriteOutput()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Table, [0x191, 0xDEAD, 0x192, 0xBEEF]);
        var context = Context(runner, 65536);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Manager().Dispatch(Nid, context));
        WriteShader(runner, context, 0, 0x0040_0000);
        context.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(float.NaN), 0);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Manager().Dispatch(Nid, context));
        Assert.True(context.TryReadUInt32(Table, out var offset));
        Assert.Equal(0x191u, offset);
        Assert.True(context.TryReadUInt32(Table + 12, out var value));
        Assert.Equal(0xBEEFu, value);
    }

    [Fact]
    public void InvalidOutput_ReturnsAnErrorAndUnknownUcWritesRemainFatal()
    {
        var runner = new StreamRunner();
        var context = Context(runner, 0);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Manager().Dispatch(Nid, context));
        context[CpuRegister.Rdi] = ulong.MaxValue;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, Manager().Dispatch(Nid, context));
        runner.Host.WriteWords(Table, [0x191, 0]);
        Assert.Contains("user-config table register is not supported", runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.SetUserConfigRegisterIndirect, StreamRunner.Low(Table), StreamRunner.High(Table), 0x8000_0000, 1)).Message);
    }

    private static CpuContext Context(StreamRunner runner, uint budget)
    {
        var context = new CpuContext(runner.Host.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = Table;
        context[CpuRegister.Rdx] = budget;
        return context;
    }

    private static void WriteShader(StreamRunner runner, CpuContext context, uint vertexOutput, uint stages)
    {
        var shader = Table + 0x100;
        var registers = Table + 0x200;
        var specials = Table + 0x300;
        runner.Host.WriteQword(shader + 0x18, registers);
        runner.Host.WriteQword(shader + 0x28, specials);
        Assert.True(runner.Host.Memory.TryWrite(shader + 0x5B, new byte[] { 5 }));
        runner.Host.WriteWords(registers, [0x291, 1 << 11, 0x2D3, 32, 0x1B1, vertexOutput, 0x207, 0, 0x1FF, 32]);
        runner.Host.WriteWords(specials + 12, [stages]);
        context[CpuRegister.Rsi] = shader;
    }

    private static void AssertOutput(CpuContext context, uint pc, uint late)
    {
        var expected = new uint[] { 0x260, pc, 0x81, late };
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(context.TryReadUInt32(Table + (ulong)index * 4, out var actual));
            Assert.Equal(expected[index], actual);
        }
    }
}
