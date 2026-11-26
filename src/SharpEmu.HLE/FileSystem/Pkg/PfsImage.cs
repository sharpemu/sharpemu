// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharpEmu.HLE.FileSystem.Pkg;

// The superblock at the start of the logical PFS image. Layout from shadPS4 (pfs.h), checked
// against a retail fake PKG: the image's total block count is at 0x38 (it equals the PFSC block
// count); the 64-bit field at 0x28 holds 1 there and is not a size.
public readonly record struct PfsHeader(
    long Version,
    long Magic,
    ushort Mode,
    int BlockSize,
    long BlockCount,
    long DinodeCount,
    long DinodeBlockCount,
    long SuperrootInode)
{
    public static PfsHeader Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < 0x50)
        {
            throw new InvalidDataException("PFS header is truncated.");
        }

        return new PfsHeader(
            BinaryPrimitives.ReadInt64LittleEndian(block),
            BinaryPrimitives.ReadInt64LittleEndian(block[0x08..]),
            BinaryPrimitives.ReadUInt16LittleEndian(block[0x1C..]),
            BinaryPrimitives.ReadInt32LittleEndian(block[0x20..]),
            BinaryPrimitives.ReadInt64LittleEndian(block[0x38..]),
            BinaryPrimitives.ReadInt64LittleEndian(block[0x30..]),
            BinaryPrimitives.ReadInt64LittleEndian(block[0x40..]),
            BinaryPrimitives.ReadInt64LittleEndian(block[0x48..]));
    }
}

// A dinode of the unsigned PFS layout (0xA8 bytes; only the leading fields are used).
public readonly record struct PfsInode(ushort Mode, ushort Nlink, uint Flags, long Size, long SizeCompressed, uint Blocks, uint Location)
{
    public const int Stride = 0xA8;

    public const ushort ModeDirectory = 0x4000;
    public const ushort ModeFile = 0x8000;

    public bool IsDirectory => (Mode & ModeDirectory) != 0;
    public bool IsFile => (Mode & ModeFile) != 0;
    public bool IsEmpty => Mode == 0;

    public static PfsInode Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x68)
        {
            throw new InvalidDataException("PFS inode is truncated.");
        }

        return new PfsInode(
            BinaryPrimitives.ReadUInt16LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x60..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[0x64..]));
    }
}

public enum PfsDirentType
{
    File = 2,
    Directory = 3,
    CurrentDirectory = 4,
    ParentDirectory = 5,
}

