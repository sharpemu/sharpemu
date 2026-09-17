// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ComputeRejectionPolicyTests
{
    private const string Variable = "SHARPEMU_STRICT_COMPUTE";
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("invalid", false)]
    [InlineData("invalid", true)]
    public void ComputeRejectionUsesTheConfiguredPolicy(string? value, bool rejectPlan)
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, value);
            using var fatal = new FatalScope();
            var guest = new PipelineTestGuest();
            guest.RegisterProgram(CodeAddress, HeaderAddress, rejectPlan
                ? [0x7E100500, 0xF42C0402, 0x10000000, 0xF0000108, 0x00040000, 0xBF810000]
                : PipelineTestGuest.EndProgram);
            if (!rejectPlan) guest.Compiler.Rejection = "The test instruction is not supported.";
            var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
            var registers = Registers();
            ComputeProgram Lookup() => cache.GetComputeProgram(registers, new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);
            if (value != "0")
                Assert.Throws<SchedulerFatalException>(() => Lookup());
            else
            {
                var result = Lookup();
                Assert.False(result.Available);
                Assert.False(result.Consumed);
                Assert.False(result.Program.IsValid);
                Assert.False(Lookup().Available);
            }
            Assert.Empty(guest.Host.Modules);
            Assert.Empty(guest.Host.ComputePipelines);
            Assert.Equal(0, guest.Compiler.Compilations);
            if (!rejectPlan && value == "0")
            {
                guest.Compiler.Rejection = null;
                Assert.True(Lookup().Available);
                Assert.Single(guest.Host.Modules);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void FailedResourceReadsRemainFatalWithSkippingEnabled()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress,
            [0xF4080000, 0xFA000000, 0xE0000000, 0x80000000, 0xBF810000]);
        var source = guest.Source(CodeAddress, ShaderStage.Compute, new uint[2]);
        var cursor = 0u;
        var failure = Assert.Throws<SchedulerFatalException>(() => guest.Programs.TryGetProgram(
            source, PipelineTestGuest.ComputeOptions(1), false, ref cursor, out _, out _, out _));
        Assert.Contains("could not be materialized", failure.Message);
        Assert.Empty(guest.Compiler.Requests);
    }

    [Fact]
    public void UnexpectedCompilerErrorsAreNotConvertedIntoSkippedDispatches()
    {
        var guest = new PipelineTestGuest(_ => throw new InvalidOperationException("Unexpected compiler error."));
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var source = guest.Source(CodeAddress, ShaderStage.Compute, []);
        var cursor = 0u;
        Assert.Throws<InvalidOperationException>(() => guest.Programs.TryGetProgram(
            source, PipelineTestGuest.ComputeOptions(1), false, ref cursor, out _, out _, out _));
    }

    [Fact]
    public void DirectProgramLookupRemainsStrict()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        guest.Compiler.Rejection = "The test instruction is not supported.";
        var source = guest.Source(CodeAddress, ShaderStage.Pixel, []);
        var options = new StageCompileOptions { PixelInfo = new PixelInputInfo() };
        var cursor = 0u;
        Assert.Throws<SchedulerFatalException>(() => guest.Programs.GetOrCompile(source, options, ref cursor, out _));
        Assert.Throws<SchedulerFatalException>(() => guest.Programs.TryGetProgram(
            source, options, true, ref cursor, out _, out _, out _));
    }

    private static ComputeStageRegisters Registers() => new()
    {
        Address = CodeAddress, ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1, UserScalarCount = 8,
    };
}
