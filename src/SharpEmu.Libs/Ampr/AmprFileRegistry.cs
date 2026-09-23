// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Ampr;

internal static class AmprFileRegistry
{
    private const uint CacheMagicV2 = 0x32495041u; // 'API2'
    private const uint CacheVersionV2 = 2;
    private const uint CacheMagicV3 = 0x33495041u; // 'API3'
    private const uint CacheVersionV3 = 3;

    // Which spelling produced a compatibility id. When two files collide on one
    // 31-bit id, the better-ranked spelling owns it; only a tie is ambiguous.
    // Ranking makes the outcome independent of publish order, which the
    // parallel app0 index and concurrent guest resolves do not guarantee.
    private enum AliasRank : byte
    {
        // The title resolved this exact guest path through APR.
        Resolved,
        // "$/" and "/app0/": spellings titles bake into asset tables.
        GuestPath,
        // "app0/" and bare relative paths: emulator-side fallbacks.
        Compatibility,
    }

    private readonly record struct AliasEntry(string HostPath, AliasRank Rank, bool Ambiguous);

    private static readonly ConcurrentDictionary<uint, AliasEntry> _hostPathsById = new(
        concurrencyLevel: Math.Max(4, Environment.ProcessorCount),
        capacity: 1_048_576);
    private static readonly object _indexGate = new();
    private static readonly object _resolvedFileGate = new();
    private static readonly Dictionary<string, uint> _resolvedIdsByPath = new(HostFsPath.Comparer);
    private static readonly ConcurrentDictionary<uint, string> _resolvedPathsById = new();
    // Resolved handles use a separate range from the 31-bit compatibility hashes.
    // Never reuse a handle while queued reads can still refer to it.
    private static uint _nextResolvedId = 0x80000000;
    private static string? _indexedApp0Root;
    private static string? _indexingApp0Root;
    private static int _preloadStarted;

    public static uint Register(string guestPath, string hostPath)
    {
        var resolvedPathKey = Path.GetFullPath(hostPath);
        if (TryGetApp0Relative(guestPath, out var relative) && relative.Length != 0)
        {
            RegisterApp0Relative(relative, hostPath);
        }
        lock (_resolvedFileGate)
        {
            if (_resolvedIdsByPath.TryGetValue(resolvedPathKey, out var existingId))
                return existingId;
            if (_nextResolvedId == uint.MaxValue)
                throw new InvalidOperationException("APR file identifiers are exhausted.");
            var resolvedId = _nextResolvedId++;
            _resolvedPathsById[resolvedId] = hostPath;
            _resolvedIdsByPath.Add(resolvedPathKey, resolvedId);
            return resolvedId;
        }
    }

    public static uint RegisterAprResolvedPath(string guestPath, string hostPath)
    {
        if (TryGetApp0Relative(guestPath, out var relative) && relative.Length != 0)
        {
            RegisterApp0Relative(relative, hostPath);
        }

        // APR file ids are part of the guest ABI: ResolveFilepathsToIds returns
        // the 31-bit FNV-1a hash of the guest path. Keep the collision-safe
        // process-local handles used by Register() separate from this path so a
        // title can compare resolved ids with ids baked into its asset tables.
        var fileId = ComputeFileId(guestPath);
        PublishCompatibilityPath(fileId, hostPath, AliasRank.Resolved);
        return fileId;
    }

    public static bool TryGetHostPath(uint id, out string hostPath)
    {
        if ((id & 0x80000000) != 0)
            return _resolvedPathsById.TryGetValue(id, out hostPath!);
        if (_hostPathsById.TryGetValue(id, out var entry) && !entry.Ambiguous)
        {
            hostPath = entry.HostPath;
            return true;
        }

        hostPath = null!;
        return false;
    }

    /// <summary>Test hook: wipe registry state between cases.</summary>
    internal static void ClearForTests()
    {
        lock (_indexGate)
        {
            _hostPathsById.Clear();
            lock (_resolvedFileGate)
            {
                _resolvedIdsByPath.Clear();
                _resolvedPathsById.Clear();
                _nextResolvedId = 0x80000000;
            }
            _indexedApp0Root = null;
            _indexingApp0Root = null;
            _preloadStarted = 0;
        }
    }

    /// <summary>Test hook for the allocation-free alias publisher.</summary>
    internal static void RegisterApp0RelativeForTests(string relative, string hostPath) =>
        RegisterApp0Relative(relative, hostPath);

