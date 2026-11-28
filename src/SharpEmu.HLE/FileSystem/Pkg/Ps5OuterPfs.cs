// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.HLE.FileSystem.Pkg;

// The outer PFS of a finalized PS5 image. It holds two files: pfs_image.dat (the inner image, stored
// or Kraken-compressed) and naps_pkg_layout.dat (the map from inner logical offsets to stored
// bytes). Block numbers count blocks from the start of the PFS segment. Layouts follow pkg-to-anyps5.
internal sealed class Ps5OuterPfs
{
    public const ulong PfsMagic = 20130315; // 0x01332A0B
    private const ushort ModeSigned = 0x1;
    private const ushort Mode64Bit = 0x2;
    private const ushort ModeEncrypted = 0x4;
    private const int DirectBlocks = 12;
    private const int IndirectLevels = 5;
    private const uint DirentFile = 2;
    private const uint DirentDir = 3;
    private const long MaxBlockSize = 1 << 20;
    // The outer tree holds a handful of entries; a larger directory is corrupt.
    private const long MaxOuterDirSize = 8 << 20;

    private static readonly byte[] PlainNoAuthSeed = "PPRPLAIN-NOAUTH!"u8.ToArray();

    private readonly PackageSource _source;
    private readonly long _base;
    private readonly long _size;
    private readonly InodeLayout _layout;
    private readonly Inode[] _inodes;

    private Ps5OuterPfs(PackageSource source, long pfsOffset, long pfsSize, Ps5PfsSuperblock superblock, InodeLayout layout, Inode[] inodes)
    {
        _source = source;
        _base = pfsOffset;
        _size = pfsSize;
        Superblock = superblock;
        _layout = layout;
        _inodes = inodes;
    }

    public Ps5PfsSuperblock Superblock { get; }

    internal sealed record Ps5PfsSuperblock(ushort Mode, long BlockSize, long InodeCount, long InodeBlockCount, long InodeTableBlock, byte[] Seed)
    {
        public static Ps5PfsSuperblock Parse(ReadOnlySpan<byte> buf)
        {
            if (buf.Length < 0x380)
            {
                throw new InvalidDataException("The PFS superblock is truncated.");
            }

            var version = BinaryPrimitives.ReadUInt64LittleEndian(buf);
            var magic = BinaryPrimitives.ReadUInt64LittleEndian(buf[8..]);
            if (magic != PfsMagic || version is not (1 or 2))
            {
                throw new InvalidDataException($"No PFS superblock (version {version}, magic 0x{magic:X}).");
            }

            long blockSize = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x20..]);
            if (blockSize == 0 || blockSize > MaxBlockSize || blockSize % 0x1000 != 0)
            {
                throw new InvalidDataException($"Unsupported PFS block size 0x{blockSize:X}.");
            }

