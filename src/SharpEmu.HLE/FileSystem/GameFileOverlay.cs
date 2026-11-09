// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Enumeration;

namespace SharpEmu.HLE.FileSystem;

// A session-local lookup: mod files stay in their own directories, including for archived games.
public sealed class GameFileOverlay : IDisposable
{
    private static GameFileOverlay? _current;
    private static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string _root;
    private readonly Dictionary<string, (string Path, string Root)> _files = new(Comparer);
    private readonly Dictionary<string, (string Path, string Root)> _directories = new(Comparer);
    private readonly Dictionary<string, HashSet<string>> _children = new(Comparer);

    public GameFileOverlay(string applicationRoot, IEnumerable<string> modDirectories)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        foreach (var mod in modDirectories)
        {
            var directory = Path.GetFullPath(mod);
            if (!Directory.Exists(directory) || !IsSafePath(directory, directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                if (!IsSafePath(directory, file)) continue;
                var relative = Path.GetRelativePath(directory, file);
                _files[relative] = (file, directory);
                var child = relative;
                while (Path.GetDirectoryName(child) is { } parent)
                {
                    if (!_children.TryGetValue(parent, out var names)) _children[parent] = names = new(Comparer);
                    names.Add(Path.GetFileName(child));
                    _directories[parent] = (Path.Combine(directory, parent), directory);
                    if (parent.Length == 0) break;
                    child = parent;
                }
            }
        }
        Volatile.Write(ref _current, this);
    }

    public static string ResolveFile(string hostPath)
    {
        var current = Volatile.Read(ref _current);
        if (current is null || !current.TryRelative(hostPath, out var relative) ||
            !current._files.TryGetValue(relative, out var file)) return hostPath;
        // Do not follow a mod directory replaced with a symlink after the session started.
        return File.Exists(file.Path) && IsSafePath(file.Root, file.Path) ? file.Path : hostPath;
    }

    public static bool IsDirectory(string hostPath) => DirectorySource(hostPath) is not null;

    public static string? DirectorySource(string hostPath)
    {
        var current = Volatile.Read(ref _current);
        return current is not null && current.TryRelative(hostPath, out var relative) &&
               current._directories.TryGetValue(relative, out var directory) && Directory.Exists(directory.Path) && IsSafePath(directory.Root, directory.Path)
            ? directory.Path : null;
    }

    public static IEnumerable<string> List(string hostDirectory)
    {
        var current = Volatile.Read(ref _current);
        return current is not null && current.TryRelative(hostDirectory, out var relative) &&
               current._children.TryGetValue(relative, out var children) ? children : [];
    }

    public static IEnumerable<string> EnumerateFiles(string hostDirectory, string pattern, SearchOption option)
    {
        var current = Volatile.Read(ref _current);
        if (current is null || !current.TryRelative(hostDirectory, out var relative)) yield break;
        var prefix = relative.Length == 0 ? string.Empty : relative + Path.DirectorySeparatorChar;
        foreach (var file in current._files.Keys)
        {
            if (!file.StartsWith(prefix, Comparison)) continue;
            var tail = file[prefix.Length..];
            if (option == SearchOption.TopDirectoryOnly && tail.Contains(Path.DirectorySeparatorChar)) continue;
            if (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), ignoreCase: OperatingSystem.IsWindows()))
                yield return Path.Combine(current._root, file);
        }
    }

    private bool TryRelative(string hostPath, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrEmpty(hostPath)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));
        if (string.Equals(full, _root, Comparison)) return true;
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, Comparison)) return false;
        relative = full[(_root.Length + 1)..];
        return true;
    }

    internal static bool IsSafePath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        if (!string.Equals(root, path, Comparison) && !path.StartsWith(root + Path.DirectorySeparatorChar, Comparison)) return false;
        try
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current, root, Comparison)) return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        return false;
    }

    public void Dispose() => Interlocked.CompareExchange(ref _current, null, this);
}
