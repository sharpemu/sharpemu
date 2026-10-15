// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// The display patch resolution is the size title display patches rewrite a game's compiled
/// 4K sizes to. It is a host setting only: it is not the window size and not what the guest
/// is told about the display.
/// </summary>
public sealed class DisplayPatchResolutionTests
{
    [Fact]
    public void TheDefaultPatchTargetIs1080p()
    {
        var options = HostVideoOptions.Default;

        Assert.Equal(DisplayPatchResolution.Hd, options.DisplayResolution);
        Assert.Equal((1920u, 1080u), options.DisplayPatchSize);
    }

    [Fact]
    public void NativeTurnsThePatchesOff()
    {
        var options = new HostVideoOptions { DisplayResolution = DisplayPatchResolution.Native };

        Assert.Null(options.DisplayPatchSize);
    }

    [Fact]
    public void AnUnknownSettingFallsBackToTheDefault()
    {
        var options = new HostVideoOptions { DisplayResolution = (DisplayPatchResolution)42 }.Normalize();

        Assert.Equal(DisplayPatchResolution.Hd, options.DisplayResolution);
    }

    [Fact]
    public void TheWindowSizeAndThePatchTargetAreIndependent()
    {
        var options = new HostVideoOptions
        {
            Width = 3840,
            Height = 2160,
            DisplayResolution = DisplayPatchResolution.Hd,
        }.Normalize();

        Assert.Equal(3840, options.Width);
        Assert.Equal((1920u, 1080u), options.DisplayPatchSize);
    }
}