            return new Ps5PfsSuperblock(
                BinaryPrimitives.ReadUInt16LittleEndian(buf[0x1C..]),
                blockSize,
                ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(buf[0x30..])),
                Math.Max(1, ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(buf[0x40..]))),
                ClampToLong(BinaryPrimitives.ReadUInt64LittleEndian(buf[0xD8..])),
                buf.Slice(0x370, 16).ToArray());
        }

        internal InodeLayout GetInodeLayout() => ((Mode & ModeSigned) != 0, (Mode & Mode64Bit) != 0) switch
        {
            (true, false) => new InodeLayout(0x2C8, 0x64, 36, 32, false),
            (true, true) => new InodeLayout(0x310, 0x68, 40, 32, true),
            (false, false) => new InodeLayout(0xA8, 0x64, 4, 0, false),
            (false, true) => new InodeLayout(0x100, 0x68, 8, 0, true),
        };

        private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    }

    // Where block pointers sit in an inode and in indirect blocks. Signed images put a 32-byte hash
    // before every pointer.
    internal readonly record struct InodeLayout(int Size, int FirstPointer, int Stride, int PointerAt, bool Pointer64)
    {
        public long Pointer(ReadOnlySpan<byte> buf, int index)
        {
            var at = index * Stride + PointerAt;
            if (at < 0 || at + (Pointer64 ? 8 : 4) > buf.Length)
            {
                throw new InvalidDataException("A PFS block pointer lies outside its block.");
            }

            return Pointer64
                ? (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(buf[at..]), long.MaxValue)
                : BinaryPrimitives.ReadUInt32LittleEndian(buf[at..]);
        }
    }

    internal sealed record Inode(long Size, long[] Direct, long[] Indirect);

    public static Ps5OuterPfs Open(PackageSource source, long pfsOffset, long pfsSize, long superblockOffset)
    {
        // Data-first images keep the superblock near the end, where the header points; classic
        // images keep it at the start of the segment.
        var inSegment = superblockOffset >= pfsOffset && superblockOffset <= pfsOffset + pfsSize - 0x400;
        var at = inSegment ? superblockOffset : pfsOffset;
        var superblock = Ps5PfsSuperblock.Parse(source.ReadBytes(0x400, at));
        if ((superblock.Mode & ModeEncrypted) != 0 && !superblock.Seed.AsSpan().SequenceEqual(PlainNoAuthSeed))
        {
            throw new InvalidDataException(
                "The PS5 package's outer PFS is encrypted (its seed is not PPRPLAIN-NOAUTH!); only fake/debug packages can be installed.");
        }

        if (superblock.InodeCount is 0 or > 1 << 20)
        {
            throw new InvalidDataException($"The outer PFS inode count {superblock.InodeCount} is implausible.");
        }

        var layout = superblock.GetInodeLayout();
        var perBlock = (int)(superblock.BlockSize / layout.Size);
        if (perBlock == 0)
        {
            throw new InvalidDataException("The outer PFS block is too small for its inodes.");
        }

        if (superblock.InodeBlockCount > pfsSize / superblock.BlockSize || superblock.InodeTableBlock > pfsSize / superblock.BlockSize)
        {
            throw new InvalidDataException("The outer PFS inode table does not fit in the PFS segment.");
        }

        var table = source.ReadBytes(superblock.InodeBlockCount * superblock.BlockSize, pfsOffset + superblock.InodeTableBlock * superblock.BlockSize);
        var inodes = new Inode[superblock.InodeCount];
        for (var i = 0; i < inodes.Length; i++)
        {
            var offset = (long)(i / perBlock) * superblock.BlockSize + (long)(i % perBlock) * layout.Size;
            if (offset + layout.Size > table.Length)
            {
                throw new InvalidDataException("The outer PFS inode table is short.");
            }

            inodes[i] = ParseInode(table.AsSpan((int)offset, layout.Size), layout);
        }

        return new Ps5OuterPfs(source, pfsOffset, pfsSize, superblock, layout, inodes);
    }

    internal static Inode ParseInode(ReadOnlySpan<byte> record, InodeLayout layout)
    {
        var pointers = record[layout.FirstPointer..];
        var direct = new long[DirectBlocks];
        for (var i = 0; i < direct.Length; i++)
        {
            direct[i] = layout.Pointer(pointers, i);
        }

        var indirect = new long[IndirectLevels];
        for (var i = 0; i < indirect.Length; i++)
        {
            indirect[i] = layout.Pointer(pointers, DirectBlocks + i);
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        return new Inode(size > long.MaxValue ? long.MaxValue : (long)size, direct, indirect);
    }

    public int InodeCount => _inodes.Length;

    public Ps5OuterFile OpenFile(uint inode)
    {
        if (inode >= _inodes.Length)
        {
            throw new InvalidDataException($"Outer PFS inode {inode} is out of range.");
        }

        var node = _inodes[inode];
        if (node.Size > _size)
        {
            throw new InvalidDataException($"Outer PFS inode {inode} is larger than the PFS segment.");
        }

        var blockSize = Superblock.BlockSize;
        var count = (node.Size + blockSize - 1) / blockSize;
        return new Ps5OuterFile(_source, _base, blockSize, BlockList(node, count), node.Size);
    }

    // Finds a regular file by name anywhere in the outer tree.
    public uint? Find(string name)
    {
        var target = System.Text.Encoding.UTF8.GetBytes(name);
        var stack = new Stack<uint>();
        stack.Push(0);
        var seen = new bool[_inodes.Length];
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (seen[dir])
            {
                continue;
            }

            seen[dir] = true;
            var file = OpenFile(dir);
            if (file.Size > MaxOuterDirSize)
            {
                throw new InvalidDataException($"Outer PFS directory inode {dir} is implausibly large.");
            }

            foreach (var entry in Ps5Dirent.Parse(file.ReadAll(), (int)Superblock.BlockSize))
            {
                if (entry.Inode >= _inodes.Length || entry.IsSelfOrParentDirectory)
                {
                    continue;
                }

                if (entry.Kind == DirentDir)
                {
                    stack.Push(entry.Inode);
                }
                else if (entry.Kind == DirentFile && entry.Name.AsSpan().SequenceEqual(target))
                {
                    return entry.Inode;
                }
            }
        }

        return null;
    }

    private long[] BlockList(Inode node, long count)
    {
        var blocks = new List<long>((int)Math.Min(count, 1 << 20));
        for (var i = 0; i < DirectBlocks && blocks.Count < count; i++)
        {
            blocks.Add(node.Direct[i]);
        }

        var perBlock = (int)(Superblock.BlockSize / _layout.Stride);
        var level = 0;
        while (blocks.Count < count)
        {
            if (level >= 3)
            {
                throw new InvalidDataException("An outer PFS file needs more than triple-indirect blocks.");
            }

            WalkIndirect(node.Indirect[level], level, count, perBlock, blocks);
            level++;
        }

        return [.. blocks];
    }

    private void WalkIndirect(long block, int depth, long count, int perBlock, List<long> output)
    {
        var buffer = _source.ReadBytes(Superblock.BlockSize, BlockOffset(_base, block, Superblock.BlockSize));
        for (var i = 0; i < perBlock && output.Count < count; i++)
        {
            var pointer = _layout.Pointer(buffer, i);
            if (depth == 0)
            {
                output.Add(pointer);
            }
            else
            {
                WalkIndirect(pointer, depth - 1, count, perBlock, output);
            }
        }
    }

    // Package offset of a PFS block; an overflow means a corrupt pointer.
    internal static long BlockOffset(long baseOffset, long block, long blockSize)
    {
        try
        {
            return checked(baseOffset + block * blockSize);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"Outer PFS block pointer 0x{block:X} is out of range.");
        }
    }
}

