// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A file of the inner image: path components below uroot, logical byte offset and size.
internal sealed record Ps5InnerFile(string[] Components, long Logical, long Size);

// The inner image (pfs_image.dat): a PFS whose logical bytes are stored as 256 KiB blocks, each raw
// or Kraken-compressed, as NAPS describes. The inner superblock, inodes and directories sit after
// the last file in logical order, so they are found by decoding that tail and looking for a
// superblock whose size matches the NAPS mount size. Blocks are decoded one at a time (the last one
// is cached) so an image of any size needs a few hundred KiB of memory per BlockReader. The block
// table is immutable after Open; only a reader holds decode state, so one reader per thread can
// decode the same image in parallel.
internal sealed class Ps5InnerImage
{
    private const int InnerInodeSize = 0xA8;
    private const uint DirentFile = 2;
    private const uint DirentDir = 3;
    private const long MaxDirSize = 8 << 20;
    // The superblock, inodes and directories after the last file. Real images keep a few MiB there;
    // a larger tail means a corrupt layout.
    private const long MaxMetaTail = 1L << 30;
    private const int MaxDepth = 64;

    private readonly Ps5OuterFile _image;
    private readonly UBlock[] _blocks;
    // NAPS file-offset entries: logical offsets of file boundaries.
    private readonly ulong[] _boundaries;

    // Reader behind ReadAt and CopyTo; not shared with the parallel extraction workers.
    private readonly BlockReader _defaultReader;

    internal readonly record struct UBlock(long Logical, long OnDisk, uint Compressed, uint Uncompressed, bool Kraken, uint EvenComp, uint Flags);

    private Ps5InnerImage(Ps5OuterFile image, UBlock[] blocks, ulong[] boundaries, long mount)
    {
        _image = image;
        _blocks = blocks;
        _boundaries = boundaries;
        Mount = mount;
        _defaultReader = new BlockReader(this);
    }

    // Logical size of the inner image.
    public long Mount { get; }

    public static Ps5InnerImage Open(Ps5OuterFile image, byte[] napsBlob)
    {
        var layout = Ps5Naps.Parse(napsBlob);
        var mount = layout.MountSize();
        if (mount == 0)
        {
            throw new InvalidDataException("The NAPS layout has no mount size entry.");
        }

        // Keep the one table placement whose blocks cover the mount exactly.
        var walks = new List<UBlock[]>();
        var problems = new List<string>();
        foreach (var candidate in layout.Candidates)
        {
            try
            {
                walks.Add(WalkBlocks(layout, candidate.Cblocks, mount));
            }
            catch (InvalidDataException exception)
            {
                problems.Add($"table at 0x{candidate.Start:X}: {exception.Message}");
            }
        }

        if (walks.Count > 1)
        {
            throw new InvalidDataException("Two NAPS CblockInfo placements both map the whole image; refusing to guess.");
        }

        if (walks.Count == 0)
        {
            throw new InvalidDataException(string.Join("; ", problems));
        }

        var blocks = walks[0];
        foreach (var block in blocks)
        {
            if (block.OnDisk + block.Compressed > image.Size)
            {
                throw new InvalidDataException(
                    $"The block at logical 0x{block.Logical:X} is stored at 0x{block.OnDisk:X}+0x{block.Compressed:X}, past the 0x{image.Size:X}-byte image.");
            }
        }

        return new Ps5InnerImage(image, blocks, layout.FileOffsets.Select(entry => entry.UncompressedOffset).ToArray(), mount);
    }

    // Lists the regular files under uroot, every name checked with PkgPackage.ResolveSafe.
    public List<Ps5InnerFile> ListFiles()
    {
        // Metadata follows the last file boundary below the mount size.
        long metaBase = 0;
        foreach (var boundary in _boundaries)
        {
            if (boundary > 0 && boundary < (ulong)Mount && (long)boundary > metaBase)
            {
                metaBase = (long)boundary;
            }
        }

        var first = PartitionPoint(block => block.Logical + block.Uncompressed <= metaBase);
        if (first >= _blocks.Length)
        {
            throw new InvalidDataException("There are no blocks after the last file.");
        }

        var tailBase = _blocks[first].Logical;
        var tailLength = Mount - tailBase;
        if (tailLength > MaxMetaTail)
        {
            throw new InvalidDataException("The inner image metadata after the last file is implausibly large.");
        }

        var tail = new byte[tailLength];
        ReadAt(tailBase, tail);
        var superblockOffset = FindSuperblock(tail, Mount)
            ?? throw new InvalidDataException("No inner PFS superblock covers the image (corrupt or unsupported layout).");
        var tree = Tree.Parse(tail, superblockOffset, tailBase);
        var files = new List<Ps5InnerFile>();
        WalkDir(tree, 0, [], false, files, new bool[tree.Inodes.Length], 0);
        if (files.Count == 0)
        {
            throw new InvalidDataException("The inner tree has no files under uroot.");
        }

        return files;
    }

