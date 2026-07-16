// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public static class GameFileSystemMount
{
    private static readonly object Gate = new();
    private static MountedGame? _current;

    public static MountedGame? Current => Volatile.Read(ref _current);

    public static IDisposable Bind(GameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (Gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("A game filesystem is already mounted.");
            }

            _current = new MountedGame(
                source.SourcePath,
                source.DisplayName,
                source.ExecutablePath,
                source.IsArchive,
                source.FileSystem);
            return new Scope();
        }
    }

    public sealed record MountedGame(
        string SourcePath,
        string DisplayName,
        string ExecutablePath,
        bool IsArchive,
        IReadOnlyGameFileSystem FileSystem);

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _current = null;
            }
        }
    }
}
