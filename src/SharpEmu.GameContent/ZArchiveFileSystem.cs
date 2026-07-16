// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace SharpEmu.GameContent;

public sealed class ZArchiveFileSystem : IReadOnlyGameFileSystem
{
    private const int BlockSize = 64 * 1024;
    private const int EntriesPerOffsetRecord = 16;
    private const int OffsetRecordSize = 40;
    private const int TreeEntrySize = 16;
    private const int FooterSize = 144;
    private const int CacheBlockCount = 64;
    private const uint Magic = 0x169F52D6;
    private const uint Version1 = 0x61BF3A01;
    private const uint RootNameOffset = 0x7FFFFFFF;

    private static readonly Encoding NameEncoding = CreateNameEncoding();

    private readonly FileStream _stream;
    private readonly object _gate = new();
    private readonly OffsetRecord[] _offsetRecords;
    private readonly Node[] _nodes;
    private readonly Section _compressedData;
    private readonly DateTime _archiveTimestampUtc;
    private readonly Dictionary<long, CacheEntry> _cache = new();
    private readonly LinkedList<long> _lru = new();
    private bool _disposed;

    internal long DecompressionCount { get; private set; }

    public ZArchiveFileSystem(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        _stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _archiveTimestampUtc = File.GetLastWriteTimeUtc(fullPath);

        try
        {
            if (_stream.Length <= FooterSize)
            {
                throw Invalid("archive is too small to contain a footer");
            }

            Span<byte> footerBytes = stackalloc byte[FooterSize];
            ReadExactlyAt(_stream.Length - FooterSize, footerBytes);
            var footer = ParseFooter(footerBytes);
            ValidateFooter(footer, _stream.Length);

            _compressedData = footer.Sections[0];
            var offsetBytes = ReadSection(footer.Sections[1]);
            var nameBytes = ReadSection(footer.Sections[2]);
            var treeBytes = ReadSection(footer.Sections[3]);
            _offsetRecords = ParseOffsetRecords(offsetBytes);
            _nodes = ParseTree(treeBytes, nameBytes);
            ValidateTreeAndBlocks();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public bool TryGetEntry(string path, out GameFileEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = GamePath.Normalize(path);
        if (!TryResolve(normalized, out var nodeIndex))
        {
            entry = default;
            return false;
        }

        var node = _nodes[nodeIndex];
        entry = new GameFileEntry(
            nodeIndex == 0 ? string.Empty : node.Name,
            !node.IsFile,
            node.IsFile ? checked((long)node.Size) : 0,
            _archiveTimestampUtc);
        return true;
    }

    public IReadOnlyList<GameFileEntry> EnumerateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = GamePath.Normalize(path);
        if (!TryResolve(normalized, out var nodeIndex) || _nodes[nodeIndex].IsFile)
        {
            throw new DirectoryNotFoundException($"Archive directory was not found: {path}");
        }

        var directory = _nodes[nodeIndex];
        var entries = new GameFileEntry[directory.ChildCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var child = _nodes[checked((int)directory.ChildStart + i)];
            entries[i] = new GameFileEntry(
                child.Name,
                !child.IsFile,
                child.IsFile ? checked((long)child.Size) : 0,
                _archiveTimestampUtc);
        }

        return entries;
    }

    public Stream OpenRead(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = GamePath.Normalize(path);
        if (!TryResolve(normalized, out var nodeIndex) || !_nodes[nodeIndex].IsFile)
        {
            throw new FileNotFoundException("Archive file was not found.", normalized);
        }

        var node = _nodes[nodeIndex];
        return new ArchiveFileStream(this, node.Offset, node.Size);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            _lru.Clear();
            _stream.Dispose();
        }
    }

    internal int ReadFile(ulong fileOffset, ulong fileSize, ulong position, Span<byte> destination)
    {
        if (position >= fileSize || destination.IsEmpty)
        {
            return 0;
        }

        var requested = (int)Math.Min((ulong)destination.Length, fileSize - position);
        var rawOffset = checked(fileOffset + position);
        var written = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (written < requested)
            {
                var blockIndex = checked((long)(rawOffset / BlockSize));
                var blockOffset = (int)(rawOffset % BlockSize);
                var count = Math.Min(requested - written, BlockSize - blockOffset);
                var block = GetBlock(blockIndex);
                block.AsSpan(blockOffset, count).CopyTo(destination[written..]);
                rawOffset += (uint)count;
                written += count;
            }
        }

