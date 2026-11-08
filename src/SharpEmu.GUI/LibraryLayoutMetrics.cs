// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal readonly record struct LibraryTileMetrics(
    double CoverSize,
    double ItemWidth,
    double RailItemHeight,
    double GridItemHeight,
    double SelectedCoverSize,
    double SelectedItemWidth,
    double SelectedRailItemHeight);

internal static class LibraryLayoutMetrics
{
    internal const double MinimumCoverSize = 112;
    internal const double MaximumCoverSize = 148;
    private const double HorizontalPadding = 24;
    private const double ItemWidthExtra = 12;
    private const double ItemMargin = 12;
    private const double RailHeightExtra = 24;
    private const double GridHeightExtra = 52;
    private const double TargetColumnCount = 9;

    // The row shows fewer, larger covers: it is one line under the selected game, not a wall.
    internal const double MinimumRailCoverSize = 150;
    internal const double MaximumRailCoverSize = 240;
    private const double RailColumnCount = 6.5;
    // The selected game in the row stands out like a console home screen; the grid keeps
    // every tile the same size.
    internal const double RailSelectedScale = 1.3;

    // scale is the user's tile size: it multiplies the cover that fits the width.
    internal static LibraryTileMetrics Calculate(double availableWidth, double scale = 1.0, bool rail = false)
    {
        var contentWidth = Math.Max(0, availableWidth - HorizontalPadding);
        var coverSize = (rail
            ? Math.Clamp(
                (contentWidth / RailColumnCount) - ItemWidthExtra - ItemMargin,
                MinimumRailCoverSize,
                MaximumRailCoverSize)
            : Math.Clamp(
                (contentWidth / TargetColumnCount) - ItemWidthExtra - ItemMargin,
                MinimumCoverSize,
                MaximumCoverSize)) * scale;

        var selectedCoverSize = rail ? coverSize * RailSelectedScale : coverSize;
        return new LibraryTileMetrics(
            CoverSize: coverSize,
            ItemWidth: coverSize + ItemWidthExtra,
            RailItemHeight: coverSize + RailHeightExtra,
            GridItemHeight: coverSize + GridHeightExtra,
            SelectedCoverSize: selectedCoverSize,
            SelectedItemWidth: selectedCoverSize + ItemWidthExtra,
            SelectedRailItemHeight: selectedCoverSize + RailHeightExtra);
    }
}
