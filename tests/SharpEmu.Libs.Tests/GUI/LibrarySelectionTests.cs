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

    private static GameEntry CreateGame(string name) =>
        new(name, null, null, $"/games/{name}/eboot.bin", 0, null, null);
}
