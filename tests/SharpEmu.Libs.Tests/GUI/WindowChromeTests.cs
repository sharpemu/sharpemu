// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class WindowChromeTests
{
    [Theory]
    [InlineData(true, false, 0, false, true)]
    [InlineData(false, false, 0, false, false)]
    [InlineData(true, true, 0, false, false)]
    [InlineData(true, false, 1, false, false)]
    [InlineData(true, true, 1, false, false)]
    [InlineData(true, false, 0, true, false)]
    public void ConsoleVisibilityRequiresTheLibraryPage(
        bool requested, bool detached, int activePageIndex, bool gameOptionsOpen, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldShowEmbeddedConsole(requested, detached, activePageIndex, gameOptionsOpen));
    }

    [Theory]
    [InlineData(100, 100, 1, 100, 100)]
    [InlineData(5000, 2000, 1, 940, 460)]
    [InlineData(-5000, -2000, 1, 0, 0)]
    [InlineData(5000, 2000, 1.5, 450, 150)]
    [InlineData(5000, 2000, 3, 0, 0)]
    public void ConsolePositionStaysInsideTheCurrentDisplay(
        int left, int top, double scaling, int expectedLeft, int expectedTop)
    {
        Assert.Equal(new PixelPoint(expectedLeft, expectedTop), ConsoleWindow.ConstrainPosition(
            new PixelPoint(left, top), new PixelRect(0, 0, 1920, 1080), new Size(980, 620), scaling));
    }

    [Fact]
    public void ConsolePositionSupportsDisplaysWithNegativeCoordinates()
    {
        var position = new PixelPoint(-1800, 120);
        Assert.Equal(position, ConsoleWindow.ConstrainPosition(
            position, new PixelRect(-1920, 0, 1920, 1080), new Size(980, 620), 1));
    }

    [Theory]
    [InlineData(WindowState.Normal, "crop_square", "Maximize", "Maximize window")]
    [InlineData(WindowState.Maximized, "filter_none", "Restore", "Restore window")]
    public void GetMaximizeButtonState_ReturnsConsistentVisualAndAccessibleState(
        WindowState windowState,
        string expectedGlyph,
        string expectedToolTip,
        string expectedAutomationName)
    {
        var state = MainWindow.GetMaximizeButtonState(windowState);

        Assert.Equal(expectedGlyph, state.Glyph);
        Assert.Equal(expectedToolTip, state.ToolTip);
        Assert.Equal(expectedAutomationName, state.AutomationName);
    }
}
