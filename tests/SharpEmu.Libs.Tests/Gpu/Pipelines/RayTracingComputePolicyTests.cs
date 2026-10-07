// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class RayTracingComputePolicyTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;

    [Theory]
    [InlineData(0xF1989F07u, null, false)]
    [InlineData(0xF19C9F07u, null, false)]
    [InlineData(0xF1989F07u, "1", false)]
    [InlineData(0xF19C9F07u, "1", false)]
    [InlineData(0xF1989F07u, "0", false)]
    [InlineData(0xF19C9F07u, "0", false)]
    [InlineData(0xF1989F07u, null, true)]
    [InlineData(0xF19C9F07u, null, true)]
    [InlineData(0xF1989F07u, "1", true)]
    [InlineData(0xF19C9F07u, "1", true)]
    [InlineData(0xF1989F07u, "0", true)]
    [InlineData(0xF19C9F07u, "0", true)]
    public void RayTracingComputeUsesTheConfiguredSkip(uint instruction, string? setting, bool strict)
    {
        var previousSkip = Environment.GetEnvironmentVariable("SHARPEMU_SKIP_RT");
        var previousStrict = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE");
        var previousError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SKIP_RT", setting);
            Environment.SetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE", strict ? "1" : "0");
            Console.SetError(output);
            using var fatal = new FatalScope();
            var guest = new PipelineTestGuest();
            guest.RegisterProgram(CodeAddress, HeaderAddress,
                [instruction, 0x00000707, 0x5A59580C, 0x605D5C5B, 0x000A0908, 0xBF810000]);
            var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = cache.GetComputeProgram(Registers(), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);
                Assert.Equal(setting != "1", result.Available);
                Assert.False(result.Consumed);
                Assert.Equal(setting != "1", result.Program.IsValid);
            }
            if (setting != "1")
            {
                Assert.Single(guest.Compiler.Requests);
                Assert.Single(guest.Host.Modules);
                Assert.DoesNotContain("RAY_TRACING_SKIPPED", output.ToString());
            }
            else
            {
                Assert.Empty(guest.Host.Modules);
                Assert.Empty(guest.Compiler.Requests);
                Assert.Single(output.ToString().Split('\n'), line => line.Contains("RAY_TRACING_SKIPPED", StringComparison.Ordinal));
            }
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("SHARPEMU_SKIP_RT", previousSkip);
            Environment.SetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE", previousStrict);
        }
    }

    [Fact]
    public void OrdinaryComputeRemainsAvailable()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        Assert.True(cache.GetComputeProgram(Registers(), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1).Available);
        Assert.Single(guest.Host.Modules);
    }

    private static ComputeStageRegisters Registers() => new()
    {
        Address = CodeAddress, ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1, UserScalarCount = 8,
    };
}
