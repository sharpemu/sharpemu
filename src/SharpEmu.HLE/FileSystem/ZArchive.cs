// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace SharpEmu.HLE.FileSystem;

// A read-only ZArchive (.zar): a game folder packed into zstd-compressed 64 KiB blocks with an index,
// so any byte range reads without unpacking the archive. Format by Exzap
// (https://github.com/Exzap/ZArchive, MIT No Attribution); every multi-byte field is big-endian.
//
// Layout, read from the footer at the end of the file:
//   compressed data   blocks of 64 KiB once decompressed; a block stored at exactly 64 KiB is raw
//   offset records    per 16 blocks: the first block's offset (u64) and each block's size - 1 (u16)
//   names             per node: a 1-byte length (or 2 bytes when the top bit is set) and the bytes
//   file tree         16-byte nodes; node 0 is the unnamed root directory
public sealed class ZArchive : IDisposable
{
    public const int BlockSize = 64 * 1024;
    private const int BlocksPerOffsetRecord = 16;
    private const int OffsetRecordBytes = 8 + 2 * BlocksPerOffsetRecord;
    private const int NodeBytes = 16;
    private const int FooterBytes = 16 * 6 + 32 + 8 + 4 + 4;
    private const uint FooterMagic = 0x169F52D6;
    private const uint FooterVersion1 = 0x61BF3A01;
    private const uint FileFlag = 0x80000000;
    private const uint NameOffsetMask = 0x7FFFFFFF;
    private const int CacheBlocks = 64;

    public const int InvalidNode = -1;

    private readonly FileStream _stream;
    private readonly OffsetRecord[] _offsetRecords;
    private readonly byte[] _names;
    private readonly Node[] _nodes;
    private readonly ulong _dataOffset;
    private readonly ulong _dataSize;
    private readonly object _gate = new();
    private readonly Decompressor _decompressor = new();
    private readonly byte[] _compressed = new byte[BlockSize];
    // Decompressed blocks, least recently used first.
    private readonly LinkedList<(ulong Block, byte[] Data)> _cache = new();
    private readonly Dictionary<ulong, LinkedListNode<(ulong Block, byte[] Data)>> _cached = new();

    private readonly record struct OffsetRecord(ulong BaseOffset, ushort[] Sizes);

    private readonly record struct Node(uint NameAndType, uint Word0, uint Word1, uint Word2)
    {
        public bool IsFile => (NameAndType & FileFlag) != 0;

        public uint NameOffset => NameAndType & NameOffsetMask;

        public ulong FileOffset => Word0 | ((ulong)(Word2 & 0xFFFF) << 32);

        public ulong FileSize => Word1 | ((ulong)(Word2 & 0xFFFF0000) << 16);

        public uint FirstChild => Word0;

        public uint ChildCount => Word1;
    }

    public readonly record struct Entry(string Name, bool IsDirectory, ulong Size);

    public string Path { get; }

    private ZArchive(string path, FileStream stream, OffsetRecord[] offsetRecords, byte[] names, Node[] nodes, ulong dataOffset, ulong dataSize)
    {
        Path = path;
        _stream = stream;
        _offsetRecords = offsetRecords;
        _names = names;
        _nodes = nodes;
        _dataOffset = dataOffset;
        _dataSize = dataSize;
    }

    public static bool IsArchivePath(string? path) =>
        path is not null && path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    // Null when the file is not a well-formed version-1 archive.
    public static ZArchive? Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        try
        {
            if (TryOpen(path, stream) is { } archive)
            {
                return archive;
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException)
        {
        }

        stream.Dispose();
        return null;
    }

