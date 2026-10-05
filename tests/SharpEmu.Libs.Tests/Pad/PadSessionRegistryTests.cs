// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadSessionRegistryTests
{
    private const int PrimaryUserId = 0x10000000;

    [Fact]
    public void MotionSensorDefaults_AreEnabledForEachNewSession()
    {
        var registry = new PadSessionRegistry();
        registry.Initialize();
        var firstHandle = registry.Open(PrimaryUserId, 0, 0, allowMultipleOpens: false);
        var secondHandle = registry.Open(PrimaryUserId, 2, 0, allowMultipleOpens: true);
        Assert.True(registry.TryGet(firstHandle, out var firstSession));
        Assert.True(registry.TryGet(secondHandle, out var secondSession));
        Assert.Equal(1, firstSession.MotionSensorEnabled);
        Assert.Equal(1, secondSession.MotionSensorEnabled);

        firstSession.MotionSensorEnabled = 0;
        registry.Initialize();
        Assert.Equal(0, firstSession.MotionSensorEnabled);
        Assert.Equal(1, secondSession.MotionSensorEnabled);
        Assert.Equal(0, registry.Close(firstHandle));

        var reopenedHandle = registry.Open(PrimaryUserId, 0, 0, allowMultipleOpens: false);
        Assert.NotEqual(firstHandle, reopenedHandle);
        Assert.True(registry.TryGet(reopenedHandle, out var reopenedSession));
        Assert.Equal(1, reopenedSession.MotionSensorEnabled);
    }
}
