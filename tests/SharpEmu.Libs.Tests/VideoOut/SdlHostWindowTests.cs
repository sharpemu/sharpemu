// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SDL;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class SdlHostWindowTests
{
    [Theory]
    [InlineData(SDL_Keycode.SDLK_F10, false)]
    [InlineData(SDL_Keycode.SDLK_F11, false)]
    [InlineData(SDL_Keycode.SDLK_F12, true)]
    public void OnlyF12RequestsCapture(SDL_Keycode key, bool expected)
    {
        Assert.Equal(expected, SdlHostWindow.IsCaptureKey(key));
    }

    [Theory]
    [InlineData(false, "SharpEmu - Application")]
    [InlineData(true, "SharpEmu - Application · Press F12 for capture")]
    public void WindowTitleShowsCaptureHintOnlyWhenAvailable(bool captureAvailable, string expected)
    {
        Assert.Equal(expected, SdlHostWindow.FormatWindowTitle("SharpEmu - Application", captureAvailable));
    }

    [Fact]
    public void UpdatedWindowTitleKeepsApplicationAndGpuDetails()
    {
        const string title = "SharpEmu - Application · GPU";
        Assert.Equal($"{title} · Press F12 for capture", SdlHostWindow.FormatWindowTitle(title, true));
    }
}
