// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem.Pkg;

namespace SharpEmu.HLE.FileSystem;

// A fake PS5 package mounted as a game folder: the package's inner image serves /app0 without
// extracting its whole tree (a large game needs several hundred GiB unpacked, several times what
// the package itself occupies). A host path under the mount resolves to a file inside the package,
// which is decoded block by block when the guest reads it.
public sealed class PkgArchive : IDisposable
{
    private static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Ps5Package _package;
    private readonly Dictionary<string, Ps5InnerFile> _files;
    private readonly Dictionary<string, List<ZArchive.Entry>> _directories;

    private PkgArchive(Ps5Package package, List<Ps5InnerFile> files)
    {
        _package = package;
        _files = new Dictionary<string, Ps5InnerFile>(Comparer);
        _directories = new Dictionary<string, List<ZArchive.Entry>>(Comparer) { [string.Empty] = [] };
        foreach (var file in files)
        {
            var relative = string.Join('/', file.Components);
            // Names differing only by case collapse into one entry on Windows; the first wins.
            if (!_files.TryAdd(relative, file))
            {
                continue;
            }

            for (var depth = 1; depth < file.Components.Length; depth++)
            {
                _directories.TryAdd(string.Join('/', file.Components[..depth]), []);
            }
        }

        foreach (var (relative, file) in _files)
        {
            var (parent, name) = Split(relative);
            _directories[parent].Add(new ZArchive.Entry(name, false, (ulong)file.Size));
        }

        foreach (var directory in _directories.Keys.Where(static path => path.Length > 0).ToArray())
        {
            var (parent, name) = Split(directory);
            _directories[parent].Add(new ZArchive.Entry(name, true, 0));
        }

        foreach (var entries in _directories.Values)
        {
            entries.Sort(static (left, right) => Comparer.Compare(left.Name, right.Name));
        }
    }

    // True for a path that names a .pkg file; Open decides whether it is a mountable fake PS5 one.
    public static bool IsPackagePath(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase);

    // Null when the file is not a package; throws when it is one that cannot be read.
    public static PkgArchive? Open(string path)
    {
        if (!IsPackagePath(path))
        {
            return null;
        }

        var package = GamePackage.Open(path);
        if (package is not Ps5Package ps5)
        {
            // PS4 packages are only supported by extracting them.
            package.Dispose();
            return null;
        }

        try
        {
            return new PkgArchive(ps5, ps5.Tree.Files);
        }
        catch
        {
            ps5.Dispose();
            throw;
        }
    }

    public bool TryGetFile(string relativePath, out ulong size)
    {
        if (_files.TryGetValue(Normalize(relativePath), out var file))
        {
            size = (ulong)file.Size;
            return true;
        }

        size = 0;
        return false;
    }

    public bool IsDirectory(string relativePath) => _directories.ContainsKey(Normalize(relativePath));

    public IReadOnlyList<ZArchive.Entry> GetEntries(string relativePath) =>
        _directories.TryGetValue(Normalize(relativePath), out var entries) ? entries : [];

    // Every file, as paths relative to the mount with '/' separators.
    public List<string> ListAllFiles() => [.. _files.Keys];

    // A read-only, seekable stream over a file, or null when the mount has no such file. `name` is
    // the host path the stream reports, for the callers that log it.
    public Stream? OpenRead(string relativePath, string? name = null) =>
        _files.TryGetValue(Normalize(relativePath), out var file)
            ? _package.OpenFile(file, name ?? relativePath)
            : null;

    public void Dispose() => _package.Dispose();

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static (string Parent, string Name) Split(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? (string.Empty, relativePath) : (relativePath[..cut], relativePath[(cut + 1)..]);
    }
}