    private void WalkDir(Tree tree, uint inode, string[] path, bool underUroot, List<Ps5InnerFile> files, bool[] seen, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("The inner directory tree is too deep.");
        }

        if (inode >= tree.Inodes.Length)
        {
            throw new InvalidDataException($"Inner inode {inode} is out of range.");
        }

        if (seen[inode])
        {
            // Already walked (a hard link or a cycle): nothing new below it.
            return;
        }

        seen[inode] = true;
        var dir = tree.Inodes[inode];
        if (!dir.IsDirectory || dir.Size == 0 || dir.Size > MaxDirSize)
        {
            throw new InvalidDataException($"Inner inode {inode} is not a readable directory.");
        }

        var bytes = tree.Local(dir.Logical, dir.Size);
        if (bytes is null)
        {
            bytes = new byte[dir.Size];
            ReadAt(dir.Logical, bytes);
        }

        foreach (var entry in Ps5Dirent.Parse(bytes, tree.BlockSize))
        {
            if (entry.IsSelfOrParentDirectory)
            {
                continue;
            }

            string name;
            try
            {
                name = new UTF8Encoding(false, true).GetString(entry.Name);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("An inner file name is not valid UTF-8.");
            }

            if (entry.Inode >= tree.Inodes.Length)
            {
                throw new InvalidDataException($"{name}: inode {entry.Inode} is out of range.");
            }

            var isUroot = !underUroot && path.Length == 0 && name == "uroot";
            if (!underUroot && !isUroot)
            {
                continue;
            }

            // Refuses separators, "..", Windows-reserved characters.
            PkgPackage.ResolveSafe(string.Empty, [name]);
            var childPath = isUroot ? path : [.. path, name];
            if (entry.Kind == DirentDir)
            {
                WalkDir(tree, entry.Inode, childPath, true, files, seen, depth + 1);
            }
            else if (entry.Kind == DirentFile)
            {
                var child = tree.Inodes[entry.Inode];
                if (child.Logical < 0 || child.Size < 0 || child.Logical > Mount || child.Size > Mount - child.Logical)
                {
                    throw new InvalidDataException($"{string.Join('/', childPath)} runs past the end of the inner image.");
                }

                files.Add(new Ps5InnerFile(childPath, child.Logical, child.Size));
            }
        }
    }

    // Reads logical bytes of the inner image.
    public void ReadAt(long logical, byte[] buffer)
    {
        var done = 0;
        CopyTo(logical, buffer.Length, chunk =>
        {
            chunk.CopyTo(buffer.AsSpan(done));
            done += chunk.Length;
        });
    }

    // Streams logical bytes to `sink` one block at a time.
    public void CopyTo(long logical, long size, SpanAction sink) => _defaultReader.CopyTo(logical, size, sink);

    // A reader with its own decode buffers, for one thread of a parallel extraction.
    internal BlockReader CreateReader() => new(this);

    public delegate void SpanAction(ReadOnlySpan<byte> chunk);

    private int PartitionPoint(Func<UBlock, bool> predicate)
    {
        int low = 0, high = _blocks.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (predicate(_blocks[mid]))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // Number of blocks; indexes run 0..BlockCount-1 in logical order.
    internal int BlockCount => _blocks.Length;

    internal UBlock GetBlock(int index) => _blocks[index];

    internal int FindBlock(long logical)
    {
        var index = PartitionPoint(block => block.Logical + block.Uncompressed <= logical);
        if (index >= _blocks.Length || _blocks[index].Logical > logical)
        {
            throw new InvalidDataException($"No block covers logical 0x{logical:X}.");
        }

        return index;
    }

    // Decode state of one thread: the last decoded block and its stored payload.
    internal sealed class BlockReader
    {
        private readonly Ps5InnerImage _owner;
        private int _cachedIndex = -1;
        private byte[] _decoded = [];
        private byte[] _payload = [];

        internal BlockReader(Ps5InnerImage owner) => _owner = owner;

        // Streams logical bytes to `sink` one block at a time.
        public void CopyTo(long logical, long size, SpanAction sink)
        {
            if (logical < 0 || size < 0 || size > long.MaxValue - logical)
            {
                throw new InvalidDataException("A logical range of the inner image overflows.");
            }

            var position = logical;
            var end = logical + size;
            while (position < end)
            {
                var index = _owner.FindBlock(position);
                var block = _owner._blocks[index];
                var within = (int)(position - block.Logical);
                var count = (int)Math.Min(block.Uncompressed - within, end - position);
                LoadBlock(index);
                sink(_decoded.AsSpan(within, count));
                position += count;
            }
        }

        private void LoadBlock(int index)
        {
            if (_cachedIndex == index)
            {
                return;
            }

            _cachedIndex = -1;
            var block = _owner._blocks[index];
            if (_decoded.Length != block.Uncompressed)
            {
                _decoded = new byte[block.Uncompressed];
            }

            DecodeBlock(index, _decoded);
            _cachedIndex = index;
        }

        // Decodes one block into `destination` (exactly the block's uncompressed size); a sparse
        // block reads as zeros. It leaves the CopyTo state alone, so a caller with its own block
        // cache can decode on any thread with that thread's reader.
        internal void DecodeBlock(int index, byte[] destination)
        {
            var block = _owner._blocks[index];
            if (destination.Length != block.Uncompressed)
            {
                throw new ArgumentException("The destination does not match the block size.", nameof(destination));
            }

            if (block.Compressed == 0)
            {
                Array.Clear(destination);
                return;
            }

            if (_payload.Length != block.Compressed)
            {
                _payload = new byte[block.Compressed];
            }

            _owner._image.ReadExactAt(_payload, block.OnDisk);
            if (_payload.Length == destination.Length)
            {
                _payload.CopyTo(destination, 0);
                return;
            }

            try
            {
                Kraken.DecodeBlock(_payload, block.Flags, block.EvenComp, destination);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"{exception.Message} (block at logical 0x{block.Logical:X}, stored at 0x{block.OnDisk:X}, {block.Compressed} -> {block.Uncompressed} bytes, flags 0x{block.Flags:X}).");
            }
        }
    }

    // Builds the logical-to-stored block list from the CblockInfo records.
    internal static UBlock[] WalkBlocks(Ps5Naps layout, NapsCblock[] records, long mount)
    {
        var output = new List<UBlock>((int)Math.Min(layout.Counts.NumUblocks + 16L, 1 << 20));
        long onDisk = 0;
        long uncompressed = 0;
        var i = 0;
        while (i < records.Length)
        {
            var record = records[i];
            if (record.IsRunBase)
            {
                if (i + 1 >= records.Length || records[i + 1].IsRunBase)
                {
                    throw new InvalidDataException($"NAPS run base {i} is not followed by a data record.");
                }

                onDisk = record.RunOnDisk(records[i + 1]);
                i++;
                continue;
            }

            AppendSparse(layout, mount, ref uncompressed, output);
            if (uncompressed >= mount || i + 1 >= records.Length)
            {
                break;
            }

            var fileEnd = layout.NextBoundary(uncompressed, mount);
            var length = Math.Min(Ps5Naps.UblockSize, Math.Max(fileEnd - uncompressed, 0));
            if (length == 0)
            {
                break;
            }

            // The next record's offset within its 256 KiB block marks where this block's stored
            // bytes end, for stored and Kraken blocks alike.
            long diff = (long)records[i + 1].CoffsetMod - record.CoffsetMod;
            if (diff <= 0)
            {
                diff += Ps5Naps.UblockSize;
            }

            output.Add(new UBlock(
                uncompressed,
                onDisk,
                (uint)diff,
                (uint)length,
                record.IsKraken || diff != length,
                record.EvenComp,
                record.KrakenFlags));
            onDisk += diff;
            uncompressed += length;
            if (uncompressed >= mount)
            {
                break;
            }

            i++;
        }

        AppendSparse(layout, mount, ref uncompressed, output);
        if (uncompressed != mount)
        {
            throw new InvalidDataException($"NAPS blocks cover 0x{uncompressed:X} bytes, but the mount size is 0x{mount:X}.");
        }

        return [.. output];
    }

    // A mount-kind entry below the mount size opens a region with no stored records; it reads as zeros.
    private static void AppendSparse(Ps5Naps layout, long mount, ref long uncompressed, List<UBlock> output)
    {
        while (uncompressed < mount)
        {
            var entry = layout.FirstAtOrAfter(uncompressed);
            if (entry is not { Kind: Ps5Naps.KindMount } found || found.UncompressedOffset != (ulong)uncompressed)
            {
                return;
            }

            var end = layout.NextBoundary(uncompressed, mount);
            var length = Math.Min(Ps5Naps.UblockSize, end - uncompressed);
            if (length <= 0)
            {
                throw new InvalidDataException($"Empty sparse region at 0x{uncompressed:X}.");
            }

            output.Add(new UBlock(uncompressed, 0, 0, (uint)length, false, 0, 0));
            uncompressed += length;
        }
    }

    // Requires the superblock to cover exactly the NAPS mount size, so the outer image's superblock
    // can never be taken for the inner one.
    internal static int? FindSuperblock(byte[] buffer, long mount)
    {
        for (long offset = 0; offset + 0x40 <= buffer.Length; offset += 0x10000)
        {
            var span = buffer.AsSpan((int)offset);
            long blockSize = BinaryPrimitives.ReadUInt32LittleEndian(span[0x20..]);
            if (BinaryPrimitives.ReadUInt64LittleEndian(span) == 2
                && BinaryPrimitives.ReadUInt64LittleEndian(span[8..]) == Ps5OuterPfs.PfsMagic
                && BinaryPrimitives.ReadUInt64LittleEndian(span[0x30..]) > 0
                && unchecked(BinaryPrimitives.ReadUInt64LittleEndian(span[0x38..]) * (ulong)blockSize) == (ulong)mount)
            {
                return (int)offset;
            }
        }

        return null;
    }

    private readonly record struct InnerInode(ushort Mode, long Size, long Logical)
    {
        public bool IsDirectory => (Mode & 0xF000) == 0x4000;
    }

    private sealed class Tree
    {
        private readonly byte[] _tail;
        private readonly long _tailBase;

        private Tree(byte[] tail, long tailBase, int blockSize, InnerInode[] inodes)
        {
            _tail = tail;
            _tailBase = tailBase;
            BlockSize = blockSize;
            Inodes = inodes;
        }

        public int BlockSize { get; }

        public InnerInode[] Inodes { get; }

        public static Tree Parse(byte[] tail, int superblockOffset, long tailBase)
        {
            var sb = tail.AsSpan(superblockOffset);
            if (sb.Length < 0x40)
            {
                throw new InvalidDataException("The inner superblock is truncated.");
            }

            long blockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x20..]);
            var count = BinaryPrimitives.ReadUInt64LittleEndian(sb[0x30..]);
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(sb[0x1C..]);
            if (blockSize == 0 || blockSize % 0x1000 != 0 || blockSize > 1 << 20 || count == 0 || count > 1_000_000)
            {
                throw new InvalidDataException($"Inner superblock: block size 0x{blockSize:X}, {count} inodes.");
            }

            // Mode 0x10 is the compact flat layout: an inode stores the byte address of its data at
            // 0x60 instead of signed block pointers.
            if ((mode & 0x13) != 0x10)
            {
                throw new InvalidDataException($"Unsupported inner PFS mode 0x{mode:X} (only the flat 0x10 layout is known).");
            }

            var perBlock = blockSize / InnerInodeSize;
            var table = superblockOffset + blockSize;
            var inodes = new InnerInode[count];
            for (var i = 0; i < inodes.Length; i++)
            {
                var at = table + i / perBlock * blockSize + i % perBlock * InnerInodeSize;
                if (at + InnerInodeSize > tail.Length)
                {
                    throw new InvalidDataException("The inner inode table is truncated.");
                }

                var record = tail.AsSpan((int)at, InnerInodeSize);
                var size = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
                var logical = BinaryPrimitives.ReadUInt64LittleEndian(record[0x60..]);
                inodes[i] = new InnerInode(
                    BinaryPrimitives.ReadUInt16LittleEndian(record),
                    size > long.MaxValue ? long.MaxValue : (long)size,
                    logical > long.MaxValue ? long.MaxValue : (long)logical);
            }

            return new Tree(tail, tailBase, (int)blockSize, inodes);
        }

        // The bytes of [logical, logical+size) when they sit inside the decoded tail.
        public byte[]? Local(long logical, long size)
        {
            var start = logical - _tailBase;
            if (start < 0 || size < 0 || start > _tail.Length || size > _tail.Length - start)
            {
                return null;
            }

            return _tail.AsSpan((int)start, (int)size).ToArray();
        }
    }
}