    /// <summary>
    /// Kick off <see cref="EnsureApp0Indexed"/> on a background thread as soon as
    /// the host knows app0. otherwise pays the full tree walk on the
    /// first cooked-id APR miss mid-boot (~8s under Rosetta for DeS).
    /// </summary>
    public static void BeginApp0IndexPreload(string? app0Root)
    {
        if (string.IsNullOrWhiteSpace(app0Root) || !Directory.Exists(app0Root))
        {
            return;
        }

        if (Interlocked.Exchange(ref _preloadStarted, 1) != 0)
        {
            return;
        }

        var root = app0Root;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                try
                {
                    EnsureApp0Indexed((string)state!);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] ampr.app0_index_preload_failed: {exception.Message}");
                }
            },
            root);
    }

    /// <summary>
    /// Indexes every file under app0 under both <c>$/</c> and <c>/app0/</c> FNV
    /// ids. Cooked asset tables ship precomputed ids; without this walk, a title
    /// that never resolves those paths through APR leaves ReadFile permanently
    /// NOT_FOUND.
    /// </summary>
    public static void EnsureApp0Indexed(string app0Root)
    {
        if (string.IsNullOrWhiteSpace(app0Root) || !Directory.Exists(app0Root))
        {
            return;
        }

        var normalizedRoot = Path.GetFullPath(app0Root);
        var stopwatch = Stopwatch.StartNew();

        lock (_indexGate)
        {
            while (true)
            {
                if (string.Equals(_indexedApp0Root, normalizedRoot, HostFsPath.Comparison))
                {
                    return;
                }

                if (_indexingApp0Root is not null)
                {
                    // Another thread (preload) owns the walk — wait instead of
                    // stacking a second 8s index on the guest APR miss path.
                    if (string.Equals(
                            _indexingApp0Root,
                            normalizedRoot,
                            HostFsPath.Comparison))
                    {
                        Monitor.Wait(_indexGate);
                        continue;
                    }

                    Monitor.Wait(_indexGate, 50);
                    continue;
                }

                _indexingApp0Root = normalizedRoot;
                break;
            }
        }

        try
        {
            var cachePathV3 = GetIndexCachePath(normalizedRoot, version: 3);
            var cachePathV2 = GetIndexCachePath(normalizedRoot, version: 2);
            if (TryLoadIndexCache(normalizedRoot, cachePathV3, preferV3: true, out var cachedFiles))
            {
                lock (_indexGate)
                {
                    _indexedApp0Root = normalizedRoot;
                }

                Console.Error.WriteLine(
                    $"[LOADER][INFO] ampr.app0_index_cache_hit root={normalizedRoot} " +
                    $"files={cachedFiles} ids={_hostPathsById.Count} " +
                    $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
                return;
            }

            if (TryLoadIndexCache(normalizedRoot, cachePathV2, preferV3: false, out cachedFiles))
            {
                lock (_indexGate)
                {
                    _indexedApp0Root = normalizedRoot;
                }

                // Promote v2 (rehash-on-load) to v3 (precomputed ids) so the
                // next boot skips the Rosetta FNV storm.
                TrySaveIndexCache(normalizedRoot, cachePathV3, cachedFiles);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] ampr.app0_index_cache_hit root={normalizedRoot} " +
                    $"files={cachedFiles} ids={_hostPathsById.Count} " +
                    $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1} upgraded=v3");
                return;
            }

            var relatives = new List<string>(256 * 1024);
            try
            {
                foreach (var hostPath in Directory.EnumerateFiles(
                             normalizedRoot,
                             "*",
                             SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(normalizedRoot, hostPath)
                        .Replace('\\', '/');
                    if (string.IsNullOrEmpty(relative) ||
                        relative.StartsWith("..", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    relatives.Add(relative);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The walk is an opportunistic warm-up reached synchronously from
                // sceAmprCommandBufferConstructor; a dump that moves or a mount
                // that hiccups must not fault the guest export. The background
                // preload already swallows this. Leave the root unindexed so a
                // later call retries.
                Console.Error.WriteLine(
                    $"[LOADER][WARN] ampr.app0_index_walk_failed root={normalizedRoot}: {exception.Message}");
                return;
            }

            // Hash + dictionary fill dominates under Rosetta once the walk is
            // done; parallelize across cores without re-walking the tree.
            Parallel.ForEach(
                relatives,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1),
                },
                relative =>
                {
                    var hostPath = Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    RegisterApp0Relative(relative, hostPath);
                });

            lock (_indexGate)
            {
                _indexedApp0Root = normalizedRoot;
            }

            TrySaveIndexCache(normalizedRoot, cachePathV3, relatives.Count);
            Console.Error.WriteLine(
                $"[LOADER][INFO] ampr.app0_indexed root={normalizedRoot} " +
                $"files={relatives.Count} ids={_hostPathsById.Count} " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }
        finally
        {
            lock (_indexGate)
            {
                _indexingApp0Root = null;
                Monitor.PulseAll(_indexGate);
            }
        }
    }

    /// <summary>
    /// Registers the four Insomniac path aliases for one app0-relative file
    /// without allocating intermediate guest-path strings.
    /// </summary>
    private static void RegisterApp0Relative(string relative, string hostPath)
    {
        // "$/" + relative
        Publish(FnvContinueAscii(FnvContinueAscii(OffsetBasis, (byte)'$'), (byte)'/'), relative, hostPath, AliasRank.GuestPath);
        // "/app0/" + relative
        Publish(FnvContinueAsciiPrefix(OffsetBasis, "/app0/"u8), relative, hostPath, AliasRank.GuestPath);
        // "app0/" + relative
        Publish(FnvContinueAsciiPrefix(OffsetBasis, "app0/"u8), relative, hostPath, AliasRank.Compatibility);
        // bare relative
        Publish(OffsetBasis, relative, hostPath, AliasRank.Compatibility);
    }

    private static void Publish(uint hash, string relative, string hostPath, AliasRank rank)
    {
        PublishCompatibilityPath(NormalizeFileId(FnvContinueUtf8(hash, relative)), hostPath, rank);
    }

    private static void PublishCompatibilityPath(uint id, string hostPath, AliasRank rank)
    {
        var incoming = new AliasEntry(hostPath, rank, Ambiguous: false);
        while (true)
        {
            if (!_hostPathsById.TryGetValue(id, out var existing))
            {
                if (_hostPathsById.TryAdd(id, incoming))
                    return;
                continue;
            }

            var merged = MergeAlias(existing, incoming);
            if (merged == existing || _hostPathsById.TryUpdate(id, merged, existing))
                return;
        }
    }

    private static AliasEntry MergeAlias(AliasEntry existing, AliasEntry incoming)
    {
        if (incoming.Rank != existing.Rank)
            return incoming.Rank < existing.Rank ? incoming : existing;
        // A title may re-resolve an id; its latest resolve owns it.
        if (incoming.Rank == AliasRank.Resolved)
            return incoming;
        if (existing.Ambiguous || HostFsPath.Comparer.Equals(existing.HostPath, incoming.HostPath))
            return existing;
        return existing with { Ambiguous = true };
    }

    internal static uint ComputeFileId(string guestPath)
    {
        return NormalizeFileId(FnvContinueUtf8(OffsetBasis, guestPath));
    }

    internal static IEnumerable<string> EnumerateApp0PathAliases(string guestPath)
    {
        if (string.IsNullOrEmpty(guestPath))
        {
            yield break;
        }

        if (!TryGetApp0Relative(guestPath, out var relative) ||
            string.IsNullOrEmpty(relative))
        {
            yield break;
        }

        yield return "$/" + relative;
        yield return "/app0/" + relative;
        yield return "app0/" + relative;
        yield return relative;
    }

    private static bool TryGetApp0Relative(string guestPath, out string relative)
    {
        relative = string.Empty;
        var normalized = guestPath.Replace('\\', '/');

        if (normalized.StartsWith("$/", StringComparison.Ordinal))
        {
            relative = normalized[2..].TrimStart('/');
            return relative.Length != 0;
        }

        if (normalized.StartsWith("/app0/", StringComparison.OrdinalIgnoreCase))
        {
            relative = normalized["/app0/".Length..].TrimStart('/');
            return relative.Length != 0;
        }

        if (normalized.StartsWith("app0/", StringComparison.OrdinalIgnoreCase))
        {
            relative = normalized["app0/".Length..].TrimStart('/');
            return relative.Length != 0;
        }

        // Bare relative paths are treated as app0-relative by ResolveGuestPath.
        if (!normalized.StartsWith('/') &&
            !Path.IsPathFullyQualified(guestPath))
        {
            relative = normalized.TrimStart('/');
            return relative.Length != 0;
        }

        return false;
    }

    private static string GetIndexCachePath(string normalizedRoot, int version)
    {
        var overrideDir = Environment.GetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE");
        var cacheDir = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SharpEmu",
                "ampr-index");
        Directory.CreateDirectory(cacheDir);

        // Distinct roots must not share a cache file. Folding case is only
        // correct where the host filesystem folds it too.
        var rootKey = OperatingSystem.IsWindows() ? normalizedRoot.ToLowerInvariant() : normalizedRoot;
        // Keep raw hashes for cache filenames. Guest file identifiers use 31 bits.
        var rootHash = FnvContinueUtf8(OffsetBasis, rootKey);
        return Path.Combine(cacheDir, $"app0-{rootHash:x8}.v{version}.idx");
    }

    private static bool TryLoadIndexCache(
        string normalizedRoot,
        string cachePath,
        bool preferV3,
        out int fileCount)
    {
        fileCount = 0;
        try
        {
            if (!File.Exists(cachePath))
            {
                return false;
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_AMPR_REINDEX"),
                    "1",
                    StringComparison.Ordinal))
            {
                return false;
            }

            using var stream = File.OpenRead(cachePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var magic = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            var isV3 = magic == CacheMagicV3 && version == CacheVersionV3;
            var isV2 = magic == CacheMagicV2 && version == CacheVersionV2;
            if (preferV3)
            {
                if (!isV3)
                {
                    return false;
                }
            }
            else if (!isV2)
            {
                return false;
            }

            var root = reader.ReadString();
            if (!string.Equals(root, normalizedRoot, HostFsPath.Comparison))
            {
                return false;
            }

            var expectedFiles = reader.ReadInt32();
            var expectedParamTicks = reader.ReadInt64();
            var actualParamTicks = GetParamJsonWriteTicks(normalizedRoot);
            if (actualParamTicks == 0 || actualParamTicks != expectedParamTicks)
            {
                return false;
            }

            if (expectedFiles < 0 || expectedFiles > 8_000_000)
            {
                return false;
            }

            if (isV3)
            {
                // Precomputed FNV ids — no Rosetta hash storm on every boot.
                var entries = new (string Relative, uint Id0, uint Id1, uint Id2, uint Id3)[expectedFiles];
                for (var i = 0; i < expectedFiles; i++)
                {
                    entries[i] = (
                        reader.ReadString(),
                        reader.ReadUInt32(),
                        reader.ReadUInt32(),
                        reader.ReadUInt32(),
                        reader.ReadUInt32());
                }

                Parallel.ForEach(
                    entries,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1),
                    },
                    entry =>
                    {
                        if (string.IsNullOrEmpty(entry.Relative) ||
                            entry.Relative.Contains("..", StringComparison.Ordinal))
                        {
                            return;
                        }

                        var hostPath = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                            ? normalizedRoot + entry.Relative.Replace('/', Path.DirectorySeparatorChar)
                            : normalizedRoot + Path.DirectorySeparatorChar +
                              entry.Relative.Replace('/', Path.DirectorySeparatorChar);
                        // Normalize cached identifiers to the guest's 31-bit range.
                        // Ids are stored in RegisterApp0Relative order: $/ /app0/ app0/ bare.
                        PublishCompatibilityPath(NormalizeFileId(entry.Id0), hostPath, AliasRank.GuestPath);
                        PublishCompatibilityPath(NormalizeFileId(entry.Id1), hostPath, AliasRank.GuestPath);
                        PublishCompatibilityPath(NormalizeFileId(entry.Id2), hostPath, AliasRank.Compatibility);
                        PublishCompatibilityPath(NormalizeFileId(entry.Id3), hostPath, AliasRank.Compatibility);
                    });
            }
            else
            {
                var relatives = new string[expectedFiles];
                for (var i = 0; i < expectedFiles; i++)
                {
                    relatives[i] = reader.ReadString();
                }

                Parallel.ForEach(
                    relatives,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1),
                    },
                    relative =>
                    {
                        if (string.IsNullOrEmpty(relative) ||
                            relative.Contains("..", StringComparison.Ordinal))
                        {
                            return;
                        }

                        var hostPath = Path.Combine(
                            normalizedRoot,
                            relative.Replace('/', Path.DirectorySeparatorChar));
                        RegisterApp0Relative(relative, hostPath);
                    });
            }

            fileCount = expectedFiles;
            return true;
        }
        catch (Exception exception)
        {
            _hostPathsById.Clear();
            Console.Error.WriteLine(
                $"[LOADER][WARN] ampr.app0_index_cache_load_failed: {exception.Message}");
            return false;
        }
    }

    private static void TrySaveIndexCache(
        string normalizedRoot,
        string cachePath,
        int fileCount)
    {
        try
        {
            var paramTicks = GetParamJsonWriteTicks(normalizedRoot);
            if (paramTicks == 0 || fileCount <= 0)
            {
                return;
            }

            var relatives = new HashSet<string>(HostFsPath.Comparer);
            foreach (var entry in _hostPathsById.Values)
            {
                var relative = Path.GetRelativePath(normalizedRoot, entry.HostPath)
                    .Replace('\\', '/');
                if (string.IsNullOrEmpty(relative) ||
                    relative.StartsWith("..", StringComparison.Ordinal))
                {
                    continue;
                }

                relatives.Add(relative);
            }

            var tempPath = cachePath + ".tmp";
            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(CacheMagicV3);
                writer.Write(CacheVersionV3);
                writer.Write(normalizedRoot);
                writer.Write(relatives.Count);
                writer.Write(paramTicks);
                foreach (var relative in relatives)
                {
                    writer.Write(relative);
                    // Mirror RegisterApp0Relative id order: $/ /app0/ app0/ bare.
                    writer.Write(ComputeApp0AliasIds(relative, out var id1, out var id2, out var id3));
                    writer.Write(id1);
                    writer.Write(id2);
                    writer.Write(id3);
                }
            }

            File.Move(tempPath, cachePath, overwrite: true);
            Console.Error.WriteLine(
                $"[LOADER][INFO] ampr.app0_index_cache_saved path={cachePath} " +
                $"files={relatives.Count}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ampr.app0_index_cache_save_failed: {exception.Message}");
        }
    }

    private static uint ComputeApp0AliasIds(
        string relative,
        out uint app0Slash,
        out uint app0,
        out uint bare)
    {
        var dollar = NormalizeFileId(FnvContinueUtf8(
            FnvContinueAscii(FnvContinueAscii(OffsetBasis, (byte)'$'), (byte)'/'),
            relative));
        app0Slash = NormalizeFileId(
            FnvContinueUtf8(FnvContinueAsciiPrefix(OffsetBasis, "/app0/"u8), relative));
        app0 = NormalizeFileId(
            FnvContinueUtf8(FnvContinueAsciiPrefix(OffsetBasis, "app0/"u8), relative));
        bare = NormalizeFileId(FnvContinueUtf8(OffsetBasis, relative));
        return dollar;
    }

    /// <summary>
    /// Cheap dump fingerprint. Full-tree walks are too expensive for cache
    /// validation; param.json changes with title updates. Force a rebuild with
    /// SHARPEMU_AMPR_REINDEX=1 after manual dump edits.
    /// </summary>
    private static long GetParamJsonWriteTicks(string normalizedRoot)
    {
        try
        {
            var paramPath = Path.Combine(normalizedRoot, "sce_sys", "param.json");
            return File.Exists(paramPath)
                ? File.GetLastWriteTimeUtc(paramPath).Ticks
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private const uint OffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NormalizeFileId(uint hash)
    {
        // Keep compatibility hashes in the nonnegative signed 32-bit range.
        return hash & 0x7fffffffu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FnvContinueAscii(uint hash, byte value)
    {
        hash ^= value;
        return hash * FnvPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FnvContinueAsciiPrefix(uint hash, ReadOnlySpan<byte> ascii)
    {
        foreach (var value in ascii)
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static uint FnvContinueUtf8(uint hash, string text)
    {
        // Game asset paths are overwhelmingly ASCII; avoid Encoding.GetBytes
        // allocations on the 223k-file DeS index hot path.
        Span<byte> utf8Scratch = stackalloc byte[4];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x80)
            {
                hash ^= (byte)ch;
                hash *= FnvPrime;
                continue;
            }

            var written = Encoding.UTF8.GetBytes(text.AsSpan(i, 1), utf8Scratch);
            for (var b = 0; b < written; b++)
            {
                hash ^= utf8Scratch[b];
                hash *= FnvPrime;
            }
        }

        return hash;
    }
}
