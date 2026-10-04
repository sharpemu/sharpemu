// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

/// <summary>
/// Finds game executables without walking every file inside an installation.
/// Once a directory contains eboot.bin it is a game root, so its often-large
/// content tree does not need to be searched.
/// </summary>
internal static class GameLibraryScanner
{
    private const int DefaultMaxRecursionDepth = 8;

    internal static IEnumerable<string> EnumerateExecutables(
        string root,
        CancellationToken cancellationToken,
        int maxRecursionDepth = DefaultMaxRecursionDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRecursionDepth);

        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((Path.GetFullPath(root), 0));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            var executable = Path.Combine(directory, "eboot.bin");
            if (File.Exists(executable))
            {
                yield return executable;
                continue;
            }

            if (depth >= maxRecursionDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                });
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(children, GameLibraryPath.Comparer);
            for (var index = children.Length - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }
    }
}
