// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal readonly record struct LibraryTileMetrics(
    double CoverSize,
    double ItemWidth,
    double RailItemHeight,
    double GridItemHeight);

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

    internal static LibraryTileMetrics Calculate(double availableWidth)
    {
        var contentWidth = Math.Max(0, availableWidth - HorizontalPadding);
        var coverSize = Math.Clamp(
            (contentWidth / TargetColumnCount) - ItemWidthExtra - ItemMargin,
            MinimumCoverSize,
            MaximumCoverSize);

        return new LibraryTileMetrics(
            CoverSize: coverSize,
            ItemWidth: coverSize + ItemWidthExtra,
            RailItemHeight: coverSize + RailHeightExtra,
            GridItemHeight: coverSize + GridHeightExtra);
    }
}
