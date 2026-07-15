// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.CodeAnalysis;
using Xunit;

namespace SharpEmu.SourceGenerators.Tests;

public sealed class SysAbiExportAnalyzerTests
{
    private static IReadOnlyList<Diagnostic> Analyze(string source, params AdditionalText[] additionalFiles) =>
        RoslynTestHost.RunAnalyzer(RoslynTestHost.Compile(source), additionalFiles);

    private static void AssertSingle(IReadOnlyList<Diagnostic> diagnostics, string id)
    {
        Assert.Single(diagnostics);
        Assert.Equal(id, diagnostics[0].Id);
    }

    [Fact]
    public void CleanExportProducesNoDiagnostics()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "Zxa0VhQVTsk", ExportName = "sceKernelWaitSema", Target = Generation.Gen5)]
                public static int WaitSema(CpuContext ctx) => 0;
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DuplicateNidIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "Zxa0VhQVTsk", ExportName = "sceKernelWaitSema")]
                public static int First(CpuContext ctx) => 0;

                // Same NID via derivation: duplicates must be caught across both forms.
                [SysAbiExport(ExportName = "sceKernelWaitSema")]
                public static int Second(CpuContext ctx) => 0;
            }
            """);

        AssertSingle(diagnostics, "SHEM001");
    }

    [Fact]
    public void MalformedNidIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "not_a_nid", ExportName = "sceKernelWaitSema")]
                public static int WaitSema(CpuContext ctx) => 0;
            }
            """);

        AssertSingle(diagnostics, "SHEM002");
    }

    [Fact]
    public void WrongSignatureIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "Zxa0VhQVTsk", ExportName = "sceKernelWaitSema")]
                public static void WaitSema(CpuContext ctx) { }
            }
            """);

        AssertSingle(diagnostics, "SHEM003");
    }

    [Fact]
    public void NidContradictingExportNameIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                // This NID belongs to sceKernelSignalSema, not sceKernelWaitSema.
                [SysAbiExport(Nid = "4czppHBiriw", ExportName = "sceKernelWaitSema")]
                public static int WaitSema(CpuContext ctx) => 0;
            }
            """);

        AssertSingle(diagnostics, "SHEM004");
    }

    [Fact]
    public void ExportWithNeitherNidNorNameIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Target = Generation.Gen5)]
                public static int Mystery(CpuContext ctx) => 0;
            }
            """);

        AssertSingle(diagnostics, "SHEM005");
    }

    [Fact]
    public void NameOutsideTheCatalogWarnsOnlyWhenCatalogIsWired()
    {
        const string source = """
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(ExportName = "sceKernelWiatSema", Target = Generation.Gen5)]
                public static int Typo(CpuContext ctx) => 0;
            }
            """;

        var withoutCatalog = Analyze(source);
        Assert.Empty(withoutCatalog);

        var catalog = new InMemoryAdditionalText(
            "/repo/scripts/ps5_names.txt",
            "sceKernelWaitSema\nsceKernelSignalSema\n");
        var withCatalog = Analyze(source, catalog);
        AssertSingle(withCatalog, "SHEM006");
    }

    [Fact]
    public void PrivateHandlerIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "Zxa0VhQVTsk", ExportName = "sceKernelWaitSema")]
                private static int Hidden(CpuContext ctx) => 0;
            }
            """);

        AssertSingle(diagnostics, "SHEM007");
    }
}
