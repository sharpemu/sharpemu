// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.HLE.FileSystem.Pkg;

internal sealed record NapsCounts(
    uint NumFiles,
    byte CompressionType,
    uint NumKeys,
    uint NumShuffle,
    uint NumUblocks,
    uint NumOuterBlocks,
    uint NumCblockInfo)
{
    public long NumU2c => (NumUblocks + 8) >> 3;
}

internal readonly record struct NapsFileOffset(byte Kind, ulong UncompressedOffset);

internal readonly record struct NapsCblock(
    bool IsRunBase,
    uint CoffsetMod,
    uint ClenEvenMinus1,
    byte KdePredictor,
    byte ShuffleIdx,
    uint CoffsetStart256K)
{
    // Compressed length of the first Kraken chunk of a block.
    public uint EvenComp => ClenEvenMinus1 + 1;

    public bool IsKraken => (KdePredictor & 2) != 0;

    public uint KrakenFlags => KdePredictor | ((uint)ShuffleIdx << 4);

    // A run base holds twice the 256 KiB block index; the next data record supplies the byte offset
    // within that block.
    public long RunOnDisk(NapsCblock first) =>
        (long)(CoffsetStart256K / 2) * Ps5Naps.UblockSize + first.CoffsetMod;
}

// A placement of the CblockInfo table that passed the structural checks. Only a walk over all
// blocks can tell two such placements apart.
internal sealed record NapsCandidate(int Start, NapsCblock[] Cblocks);

// naps_pkg_layout.dat: the map from the inner image's logical offsets to the stored (raw or
// Kraken-compressed) bytes of pfs_image.dat. Layout: a 16-byte packed header, outer-block digests
// (8 bytes each), shuffle patterns (8 bytes each), file-offset entries (6 bytes each, padded to
// 16), u2c entries (10 bytes each, padded to 8), then 9-byte CblockInfo records. The format follows
// the PS5PCEM and LibProsperoPKG readers via pkg-to-anyps5.
internal sealed class Ps5Naps
{
    public const long UblockSize = 0x40000;
    // The header counts pfs_image.dat in 64 KiB outer blocks.
    private const long OuterBlockSize = 0x10000;
    private const int HeaderSize = 16;
    private const int OuterStride = 8;
    private const int ShuffleStride = 8;
    private const int FileOffsetStride = 6;
    private const int U2cStride = 10;
    private const int CblockStride = 9;
    // File-offset kind of the terminal mount-size entry and of sparse regions.
    public const byte KindMount = 0x40;

    // Largest inner image accepted. Sparse regions cost no stored bytes, so a forged mount size
    // would otherwise make the reader build one 256 KiB block record per step of a huge image.
    public const long MaxMountSize = 512L << 30;

    private Ps5Naps(NapsCounts counts, NapsFileOffset[] fileOffsets, List<NapsCandidate> candidates)
    {
        Counts = counts;
        FileOffsets = fileOffsets;
        Candidates = candidates;
    }

    public NapsCounts Counts { get; }

    // Sorted by offset.
    public NapsFileOffset[] FileOffsets { get; }

    public IReadOnlyList<NapsCandidate> Candidates { get; }

