// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// The import profile sits on the path every guest HLE call takes, so what it
// must never do is matter when it is off, and never throw when it is on.
public sealed class ImportProfileTests
{
    [Fact]
    public void Disabled_BeginReturnsZeroSoEndIsADroppedCall()
    {
        if (ImportProfile.Enabled)
        {
            // The suite runs without SHARPEMU_PROFILE_IMPORTS; if an outer
            // harness set it, the disabled contract is not what is under test.
            return;
        }

        Assert.Equal(0, ImportProfile.Begin());

        // End on a zero timestamp is the disabled path and must be inert, even
        // for an import index no table ever had.
        ImportProfile.End(importIndex: 12345, started: 0);
        ImportProfile.MarkBodyStart();
        ImportProfile.MarkBodyEnd();
    }

    [Fact]
    public void Configure_IsIgnoredWhileDisabled()
    {
        if (ImportProfile.Enabled)
        {
            return;
        }

        ImportProfile.Configure(["libKernel:scePthreadMutexLock"]);

        // Nothing was recorded, so the report has no per-import rows to show.
        var report = ImportProfile.FormatReport(TimeSpan.FromSeconds(1), topCount: 10);
        Assert.DoesNotContain("scePthreadMutexLock", report);
    }

    [Fact]
    public void FormatReport_SurvivesAnEmptyTableAndReportsTheHeader()
    {
        var report = ImportProfile.FormatReport(TimeSpan.FromSeconds(2.5), topCount: 5);

        Assert.StartsWith("[IMPORTS] t=2.5s", report);
        Assert.Contains("transition_share=", report);
    }

    [Fact]
    public void FormatReport_ToleratesANonPositiveTopCount()
    {
        var report = ImportProfile.FormatReport(TimeSpan.Zero, topCount: 0);

        Assert.Contains("[IMPORTS] t=0.0s", report);
    }
}
