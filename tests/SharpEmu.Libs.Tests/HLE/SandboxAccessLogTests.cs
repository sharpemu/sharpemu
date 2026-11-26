// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

// The log is process-wide state, so these tests must not run beside each other.
[Collection("SandboxAccessLog")]
public sealed class SandboxAccessLogTests : IDisposable
{
    public SandboxAccessLogTests()
    {
        SandboxAccessLog.ResetForTests();
    }

    public void Dispose()
    {
        SandboxAccessLog.SandboxOverride = null;
        SandboxAccessLog.ResetForTests();
    }

    [Fact]
    public void ReportBlocked_OutsideSandbox_DoesNothing()
    {
        SandboxAccessLog.SandboxOverride = false;
        SandboxAccessLog.ReportBlocked(@"C:\blocked\a.bin");
        Assert.Equal(0, SandboxAccessLog.LoggedCount);
    }

    [Fact]
    public void ReportBlocked_LogsEachPathOnce_IgnoringCase()
    {
        SandboxAccessLog.SandboxOverride = true;
        SandboxAccessLog.ReportBlocked(@"C:\blocked\a.bin");
        SandboxAccessLog.ReportBlocked(@"C:\BLOCKED\a.bin");
        SandboxAccessLog.ReportBlocked(@"C:\blocked\b.bin");
        Assert.Equal(2, SandboxAccessLog.LoggedCount);
    }

    [Fact]
    public void ReportBlocked_StopsAtFiftyPaths()
    {
        SandboxAccessLog.SandboxOverride = true;
        for (var i = 0; i < 80; i++)
        {
            SandboxAccessLog.ReportBlocked($@"C:\blocked\{i}.bin");
        }

        Assert.Equal(50, SandboxAccessLog.LoggedCount);
    }

    [Fact]
    public void ReportBlocked_IgnoresEmptyPath()
    {
        SandboxAccessLog.SandboxOverride = true;
        SandboxAccessLog.ReportBlocked((string?)null);
        SandboxAccessLog.ReportBlocked(string.Empty);
        Assert.Equal(0, SandboxAccessLog.LoggedCount);
    }

    [Theory]
    [InlineData("Access to the path 'C:/x/y.bin' is denied.", "C:/x/y.bin")]
    [InlineData("denied", "denied")]
    public void PathFromMessage_ExtractsTheQuotedPath(string message, string expected)
    {
        Assert.Equal(expected, SandboxAccessLog.PathFromMessage(message));
    }

    [Fact]
    public void ReportBlocked_Exception_UsesThePathOfTheMessage()
    {
        SandboxAccessLog.SandboxOverride = true;
        SandboxAccessLog.ReportBlocked(new UnauthorizedAccessException("Access to the path 'C:/x/y.bin' is denied."));
        SandboxAccessLog.ReportBlocked(@"C:/x/y.bin");
        Assert.Equal(1, SandboxAccessLog.LoggedCount);
    }
}
