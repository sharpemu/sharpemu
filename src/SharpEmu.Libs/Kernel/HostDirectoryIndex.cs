// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Per-directory metadata index over the host paths a mount resolves to.
/// </summary>
/// <remarks>
/// Resolving a guest path has to answer three questions about every path
/// component: does it exist, is it a symlink (which would escape the mount),
/// and — for the leaf — how large is it. Asking the host one component at a
/// time costs one <c>lstat</c> per component, and the game image lives on an
/// external volume where a cold random <c>lstat</c> measures ~180 us. Demon's
/// Souls resolves ~222k distinct asset paths during boot, so that is ~40 s of
/// pure metadata latency.
///
/// Listing the component's parent directory instead answers the same questions
/// for every sibling at once, and a <c>readdir</c> followed by stats in
/// directory order measures ~37 us per entry on the same volume — 5x cheaper
/// per file, and the cost is paid once per directory rather than once per
/// asset. Asset tables address files in directory-sized clusters, so one
/// listing typically serves hundreds of resolutions.
///
/// A listing is only authoritative about an absent name if it covers every
/// spelling the host filesystem would accept, so case folding is probed per
/// directory: Windows always folds, macOS answers through
/// <c>pathconf(_PC_CASE_SENSITIVE)</c>, and elsewhere (Linux has no such
/// query, yet exFAT, vfat, NTFS and casefolded ext4 all fold) one listed name
/// is stat'ed with its ASCII case swapped. A name with non-ASCII characters
/// that is not in the listing reports <see cref="Verdict.Unknown"/> — Unicode
/// normalization and case tables differ per filesystem — and the caller falls
/// back to its own per-component stat.
/// </remarks>
internal static partial class HostDirectoryIndex
{
    internal enum Verdict
    {
        /// <summary>The index cannot answer; the caller must stat the path itself.</summary>
        Unknown,
        /// <summary>The parent directory was listed and does not contain the name.</summary>
        Absent,
        Directory,
        File,
        /// <summary>A symlink (any type). Resolving through it would leave the mount.</summary>
        Symlink,
    }

    internal readonly record struct Entry(Verdict Verdict, long Length);

    // Guards total memory: a pathological root (or a guest walking a host-wide
    // mount) must not grow this without bound. Demon's Souls needs ~223k.
    private const int MaxIndexedEntries = 1 << 21;

    // A directory this large costs more to list than the handful of files a
    // caller is likely to want from it, so it is left to per-component stats.
    private const int MaxDirectoryEntries = 1 << 17;

    private sealed class DirectoryListing
    {
        public required FrozenDictionary<string, Entry> Ordinal { get; init; }

        /// <summary>Non-null only where the host filesystem folds case.</summary>
        public FrozenDictionary<string, Entry>? IgnoreCase { get; init; }

        /// <summary>
        /// False when the listing could not be completed (I/O error, entry cap),
        /// in which case a missing name means "not known" rather than "absent".
        /// </summary>
        public required bool Complete { get; init; }

        public static readonly DirectoryListing Incomplete = new()
        {
            Ordinal = FrozenDictionary<string, Entry>.Empty,
            Complete = false,
        };
    }

    private static readonly ConcurrentDictionary<string, DirectoryListing> _directories =
        new(HostFsPath.Comparer);

    // Volume root (or the deepest probed directory) -> whether names fold case.
    private static readonly ConcurrentDictionary<string, bool> _caseFolding =
        new(HostFsPath.Comparer);

    private static int _indexedEntries;

    internal static bool Enabled { get; } = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_DIR_INDEX"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Describes <paramref name="hostPath"/> from its parent directory's listing,
    /// building that listing on first use.
    /// </summary>
    internal static Entry Describe(string hostPath)
    {
        if (!Enabled || string.IsNullOrEmpty(hostPath))
        {
            return default;
        }

        string? directory;
        string name;
        try
        {
            directory = Path.GetDirectoryName(hostPath);
            name = Path.GetFileName(hostPath);
        }
        catch (ArgumentException)
        {
            return default;
        }

        if (string.IsNullOrEmpty(directory) || name.Length == 0)
        {
            // A volume root has no parent to list, and the mount roots
            // themselves are verified by the caller's containment check.
            return default;
        }

        var listing = GetListing(directory);
        if (listing.Ordinal.TryGetValue(name, out var entry))
        {
            return entry;
        }

        if (listing.IgnoreCase is { } folded && folded.TryGetValue(name, out entry))
        {
            return entry;
        }

        // Only an ASCII name is known to have no other spelling the host would
        // accept; a non-ASCII one may differ by normalization or by a case
        // table the ignore-case map does not share, so the caller stats it.
        return listing.Complete && System.Text.Ascii.IsValid(name)
            ? new Entry(Verdict.Absent, 0)
            : default;
    }

