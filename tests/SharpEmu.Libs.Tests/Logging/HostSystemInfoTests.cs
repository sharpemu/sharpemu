// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Libs.Tests.Logging;

public sealed class HostSystemInfoTests
{
    [Fact]
    public void MemoryDescriptionIsResolvedOnEverySupportedHost()
    {
        // GC.GetGCMemoryInfo reports total memory on all supported platforms, so
        // "unknown" here means the query regressed rather than that the host is exotic.
        Assert.Matches(@"^[\d,]+ MB \([\d.]+ GB\)$", GetMemoryField(HostSystemInfo.Summary));
    }

    [Fact]
    public void CpuNameIsNeverBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(HostSystemInfo.CpuName));
    }

    [Fact]
    public void SummaryReportsEveryField()
    {
        var summary = HostSystemInfo.Summary;
        Assert.StartsWith("Host hardware: CPU: ", summary);
        Assert.Contains("; GPU: ", summary);
        Assert.Contains("; RAM: ", summary);
        Assert.EndsWith(".", summary);
    }

    [Fact]
    public void MacOsResolvesTheCpuBrandRatherThanTheCoreCountFallback()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // The bare fallback carries no model name; sysctl must supply one.
        Assert.DoesNotMatch(@"^\d+ logical processors$", HostSystemInfo.CpuName);
        Assert.Contains("logical processors", HostSystemInfo.CpuName);
    }

    [Fact]
    public void MacOsResolvesTheGpuName()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.NotEqual("unknown", HostSystemInfo.GpuName);
        Assert.False(string.IsNullOrWhiteSpace(HostSystemInfo.GpuName));
    }

    [Fact]
    public void GpuNameIsStableAcrossCalls()
    {
        // The Metal device is retained and released on every miss; a leak or a
        // double-release would surface as a differing or empty second answer.
        Assert.Equal(HostSystemInfo.GpuName, HostSystemInfo.GpuName);
    }

    private static string GetMemoryField(string summary)
    {
        const string Marker = "; RAM: ";
        var start = summary.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        return summary[start..].TrimEnd('.');
    }
}