    public static Ps5Naps Parse(byte[] blob)
    {
        var counts = DecodeHeader(blob);
        var pos = HeaderSize + (long)counts.NumOuterBlocks * OuterStride + (long)counts.NumShuffle * ShuffleStride;
        long fileCount = counts.NumFiles;
        if (pos + fileCount * FileOffsetStride > blob.Length)
        {
            throw new InvalidDataException("The NAPS file-offset table is truncated.");
        }

        var fileOffsets = new NapsFileOffset[fileCount];
        for (var i = 0; i < fileOffsets.Length; i++)
        {
            var at = (int)pos + i * FileOffsetStride;
            ulong offset = 0;
            for (var b = 0; b < 5; b++)
            {
                offset |= (ulong)blob[at + b] << (8 * b);
            }

            fileOffsets[i] = new NapsFileOffset(blob[at + 5], offset);
            if (fileOffsets[i].Kind == KindMount && offset > MaxMountSize)
            {
                throw new InvalidDataException($"The NAPS mount size 0x{offset:X} is implausibly large.");
            }
        }

        var fileOffsetsEnd = pos + fileCount * FileOffsetStride;
        // The u2c table ends on an 8-byte boundary. Where it starts differs between packages: some
        // pad the file-offset table to 16 bytes, others start u2c right after it. Both placements
        // are tried; only one yields a valid record table.
        var starts = new List<long>();
        foreach (var u2c in new[] { AlignUp(fileOffsetsEnd, 16), fileOffsetsEnd })
        {
            var start = AlignUp(u2c + counts.NumU2c * U2cStride, 8);
            if (starts.Count == 0 || starts[^1] != start)
            {
                starts.Add(start);
            }
        }

        var imageSize = (long)counts.NumOuterBlocks * OuterBlockSize;
        var problems = new List<string>();
        var candidates = new List<NapsCandidate>();
        foreach (var start in starts)
        {
            try
            {
                candidates.Add(new NapsCandidate((int)start, ReadCblocks(blob, start, counts.NumCblockInfo, imageSize)));
            }
            catch (InvalidDataException exception)
            {
                problems.Add($"at 0x{start:X}: {exception.Message}");
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException($"No valid NAPS CblockInfo table ({string.Join("; ", problems)}).");
        }

        // Stable, so entries sharing an offset keep their table order.
        var sorted = fileOffsets.Select((entry, index) => (entry, index)).OrderBy(pair => pair.entry.UncompressedOffset).ThenBy(pair => pair.index)
            .Select(pair => pair.entry).ToArray();
        return new Ps5Naps(counts, sorted, candidates);
    }

    // The logical size of the inner image: the last mount-kind entry.
    public long MountSize()
    {
        for (var i = FileOffsets.Length - 1; i >= 0; i--)
        {
            if (FileOffsets[i].Kind == KindMount)
            {
                return FileOffsets[i].UncompressedOffset > long.MaxValue ? 0 : (long)FileOffsets[i].UncompressedOffset;
            }
        }

        return 0;
    }

    // The first file boundary after `current`, or `mount` when there is none.
    public long NextBoundary(long current, long mount)
    {
        var index = PartitionPoint(offset => offset <= (ulong)current);
        return index < FileOffsets.Length ? Math.Min(Clamp(FileOffsets[index].UncompressedOffset), mount) : mount;
    }

    // The first entry at or after `offset`, in table order among equal offsets.
    public NapsFileOffset? FirstAtOrAfter(long offset)
    {
        var index = PartitionPoint(value => value < (ulong)offset);
        return index < FileOffsets.Length ? FileOffsets[index] : null;
    }

    private int PartitionPoint(Func<ulong, bool> predicate)
    {
        int low = 0, high = FileOffsets.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (predicate(FileOffsets[mid].UncompressedOffset))
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

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    // Reads the records and checks that every run base is followed by a data record and lands inside
    // the stored image.
    private static NapsCblock[] ReadCblocks(byte[] blob, long start, uint count, long imageSize)
    {
        var end = start + (long)count * CblockStride;
        if (start < 0 || end > blob.Length)
        {
            throw new InvalidDataException($"{count} records end at 0x{end:X}, past the {blob.Length}-byte layout.");
        }

        var records = new NapsCblock[count];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = DecodeCblock(blob.AsSpan((int)start + i * CblockStride, CblockStride));
        }

        for (var i = 0; i < records.Length; i++)
        {
            if (!records[i].IsRunBase)
            {
                continue;
            }

            if (i + 1 >= records.Length || records[i + 1].IsRunBase)
            {
                throw new InvalidDataException($"Run base {i} is not followed by a data record.");
            }

            var at = records[i].RunOnDisk(records[i + 1]);
            if (at >= imageSize)
            {
                throw new InvalidDataException($"Run base {i} points at 0x{at:X}, past the 0x{imageSize:X}-byte image.");
            }
        }

        return records;
    }

    public static NapsCounts DecodeHeader(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize)
        {
            throw new InvalidDataException("The NAPS header is truncated.");
        }

        var w0 = BinaryPrimitives.ReadUInt64LittleEndian(blob);
        var w1 = BinaryPrimitives.ReadUInt64LittleEndian(blob[8..]);
        return new NapsCounts(
            (uint)(w0 & 0xFF_FFFF) + 1,
            (byte)((w0 >> 24) & 3),
            (uint)((w0 >> 26) & 3) + 1,
            (uint)((w0 >> 28) & 0xF),
            (uint)((w0 >> 32) & 0xFF_FFFF),
            (uint)(w1 & 0xFF_FFFF),
            (uint)((w1 >> 24) & 0xFF_FFFF) + 2);
    }

    public static NapsCblock DecodeCblock(ReadOnlySpan<byte> raw)
    {
        var lo = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        ulong hi = raw[8];
        var coffsetMod = (uint)(lo & 0x3FFFF);
        if (((lo >> 18) & 1) == 0)
        {
            return new NapsCblock(
                false,
                coffsetMod,
                (uint)((lo >> 38) & 0x1FFFF),
                (byte)((lo >> 56) & 7),
                (byte)((lo >> 59) & 0xF),
                0);
        }

        return new NapsCblock(true, coffsetMod, 0, 0, 0, (uint)(((lo >> 49) & 0x7FFF) | ((hi & 0x1FF) << 15)));
    }
}
