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
    public void TypedHandlerShapeIsAccepted()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "12wOHk8ywb0", ExportName = "sceKernelPollSema")]
                public static int PollSema(CpuContext ctx, uint handle, int needCount) => 0;

                [SysAbiExport(Nid = "4DM06U2BNEY", ExportName = "sceKernelCancelSema")]
                public static int CancelSema(CpuContext ctx, uint a, int b, ulong c, long d, uint e, int f) => 0;
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TypedHandlerBeyondRegisterArgsOrWithUnsupportedTypeIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                // Seven args exceed the six SysV integer registers.
                [SysAbiExport(Nid = "12wOHk8ywb0", ExportName = "sceKernelPollSema")]
                public static int TooMany(CpuContext ctx, uint a, int b, ulong c, long d, uint e, int f, int g) => 0;

                // string is not register-representable (that is phase 3's marshalling).
                [SysAbiExport(Nid = "4czppHBiriw", ExportName = "sceKernelSignalSema")]
                public static int WrongKind(CpuContext ctx, string name) => 0;
            }
            """);

        // Order-insensitive: analyzers run concurrently and diagnostic order is unstable.
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("SHEM003", diagnostic.Id));
    }

    [Fact]
    public void GuestCStringParameterIsAccepted()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                [SysAbiExport(Nid = "1G3lF1Gg1k8", ExportName = "sceKernelOpen")]
                public static int KernelOpen(CpuContext ctx, [GuestCString(4096)] string path, int flags) => 0;
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MisusedGuestCStringIsReported()
    {
        var diagnostics = Analyze("""
            using SharpEmu.HLE;

            public static class Exports
            {
                // On a non-string parameter the attribute is meaningless.
                [SysAbiExport(Nid = "1G3lF1Gg1k8", ExportName = "sceKernelOpen")]
                public static int OnInt(CpuContext ctx, [GuestCString(4096)] int flags) => 0;

                // A read bounded at zero bytes can never succeed.
                [SysAbiExport(Nid = "6c3rCVE-fTU", ExportName = "_open")]
                public static int ZeroLength(CpuContext ctx, [GuestCString(0)] string path) => 0;
            }
            """);

        // Order-insensitive: analyzers run concurrently and diagnostic order is unstable.
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("SHEM008", diagnostic.Id));
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