// A file of the outer PFS, read through its block list.
internal sealed class Ps5OuterFile(PackageSource source, long baseOffset, long blockSize, long[] blocks, long size)
{
    public long Size { get; } = size;

    public void ReadExactAt(Span<byte> buffer, long offset)
    {
        if (offset < 0 || offset > Size || buffer.Length > Size - offset)
        {
            throw new InvalidDataException($"Read of {buffer.Length:N0} bytes at 0x{offset:X} is past the end of a {Size:N0}-byte outer file.");
        }

        var done = 0;
        while (done < buffer.Length)
        {
            var position = offset + done;
            var index = (int)(position / blockSize);
            var within = position % blockSize;
            // Coalesce a run of consecutive blocks into one read.
            var run = 1;
            while (index + run < blocks.Length && blocks[index + run] == blocks[index] + run)
            {
                run++;
            }

            var available = run * blockSize - within;
            var n = (int)Math.Min(available, buffer.Length - done);
            var at = Ps5OuterPfs.BlockOffset(baseOffset, blocks[index], blockSize) + within;
            source.ReadExactAt(buffer.Slice(done, n), at);
            done += n;
        }
    }

    // Package offset just past the last byte ReadExactAt(offset, count) reads; 0 for an empty range.
    public long SourceEnd(long offset, long count)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (offset < 0 || offset > Size || count > Size - offset)
        {
            throw new InvalidDataException($"Range of {count:N0} bytes at 0x{offset:X} is past the end of a {Size:N0}-byte outer file.");
        }

        long end = 0;
        var last = (offset + count - 1) / blockSize;
        for (var index = offset / blockSize; index <= last; index++)
        {
            var within = Math.Min(offset + count, (index + 1) * blockSize) - index * blockSize;
            end = Math.Max(end, Ps5OuterPfs.BlockOffset(baseOffset, blocks[index], blockSize) + within);
        }

        return end;
    }

    public byte[] ReadAll()
    {
        var bytes = new byte[Size];
        ReadExactAt(bytes, 0);
        return bytes;
    }
}

internal readonly record struct Ps5Dirent(uint Inode, uint Kind, byte[] Name)
{
    // The "." and ".." directory records: typed as directories (3) by some builders, or with the
    // PFS self (4) and parent (5) types by others. A file that merely carries one of those names is not one.
    public bool IsSelfOrParentDirectory => Kind is 3 or 4 or 5 && (Name.AsSpan().SequenceEqual("."u8) || Name.AsSpan().SequenceEqual(".."u8));

    // Directory records: inode, type, name length, record size, then the name. An all-zero record
    // pads to the end of its block.
    public static List<Ps5Dirent> Parse(byte[] buffer, int blockSize)
    {
        var entries = new List<Ps5Dirent>();
        var position = 0;
        while (position < buffer.Length)
        {
            var rest = buffer.AsSpan(position);
            if (rest.Length < 16)
            {
                if (rest.IndexOfAnyExcept((byte)0) < 0)
                {
                    break;
                }

                throw new InvalidDataException("Truncated PFS directory record.");
            }

            var inode = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(rest[4..]);
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(rest[8..]);
            var recordSize = BinaryPrimitives.ReadUInt32LittleEndian(rest[12..]);
            if (inode == 0 && kind == 0 && nameLength == 0 && recordSize == 0)
            {
                var skip = blockSize - position % blockSize;
                position += Math.Min(skip, rest.Length);
                continue;
            }

            if (recordSize is < 16 or > 0x1000 || nameLength > recordSize - 16 || recordSize > rest.Length)
            {
                throw new InvalidDataException($"Bad PFS directory record at 0x{position:X} (name {nameLength} bytes, record {recordSize} bytes).");
            }

            var name = rest.Slice(16, (int)nameLength);
            var end = name.IndexOf((byte)0);
            if (end >= 0)
            {
                name = name[..end];
            }

            position += (int)recordSize;
            if (name.IsEmpty)
            {
                throw new InvalidDataException("PFS directory record without a name.");
            }

            entries.Add(new Ps5Dirent(inode, kind, name.ToArray()));
        }

        return entries;
    }
}
