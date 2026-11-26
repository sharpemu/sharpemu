// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A PS5 package served as /app0 without installing it. The tree is what Extract would write: the
// container's sce_sys files first, then the inner image's files, which win on a name clash. File
// bytes are decoded from the inner image on demand, one block at a time, into a bounded LRU cache.
// The cache lock is never held while decoding; threads that want a block somebody is already
// decoding wait for that decode. A file read forward in sequence also decodes the next few blocks
// on the thread pool, because one thread decodes about 200 MB/s and a game streams faster.
public sealed class Ps5PackageSource : IApp0Source
{
    // Decoded blocks kept at once (a block is up to 256 KiB).
    private const long DefaultCacheBytes = 256L << 20;
    // Blocks decoded ahead of a sequential reader, and the cap on such decodes running at once.
    private const int DefaultReadAhead = 8;
    private const string StatsVariable = "SHARPEMU_PKG_STATS";

    private readonly Ps5Package _package;
    private readonly Ps5InnerImage _image;
    private readonly List<Node> _nodes = [];
    private readonly long _cacheBytesLimit;
    private readonly int _readAhead;
    private readonly ThreadLocal<Ps5InnerImage.BlockReader> _readers;

    private readonly object _gate = new();
    private readonly Dictionary<int, CacheEntry> _entries = [];
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _cacheBytes;

    // End offset of the last read of each file, to tell a sequential reader from a random one.
    private readonly ConcurrentDictionary<int, ulong> _cursors = new();
    private int _readAheadInFlight;
    private volatile bool _disposed;

    private long _decodedBytes;
    private long _decodeTicks;
    private long _hits;
    private long _misses;
    private long _readAheadStarted;

    private sealed class Node
    {
        public required string Name { get; init; }

        public bool IsFile { get; set; }

        public long Size { get; set; }

        // Inner files: logical offset in the inner image. Metadata files: Data holds the bytes.
        public long Logical { get; set; }

        public byte[]? Data { get; set; }

        public List<int> Children { get; } = [];

