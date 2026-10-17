// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class HostDisplayOptionsTests
{
    private static readonly HostDisplayInfo Display = new(
        1,
        "Test monitor",
        [
            new HostDisplayMode(2560, 1440, 144),
            new HostDisplayMode(2560, 1440, 60),
            new HostDisplayMode(1920, 1080, 120),
            new HostDisplayMode(1920, 1080, 60),
        ]);

    [Fact]
    public void BuildDisplays_PreservesUnavailableSavedIndex()
    {
        var displays = HostDisplayOptions.BuildDisplays([Display], 3);

        Assert.Equal([1, 3], displays.Select(display => display.Index));
        Assert.Equal(3, HostDisplayOptions.SelectDisplay(displays, 3).Index);
    }

    [Fact]
    public void BuildResolutions_DeduplicatesModesAndPreservesCustomValue()
    {
        var display = new HostDisplayOption(Display);

        var resolutions = HostDisplayOptions.BuildResolutions(display, "3440x1440");

        Assert.Equal(["3440x1440", "2560x1440", "1920x1080"], resolutions);
    }

    [Fact]
    public void BuildRefreshRates_FiltersResolutionAndKeepsAutomaticFirst()
    {
        var display = new HostDisplayOption(Display);

        var rates = HostDisplayOptions.BuildRefreshRates(display, "1920x1080", 75, "Automatic");

        Assert.Equal([0, 120, 75, 60], rates.Select(rate => rate.Value));
        Assert.Equal("Automatic", rates[0].Label);
    }
}
