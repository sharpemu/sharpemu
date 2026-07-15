// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.SourceGenerators.Tests;

public sealed class SysAbiExportGeneratorTests
{
    private const string HandlerSource = """
        using SharpEmu.HLE;

        namespace TestExports;

        public static class SampleExports
        {
            [SysAbiExport(Nid = "Zxa0VhQVTsk", ExportName = "sceKernelWaitSema", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
            public static int WaitSema(CpuContext ctx) => 0;

            // NID omitted on purpose: the generator must derive it from the name.
            [SysAbiExport(ExportName = "sceKernelSignalSema", Target = Generation.Gen5)]
            public static int SignalSema(CpuContext ctx) => 0;

            // Parameterless handler shape: must be wrapped to the SysAbiFunction contract.
            [SysAbiExport(Nid = "ekNvsT22rsY", ExportName = "sceAudioOutOpen")]
            public static int Open() => 0;
        }
        """;

    [Fact]
    public void GeneratedRegistryCompilesAgainstRealHleTypes()
    {
        var compilation = RoslynTestHost.Compile(HandlerSource);
        var (updated, generated) = RoslynTestHost.RunGenerator(compilation);

        Assert.NotEqual(string.Empty, generated);
        RoslynTestHost.AssertCompiles(updated);
    }

    [Fact]
    public void RegistryContainsDeclaredDerivedAndWrappedExports()
    {
        var (_, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(HandlerSource));

        // Declared NID passes through verbatim.
        Assert.Contains("\"Zxa0VhQVTsk\"", generated, StringComparison.Ordinal);
        Assert.Contains("global::TestExports.SampleExports.WaitSema", generated, StringComparison.Ordinal);

        // Omitted NID is derived from the export name at compile time.
        Assert.Contains("\"4czppHBiriw\"", generated, StringComparison.Ordinal);

        // Parameterless handlers are adapted to the SysAbiFunction shape.
        Assert.Contains("static _ => global::TestExports.SampleExports.Open()", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationFilteringMatchesTheReflectionScanSemantics()
    {
        var (_, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(HandlerSource));

        // The Add helper reproduces ResolveExportInfo: None inherits the registration
        // generation, and exports outside the registration generation are skipped.
        Assert.Contains("attributeTarget == global::SharpEmu.HLE.Generation.None ? registrationGeneration : attributeTarget", generated, StringComparison.Ordinal);
        Assert.Contains("(target & registrationGeneration) == 0", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidHandlersAreSkippedNotEmitted()
    {
        const string invalid = """
            using SharpEmu.HLE;

            namespace TestExports;

            public static class BrokenExports
            {
                [SysAbiExport(ExportName = "sceKernelUsleep")]
                public static long WrongReturn(CpuContext ctx) => 0;

                [SysAbiExport(ExportName = "sceKernelGettimeofday")]
                private static int Inaccessible(CpuContext ctx) => 0;
            }
            """;
        var (updated, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(invalid));

        Assert.DoesNotContain("WrongReturn", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("Inaccessible", generated, StringComparison.Ordinal);
        RoslynTestHost.AssertCompiles(updated);
    }
}
