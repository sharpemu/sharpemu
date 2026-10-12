// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Enumeration;

namespace SharpEmu.HLE.FileSystem;

// A game run from a .zar mounts /app0 at a small host directory that holds the files host code reads
// by path (the executable, its modules, sce_sys); every other /app0 path is served from the archive.
// A host path under that directory that is not on disk resolves here: stat, listing and read-only
// streams come from the archive, and a host consumer that needs a real file can extract just it.
public static class App0Archive
{
    private static readonly object Gate = new();
    private static ZArchive? _archive;
    private static string? _root;

    public static bool IsMounted => Volatile.Read(ref _archive) is not null;

    public static string? Root => Volatile.Read(ref _root);

    public static void Mount(ZArchive archive, string root)
    {
        lock (Gate)
        {
            _archive?.Dispose();
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            Volatile.Write(ref _archive, archive);
        }
    }

    public static void Unmount()
    {
        lock (Gate)
        {
            _archive?.Dispose();
            Volatile.Write(ref _archive, null);
            _root = null;
        }
    }

    // The archive and the node a host path names, when it lies under the mount and the archive has it.
    public static bool TryResolve(string hostPath, out ZArchive archive, out int node)
    {
        archive = Volatile.Read(ref _archive)!;
        node = ZArchive.InvalidNode;
        var root = Volatile.Read(ref _root);
        if (archive is null || root is null || string.IsNullOrEmpty(hostPath))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string relative;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            relative = string.Empty;
        }
        else if (full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            full[root.Length] is '\\' or '/')
        {
            relative = full[(root.Length + 1)..];
        }
        else
        {
            return false;
        }

        node = archive.LookUp(relative);
        return node != ZArchive.InvalidNode;
    }

    public static bool TryGetFile(string hostPath, out ulong size)
    {
        size = 0;
        if (!TryResolve(hostPath, out var archive, out var node) || !archive.IsFile(node))
        {
            return false;
        }

        size = archive.GetFileSize(node);
        return true;
    }

    public static bool IsDirectory(string hostPath) =>
        TryResolve(hostPath, out var archive, out var node) && archive.IsDirectory(node);

    public static bool Exists(string hostPath) => TryResolve(hostPath, out _, out _);

    // The names in the archive directory a host path names; empty when there is none.
    public static IReadOnlyList<ZArchive.Entry> List(string hostPath) =>
        TryResolve(hostPath, out var archive, out var node) ? archive.GetEntries(node) : [];

    // A read-only, seekable stream over the archive file a host path names.
    public static ZArchiveFileStream? OpenRead(string hostPath) =>
        TryResolve(hostPath, out var archive, out var node) && archive.IsFile(node)
            ? new ZArchiveFileStream(archive, node, hostPath)
            : null;

    // Opens a host path for reading: from the archive when it is under the mount and not on disk,
    // otherwise as a host file.
    public static Stream OpenReadOrFile(string hostPath, FileShare share = FileShare.Read) =>
        !File.Exists(hostPath) && OpenRead(hostPath) is { } archived
            ? archived
            : new FileStream(hostPath, FileMode.Open, FileAccess.Read, share);

    // Writes the archive file a host path names to that path, for host code that opens files itself.
    // True when the file is on disk afterwards.
    public static bool Extract(string hostPath)
    {
        if (File.Exists(hostPath))
        {
            return true;
        }

        if (OpenRead(hostPath) is not { } source)
        {
            return false;
        }

        using (source)
        {
            lock (Gate)
            {
                WriteFile(source, hostPath);
            }
        }

        return true;
    }

    // Copies an archive file to a host path through a temporary name, so a partial copy is never seen.
    internal static void WriteFile(Stream source, string hostPath)
    {
        if (File.Exists(hostPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hostPath))!);
        var temporary = hostPath + ".partial";
        using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target, ZArchive.BlockSize);
        }

        File.Move(temporary, hostPath, overwrite: true);
    }

    // Every file in the mounted archive, as paths relative to the mount with '/' separators.
    public static List<string> ListAllFiles()
    {
        var files = new List<string>();
        if (Volatile.Read(ref _archive) is { } archive)
        {
            AddFiles(archive, archive.LookUp(string.Empty), string.Empty, files);
        }

        return files;
    }

    private static void AddFiles(ZArchive archive, int directory, string prefix, List<string> files)
    {
        foreach (var entry in archive.GetEntries(directory))
        {
            var path = prefix + entry.Name;
            if (entry.IsDirectory)
            {
                AddFiles(archive, archive.LookUp(path), path + "/", files);
            }
            else
            {
                files.Add(path);
            }
        }
    }

    // Files under a host directory and all its subdirectories whose names match a pattern: those on
    // disk and, when the directory is under the mount, those in the archive.
    public static IEnumerable<string> EnumerateFilesRecursive(string hostDirectory, string pattern)
    {
        var found = Directory.Exists(hostDirectory)
            ? Directory.EnumerateFiles(hostDirectory, pattern, SearchOption.AllDirectories).ToList()
            : [];
        var root = Root;
        if (root is null || !TryResolve(hostDirectory, out var archive, out var node) || !archive.IsDirectory(node))
        {
            return found;
        }

        var seen = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
        var start = Path.GetRelativePath(root, Path.GetFullPath(hostDirectory));
        var prefix = start == "." ? string.Empty : start.Replace('\\', '/') + "/";
        var archived = new List<string>();
        AddFiles(archive, node, prefix, archived);
        foreach (var relative in archived)
        {
            var hostPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(hostPath)) && seen.Add(hostPath))
            {
                found.Add(hostPath);
            }
        }

        return found;
    }

    // Extracts every file under the archive directory into the mount, for the files host code reads by path.
    public static void ExtractDirectory(string relative)
    {
        var root = Root ?? throw new InvalidOperationException("No archive is mounted.");
        var directory = Path.Combine(root, relative);
        foreach (var entry in List(directory))
        {
            var path = Path.Combine(relative, entry.Name);
            if (entry.IsDirectory)
            {
                ExtractDirectory(path);
            }
            else
            {
                Extract(Path.Combine(root, path));
            }
        }
    }
}

// One archive file as a read-only, seekable stream; reads at any position go through the archive's
// block cache. Name is the host path it was opened as.
public sealed class ZArchiveFileStream : Stream
{
    private readonly ZArchive _archive;
    private readonly int _node;
    private readonly long _length;
    private long _position;

    public ZArchiveFileStream(ZArchive archive, int node, string name)
    {
        _archive = archive;
        _node = node;
        _length = (long)archive.GetFileSize(node);
        Name = name;
    }

    public string Name { get; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    // Reads at an offset without moving the position, like a positional read on a file handle.
    public int ReadAt(Span<byte> buffer, long offset) =>
        offset < 0 ? throw new ArgumentOutOfRangeException(nameof(offset)) : _archive.Read(_node, (ulong)offset, buffer);

    public override int Read(Span<byte> buffer)
    {
        var read = ReadAt(buffer, _position);
        _position += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("An archive file is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("An archive file is read-only.");
}
