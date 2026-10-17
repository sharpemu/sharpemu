// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendLlePreferenceTests
{
    [Fact]
    public void ExplicitLlePreference_AllowsNonKernelRegisteredExport()
    {
        var export = Export("libSceNpCppWebApi", preferLle: true);

        Assert.True(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: false));
    }

    [Fact]
    public void ExplicitLlePreference_CannotOverrideKernelHleBoundary()
    {
        var export = Export("libKernel", preferLle: true);

        Assert.False(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: true));
    }

    [Fact]
    public void RegisteredExportWithoutLlePreference_RemainsHle()
    {
        var export = Export("libSceNpCppWebApi", preferLle: false);

        Assert.False(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: false));
    }

    [Fact]
    public void ExistingLibcPolicy_CanStillSelectRegisteredFirmwareExport()
    {
        var export = Export("libSceLibcInternal", preferLle: false);

        Assert.True(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: true));
    }

    private static ExportedFunction Export(string libraryName, bool preferLle) =>
        new(
            libraryName,
            "Zxa0VhQVTsk",
            "sceKernelWaitSema",
            Generation.Gen5,
            static _ => 0,
            preferLle);
}
