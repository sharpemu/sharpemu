// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class WindowsBalloonNotificationTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TextLongerThanTheBalloonFieldIsCutWithinTheLimit()
    {
        var text = new string('x', 300);

        var cut = WindowsBalloonNotification.Truncate(text, 255);

        Assert.Equal(255, cut.Length);
        Assert.EndsWith("...", cut);
        Assert.Equal("short", WindowsBalloonNotification.Truncate("short", 255));
    }
}
