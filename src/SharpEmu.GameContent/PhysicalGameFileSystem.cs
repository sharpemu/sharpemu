// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

public sealed class PhysicalGameFileSystem : IReadOnlyGameFileSystem
{
    private readonly string _root;

    public PhysicalGameFileSystem(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Game directory was not found: {_root}");
        }
    }

    public bool TryGetEntry(string path, out GameFileEntry entry)
    {
        var normalized = GamePath.Normalize(path);
        var physicalPath = Resolve(normalized);
        if (File.Exists(physicalPath))
        {
            var info = new FileInfo(physicalPath);
            entry = new GameFileEntry(
                normalized.Length == 0 ? info.Name : GetName(normalized),
                IsDirectory: false,
                info.Length,
                info.LastWriteTimeUtc);
            return true;
        }

        if (Directory.Exists(physicalPath))
        {
            var info = new DirectoryInfo(physicalPath);
            entry = new GameFileEntry(
                normalized.Length == 0 ? string.Empty : GetName(normalized),
                IsDirectory: true,
                Length: 0,
                info.LastWriteTimeUtc);
            return true;
        }

        entry = default;
        return false;
    }

    public IReadOnlyList<GameFileEntry> EnumerateDirectory(string path)
    {
        var physicalPath = Resolve(GamePath.Normalize(path));
        if (!Directory.Exists(physicalPath))
        {
            throw new DirectoryNotFoundException($"Game directory was not found: {path}");
        }

        return new DirectoryInfo(physicalPath)
            .EnumerateFileSystemInfos()
            .Select(static info => info switch
            {
                DirectoryInfo directory => new GameFileEntry(
                    directory.Name, IsDirectory: true, Length: 0, directory.LastWriteTimeUtc),
                FileInfo file => new GameFileEntry(
                    file.Name, IsDirectory: false, file.Length, file.LastWriteTimeUtc),
                _ => default,
            })
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Stream OpenRead(string path)
    {
        var normalized = GamePath.Normalize(path);
        var physicalPath = Resolve(normalized);
        if (!File.Exists(physicalPath))
        {
            throw new FileNotFoundException("Game file was not found.", normalized);
        }

        return new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void Dispose()
    {
    }

    internal string ResolveHostPath(string path) => Resolve(GamePath.Normalize(path));

    private string Resolve(string normalized)
    {
        var candidate = Path.GetFullPath(Path.Combine(
            _root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = _root + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, _root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Game path escapes the content root.", nameof(normalized));
        }

        return candidate;
    }

    private static string GetName(string normalized)
    {
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }
}
