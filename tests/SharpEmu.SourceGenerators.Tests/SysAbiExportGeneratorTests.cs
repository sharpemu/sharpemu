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

            // Typed handler shape: the generator emits the SysV register thunk.
            [SysAbiExport(Nid = "12wOHk8ywb0", ExportName = "sceKernelPollSema")]
            public static int PollSema(CpuContext ctx, uint handle, int needCount) => 0;

            // All four integer kinds across all six argument registers.
            [SysAbiExport(Nid = "4DM06U2BNEY", ExportName = "sceKernelCancelSema")]
            public static int CancelSema(CpuContext ctx, uint a, int b, ulong c, long d, uint e, int f) => 0;

            // Guest string marshalling: the thunk reads the pointer before the handler.
            [SysAbiExport(Nid = "1G3lF1Gg1k8", ExportName = "sceKernelOpen")]
            public static int KernelOpen(CpuContext ctx, [GuestCString(4096)] string path, int flags) => 0;
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
    public void TypedHandlersGetSysVRegisterThunks()
    {
        var (_, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(HandlerSource));

        // Parameters map positionally to RDI/RSI/... with the same unchecked-cast idiom
        // hand-written handlers use; ulong reads the register raw.
        Assert.Contains(
            "static ctx => global::TestExports.SampleExports.PollSema(ctx, " +
            "unchecked((uint)ctx[global::SharpEmu.HLE.CpuRegister.Rdi]), " +
            "unchecked((int)ctx[global::SharpEmu.HLE.CpuRegister.Rsi]))",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "static ctx => global::TestExports.SampleExports.CancelSema(ctx, " +
            "unchecked((uint)ctx[global::SharpEmu.HLE.CpuRegister.Rdi]), " +
            "unchecked((int)ctx[global::SharpEmu.HLE.CpuRegister.Rsi]), " +
            "ctx[global::SharpEmu.HLE.CpuRegister.Rdx], " +
            "unchecked((long)ctx[global::SharpEmu.HLE.CpuRegister.Rcx]), " +
            "unchecked((uint)ctx[global::SharpEmu.HLE.CpuRegister.R8]), " +
            "unchecked((int)ctx[global::SharpEmu.HLE.CpuRegister.R9]))",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuestCStringParametersAreMarshalledWithAFaultPath()
    {
        var (_, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(HandlerSource));

        // The string is read from the pointer in RDI before the handler runs, and a
        // failed read returns MEMORY_FAULT to the guest without invoking the handler.
        Assert.Contains(
            "if (!ctx.TryReadNullTerminatedUtf8(ctx[global::SharpEmu.HLE.CpuRegister.Rdi], 4096, out var guestString0))",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "return ctx.SetReturn(global::SharpEmu.HLE.OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "return global::TestExports.SampleExports.KernelOpen(ctx, guestString0, " +
            "unchecked((int)ctx[global::SharpEmu.HLE.CpuRegister.Rsi]));",
            generated,
            StringComparison.Ordinal);
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
    public void AssemblyWithoutExportsEmitsNoRegistry()
    {
        // Referencing the analyzer must not mint a colliding
        // SharpEmu.Generated.SysAbiExportRegistry type in export-free assemblies.
        const string noExports = """
            public static class PlainCode
            {
                public static int Nothing() => 0;
            }
            """;
        var (_, generated) = RoslynTestHost.RunGenerator(RoslynTestHost.Compile(noExports));

        Assert.Equal(string.Empty, generated);
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
