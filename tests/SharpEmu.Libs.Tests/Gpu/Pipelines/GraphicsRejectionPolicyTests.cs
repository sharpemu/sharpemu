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
public sealed class GraphicsRejectionPolicyTests
{
    private const ulong VertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong PixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong TableAddress = PipelineTestGuest.MemoryBase + 0x40000;

    [Fact]
    public void RejectedGraphicsDumpSavesCodeAndRegistersWithoutCompilation()
    {
        var variables = new[] { "SHARPEMU_DUMP_SPIRV", "SHARPEMU_SHADER_SPIRV_DUMP_DIR", "SHARPEMU_DUMP_SPIRV_ADDRESS" };
        var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "sharpemu-rejected-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "1");
            Environment.SetEnvironmentVariable(variables[1], directory);
            Environment.SetEnvironmentVariable(variables[2], null);
            var guest = new PipelineTestGuest();
            guest.RegisterProgram(VertexAddress, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram);
            var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
            var banks = new RegisterBanks(message => new InvalidOperationException(message));
            banks.Shader.Vertex.ExportAddress = VertexAddress;
            banks.Context.ShaderStages = 0x2000;
            cache.DumpRejectedGraphics(banks, false);
            cache.DumpRejectedGraphics(banks, false);
            var json = Assert.Single(Directory.GetFiles(directory, "*.json"));
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            Assert.Equal(0x2000u, document.RootElement.GetProperty("ShaderStages").GetUInt32());
            Assert.Equal(VertexAddress, document.RootElement.GetProperty("Vertex").GetProperty("ExportAddress").GetUInt64());
            Assert.Equal(PipelineTestGuest.EndProgram.Length * 4, File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.bin"))).Length);
            Assert.Empty(guest.Compiler.Requests);
        }
        finally
        {
            for (var index = 0; index < variables.Length; index++) Environment.SetEnvironmentVariable(variables[index], previous[index]);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("invalid", false)]
    [InlineData("invalid", true)]
    public void GraphicsCompilationUsesTheSameStrictSwitch(string? setting, bool pixelActive)
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE", setting);
            using var fatal = new FatalScope();
            var guest = new PipelineTestGuest();
            guest.RegisterProgram(VertexAddress, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram,
                userDataAddress: PipelineTestGuest.MemoryBase + 0x9000);
            guest.RegisterProgram(PixelAddress, PipelineTestGuest.MemoryBase + 0x8100, PipelineTestGuest.EndProgram,
                userDataAddress: PipelineTestGuest.MemoryBase + 0xA000);
            guest.Compiler.Rejection = "The test instruction is not supported.";
            var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
            GraphicsPrograms Lookup() => cache.GetGraphicsPrograms(
                new VertexStageRegisters { ExportAddress = VertexAddress }, new PixelStageRegisters { Address = PixelAddress },
                new ShaderInterfaceRegisters(), new ContextRegisters(), [], pixelActive, depthBound: false);
            if (setting != "0")
                Assert.Contains("cannot be compiled", Assert.Throws<SchedulerFatalException>(() => Lookup()).Message);
            else Assert.False(Lookup().Available);
            Assert.Empty(guest.Host.Modules);
            Assert.Empty(guest.Host.GraphicsPipelines);
            if (setting == "0")
            {
                guest.Compiler.Rejection = null;
                Assert.True(Lookup().Available);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE", previous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PixelCandidateValidationHonorsCapacityAndStrictMode(bool strict, bool distinctCandidates)
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(PixelAddress, PipelineTestGuest.MemoryBase + 0x8100,
            [0x7E240500, 0xBE931312, 0x8F6A8513, 0xB7EA0158, 0xF40C0100, 0xD4000000,
             0xF0000108, 0x00010400, 0xBF810000]);
        for (var index = 0; index < 33; index++)
            guest.WriteWords(TableAddress + 312 + (ulong)index * 32,
                distinctCandidates ? 0x1000u + (uint)index : 0x1000u, 20u << 20, 0,
                0xFACu | ((!distinctCandidates && index == 1 ? 10u : 9u) << 28), 0, 0, 0, 0);
        var source = guest.Source(PixelAddress, ShaderStage.Pixel, [(uint)(TableAddress & uint.MaxValue), (uint)(TableAddress >> 32)]);
        var options = new StageCompileOptions { PixelInfo = new PixelInputInfo() };
        var cursor = 7u;
        if (distinctCandidates)
        {
            Assert.True(guest.Programs.TryGetProgram(source, options, strict, ref cursor, out var program, out _, out var rejection), rejection);
            Assert.True(program.IsValid);
            Assert.Single(guest.Compiler.Requests);
            Assert.Single(guest.Host.Modules);
            return;
        }
        if (strict)
        {
            var failure = Assert.Throws<SchedulerFatalException>(() => guest.Programs.TryGetProgram(
                source, options, true, ref cursor, out _, out _, out _));
            Assert.Contains("could not be materialized", failure.Message);
        }
        else
        {
            Assert.False(guest.Programs.TryGetProgram(source, options, false, ref cursor, out var program, out _, out var rejection));
            Assert.False(program.IsValid);
            Assert.Contains("could not be materialized", rejection);
        }
        Assert.Equal(7u, cursor);
        Assert.Empty(guest.Compiler.Requests);
        Assert.Empty(guest.Host.Modules);
    }
}
