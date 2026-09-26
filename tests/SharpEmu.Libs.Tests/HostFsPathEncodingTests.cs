// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HostFsPathEncodingTests
{
    [Fact]
    public void EncodeRoundTripsColonAndPercentOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string guest = "rg_ac_Arcade Spirits: The New Challengers_0.dat";
        var host = HostFsPath.EncodeHostPathSegment(guest);
        Assert.Equal("rg_ac_Arcade Spirits%3A The New Challengers_0.dat", host);
        Assert.Equal(guest, HostFsPath.DecodeHostPathSegment(host));
        Assert.DoesNotContain(':', host);
    }

    [Fact]
    public void EncodeIsNoOpOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string guest = "Arcade Spirits: The New Challengers";
        Assert.Equal(guest, HostFsPath.EncodeHostPathSegment(guest));
    }
}