public readonly record struct PfsDirent(int Inode, int Type, string Name)
{
    // Sequential directory entries: ino, type, namelen, entsize (all int32), then the name.
    // Entries never cross a block; a zero inode or entry size ends the block.
    public static List<PfsDirent> ParseBlock(ReadOnlySpan<byte> block)
    {
        var entries = new List<PfsDirent>();
        var offset = 0;
        while (offset + 16 <= block.Length)
        {
            var inode = BinaryPrimitives.ReadInt32LittleEndian(block[offset..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(block[(offset + 4)..]);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(block[(offset + 8)..]);
            var entrySize = BinaryPrimitives.ReadInt32LittleEndian(block[(offset + 12)..]);
            if (inode == 0 || entrySize < 16 || nameLength < 0 || offset + 16 + nameLength > block.Length)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(block.Slice(offset + 16, nameLength));
            entries.Add(new PfsDirent(inode, type, name));
            offset += entrySize;
        }

        return entries;
    }
}

// The compressed container around the inner file system: a header, a table of block offsets and
// the 64 KiB blocks, each stored raw or zlib compressed.
public sealed class PfscImage
{
    public const uint Magic = 0x43534650; // "PFSC"
    public const int BlockSize = 0x10000;

    private readonly Stream _source;
    private readonly long _offset;
    private readonly long[] _blockOffsets;
    private byte[] _compressed = new byte[BlockSize];

    public PfscImage(Stream source, long pfscOffset)
    {
        _source = source;
        _offset = pfscOffset;
        Span<byte> header = stackalloc byte[0x30];
        ReadExactly(pfscOffset, header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw new InvalidDataException("PFSC header not found.");
        }

        var blockSize = BinaryPrimitives.ReadInt64LittleEndian(header[0x10..]);
        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(header[0x18..]);
        DataLength = BinaryPrimitives.ReadInt64LittleEndian(header[0x28..]);
        if (blockSize != BlockSize || DataLength < 0 || tableOffset < 0x30)
        {
            throw new InvalidDataException("Unsupported PFSC layout.");
        }

        BlockCount = (int)((DataLength + BlockSize - 1) / BlockSize);
        var table = new byte[(BlockCount + 1) * 8];
        ReadExactly(pfscOffset + tableOffset, table);
        _blockOffsets = new long[BlockCount + 1];
        for (var i = 0; i < _blockOffsets.Length; i++)
        {
            _blockOffsets[i] = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * 8));
        }
    }

    public long DataLength { get; }

    public int BlockCount { get; }

    // Finds the PFSC inside a decrypted PFS image: it starts on a 64 KiB boundary after the
    // image's own header blocks.
    public static long FindOffset(Stream image, int maxCandidates = 4096)
    {
        Span<byte> probe = stackalloc byte[4];
        for (var i = 0; i < maxCandidates; i++)
        {
            var offset = 0x20000L + i * 0x10000L;
            if (offset + 4 > image.Length)
            {
                break;
            }

            image.Position = offset;
            image.ReadExactly(probe);
            if (BinaryPrimitives.ReadUInt32LittleEndian(probe) == Magic)
            {
                return offset;
            }
        }

        throw new InvalidDataException("No PFSC container found in the PFS image.");
    }

    // Decompresses block `index` into `destination` (64 KiB). Missing data reads as zeros.
    public void ReadBlock(long index, Span<byte> destination)
    {
        if (index < 0 || index >= BlockCount)
        {
            throw new InvalidDataException($"PFSC block {index} is out of range.");
        }

        var start = _blockOffsets[index];
        var size = _blockOffsets[index + 1] - start;
        if (size < 0 || size > BlockSize)
        {
            throw new InvalidDataException($"PFSC block {index} has an invalid size {size}.");
        }

        destination[..BlockSize].Clear();
        if (size == 0)
        {
            return;
        }

        if (_compressed.Length < size)
        {
            _compressed = new byte[BlockSize];
        }

        var data = _compressed.AsSpan(0, (int)size);
        ReadExactly(_offset + start, data);
        if (size == BlockSize)
        {
            data.CopyTo(destination);
            return;
        }

        using var inflater = new ZLibStream(new MemoryStream(_compressed, 0, (int)size, writable: false), CompressionMode.Decompress);
        var filled = 0;
        while (filled < BlockSize)
        {
            var read = inflater.Read(destination[filled..BlockSize]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }
    }

    private void ReadExactly(long position, Span<byte> destination)
    {
        _source.Position = position;
        _source.ReadExactly(destination);
    }
}

// The inner file system: superblock, inode table and directories on top of a PFSC.
public sealed class PfsImage
{
    private readonly PfscImage _pfsc;
    private readonly byte[] _block = new byte[PfscImage.BlockSize];
    private PfsInode[]? _inodes;

    public PfsImage(PfscImage pfsc)
    {
        _pfsc = pfsc;
        _pfsc.ReadBlock(0, _block);
        Header = PfsHeader.Parse(_block);
        if (Header.BlockSize != PfscImage.BlockSize)
        {
            throw new InvalidDataException($"Unsupported PFS block size {Header.BlockSize}.");
        }

        if (Header.BlockCount <= 0 || Header.BlockCount > _pfsc.BlockCount)
        {
            throw new InvalidDataException("PFS block count does not fit its PFSC container.");
        }
    }

    public PfsHeader Header { get; }

    public PfsInode GetInode(long number)
    {
        _inodes ??= ReadInodes();
        if (number < 0 || number >= _inodes.Length)
        {
            throw new InvalidDataException($"PFS inode {number} is out of range.");
        }

        return _inodes[number];
    }

    // Inodes are packed per block (a block holds 390 of them); the table starts at block 1.
    private PfsInode[] ReadInodes()
    {
        var perBlock = PfscImage.BlockSize / PfsInode.Stride;
        if (Header.DinodeBlockCount <= 0 || Header.DinodeBlockCount > Header.BlockCount ||
            Header.DinodeCount > Header.DinodeBlockCount * perBlock)
        {
            throw new InvalidDataException("PFS inode table is implausibly large.");
        }

        var count = checked((int)Header.DinodeCount);
        var inodes = new PfsInode[count];
        for (var i = 0; i < count; i++)
        {
            var slot = i % perBlock;
            if (slot == 0)
            {
                _pfsc.ReadBlock(1 + i / perBlock, _block);
            }

            inodes[i] = PfsInode.Parse(_block.AsSpan(slot * PfsInode.Stride, PfsInode.Stride));
        }

        return inodes;
    }

    public List<PfsDirent> ReadDirectory(PfsInode directory)
    {
        var entries = new List<PfsDirent>();
        for (uint i = 0; i < directory.Blocks; i++)
        {
            _pfsc.ReadBlock((long)directory.Location + i, _block);
            entries.AddRange(PfsDirent.ParseBlock(_block));
        }

        return entries;
    }

    // The game's root directory: "uroot" inside the super root.
    public PfsInode GetUserRoot()
    {
        var superRoot = GetInode(Header.SuperrootInode);
        foreach (var entry in ReadDirectory(superRoot))
        {
            if (entry.Name == "uroot")
            {
                return GetInode(entry.Inode);
            }
        }

        throw new InvalidDataException("PFS image has no uroot directory.");
    }

    // Streams a file's blocks to `destination`; the last block is cut to the file size.
    public void CopyFile(PfsInode file, Stream destination, Action<long>? onBytes = null)
    {
        if (file.Size < 0 || file.Blocks * (long)PfscImage.BlockSize < file.Size)
        {
            throw new InvalidDataException("PFS file claims more data than its blocks hold.");
        }

        var remaining = file.Size;
        for (uint i = 0; i < file.Blocks && remaining > 0; i++)
        {
            _pfsc.ReadBlock((long)file.Location + i, _block);
            var chunk = (int)Math.Min(remaining, PfscImage.BlockSize);
            destination.Write(_block, 0, chunk);
            remaining -= chunk;
            onBytes?.Invoke(chunk);
        }
    }
}
