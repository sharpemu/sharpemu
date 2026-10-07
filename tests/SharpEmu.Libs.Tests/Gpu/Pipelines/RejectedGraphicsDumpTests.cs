// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class RejectedGraphicsDumpTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private readonly string _directory = Directory.CreateTempSubdirectory("rejected-graphics-test-").FullName;
    private readonly string[] _names = ["SHARPEMU_DUMP_SPIRV", "SHARPEMU_DUMP_SPIRV_ADDRESS",
        "SHARPEMU_DUMP_SPIRV_HASH", "SHARPEMU_SHADER_SPIRV_DUMP_DIR"];
    private readonly string?[] _previous;
    private readonly FatalScope _fatal = new();

    public RejectedGraphicsDumpTests()
    {
        _previous = _names.Select(Environment.GetEnvironmentVariable).ToArray();
        Environment.SetEnvironmentVariable(_names[0], "1");
        Environment.SetEnvironmentVariable(_names[1], null);
        Environment.SetEnvironmentVariable(_names[2], null);
        Environment.SetEnvironmentVariable(_names[3], _directory);
    }

    public void Dispose()
    {
        for (var index = 0; index < _names.Length; index++)
            Environment.SetEnvironmentVariable(_names[index], _previous[index]);
        _fatal.Dispose();
        Directory.Delete(_directory, true);
    }

    private static ShaderPipelineCache Cache(PipelineTestGuest guest) =>
        new(guest.Context, guest.Host, guest.Compiler, guest.Registry);

    private static RegisterBanks RejectedBanks()
    {
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = CodeAddress;
        banks.Context.ShaderStages = 0x1;
        return banks;
    }

    [Theory]
    [InlineData("0", null, null, false)]
    [InlineData("1", "100001004", null, false)]
    [InlineData("1", "invalid", null, false)]
    [InlineData("1", null, "1234", false)]
    [InlineData("1", "100001000", null, true)]
    public void DumpGateControlsCodeAndRegisterFiles(string enabled, string? address, string? hash, bool expected)
    {
        Environment.SetEnvironmentVariable(_names[0], enabled);
        Environment.SetEnvironmentVariable(_names[1], address);
        Environment.SetEnvironmentVariable(_names[2], hash);
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        Cache(guest).DumpRejectedGraphics(RejectedBanks(), true);
        Assert.Equal(expected ? 1 : 0, Directory.GetFiles(_directory, "*.json").Length);
        Assert.Equal(expected ? 1 : 0, Directory.GetFiles(_directory, "*.bin").Length);
        Assert.Empty(guest.Compiler.Requests);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void DrawRejectionCapturesOnlyNonemptyDrawsWithShader(bool indexed, bool zeroCount, bool missingShader)
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var host = new RecordingRenderHost();
        var executor = new RenderExecutor(host, Cache(guest));
        var banks = RejectedBanks();
        if (missingShader) banks.Shader.Vertex.ExportAddress = 0;
        var count = zeroCount ? 0u : 3u;
        if (indexed) executor.DrawIndexed(1, banks, Indexed(count));
        else executor.DrawAuto(1, banks, Auto(count));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("draw ", StringComparison.Ordinal) ||
            call.StartsWith("draw_indexed ", StringComparison.Ordinal));
        Assert.Empty(guest.Compiler.Requests);
        var files = Directory.GetFiles(_directory, "*.json");
        if (zeroCount || missingShader)
        {
            Assert.Empty(files);
            return;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(Assert.Single(files)));
        Assert.Equal(!indexed, document.RootElement.GetProperty("AutoDraw").GetBoolean());
        Assert.Equal(0x1u, document.RootElement.GetProperty("ShaderStages").GetUInt32());
        Assert.Equal(new byte[] { 0, 0, 0x81, 0xBF },
            File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.bin"))));
    }

    [Fact]
    public void DumpLimitBoundsDistinctKeysAndKeepsDrawModesSeparate()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var cache = Cache(guest);
        var banks = RejectedBanks();
        cache.DumpRejectedGraphics(banks, false);
        cache.DumpRejectedGraphics(banks, false);
        cache.DumpRejectedGraphics(banks, true);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.json").Length);
        for (var stage = 1u; stage <= 70; stage++)
        {
            banks.Context.ShaderStages = stage;
            cache.DumpRejectedGraphics(banks, false);
        }
        Assert.Equal(64, Directory.GetFiles(_directory, "*.json").Length);
        Assert.Equal(64, Directory.GetFiles(_directory, "*.bin").Length);
        Assert.Empty(guest.Compiler.Requests);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x1000001u)]
    [InlineData(0x100000u)]
    public void UnreadableOrInvalidCodeDoesNotEscapeTheDiagnostic(uint size)
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        guest.WriteWords(HeaderAddress + 0x44, size);
        Cache(guest).DumpRejectedGraphics(RejectedBanks(), false);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Empty(guest.Compiler.Requests);
    }

    [Fact]
    public void DiagnosticReaderPreservesBothFusedCodeRanges()
    {
        var guest = new PipelineTestGuest();
        var continuation = CodeAddress + 0x100;
        var continuationHeader = HeaderAddress + 0x100;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        guest.RegisterProgram(continuation, continuationHeader, [0xBF800000, 0xBF810000]);
        var registry = new ShaderHeaderRegistry(guest.Context,
            address => address == CodeAddress ? HeaderAddress : 0,
            address => address == CodeAddress ? new FusedProgramParts(continuation, continuationHeader) : null);
        var ranges = registry.ReadDiagnosticCode(CodeAddress);
        Assert.Equal(new[] { CodeAddress, continuation }, ranges.Select(range => range.Address));
        Assert.Equal(new byte[] { 0, 0, 0x81, 0xBF }, ranges[0].Code);
        Assert.Equal(new byte[] { 0, 0, 0x80, 0xBF, 0, 0, 0x81, 0xBF }, ranges[1].Code);
        Assert.Empty(guest.Compiler.Requests);
    }
}
