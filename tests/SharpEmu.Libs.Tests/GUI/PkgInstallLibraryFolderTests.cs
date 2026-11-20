// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class PkgInstallLibraryFolderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sharpemu-library");

    [Fact]
    public void AGameUnderALibraryFolderIsAlreadyCovered()
    {
        var game = Path.Combine(Root, "Games", "Some Game");
        Assert.True(MainWindow.IsUnderLibraryFolder([Path.Combine(Root, "Games")], game));
        Assert.True(MainWindow.IsUnderLibraryFolder([game + Path.DirectorySeparatorChar], game));
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotCovered()
    {
        var library = Path.Combine(Root, "Games");
        Assert.False(MainWindow.IsUnderLibraryFolder([library], Path.Combine(Root, "Games2", "Some Game")));
        Assert.False(MainWindow.IsUnderLibraryFolder([], Path.Combine(library, "Some Game")));
    }
}
