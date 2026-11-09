// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPermutationDumpTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private readonly string _directory = Directory.CreateTempSubdirectory("shader-permutation-test-").FullName;
    private readonly string[] _names = ["SHARPEMU_DUMP_SPIRV", "SHARPEMU_DUMP_SPIRV_ADDRESS", "SHARPEMU_SHADER_SPIRV_DUMP_DIR"];
    private readonly string?[] _previous;
    private readonly FatalScope _fatal = new();

    public ShaderPermutationDumpTests()
    {
        _previous = _names.Select(Environment.GetEnvironmentVariable).ToArray();
        Environment.SetEnvironmentVariable(_names[0], "1");
        Environment.SetEnvironmentVariable(_names[1], $"0x{CodeAddress:X}");
        Environment.SetEnvironmentVariable(_names[2], _directory);
    }

    public void Dispose()
    {
        for (var index = 0; index < _names.Length; index++) Environment.SetEnvironmentVariable(_names[index], _previous[index]);
        _fatal.Dispose();
        Directory.Delete(_directory, true);
    }

    private string[] InputFiles() => Directory.GetFiles(_directory, "*.variant-*.json").Order().ToArray();

    [Theory]
    [InlineData("0", true, false)]
    [InlineData("1", false, false)]
    [InlineData("1", true, false)]
    [InlineData("1", true, true)]
    public void ResourceFailureCaptureUsesTheDumpGateAndKeepsSeparateFiles(string enabled, bool matchingAddress, bool blockedDirectory)
    {
        Environment.SetEnvironmentVariable(_names[0], enabled);
        Environment.SetEnvironmentVariable(_names[1], $"0x{(matchingAddress ? CodeAddress : CodeAddress + 4):X}");
        if (blockedDirectory)
        {
            var blockedPath = Path.Combine(_directory, "blocked");
            File.WriteAllText(blockedPath, "Directory creation is blocked by this file.");
            Environment.SetEnvironmentVariable(_names[2], blockedPath);
        }
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var source = guest.Source(CodeAddress, ShaderStage.Compute, []);
        var failure = new IndirectImageFailure(0x159C, 10, 10, 13, 224, 4,
            [1, 2, 3, 4], [5, 6, 7, 8], [0, 17], [0, 1], [[1], [2]], [[1], [2]], [], [9])
        {
            SelectorDiagnostic = new IndirectSelectorDiagnostic(),
            ScalarReads = [new MaterializedScalarRead(0x22C, 1, 64, 1)],
        };
        failure.SelectorDiagnostic.KeyProbes.Add(new(36, 17));
        var capture = ShaderPermutationDump.CreateFailureCapture(source);
        Assert.Equal(enabled == "1" && matchingAddress, capture is not null);
        capture?.Invoke(failure);
        capture?.Invoke(failure);
        var files = Directory.GetFiles(_directory, "*.resource-failure-*.json");
        if (enabled != "1" || !matchingAddress || blockedDirectory)
        {
            Assert.Empty(files);
            return;
        }
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal("resource_materialization_failed", document.RootElement.GetProperty("Phase").GetString());
            var captured = document.RootElement.GetProperty("Failure");
            Assert.Equal(0x159Cu, captured.GetProperty("InstructionAddress").GetUInt32());
            Assert.Equal(17u, captured.GetProperty("Keys")[1].GetUInt32());
            Assert.Equal(2u, captured.GetProperty("TableDescriptors")[1][0].GetUInt32());
            var diagnostic = captured.GetProperty("SelectorDiagnostic");
            Assert.Equal("full_domain_no_proof", diagnostic.GetProperty("SelectionMode").GetString());
            Assert.Equal(36u, diagnostic.GetProperty("KeyProbes")[0].GetProperty("Offset").GetUInt32());
            Assert.Equal(1u, captured.GetProperty("ScalarReads")[0].GetProperty("ComponentIndex").GetUInt32());
            Assert.Equal(1u, captured.GetProperty("ScalarReads")[0].GetProperty("Value").GetUInt32());
        }
    }

    private static ulong Compile(PipelineTestGuest guest, uint[] userData, StageCompileOptions options, uint cursor = 0) =>
        guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Compute, userData), options, ref cursor, out _).Id;

    private static StageCompileOptions ThreadCountOptions(uint count, uint localSize = 64) => new()
    {
        ComputeInfo = new ComputeInputInfo
        {
            ThreadsX = localSize, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32,
            GroupIdX = true, ThreadIdCount = 1, DispatchThreadDimensions = true,
            DispatchThreadsX = count, DispatchThreadsY = 1, DispatchThreadsZ = 1,
        },
    };

    [Fact]
    public void StaticChangesAreSavedBeforeTranslationAndCacheHitsWriteNothing()
    {
        var translations = 0;
        var guest = new PipelineTestGuest(_ =>
        {
            Assert.Equal(++translations, InputFiles().Length);
            return [];
        });
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var first = Compile(guest, [], ThreadCountOptions(100));
        Assert.Equal(first, Compile(guest, [], ThreadCountOptions(100)));
        var second = Compile(guest, [], ThreadCountOptions(200, localSize: 32));
        Assert.NotEqual(first, second);
        var files = InputFiles();
        Assert.Equal(2, files.Length);
        using var firstInput = JsonDocument.Parse(File.ReadAllText(files[0]));
        using var secondInput = JsonDocument.Parse(File.ReadAllText(files[1]));
        Assert.Equal("new_source", firstInput.RootElement.GetProperty("MissReason").GetString());
        Assert.Equal("source_key_changed", secondInput.RootElement.GetProperty("MissReason").GetString());
        Assert.Equal(64u, firstInput.RootElement.GetProperty("LocalSizeX").GetUInt32());
        Assert.Equal(32u, secondInput.RootElement.GetProperty("LocalSizeX").GetUInt32());
        Assert.Single(secondInput.RootElement.GetProperty("PreviousSourceKeys").EnumerateArray());
        Assert.Equal(second, secondInput.RootElement.GetProperty("ProgramId").GetUInt64());

        var firstBase = files[0][..^5];
        var secondBase = files[1][..^5];
        ShaderPermutationDump.WriteModule(firstBase, [1, 2, 3, 4], "spv");
        ShaderPermutationDump.WriteModule(secondBase, [5, 6, 7, 8], "spv");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(firstBase + ".spv"));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(secondBase + ".spv"));
    }

    [Fact]
    public void ResourceAndPushDataMismatchesRemainDistinct()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var integerDescriptor = PipelineTestGuest.BufferDescriptor(PipelineTestGuest.MemoryBase + 0x40000, 16, 4, 75);
        var floatDescriptor = PipelineTestGuest.BufferDescriptor(PipelineTestGuest.MemoryBase + 0x40000, 16, 4, 77);
        Compile(guest, integerDescriptor, PipelineTestGuest.ComputeOptions());
        Compile(guest, floatDescriptor, PipelineTestGuest.ComputeOptions());
        Compile(guest, floatDescriptor, PipelineTestGuest.ComputeOptions(), cursor: 3);

        var files = InputFiles();
        Assert.Equal(3, files.Length);
        using var resourceInput = JsonDocument.Parse(File.ReadAllText(files[1]));
        var resource = resourceInput.RootElement;
        Assert.True(resource.GetProperty("SourceWasCached").GetBoolean());
        Assert.Equal("permutation_mismatch", resource.GetProperty("MissReason").GetString());
        var candidate = Assert.Single(resource.GetProperty("Candidates").EnumerateArray());
        Assert.False(candidate.GetProperty("SpecializationMatches").GetBoolean());
        Assert.True(candidate.GetProperty("PushDataMatches").GetBoolean());
        Assert.Equal(77u, resource.GetProperty("Specialization").GetProperty("Buffers")[0].GetProperty("DescriptorFormat").GetUInt32());

        using var pushInput = JsonDocument.Parse(File.ReadAllText(files[2]));
        var matchingResources = Assert.Single(pushInput.RootElement.GetProperty("Candidates").EnumerateArray(),
            value => value.GetProperty("SpecializationMatches").GetBoolean());
        Assert.False(matchingResources.GetProperty("PushDataMatches").GetBoolean());
        Assert.Equal(3u, pushInput.RootElement.GetProperty("PushDataCursor").GetUInt32());
    }

    [Fact]
    public void TranslationFailurePreservesInputsWithoutCreatingAModule()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        guest.Compiler.Rejection = "test shader rejection";

        var failure = Assert.Throws<SchedulerFatalException>(() =>
            Compile(guest, [], PipelineTestGuest.ComputeOptions()));

        Assert.Contains("test shader rejection", failure.Message);
        Assert.Single(guest.Compiler.Requests);
        Assert.Empty(guest.Host.Modules);
        var inputPath = Assert.Single(InputFiles());
        using var document = JsonDocument.Parse(File.ReadAllText(inputPath));
        Assert.Equal("before_translation", document.RootElement.GetProperty("Phase").GetString());
        Assert.Single(Directory.GetFiles(_directory, "*.variant-*"));
    }

    [Theory]
    [InlineData("0", true, false)]
    [InlineData("1", false, false)]
    [InlineData("1", true, true)]
    public void DisabledFilteredOrUnwritableDumpsDoNotChangeCompilation(string enabled, bool matchingAddress, bool blockedDirectory)
    {
        Environment.SetEnvironmentVariable(_names[0], enabled);
        Environment.SetEnvironmentVariable(_names[1], $"0x{(matchingAddress ? CodeAddress : CodeAddress + 4):X}");
        if (blockedDirectory)
        {
            var blockedPath = Path.Combine(_directory, "blocked");
            File.WriteAllText(blockedPath, "Directory creation is blocked by this file.");
            Environment.SetEnvironmentVariable(_names[2], blockedPath);
            ShaderPermutationDump.WriteModule(Path.Combine(blockedPath, "variant"), [1], "spv");
        }

        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        Assert.Equal(1ul, Compile(guest, [], PipelineTestGuest.ComputeOptions()));
        Assert.Single(guest.Compiler.Requests);
        Assert.Single(guest.Host.Modules);
        Assert.Empty(InputFiles());
    }
}
