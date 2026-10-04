// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameLibraryScannerTests
{
    [Fact]
    public void EnumerateExecutables_FindsNestedGamesWithoutDescendingIntoInstallations()
    {
        var root = TemporaryDirectory();
        try
        {
            var direct = Path.Combine(root, "Construction Simulator Gold Edition");
            var nested = Path.Combine(root, "collection", "The Messenger");
            Directory.CreateDirectory(Path.Combine(direct, "Media", "StreamingAssets"));
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(direct, "eboot.bin"), [1]);
            File.WriteAllBytes(Path.Combine(nested, "eboot.bin"), [2]);
            File.WriteAllBytes(
                Path.Combine(direct, "Media", "StreamingAssets", "eboot.bin"),
                [3]);

            var executables = GameLibraryScanner.EnumerateExecutables(
                    root,
                    CancellationToken.None)
                .Select(Path.GetFullPath)
                .ToArray();

            var expected = new[]
            {
                Path.Combine(direct, "eboot.bin"),
                Path.Combine(nested, "eboot.bin"),
            };
            Assert.Equal(
                expected.OrderBy(path => path, GameLibraryPath.Comparer),
                executables.OrderBy(path => path, GameLibraryPath.Comparer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateExecutables_HonoursDepthAndCancellation()
    {
        var root = TemporaryDirectory();
        try
        {
            var game = Path.Combine(root, "one", "two");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), [1]);

            Assert.Empty(GameLibraryScanner.EnumerateExecutables(
                root,
                CancellationToken.None,
                maxRecursionDepth: 1));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                GameLibraryScanner.EnumerateExecutables(root, cancellation.Token).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-library-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
