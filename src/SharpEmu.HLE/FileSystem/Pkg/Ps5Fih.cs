// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.HLE.FileSystem.Pkg;

// The finalized image header of a PS5 package ("\x7FFIH", little-endian) and the embedded CNT
// metadata container ("\x7FCNT", big-endian). Layouts follow the pkg-to-anyps5 reader.
internal sealed record Ps5Fih(ushort FormatVersion, long PfsOffset, long PfsSize, long SuperblockOffset, long CntOffset)
{
    public const int HeaderSize = 0x100;

    public const string RetailMessage =
        "This is a retail PS5 package: its files are encrypted with console keys. Only fake/debug PS5 packages can be installed.";

    private static ReadOnlySpan<byte> Magic => [0x7F, 0x46, 0x49, 0x48];

    public static Ps5Fih Read(Ps5Source source)
    {
        if (source.Length < HeaderSize)
        {
            throw new InvalidDataException("The PS5 package header is truncated.");
        }

        var header = source.ReadBytes(HeaderSize, 0);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a PS5 package (bad magic).");
        }

        // 0x00 marks a debug or fake-signed image; 0x80 marks a retail image.
        if (header[5] != 0)
        {
            throw new InvalidDataException(RetailMessage);
        }

        var pfsOffset = ReadOffset(header, 0x10);
        var pfsSize = ReadOffset(header, 0x18);
        var fih = new Ps5Fih(
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
            pfsOffset,
            pfsSize,
            ReadOffset(header, 0x20),
            ReadOffset(header, 0x58));
        if (pfsOffset <= 0 || pfsSize < 0 || pfsOffset > source.Length || pfsSize > source.Length - pfsOffset)
        {
            throw new InvalidDataException("The PFS segment does not fit inside the PS5 package (incomplete download?).");
        }

        if (fih.CntOffset <= 0 || fih.CntOffset >= source.Length)
        {
            throw new InvalidDataException("The embedded CNT offset lies outside the PS5 package.");
        }

        return fih;
    }

    // A 64-bit field that must be a plausible file offset; anything above long.MaxValue is corrupt.
    private static long ReadOffset(byte[] header, int at)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(at));
        return value > long.MaxValue ? -1 : (long)value;
    }
}

internal sealed record Ps5CntFile(string Name, byte[] Bytes);

internal sealed class Ps5Cnt
{
    private const int EntrySize = 0x20;
    private const uint EntryNamesId = 0x0200;
    private const uint EncryptedFlag = 0x8000_0000;

    // Container bookkeeping: digests, entry keys, image key, general digests, metas, entry names.
    private static readonly HashSet<uint> BookkeepingIds = [0x0001, 0x0010, 0x0020, 0x0080, 0x0100, 0x0200];

    private Ps5Cnt(string contentId, List<Ps5CntFile> files, List<string> skippedEncrypted)
    {
        ContentId = contentId;
        Files = files;
        SkippedEncrypted = skippedEncrypted;
    }

    public string ContentId { get; }

    // Plain entries to write below sce_sys, names relative to it.
    public IReadOnlyList<Ps5CntFile> Files { get; }

    public IReadOnlyList<string> SkippedEncrypted { get; }

    public long FilesSize => Files.Sum(file => (long)file.Bytes.Length);

    public static Ps5Cnt Read(Ps5Source source, long cntOffset)
    {
        var header = source.ReadBytes(0x80, cntOffset);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7F, 0x43, 0x4E, 0x54 }))
        {
            throw new InvalidDataException("No CNT container at the offset the PS5 header gives.");
        }

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
        var bodyOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x20));
        var bodySize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x28));
        var contentId = Encoding.ASCII.GetString(header, 0x40, 0x24).Split('\0')[0];
        if (entryCount is 0 or > 4096)
        {
            throw new InvalidDataException($"PS5 package CNT entry count {entryCount} is implausible.");
        }

        // The body is the last region of the container.
        var cntSize = bodyOffset > long.MaxValue || bodySize > long.MaxValue ? long.MaxValue : (long)bodyOffset + (long)bodySize;
        var table = source.ReadBytes(entryCount * EntrySize, cntOffset + tableOffset);
        var entries = new CntEntry[entryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var row = table.AsSpan(i * EntrySize, EntrySize);
            entries[i] = new CntEntry(
                BinaryPrimitives.ReadUInt32BigEndian(row),
                BinaryPrimitives.ReadUInt32BigEndian(row[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[20..]));
        }

        byte[] ReadEntry(CntEntry entry)
        {
            if ((long)entry.Offset + entry.Size > cntSize)
            {
                throw new InvalidDataException($"PS5 package CNT entry 0x{entry.Id:X4} runs past the container.");
            }

            return source.ReadBytes(entry.Size, cntOffset + entry.Offset);
        }

        var names = Array.Empty<byte>();
        foreach (var entry in entries)
        {
            if (entry.Id == EntryNamesId && !entry.Encrypted)
            {
                names = ReadEntry(entry);
                break;
            }
        }

        var files = new List<Ps5CntFile>();
        var skippedEncrypted = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Size == 0)
            {
                continue;
            }

            var name = EntryName(names, entry.Id, entry.NameOffset);
            if (entry.Encrypted)
            {
                skippedEncrypted.Add(name ?? $"0x{entry.Id:X4}");
                continue;
            }

            if (BookkeepingIds.Contains(entry.Id) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            files.Add(new Ps5CntFile(name, ReadEntry(entry)));
        }

        return new Ps5Cnt(contentId, files, skippedEncrypted);
    }

    // Name relative to sce_sys: from the names table, else the well-known id list; null when neither
    // knows the entry.
    internal static string? EntryName(byte[] names, uint id, uint nameOffset)
    {
        string? raw = null;
        if (nameOffset != 0 && nameOffset < names.Length)
        {
            var end = Array.IndexOf(names, (byte)0, (int)nameOffset);
            var length = (end < 0 ? names.Length : end) - (int)nameOffset;
            if (length > 0)
            {
                raw = Encoding.UTF8.GetString(names, (int)nameOffset, length);
            }
        }

        raw ??= WellKnownName(id);
        if (raw is null)
        {
            return null;
        }

        var name = raw.TrimStart('/', '\\');
        while (name.StartsWith("sce_sys/", StringComparison.Ordinal))
        {
            name = name["sce_sys/".Length..];
        }

        return name;
    }

    private static string? WellKnownName(uint id) => id switch
    {
        0x0400 => "license.dat",
        0x0401 => "license.info",
        0x0402 => "nptitle.dat",
        0x040a => "imagedigs.dat",
        0x1001 => "playgo-chunk.dat",
        0x1200 => "icon0.png",
        0x1220 => "pic0.png",
        0x1240 => "snd0.at9",
        0x1280 => "icon0.dds",
        0x12a0 => "pic0.dds",
        0x2000 => "param.json",
        0x2010 => "playgo-hash-table.dat",
        0x2011 => "playgo-ficm.dat",
        _ => null,
    };

    private readonly record struct CntEntry(uint Id, uint NameOffset, uint Flags1, uint Offset, uint Size)
    {
        public bool Encrypted => (Flags1 & EncryptedFlag) != 0;
    }
}
