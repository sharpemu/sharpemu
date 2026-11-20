// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.HLE.FileSystem.Pkg;

// The PS4 package container header (all fields big-endian). Layout ported from shadPS4
// (src/core/file_format/pkg.h).
public sealed record PkgHeader(
    uint PkgType,
    uint FileCount,
    uint TableEntryCount,
    uint TableEntryOffset,
    ulong BodyOffset,
    ulong BodySize,
    ulong ContentOffset,
    ulong ContentSize,
    string ContentId,
    uint DrmType,
    uint ContentType,
    uint ContentFlags,
    uint PfsImageCount,
    ulong PfsImageFlags,
    ulong PfsImageOffset,
    ulong PfsImageSize,
    ulong MountImageOffset,
    ulong MountImageSize,
    ulong PkgSize,
    uint PfsSignedSize,
    uint PfsCacheSize)
{
    public const int Size = 0x480;
    public const uint Magic = 0x7F434E54; // "\x7FCNT"

    // The nine character title id (CUSA00900) is the tail of the content id UP9000-CUSA00900_00-...
    public string TitleId => ContentId.Length >= 16 ? ContentId.Substring(7, 9) : string.Empty;

    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    public static PkgHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException("PKG header is truncated.");
        }

        if (!HasMagic(data))
        {
            throw new InvalidDataException("Not a PKG file (bad magic).");
        }

        var contentId = Encoding.ASCII.GetString(data.Slice(0x40, 0x24)).TrimEnd('\0');
        return new PkgHeader(
            PkgType: BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]),
            FileCount: BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]),
            TableEntryCount: BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]),
            TableEntryOffset: BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]),
            BodyOffset: BinaryPrimitives.ReadUInt64BigEndian(data[0x20..]),
            BodySize: BinaryPrimitives.ReadUInt64BigEndian(data[0x28..]),
            ContentOffset: BinaryPrimitives.ReadUInt64BigEndian(data[0x30..]),
            ContentSize: BinaryPrimitives.ReadUInt64BigEndian(data[0x38..]),
            ContentId: contentId,
            DrmType: BinaryPrimitives.ReadUInt32BigEndian(data[0x70..]),
            ContentType: BinaryPrimitives.ReadUInt32BigEndian(data[0x74..]),
            ContentFlags: BinaryPrimitives.ReadUInt32BigEndian(data[0x78..]),
            PfsImageCount: BinaryPrimitives.ReadUInt32BigEndian(data[0x404..]),
            PfsImageFlags: BinaryPrimitives.ReadUInt64BigEndian(data[0x408..]),
            PfsImageOffset: BinaryPrimitives.ReadUInt64BigEndian(data[0x410..]),
            PfsImageSize: BinaryPrimitives.ReadUInt64BigEndian(data[0x418..]),
            MountImageOffset: BinaryPrimitives.ReadUInt64BigEndian(data[0x420..]),
            MountImageSize: BinaryPrimitives.ReadUInt64BigEndian(data[0x428..]),
            PkgSize: BinaryPrimitives.ReadUInt64BigEndian(data[0x430..]),
            PfsSignedSize: BinaryPrimitives.ReadUInt32BigEndian(data[0x438..]),
            PfsCacheSize: BinaryPrimitives.ReadUInt32BigEndian(data[0x43C..]));
    }
}

// One 32-byte row of the entry table: small metadata files (param.sfo, icons, keys) stored in the
// package body.
public readonly record struct PkgEntry(
    uint Id,
    uint FilenameOffset,
    uint Flags1,
    uint Flags2,
    uint Offset,
    uint Size)
{
    public const int Size_ = 32;

    public const uint EntryKeysId = 0x10;
    public const uint ImageKeyId = 0x20;

    public static PkgEntry Parse(ReadOnlySpan<byte> row)
    {
        if (row.Length < Size_)
        {
            throw new InvalidDataException("PKG entry is truncated.");
        }

        return new PkgEntry(
            BinaryPrimitives.ReadUInt32BigEndian(row),
            BinaryPrimitives.ReadUInt32BigEndian(row[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(row[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(row[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(row[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(row[20..]));
    }

    public static PkgEntry[] ParseTable(ReadOnlySpan<byte> table, int count)
    {
        if (count < 0 || (long)count * Size_ > table.Length)
        {
            throw new InvalidDataException("PKG entry table is truncated.");
        }

        var entries = new PkgEntry[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = Parse(table.Slice(i * Size_, Size_));
        }

        return entries;
    }

    // Name of a well-known entry id relative to sce_sys, or null for ids that are not metadata
    // files (digests, keys, licence data).
    public string? Name => PkgEntryNames.Get(Id);
}

internal static class PkgEntryNames
{
    private static readonly Dictionary<uint, string> Fixed = new()
    {
        [0x0402] = "nptitle.dat",
        [0x0403] = "npbind.dat",
        [0x1000] = "param.sfo",
        [0x1001] = "playgo-chunk.dat",
        [0x1002] = "playgo-chunk.sha",
        [0x1003] = "playgo-manifest.xml",
        [0x1004] = "pronunciation.xml",
        [0x1005] = "pronunciation.sig",
        [0x1006] = "pic1.png",
        [0x1007] = "pubtoolinfo.dat",
        [0x1008] = "app/playgo-chunk.dat",
        [0x1009] = "app/playgo-chunk.sha",
        [0x100A] = "app/playgo-manifest.xml",
        [0x100B] = "shareparam.json",
        [0x100C] = "shareoverlayimage.png",
        [0x100D] = "save_data.png",
        [0x100E] = "shareprivacyguardimage.png",
        [0x1200] = "icon0.png",
        [0x1220] = "pic0.png",
        [0x1240] = "snd0.at9",
        [0x1260] = "changeinfo/changeinfo.xml",
        [0x1280] = "icon0.dds",
        [0x12A0] = "pic0.dds",
        [0x12C0] = "pic1.dds",
    };

    public static string? Get(uint id)
    {
        if (Fixed.TryGetValue(id, out var name))
        {
            return name;
        }

        // Numbered families: icon0_NN.png, pic1_NN.png, changeinfo_NN.xml, icon0_NN.dds,
        // pic1_NN.dds (31 variants each) and trophyNN.trp (100 variants).
        if (id is >= 0x1201 and <= 0x121F) return $"icon0_{id - 0x1200:00}.png";
        if (id is >= 0x1241 and <= 0x125F) return $"pic1_{id - 0x1240:00}.png";
        if (id is >= 0x1261 and <= 0x127F) return $"changeinfo/changeinfo_{id - 0x1260:00}.xml";
        if (id is >= 0x1281 and <= 0x129F) return $"icon0_{id - 0x1280:00}.dds";
        if (id is >= 0x12C1 and <= 0x12DF) return $"pic1_{id - 0x12C0:00}.dds";
        if (id is >= 0x1400 and <= 0x1463) return $"trophy/trophy{id - 0x1400:00}.trp";
        return null;
    }
}
