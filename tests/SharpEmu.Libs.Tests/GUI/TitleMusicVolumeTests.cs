// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class TitleMusicVolumeTests
{
    [Theory]
    [InlineData("{}", 1.0)]
    [InlineData("{\"TitleMusicVolume\":0.35}", 0.35)]
    [InlineData("{\"TitleMusicVolume\":-1}", 0.0)]
    [InlineData("{\"TitleMusicVolume\":4}", 1.0)]
    public void SavedVolumeIsRestoredAndClamped(string json, double expected)
    {
        Assert.Equal(expected, GuiSettings.NormalizeFromJson(json).TitleMusicVolume);
    }
}
