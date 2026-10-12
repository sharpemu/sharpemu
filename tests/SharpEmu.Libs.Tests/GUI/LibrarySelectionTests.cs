// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LibrarySelectionTests
{
    [Fact]
    public void ResolveLibrarySelection_SelectsFirstGameWithoutPreviousSelection()
    {
        var first = CreateGame("First");
        var second = CreateGame("Second");

        var selected = MainWindow.ResolveLibrarySelection([first, second], null);

        Assert.Same(first, selected);
    }

    [Fact]
    public void ResolveLibrarySelection_PreservesVisibleSelection()
    {
        var first = CreateGame("First");
        var second = CreateGame("Second");

        var selected = MainWindow.ResolveLibrarySelection([first, second], second.Path);

        Assert.Same(second, selected);
    }

    [Fact]
    public void ResolveLibrarySelection_SelectsFirstGameWhenPreviousSelectionIsGone()
    {
        var first = CreateGame("First");

        var selected = MainWindow.ResolveLibrarySelection([first], "/games/Missing/eboot.bin");

        Assert.Same(first, selected);
    }

    [Theory]
    [InlineData("Hades", "PPSA03355", "01.003.000", "PPSA03355-1.003")]
    [InlineData("Hades", "PPSA03355", "01.004.002", "PPSA03355-1.004.002")]
    [InlineData("Hades", "PPSA03355", "10.000.000", "PPSA03355-10.000")]
    [InlineData("Hades", "PPSA03355", null, "PPSA03355")]
    [InlineData("Hades", null, "01.003.000", "Hades-1.003")]
    [InlineData("Hades", " ", " ", "Hades")]
    [InlineData("Game", "PPSA00001", "1.0/beta", "PPSA00001-1.0_beta")]
    public void SuggestedArchiveName_IsTitleIdAndVersion(string folder, string? titleId, string? version, string expected)
    {
        Assert.Equal(expected, MainWindow.SuggestedArchiveName(folder, titleId, version));
    }

    private static GameEntry CreateGame(string name) =>
        new(name, null, null, $"/games/{name}/eboot.bin", 0, null, null);
}
