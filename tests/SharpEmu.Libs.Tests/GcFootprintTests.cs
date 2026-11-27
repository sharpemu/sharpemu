// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GcFootprintTests
{
    [Theory]
    [InlineData(16, 8, true)]
    [InlineData(32, 2, true)]
    [InlineData(32, 0, true)]
    [InlineData(32, 3, false)]
    [InlineData(-1, -1, false)]
    public void UsesPhysicalCapacityOrAvailableMemory(int totalGiB, int availableGiB, bool expected)
    {
        Assert.Equal(expected, GcFootprint.Constrained(new HostMemoryAvailability(64L << 30,
            (long)availableGiB << 30, (long)totalGiB << 30)));
    }

    [Theory]
    [InlineData(false, "10000000")]
    [InlineData(true, "4000000")]
    public void SelectsOnlyMissingChildDefaults(bool compact, string gen0)
    {
        var env = new Dictionary<string, string>();
        var selected = GcFootprint.MissingDefaults(compact, env.GetValueOrDefault).ToDictionary();
        Assert.Equal(gen0, selected["DOTNET_GCgen0size"]);
        Assert.Equal(compact ? "5" : null, selected.GetValueOrDefault("DOTNET_GCConserveMemory"));
        env["DOTNET_GCgen0size"] = "2000000";
        env["COMPlus_GCConserveMemory"] = "1";
        selected = GcFootprint.MissingDefaults(compact, env.GetValueOrDefault).ToDictionary();
        Assert.DoesNotContain("DOTNET_GCgen0size", selected.Keys);
        Assert.DoesNotContain("DOTNET_GCConserveMemory", selected.Keys);
        Assert.Equal("8000000", selected["DOTNET_GCGen1MaxBudget"]);
        env["COMPlus_GCGen1MaxBudget"] = "100000";
        Assert.DoesNotContain(GcFootprint.MissingDefaults(compact, env.GetValueOrDefault),
            pair => pair.Key == "DOTNET_GCGen1MaxBudget");
    }
}