        return written;
    }

    private bool TryResolve(string normalized, out int nodeIndex)
    {
        nodeIndex = 0;
        if (normalized.Length == 0)
        {
            return true;
        }

        foreach (var component in normalized.Split('/'))
        {
            var directory = _nodes[nodeIndex];
            if (directory.IsFile)
            {
                return false;
            }

            var found = -1;
            for (var i = 0; i < directory.ChildCount; i++)
            {
                var candidate = checked((int)directory.ChildStart + i);
                if (AsciiEquals(component, _nodes[candidate].Name))
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            nodeIndex = found;
        }

        return true;
    }

    private byte[] GetBlock(long blockIndex)
    {
        if (_cache.TryGetValue(blockIndex, out var cached))
        {
            _lru.Remove(cached.LruNode);
            _lru.AddLast(cached.LruNode);
            return cached.Data;
        }

        var recordIndex = checked((int)(blockIndex / EntriesPerOffsetRecord));
        var subIndex = (int)(blockIndex % EntriesPerOffsetRecord);
        if ((uint)recordIndex >= (uint)_offsetRecords.Length)
        {
            throw Invalid($"block {blockIndex} is outside the block table");
        }

        var record = _offsetRecords[recordIndex];
        ulong relativeOffset = record.BaseOffset;
        for (var i = 0; i < subIndex; i++)
        {
            relativeOffset = checked(relativeOffset + record.Sizes[i] + 1UL);
        }

        var compressedSize = record.Sizes[subIndex] + 1;
        if (relativeOffset > _compressedData.Size ||
            (ulong)compressedSize > _compressedData.Size - relativeOffset)
        {
            throw Invalid($"compressed block {blockIndex} is outside the data section");
        }

        var compressed = GC.AllocateUninitializedArray<byte>(compressedSize);
        ReadExactlyAt(checked((long)(_compressedData.Offset + relativeOffset)), compressed);
        byte[] data;
        if (compressedSize == BlockSize)
        {
            data = compressed;
        }
        else
        {
            try
            {
                using var decompressor = new Decompressor();
                data = decompressor.Unwrap(compressed).ToArray();
                DecompressionCount++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw Invalid($"zstd decompression failed for block {blockIndex}", ex);
            }

            if (data.Length != BlockSize)
            {
                throw Invalid($"block {blockIndex} decompressed to {data.Length} bytes instead of {BlockSize}");
            }
        }

        if (_cache.Count == CacheBlockCount)
        {
            var oldest = _lru.First!;
            _lru.RemoveFirst();
            _cache.Remove(oldest.Value);
        }

        var lruNode = _lru.AddLast(blockIndex);
        _cache.Add(blockIndex, new CacheEntry(data, lruNode));
        return data;
    }

    private Footer ParseFooter(ReadOnlySpan<byte> bytes)
    {
        var sections = new Section[6];
        for (var i = 0; i < sections.Length; i++)
        {
            var offset = i * 16;
            sections[i] = new Section(
                BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 8)..]));
        }

        return new Footer(
            sections,
            BinaryPrimitives.ReadUInt64BigEndian(bytes[128..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[136..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[140..]));
    }

    private static void ValidateFooter(Footer footer, long fileLength)
    {
        if (footer.Magic != Magic)
        {
            throw Invalid("footer magic is not recognized");
        }

        if (footer.Version != Version1)
        {
            throw new NotSupportedException($"ZArchive version 0x{footer.Version:X8} is not supported.");
        }

        if (footer.TotalSize != (ulong)fileLength)
        {
            throw Invalid("footer total size does not match the archive length");
        }

        var footerOffset = checked((ulong)fileLength - FooterSize);
        foreach (var section in footer.Sections)
        {
            if (section.Offset > footerOffset || section.Size > footerOffset - section.Offset)
            {
                throw Invalid("a footer section is outside the archive data range");
            }
        }

        for (var i = 0; i < footer.Sections.Length; i++)
        {
            if (footer.Sections[i].Size == 0)
            {
                continue;
            }

            for (var j = i + 1; j < footer.Sections.Length; j++)
            {
                if (footer.Sections[j].Size == 0)
                {
                    continue;
                }

                var left = footer.Sections[i];
                var right = footer.Sections[j];
                if (left.Offset < right.Offset + right.Size && right.Offset < left.Offset + left.Size)
                {
                    throw Invalid("footer sections overlap");
                }
            }
        }

        if (footer.Sections[1].Size == 0 || footer.Sections[1].Size % OffsetRecordSize != 0 ||
            footer.Sections[1].Size > int.MaxValue)
        {
            throw Invalid("block offset table has an invalid size");
        }

        if (footer.Sections[2].Size > int.MaxValue)
        {
            throw Invalid("name table is too large for the managed reader");
        }

        if (footer.Sections[3].Size == 0 || footer.Sections[3].Size % TreeEntrySize != 0 ||
            footer.Sections[3].Size > int.MaxValue)
        {
            throw Invalid("file tree has an invalid size");
        }
    }

    private OffsetRecord[] ParseOffsetRecords(ReadOnlySpan<byte> bytes)
    {
        var records = new OffsetRecord[bytes.Length / OffsetRecordSize];
        for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            var offset = recordIndex * OffsetRecordSize;
            var sizes = new ushort[EntriesPerOffsetRecord];
            for (var i = 0; i < sizes.Length; i++)
            {
                sizes[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 8 + (i * 2))..]);
            }

            records[recordIndex] = new OffsetRecord(
                BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]),
                sizes);
        }

        return records;
    }

    private static Node[] ParseTree(ReadOnlySpan<byte> treeBytes, ReadOnlySpan<byte> nameBytes)
    {
        var nodes = new Node[treeBytes.Length / TreeEntrySize];
        for (var i = 0; i < nodes.Length; i++)
        {
            var bytes = treeBytes[(i * TreeEntrySize)..];
            var nameAndType = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var isFile = (nameAndType & 0x80000000U) != 0;
            var nameOffset = nameAndType & RootNameOffset;
            var first = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            var second = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            var third = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
            var name = i == 0 && nameOffset == RootNameOffset
                ? string.Empty
                : ReadName(nameBytes, nameOffset);

            if (i != 0 && (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".."))
            {
                throw Invalid($"file tree node {i} has an invalid name");
            }

            nodes[i] = isFile
                ? new Node(
                    name,
                    IsFile: true,
                    Offset: first | ((ulong)(third & 0xFFFF) << 32),
                    Size: second | ((ulong)(third & 0xFFFF0000U) << 16),
                    ChildStart: 0,
                    ChildCount: 0)
                : new Node(name, IsFile: false, Offset: 0, Size: 0, first, second);
        }

        if (nodes[0].IsFile || nodes[0].Name.Length != 0)
        {
            throw Invalid("the first file-tree entry is not an unnamed root directory");
        }

        return nodes;
    }

    private void ValidateTreeAndBlocks()
    {
        var visited = new bool[_nodes.Length];
        var queue = new Queue<int>();
        visited[0] = true;
        queue.Enqueue(0);
        ulong maxEnd = 0;
        while (queue.Count != 0)
        {
            var index = queue.Dequeue();
            var node = _nodes[index];
            if (node.IsFile)
            {
                if (node.Offset > ulong.MaxValue - node.Size)
                {
                    throw Invalid($"file-tree node {index} has an overflowing extent");
                }

                maxEnd = Math.Max(maxEnd, node.Offset + node.Size);
                continue;
            }

            if (node.ChildStart > (uint)_nodes.Length ||
                node.ChildCount > (uint)_nodes.Length - node.ChildStart)
            {
                throw Invalid($"directory node {index} has an invalid child range");
            }

            var names = new HashSet<string>(AsciiNameComparer.Instance);
            for (uint i = 0; i < node.ChildCount; i++)
            {
                var childIndex = checked((int)(node.ChildStart + i));
                if (!names.Add(_nodes[childIndex].Name))
                {
                    throw Invalid($"directory node {index} has duplicate child names");
                }

                if (visited[childIndex])
                {
                    throw Invalid($"file-tree node {childIndex} is referenced more than once");
                }

                visited[childIndex] = true;
                queue.Enqueue(childIndex);
            }
        }

        if (visited.Any(static value => !value))
        {
            throw Invalid("file tree contains unreachable nodes");
        }

        var neededBlocks = maxEnd == 0 ? 0UL : ((maxEnd - 1) / BlockSize) + 1;
        var availableBlocks = checked((ulong)_offsetRecords.Length * EntriesPerOffsetRecord);
        if (neededBlocks > availableBlocks)
        {
            throw Invalid("file data extends beyond the block table");
        }

        ulong expectedOffset = 0;
        for (ulong block = 0; block < neededBlocks; block++)
        {
            var recordIndex = checked((int)(block / EntriesPerOffsetRecord));
            var subIndex = (int)(block % EntriesPerOffsetRecord);
            var record = _offsetRecords[recordIndex];
            if (subIndex == 0 && record.BaseOffset != expectedOffset)
            {
                throw Invalid($"block-offset record {recordIndex} has a non-contiguous base offset");
            }

            var size = (ulong)record.Sizes[subIndex] + 1;
            if (expectedOffset > _compressedData.Size || size > _compressedData.Size - expectedOffset)
            {
                throw Invalid($"compressed block {block} is outside the data section");
            }

            expectedOffset += size;
        }
    }

    private static string ReadName(ReadOnlySpan<byte> table, uint offset)
    {
        if (offset == RootNameOffset || offset >= (uint)table.Length)
        {
            throw Invalid("file tree references an invalid name offset");
        }

        var index = checked((int)offset);
        var first = table[index++];
        var length = first & 0x7F;
        if ((first & 0x80) != 0)
        {
            if (index >= table.Length)
            {
                throw Invalid("name table contains a truncated extended length");
            }

            length |= table[index++] << 7;
        }

        if (length == 0 || index > table.Length - length)
        {
            throw Invalid("name table contains an invalid name range");
        }

        return NameEncoding.GetString(table.Slice(index, length));
    }

    private byte[] ReadSection(Section section)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)section.Size));
        ReadExactlyAt(checked((long)section.Offset), bytes);
        return bytes;
    }

    private void ReadExactlyAt(long offset, Span<byte> destination)
    {
        _stream.Position = offset;
        _stream.ReadExactly(destination);
    }

    private static bool AsciiEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (FoldAscii(left[i]) != FoldAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char FoldAscii(char value) => value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;

    private static Encoding CreateNameEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static InvalidDataException Invalid(string detail, Exception? inner = null) =>
        new($"Invalid ZArchive: {detail}.", inner);

    private readonly record struct Section(ulong Offset, ulong Size);
    private sealed record Footer(Section[] Sections, ulong TotalSize, uint Version, uint Magic);
    private sealed record OffsetRecord(ulong BaseOffset, ushort[] Sizes);
    private sealed record Node(
        string Name,
        bool IsFile,
        ulong Offset,
        ulong Size,
        uint ChildStart,
        uint ChildCount);
    private sealed record CacheEntry(byte[] Data, LinkedListNode<long> LruNode);

    private sealed class AsciiNameComparer : IEqualityComparer<string>
    {
        public static AsciiNameComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) =>
            x is not null && y is not null && AsciiEquals(x, y);

        public int GetHashCode(string value)
        {
            var hash = new HashCode();
            foreach (var ch in value)
            {
                hash.Add(FoldAscii(ch));
            }

            return hash.ToHashCode();
        }
    }

    private sealed class ArchiveFileStream(ZArchiveFileSystem archive, ulong fileOffset, ulong fileSize) : Stream
    {
        private readonly ZArchiveFileSystem _archive = archive;
        private readonly ulong _fileOffset = fileOffset;
        private readonly ulong _fileSize = fileSize;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => checked((long)_fileSize);

        public override long Position
        {
            get => _position;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var read = _archive.ReadFile(_fileOffset, _fileSize, checked((ulong)_position), buffer);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new IOException("Attempted to seek before the beginning of the archive file.");
            }

            _position = target;
            return target;
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
