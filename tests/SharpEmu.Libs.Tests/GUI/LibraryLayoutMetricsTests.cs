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

    [Fact]
    public void Calculate_RowShowsLargerCoversAndALargerSelectedCover()
    {
        var grid = LibraryLayoutMetrics.Calculate(1436);
        var row = LibraryLayoutMetrics.Calculate(1436, rail: true);

        Assert.True(row.CoverSize >= LibraryLayoutMetrics.MinimumRailCoverSize);
        Assert.True(row.CoverSize > grid.CoverSize);
        Assert.Equal(row.CoverSize * LibraryLayoutMetrics.RailSelectedScale, row.SelectedCoverSize, 6);
        Assert.Equal(row.SelectedCoverSize + 12, row.SelectedItemWidth, 6);
        Assert.Equal(grid.CoverSize, grid.SelectedCoverSize);
    }

    [Fact]
    public void Calculate_TileScaleAppliesToBothViews()
    {
        var row = LibraryLayoutMetrics.Calculate(1920, 1.2, rail: true);

        Assert.Equal(LibraryLayoutMetrics.MaximumRailCoverSize * 1.2, row.CoverSize, 6);
        Assert.Equal(LibraryLayoutMetrics.MaximumCoverSize * 0.8, LibraryLayoutMetrics.Calculate(1920, 0.8).CoverSize, 6);
    }
}