        public Dictionary<string, int> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheEntry(int index)
    {
        public int Index { get; } = index;

        public byte[]? Data { get; set; }

        public LinkedListNode<CacheEntry>? LruNode { get; set; }

        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Ps5PackageSource(string path, Ps5Package package, long cacheBytes, int readAhead)
    {
        Path = path;
        _package = package;
        _cacheBytesLimit = Math.Max(cacheBytes, 1);
        _readAhead = Math.Max(readAhead, 0);
        var (image, files) = package.LoadTree();
        _image = image;
        _readers = new ThreadLocal<Ps5InnerImage.BlockReader>(image.CreateReader);

        _nodes.Add(new Node { Name = string.Empty });
        foreach (var file in package.MetadataFiles)
        {
            // The same name check and sce_sys root as Extract.
            var components = file.Name.Split('/');
            PkgPackage.ResolveSafe(string.Empty, components);
            AddFile(["sce_sys", .. components], file.Bytes.Length, 0, file.Bytes);
        }

        foreach (var file in files)
        {
            PkgPackage.ResolveSafe(string.Empty, file.Components);
            AddFile(file.Components, file.Size, file.Logical, null);
        }
    }

    // Test hook: builds a source with extra inner files, as a hostile or odd package might list.
    internal Ps5PackageSource(string path, Ps5Package package, IEnumerable<Ps5InnerFile> extraFiles, long cacheBytes, int readAhead)
        : this(path, package, cacheBytes, readAhead)
    {
        foreach (var file in extraFiles)
        {
            PkgPackage.ResolveSafe(string.Empty, file.Components);
            AddFile(file.Components, file.Size, file.Logical, null);
        }
    }

    public string Path { get; }

    // True for a readable .pkg file with the PS5 ("\x7FFIH") magic.
    public static bool IsPackagePath(string? path)
    {
        if (path is null || !string.Equals(System.IO.Path.GetExtension(path), ".pkg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == 4 && magic.SequenceEqual<byte>([0x7F, 0x46, 0x49, 0x48]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Opens a PS5 base game package for mounting. PS4 packages, patches and DLC are refused with a
    // message that says to install them.
    public static Ps5PackageSource Open(string path) => Open(path, DefaultCacheBytes, DefaultReadAhead);

    internal static Ps5PackageSource Open(string path, long cacheBytes, int readAhead)
    {
        var package = GamePackage.Open(path);
        if (package is not Ps5Package ps5)
        {
            package.Dispose();
            throw new InvalidDataException($"'{path}' is a PS4 package: only PS5 packages can run without installing. Install it instead.");
        }

        try
        {
            if (!ps5.IsGameApplication)
            {
                throw new InvalidDataException($"'{path}' is not a base game package: patches and DLC must be installed.");
            }

            return new Ps5PackageSource(path, ps5, cacheBytes, readAhead);
        }
        catch
        {
            ps5.Dispose();
            throw;
        }
    }

    // Adds a file below the root, creating its directories. A later file replaces an earlier one
    // at the same path (names compare without case), as Extract overwrites it.
    private void AddFile(string[] components, long size, long logical, byte[]? data)
    {
        var current = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var name = components[i];
            var directory = _nodes[current];
            var last = i == components.Length - 1;
            if (directory.ByName.TryGetValue(name, out var existing))
            {
                var node = _nodes[existing];
                if (last)
                {
                    if (!node.IsFile)
                    {
                        throw new InvalidDataException($"'{string.Join('/', components)}' is both a file and a directory in the package.");
                    }

                    node.Size = size;
                    node.Logical = logical;
                    node.Data = data;
                    return;
                }

                if (node.IsFile)
                {
                    throw new InvalidDataException($"'{string.Join('/', components)}' is below a file in the package.");
                }

                current = existing;
                continue;
            }

            var child = new Node { Name = name, IsFile = last, Size = last ? size : 0, Logical = last ? logical : 0, Data = last ? data : null };
            _nodes.Add(child);
            var childIndex = _nodes.Count - 1;
            directory.Children.Add(childIndex);
            directory.ByName[name] = childIndex;
            current = childIndex;
        }
    }

    public int LookUp(string path)
    {
        var current = 0;
        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = _nodes[current];
            if (directory.IsFile || !directory.ByName.TryGetValue(part, out current))
            {
                return IApp0Source.InvalidNode;
            }
        }

        return current;
    }

    public bool IsFile(int node) => (uint)node < (uint)_nodes.Count && _nodes[node].IsFile;

    public bool IsDirectory(int node) => (uint)node < (uint)_nodes.Count && !_nodes[node].IsFile;

    public ulong GetFileSize(int node) => IsFile(node) ? (ulong)_nodes[node].Size : 0;

    public IReadOnlyList<ZArchive.Entry> GetEntries(int directory)
    {
        if (!IsDirectory(directory))
        {
            return [];
        }

        var children = _nodes[directory].Children;
        var entries = new List<ZArchive.Entry>(children.Count);
        foreach (var index in children)
        {
            var child = _nodes[index];
            entries.Add(new ZArchive.Entry(child.Name, !child.IsFile, child.IsFile ? (ulong)child.Size : 0));
        }

        return entries;
    }

    public int Read(int file, ulong offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsFile(file))
        {
            return 0;
        }

        var node = _nodes[file];
        if (offset >= (ulong)node.Size || destination.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min((ulong)destination.Length, (ulong)node.Size - offset);
        if (node.Data is { } data)
        {
            data.AsSpan((int)offset, count).CopyTo(destination);
            return count;
        }

        var sequential = _cursors.TryGetValue(file, out var cursor) && cursor == offset;
        _cursors[file] = offset + (ulong)count;

        var position = node.Logical + (long)offset;
        var fileEnd = node.Logical + node.Size;
        var readEnd = position + count;
        var scheduled = -1;
        var done = 0;
        while (done < count)
        {
            var index = _image.FindBlock(position);
            var block = _image.GetBlock(index);
            var within = (int)(position - block.Logical);
            var length = (int)Math.Min(block.Uncompressed - (long)within, count - done);
            if (_readAhead > 0)
            {
                // A forward reader gets blocks past this read too; a one-off read only the blocks
                // it needs itself, in parallel.
                scheduled = ReadAhead(index, scheduled, sequential ? fileEnd : readEnd);
            }

            if (block.Compressed == 0)
            {
                destination.Slice(done, length).Clear();
            }
            else
            {
                GetBlock(index).AsSpan(within, length).CopyTo(destination[done..]);
            }

            done += length;
            position += length;
        }

        return count;
    }

    // Starts decoding the blocks after `index` (up to _readAhead of them, none past the logical
    // `limit`) that nobody has asked for yet. Returns the highest block index handled so far.
    private int ReadAhead(int index, int scheduled, long limit)
    {
        var first = Math.Max(index + 1, scheduled + 1);
        var last = Math.Min(index + _readAhead, _image.BlockCount - 1);
        for (var next = first; next <= last; next++)
        {
            var block = _image.GetBlock(next);
            if (block.Logical >= limit)
            {
                break;
            }

            if (block.Compressed != 0 && !StartReadAhead(next))
            {
                // Over the in-flight cap: try again from here on the next block.
                return next - 1;
            }

            scheduled = next;
        }

        return Math.Max(scheduled, index);
    }

    private bool StartReadAhead(int index)
    {
        if (Interlocked.Increment(ref _readAheadInFlight) > _readAhead)
        {
            Interlocked.Decrement(ref _readAheadInFlight);
            return false;
        }

        var entry = Acquire(index, out var owner, out _, countStats: false);
        if (!owner)
        {
            Interlocked.Decrement(ref _readAheadInFlight);
            return true;
        }

        Interlocked.Increment(ref _readAheadStarted);
        ThreadPool.UnsafeQueueUserWorkItem(
            _ =>
            {
                try
                {
                    if (!_disposed)
                    {
                        Decode(entry);
                    }
                    else
                    {
                        Abandon(entry, new ObjectDisposedException(nameof(Ps5PackageSource)));
                    }
                }
                catch (Exception)
                {
                    // The entry is gone from the cache; a reader that needs the block decodes it
                    // again and reports the failure itself.
                    _ = entry.Completion.Task.Exception;
                }
                finally
                {
                    Interlocked.Decrement(ref _readAheadInFlight);
                }
            },
            null);
        return true;
    }

    // The decoded bytes of a block, from the cache, from a decode another thread is running, or
    // decoded here.
    private byte[] GetBlock(int index)
    {
        var entry = Acquire(index, out var owner, out var ready, countStats: true);
        if (ready is not null)
        {
            return ready;
        }

        return owner ? Decode(entry) : entry.Completion.Task.GetAwaiter().GetResult();
    }

    // Finds or creates the cache entry of a block. The caller that creates it owns the decode.
    private CacheEntry Acquire(int index, out bool owner, out byte[]? ready, bool countStats)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(index, out var entry))
            {
                owner = false;
                ready = entry.Data;
                if (entry.LruNode is { } node && node.List is not null)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }

                if (countStats)
                {
                    _hits++;
                }

                return entry;
            }

