// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public sealed class GameSource : IDisposable
{
    private bool _disposed;

    private GameSource(
        string sourcePath,
        string executablePath,
        IReadOnlyGameFileSystem fileSystem,
        bool isArchive,
        string displayName,
        string? physicalRootPath)
    {
        SourcePath = sourcePath;
        ExecutablePath = executablePath;
        FileSystem = fileSystem;
        IsArchive = isArchive;
        DisplayName = displayName;
        PhysicalRootPath = physicalRootPath;
    }

    public string SourcePath { get; }
    public string ExecutablePath { get; }
    public IReadOnlyGameFileSystem FileSystem { get; }
    public bool IsArchive { get; }
    public string DisplayName { get; }
    public string? PhysicalRootPath { get; }

    public static GameSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Game source was not found.", fullPath);
        }

        if (string.Equals(Path.GetExtension(fullPath), ".zar", StringComparison.OrdinalIgnoreCase))
        {
            var archive = new ZArchiveFileSystem(fullPath);
            if (!archive.TryGetEntry("eboot.bin", out var executable) || !executable.IsFile)
            {
                archive.Dispose();
                throw new FileNotFoundException("The ZArchive does not contain eboot.bin at its root.", fullPath);
            }

            return new GameSource(
                fullPath,
                "eboot.bin",
                archive,
                isArchive: true,
                Path.GetFileNameWithoutExtension(fullPath),
                physicalRootPath: null);
        }

        var executableDirectory = Path.GetDirectoryName(fullPath)!;
        var contentRoot = executableDirectory;
        var executablePath = Path.GetFileName(fullPath);
        if (string.Equals(Path.GetFileName(executableDirectory), "decrypted", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(executableDirectory);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(Path.Combine(parent, "Media")))
            {
                contentRoot = parent;
                executablePath = GamePath.Combine("decrypted", executablePath);
            }
        }

        return new GameSource(
            fullPath,
            executablePath,
            new PhysicalGameFileSystem(contentRoot),
            isArchive: false,
            Path.GetFileName(contentRoot),
            contentRoot);
    }

    public long GetStorageSize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsArchive)
        {
            return new FileInfo(SourcePath).Length;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in FileSystem.EnumerateDirectory(directory))
            {
                if (entry.IsDirectory)
                {
                    pending.Push(GamePath.Combine(directory, entry.Name));
                }
                else
                {
                    total = checked(total + entry.Length);
                }
            }
        }

        return total;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FileSystem.Dispose();
    }
}
