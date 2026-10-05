// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPlanningDumpTests
{
    [Theory]
    [InlineData("0", true, false, false)]
    [InlineData("1", false, false, false)]
    [InlineData("1", true, true, false)]
    [InlineData("1", true, false, true)]
    public void RejectedPlanPreservesDiagnosticsOnlyWhenTheGateAndAddressMatch(string enabled, bool addressMatches, bool expectFiles, bool blockedDirectory)
    {
        var directory = Directory.CreateTempSubdirectory("shader-planning-test-").FullName;
        var names = new[] { "SHARPEMU_DUMP_SPIRV", "SHARPEMU_DUMP_SPIRV_ADDRESS", "SHARPEMU_SHADER_SPIRV_DUMP_DIR" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            const ulong codeAddress = PipelineTestGuest.MemoryBase + 0x1000;
            Environment.SetEnvironmentVariable(names[0], enabled);
            Environment.SetEnvironmentVariable(names[1], $"0x{(addressMatches ? codeAddress : codeAddress + 4):X}");
            var outputDirectory = Path.Combine(directory, "output");
            if (blockedDirectory) File.WriteAllText(outputDirectory, "This file prevents directory creation.");
            Environment.SetEnvironmentVariable(names[2], outputDirectory);
            using var fatalScope = new FatalScope();
            var guest = new PipelineTestGuest();
            // A lane-derived offset reads an image descriptor without a material-table selector.
            guest.RegisterProgram(codeAddress, PipelineTestGuest.MemoryBase + 0x8000,
                [0x7E100500, 0xF42C0402, 0x10000000, 0xF0000108, 0x00040000, 0xBF810000]);
            var source = guest.Source(codeAddress, ShaderStage.Compute, new uint[8]);
            var cursor = 0u;
            var failure = Assert.Throws<SchedulerFatalException>(() =>
                guest.Programs.GetOrCompile(source, PipelineTestGuest.ComputeOptions(1), ref cursor, out _));
            Assert.Contains("ImageHandle dword 0 is not a valid runtime value", failure.Message);
            Assert.Empty(guest.Compiler.Requests);

            var files = Directory.Exists(outputDirectory) ? Directory.GetFiles(outputDirectory) : [];
            Assert.Equal(expectFiles ? 3 : 0, files.Length);
            if (expectFiles)
            {
                var input = File.ReadAllText(Assert.Single(files, path => path.EndsWith(".input.ir.txt", StringComparison.Ordinal)));
                Assert.Contains("F42C0402_10000000", input);
                Assert.Contains("user_data_base=0 user_data_count=8", input);
                var graph = File.ReadAllText(Assert.Single(files, path => path.EndsWith(".resource-graph.txt", StringComparison.Ordinal)));
                Assert.Contains("kind=ImageHandle", graph);
                Assert.Contains("kind=ScalarBufferWord", graph);
                Assert.Contains("pending=0", graph);
                Assert.Contains("ImageHandle dword 0", File.ReadAllText(Assert.Single(files, path => path.EndsWith(".failure.txt", StringComparison.Ordinal))));
            }
        }
        finally
        {
            for (var index = 0; index < names.Length; index++) Environment.SetEnvironmentVariable(names[index], previous[index]);
            Directory.Delete(directory, true);
        }
    }
}