    /// <summary>
    /// Drops the listing that would describe <paramref name="hostPath"/>, after a
    /// create, delete, rename or truncate changed what the host holds there.
    /// </summary>
    internal static void Invalidate(string hostPath)
    {
        if (!Enabled || string.IsNullOrEmpty(hostPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(hostPath);
            if (!string.IsNullOrEmpty(directory) &&
                _directories.TryRemove(directory, out var removed))
            {
                Interlocked.Add(ref _indexedEntries, -removed.Ordinal.Count);
            }

            // The path itself may be a directory whose own listing is now stale.
            if (_directories.TryRemove(hostPath, out var self))
            {
                Interlocked.Add(ref _indexedEntries, -self.Ordinal.Count);
            }
        }
        catch (ArgumentException)
        {
            // An unusable path indexes nothing, so there is nothing to drop.
        }
    }

    /// <summary>Test hook: forget every listing and case-folding probe.</summary>
    internal static void ClearForTests()
    {
        _directories.Clear();
        _caseFolding.Clear();
        Volatile.Write(ref _indexedEntries, 0);
    }

    private static DirectoryListing GetListing(string directory)
    {
        if (_directories.TryGetValue(directory, out var cached))
        {
            return cached;
        }

        var listing = BuildListing(directory);
        // A racing build of the same directory just loses its copy; both are
        // equivalent, and double-counting the cap is corrected on the discard.
        if (_directories.TryAdd(directory, listing))
        {
            Interlocked.Add(ref _indexedEntries, listing.Ordinal.Count);
            return listing;
        }

        return _directories.TryGetValue(directory, out cached) ? cached : listing;
    }

    private static DirectoryListing BuildListing(string directory)
    {
        if (Volatile.Read(ref _indexedEntries) >= MaxIndexedEntries)
        {
            return DirectoryListing.Incomplete;
        }

        var entries = new Dictionary<string, Entry>(HostFsPath.Comparer);
        try
        {
            var enumerable = new FileSystemEnumerable<(string Name, Entry Entry)>(
                directory,
                static (ref FileSystemEntry entry) =>
                {
                    var attributes = entry.Attributes;
                    var verdict = (attributes & FileAttributes.ReparsePoint) != 0
                        ? Verdict.Symlink
                        : entry.IsDirectory
                            ? Verdict.Directory
                            : Verdict.File;
                    var length = verdict == Verdict.File ? entry.Length : 0;
                    return (entry.FileName.ToString(), new Entry(verdict, length));
                },
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    // The guest filesystem has no notion of hidden or system
                    // entries, so every name the host holds must be visible.
                    AttributesToSkip = 0,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                });

            foreach (var (name, entry) in enumerable)
            {
                if (entries.Count >= MaxDirectoryEntries)
                {
                    return DirectoryListing.Incomplete;
                }

                // A case-folding host can only hand back one spelling per name,
                // so a collision here means the host is case-sensitive and both
                // files are real. Keeping the first is enough: the ordinal map
                // holds each spelling under its own key.
                entries[name] = entry;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            // The directory may not exist, may be a file, or may be unreadable.
            // None of that is an error here: the caller stats the path itself.
            return DirectoryListing.Incomplete;
        }

        var ordinal = entries.ToFrozenDictionary(HostFsPath.Comparer);
        FrozenDictionary<string, Entry>? ignoreCase = null;
        if (FoldsCase(directory, entries))
        {
            var folded = new Dictionary<string, Entry>(
                entries.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (name, entry) in entries)
            {
                folded[name] = entry;
            }

            ignoreCase = folded.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        return new DirectoryListing
        {
            Ordinal = ordinal,
            IgnoreCase = ignoreCase,
            Complete = true,
        };
    }

    // POSIX _PC_CASE_SENSITIVE as macOS numbers it. Linux does not define the
    // name at all, which pathconf reports as EINVAL (-1).
    private const int PcCaseSensitive = 11;

    [LibraryImport("libc", EntryPoint = "pathconf", StringMarshalling = StringMarshalling.Utf8)]
    private static partial long PathConf(string path, int name);

    /// <summary>
    /// Whether the volume holding <paramref name="directory"/> folds name case.
    /// Probed once per directory and memoized.
    /// </summary>
    private static bool FoldsCase(string directory, Dictionary<string, Entry> entries)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (_caseFolding.TryGetValue(directory, out var folds))
        {
            return folds;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                // 0 = names are folded, 1 = distinct by case, -1 = no answer.
                var answer = PathConf(directory, PcCaseSensitive);
                if (answer >= 0)
                {
                    folds = answer == 0;
                    _caseFolding[directory] = folds;
                    return folds;
                }
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }

        // No query answered, so ask the filesystem itself: a listed name with
        // its case swapped either resolves (the volume folds) or does not.
        foreach (var (name, entry) in entries)
        {
            if (entry.Verdict is not (Verdict.File or Verdict.Directory))
            {
                continue;
            }

            var swapped = SwapAsciiCase(name);
            if (swapped is null)
            {
                continue;
            }

            if (entries.ContainsKey(swapped))
            {
                // Both spellings are listed, so they are distinct names.
                folds = false;
            }
            else
            {
                var probe = Path.Combine(directory, swapped);
                folds = entry.Verdict == Verdict.Directory
                    ? System.IO.Directory.Exists(probe)
                    : System.IO.File.Exists(probe);
            }

            _caseFolding[directory] = folds;
            return folds;
        }

        // No listed name has an ASCII letter, so no ASCII lookup can fold onto
        // one; the next listing of this directory probes again.
        return false;
    }

    private static string? SwapAsciiCase(string name)
    {
        var changed = false;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsAsciiLetterLower(c))
            {
                chars[i] = char.ToUpperInvariant(c);
                changed = true;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                chars[i] = char.ToLowerInvariant(c);
                changed = true;
            }
        }

        return changed ? new string(chars) : null;
    }
}
