// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.HLE.FileSystem;

/// <summary>One file the launcher opened for the child: the inherited handle, the path it stands for and its stamp.</summary>
public sealed record InheritedFile(nint Handle, string Path, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// Files the sandboxed launcher opened itself and handed down as inheritable handles (SHARPEMU_SANDBOX_FILES). An
/// AppContainer cannot open anything on an exFAT drive (no ACLs), but Windows checks access only when a file is
/// opened, so a handle opened by the launcher keeps working in the child. Code that checks or opens such a file by
/// path asks this registry first.
/// </summary>
/// <remarks>
/// The variable is a list of entries separated by <c>*</c>, each <c>&lt;handle&gt;|&lt;full path&gt;</c>; neither
/// character can occur in a Windows file name, so the paths need no escaping.
/// </remarks>
public static class InheritedFiles
{
    public const string Variable = "SHARPEMU_SANDBOX_FILES";
    private const char EntrySeparator = '*';
    private const char FieldSeparator = '|';

    // Parsed from the environment on first use; tests replace it.
    private static IReadOnlyDictionary<string, InheritedFile>? _registered;

    private static IReadOnlyDictionary<string, InheritedFile> Registered =>
        _registered ??= Parse(Environment.GetEnvironmentVariable(Variable));

    internal static void ReplaceForTests(IReadOnlyDictionary<string, InheritedFile>? files) => _registered = files;

    /// <summary>The value of <see cref="Variable"/> for these files, in the form the child parses.</summary>
    public static string Format(IEnumerable<(nint Handle, string Path)> files) =>
        string.Join(EntrySeparator, files.Select(file =>
            string.Create(CultureInfo.InvariantCulture, $"{(long)file.Handle}{FieldSeparator}{System.IO.Path.GetFullPath(file.Path)}")));

    /// <summary>Entries that are malformed, or whose handle is not an open file, are dropped.</summary>
    public static IReadOnlyDictionary<string, InheritedFile> Parse(string? value) =>
        Parse(value, TryDescribe);

    internal static IReadOnlyDictionary<string, InheritedFile> Parse(
        string? value, Func<nint, string, InheritedFile?> describe)
    {
        var files = new Dictionary<string, InheritedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (value ?? string.Empty).Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf(FieldSeparator);
            if (split <= 0 ||
                !long.TryParse(entry.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out var handle) ||
                handle == 0)
            {
                continue;
            }

            string path;
            try
            {
                path = System.IO.Path.GetFullPath(entry[(split + 1)..]);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (describe((nint)handle, path) is { } file)
            {
                files[path] = file;
            }
        }

        return files;
    }

    /// <summary>The inherited file standing for <paramref name="path"/>, compared as a full path without case.</summary>
    public static bool TryGet(string? path, out InheritedFile file) => TryGet(Registered, path, out file);

    internal static bool TryGet(IReadOnlyDictionary<string, InheritedFile> files, string? path, out InheritedFile file)
    {
        file = null!;
        if (files.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return files.TryGetValue(System.IO.Path.GetFullPath(path), out file!);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True for an inherited file, else whether the file exists on disk.</summary>
    public static bool Exists(string? path) => TryGet(path, out _) || File.Exists(path);

    /// <summary>
    /// A handle for reading, which the caller may dispose without closing the inherited one. Reads must be positional
    /// (<see cref="RandomAccess"/>): every opener shares the one file pointer.
    /// </summary>
    public static SafeFileHandle OpenRead(InheritedFile file) => new(file.Handle, ownsHandle: false);

    /// <summary>Length and write time of a file, from the inherited handle when there is one.</summary>
    public static (long Length, DateTime LastWriteTimeUtc) GetStamp(string path)
    {
        if (TryGet(path, out var inherited))
        {
            return (inherited.Length, inherited.LastWriteTimeUtc);
        }

        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static InheritedFile? TryDescribe(nint handle, string path)
    {
        try
        {
            using var file = new SafeFileHandle(handle, ownsHandle: false);
            return new InheritedFile(
                handle, path, RandomAccess.GetLength(file), File.GetLastWriteTimeUtc(file));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
