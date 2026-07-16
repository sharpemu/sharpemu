// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.GameContent;

namespace SharpEmu.Libs.Ampr;

internal static class AmprFileRegistry
{
    private static readonly ConcurrentDictionary<uint, RegisteredFile> _filesById = new();

    public static uint RegisterHost(string guestPath, string hostPath)
    {
        var id = ComputeFileId(guestPath);
        _filesById[id] = new RegisteredFile(hostPath, IsMountedGame: false);
        return id;
    }

    public static uint RegisterGame(string guestPath, string gamePath)
    {
        var id = ComputeFileId(guestPath);
        _filesById[id] = new RegisteredFile(gamePath, IsMountedGame: true);
        return id;
    }

    public static bool TryGetFile(uint id, out RegisteredFile file) =>
        _filesById.TryGetValue(id, out file!);

    internal static uint ComputeFileId(string guestPath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(guestPath);

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    internal sealed record RegisteredFile(string Path, bool IsMountedGame)
    {
        public Stream OpenRead()
        {
            if (!IsMountedGame)
            {
                return new FileStream(
                    Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1024 * 1024,
                    FileOptions.RandomAccess);
            }

            var mount = GameFileSystemMount.Current
                ?? throw new IOException("The game filesystem is not mounted.");
            return mount.FileSystem.OpenRead(Path);
        }
    }
}