    private static ZArchive? TryOpen(string path, FileStream stream)
    {
        var length = (ulong)stream.Length;
        if (length <= FooterBytes)
        {
            return null;
        }

        Span<byte> footer = stackalloc byte[FooterBytes];
        ReadExactly(stream, length - FooterBytes, footer);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(footer[(FooterBytes - 4)..]);
        var version = BinaryPrimitives.ReadUInt32BigEndian(footer[(FooterBytes - 8)..]);
        var totalSize = BinaryPrimitives.ReadUInt64BigEndian(footer[(FooterBytes - 16)..]);
        if (magic != FooterMagic || version != FooterVersion1 || totalSize != length)
        {
            return null;
        }

        // Six (offset, size) pairs: compressed data, offset records, names, file tree, metadata
        // directory and metadata; the last two are unused.
        var sections = new (ulong Offset, ulong Size)[6];
        for (var index = 0; index < sections.Length; index++)
        {
            var offset = BinaryPrimitives.ReadUInt64BigEndian(footer[(index * 16)..]);
            var size = BinaryPrimitives.ReadUInt64BigEndian(footer[(index * 16 + 8)..]);
            if (offset > length || size > length - offset)
            {
                return null;
            }

            sections[index] = (offset, size);
        }

        var (dataOffset, dataSize) = sections[0];
        var (recordsOffset, recordsSize) = sections[1];
        var (namesOffset, namesSize) = sections[2];
        var (treeOffset, treeSize) = sections[3];
        if (recordsSize == 0 || recordsSize % OffsetRecordBytes != 0 || recordsSize > uint.MaxValue ||
            namesSize > int.MaxValue || treeSize == 0 || treeSize % NodeBytes != 0 || treeSize > int.MaxValue)
        {
            return null;
        }

        var recordBytes = new byte[recordsSize];
        ReadExactly(stream, recordsOffset, recordBytes);
        var records = new OffsetRecord[recordsSize / OffsetRecordBytes];
        for (var index = 0; index < records.Length; index++)
        {
            var record = recordBytes.AsSpan(index * OffsetRecordBytes, OffsetRecordBytes);
            var sizes = new ushort[BlocksPerOffsetRecord];
            for (var block = 0; block < sizes.Length; block++)
            {
                sizes[block] = BinaryPrimitives.ReadUInt16BigEndian(record[(8 + block * 2)..]);
            }

            records[index] = new OffsetRecord(BinaryPrimitives.ReadUInt64BigEndian(record), sizes);
        }

        var names = new byte[namesSize];
        ReadExactly(stream, namesOffset, names);
        var treeBytes = new byte[treeSize];
        ReadExactly(stream, treeOffset, treeBytes);
        var nodes = new Node[treeSize / NodeBytes];
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = treeBytes.AsSpan(index * NodeBytes, NodeBytes);
            nodes[index] = new Node(
                BinaryPrimitives.ReadUInt32BigEndian(node),
                BinaryPrimitives.ReadUInt32BigEndian(node[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(node[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(node[12..]));
        }

        if (nodes[0].IsFile || NameOf(names, nodes[0].NameOffset) is not { Length: 0 })
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (!node.IsFile && ((ulong)node.FirstChild + node.ChildCount > (ulong)nodes.Length))
            {
                return null;
            }
        }

        return new ZArchive(path, stream, records, names, nodes, dataOffset, dataSize);
    }

    // The node at the path, with '/' or '\' separators and names compared without case;
    // the root for an empty path; InvalidNode when it is missing.
    public int LookUp(string path)
    {
        var current = 0;
        foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = _nodes[current];
            if (directory.IsFile)
            {
                return InvalidNode;
            }

            var match = InvalidNode;
            for (var child = directory.FirstChild; child < directory.FirstChild + directory.ChildCount; child++)
            {
                if (string.Equals(NameOf(_names, _nodes[child].NameOffset), part, StringComparison.OrdinalIgnoreCase))
                {
                    match = (int)child;
                    break;
                }
            }

            if (match == InvalidNode)
            {
                return InvalidNode;
            }

            current = match;
        }

        return current;
    }

    public bool IsFile(int node) => (uint)node < (uint)_nodes.Length && _nodes[node].IsFile;

    public bool IsDirectory(int node) => (uint)node < (uint)_nodes.Length && !_nodes[node].IsFile;

    public ulong GetFileSize(int node) => IsFile(node) ? _nodes[node].FileSize : 0;

    public IReadOnlyList<Entry> GetEntries(int directory)
    {
        if (!IsDirectory(directory))
        {
            return [];
        }

        var node = _nodes[directory];
        var entries = new List<Entry>((int)node.ChildCount);
        for (var child = node.FirstChild; child < node.FirstChild + node.ChildCount; child++)
        {
            var entry = _nodes[child];
            if (NameOf(_names, entry.NameOffset) is { Length: > 0 } name)
            {
                entries.Add(new Entry(name, !entry.IsFile, entry.IsFile ? entry.FileSize : 0));
            }
        }

        return entries;
    }

    // Copies file bytes from offset into destination; returns the count read, short at the end of
    // the file. Throws InvalidDataException for a damaged block.
    public int Read(int file, ulong offset, Span<byte> destination)
    {
        if (!IsFile(file))
        {
            return 0;
        }

        var node = _nodes[file];
        if (offset >= node.FileSize)
        {
            return 0;
        }

        var count = (int)Math.Min((ulong)destination.Length, node.FileSize - offset);
        var position = node.FileOffset + offset;
        var written = 0;
        lock (_gate)
        {
            while (written < count)
            {
                var block = position / BlockSize;
                var within = (int)(position % BlockSize);
                var step = Math.Min(count - written, BlockSize - within);
                LoadBlock(block).AsSpan(within, step).CopyTo(destination[written..]);
                written += step;
                position += (ulong)step;
            }
        }

        return written;
    }

    private byte[] LoadBlock(ulong block)
    {
        if (_cached.TryGetValue(block, out var cached))
        {
            _cache.Remove(cached);
            _cache.AddLast(cached);
            return cached.Value.Data;
        }

        var recordIndex = block / BlocksPerOffsetRecord;
        if (recordIndex >= (ulong)_offsetRecords.Length)
        {
            throw new InvalidDataException($"The archive block {block} is past its offset records.");
        }

        var record = _offsetRecords[recordIndex];
        var sub = (int)(block % BlocksPerOffsetRecord);
        var offset = record.BaseOffset;
        for (var index = 0; index < sub; index++)
        {
            offset += (ulong)record.Sizes[index] + 1;
        }

        var size = record.Sizes[sub] + 1;
        if (offset + (ulong)size > _dataSize)
        {
            throw new InvalidDataException($"The archive block {block} lies outside the compressed data.");
        }

        byte[] data;
        LinkedListNode<(ulong Block, byte[] Data)> slot;
        if (_cache.Count >= CacheBlocks)
        {
            slot = _cache.First!;
            _cache.RemoveFirst();
            _cached.Remove(slot.Value.Block);
            data = slot.Value.Data;
        }
        else
        {
            data = new byte[BlockSize];
            slot = new LinkedListNode<(ulong Block, byte[] Data)>((block, data));
        }

        if (size == BlockSize)
        {
            ReadExactly(_stream, _dataOffset + offset, data);
        }
        else
        {
            ReadExactly(_stream, _dataOffset + offset, _compressed.AsSpan(0, size));
            if (!_decompressor.TryUnwrap(_compressed.AsSpan(0, size), data, out var produced) || produced != BlockSize)
            {
                throw new InvalidDataException($"The archive block {block} does not decompress to {BlockSize} bytes.");
            }
        }

        slot.Value = (block, data);
        _cache.AddLast(slot);
        _cached[block] = slot;
        return data;
    }

    // The name at the offset; empty for a missing or malformed one. A length of 128 or more takes a
    // second byte: (first & 0x7F) | (second << 7), as the archive writer stores it.
    private static string NameOf(byte[] names, uint offset)
    {
        if (offset == NameOffsetMask || offset >= (uint)names.Length)
        {
            return string.Empty;
        }

        var position = (int)offset;
        var length = names[position] & 0x7F;
        if ((names[position] & 0x80) != 0)
        {
            if (position + 1 >= names.Length)
            {
                return string.Empty;
            }

            length |= names[position + 1] << 7;
            position += 2;
        }
        else
        {
            position++;
        }

        return position + length > names.Length ? string.Empty : Encoding.UTF8.GetString(names, position, length);
    }

    private static void ReadExactly(FileStream stream, ulong offset, Span<byte> destination)
    {
        if (RandomAccess.Read(stream.SafeFileHandle, destination, (long)offset) != destination.Length)
        {
            throw new IOException("The archive ended before a section it lists.");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _decompressor.Dispose();
    }
}