            entry = new CacheEntry(index);
            _entries.Add(index, entry);
            owner = true;
            ready = null;
            if (countStats)
            {
                _misses++;
            }

            return entry;
        }
    }

    private byte[] Decode(CacheEntry entry)
    {
        try
        {
            var buffer = new byte[_image.GetBlock(entry.Index).Uncompressed];
            var started = Stopwatch.GetTimestamp();
            _readers.Value!.DecodeBlock(entry.Index, buffer);
            Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref _decodedBytes, buffer.Length);

            lock (_gate)
            {
                entry.Data = buffer;
                entry.LruNode = _lru.AddFirst(entry);
                _cacheBytes += buffer.Length;
                // Keep at least the block just decoded, whatever the limit.
                while (_cacheBytes > _cacheBytesLimit && _lru.Last is { } oldest && oldest != entry.LruNode)
                {
                    _lru.RemoveLast();
                    _entries.Remove(oldest.Value.Index);
                    _cacheBytes -= oldest.Value.Data!.Length;
                }
            }

            entry.Completion.SetResult(buffer);
            return buffer;
        }
        catch (Exception exception)
        {
            Abandon(entry, exception);
            throw;
        }
    }

    // Drops a failed entry so the next reader retries, and wakes the threads waiting on it.
    private void Abandon(CacheEntry entry, Exception exception)
    {
        lock (_gate)
        {
            _entries.Remove(entry.Index);
        }

        entry.Completion.TrySetException(exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Read-ahead workers use the package file; wait for them (bounded) before closing it.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (Volatile.Read(ref _readAheadInFlight) > 0 && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(1);
        }

        if (Environment.GetEnvironmentVariable(StatsVariable) == "1")
        {
            var decodeMs = _decodeTicks * 1000.0 / Stopwatch.Frequency;
            var mib = _decodedBytes / (1024.0 * 1024);
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[PKG] decoded {mib:0.0} MiB in {decodeMs:0} ms of decode time ({(decodeMs > 0 ? mib * 1000 / decodeMs : 0):0} MiB/s per thread); cache hits {_hits}, misses {_misses}, read-ahead decodes {_readAheadStarted}"));
        }

        _readers.Dispose();
        _package.Dispose();
    }
}
