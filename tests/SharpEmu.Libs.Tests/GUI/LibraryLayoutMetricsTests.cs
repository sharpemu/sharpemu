// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LibraryLayoutMetricsTests
{
    [Fact]
    public void Calculate_UsesMaximumCoverSizeForWideWindow()
    {
        var metrics = LibraryLayoutMetrics.Calculate(1920);

        Assert.Equal(LibraryLayoutMetrics.MaximumCoverSize, metrics.CoverSize);
        Assert.Equal(160, metrics.ItemWidth);
    }

    [Fact]
    public void Calculate_ShrinksNineTilesToAvailableWidth()
    {
        const double availableWidth = 1436;

        var metrics = LibraryLayoutMetrics.Calculate(availableWidth);
        var usedWidth = 24 + (9 * (metrics.ItemWidth + 12));

        Assert.Equal(132.89, metrics.CoverSize, 2);
        Assert.Equal(availableWidth, usedWidth, 6);
    }

    [Fact]
    public void Calculate_UsesMinimumCoverSizeForNarrowWindow()
    {
        var metrics = LibraryLayoutMetrics.Calculate(980);

        Assert.Equal(LibraryLayoutMetrics.MinimumCoverSize, metrics.CoverSize);
        Assert.Equal(124, metrics.ItemWidth);
    }
}
