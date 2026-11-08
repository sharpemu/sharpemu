// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.GnmMode.Loader;

public static class GnmParamSfo
{
    public const int UserDefinedParamCount = 4;
    private const string UserDefinedParamPrefix = "USER_DEFINED_PARAM_";

    private readonly record struct Entry(string Key, ushort Format, uint Length, uint Capacity, ulong Offset);

    public static (string? Title, string? TitleId, string? Version) Read(ReadOnlySpan<byte> data)
    {
        string? title = null, titleId = null, version = null;
        foreach (var entry in Entries(data))
        {
            if (entry.Key is not ("TITLE" or "TITLE_ID" or "APP_VER"))
                continue;
            if (entry.Format != 0x204 || entry.Length == 0 || entry.Length > entry.Capacity ||
                entry.Offset > (ulong)data.Length || entry.Capacity > (ulong)data.Length - entry.Offset)
                throw new InvalidDataException($"Invalid PS4 param.sfo value for {entry.Key}.");
            var text = data.Slice((int)entry.Offset, (int)entry.Length);
            if (text[^1] != 0)
                throw new InvalidDataException($"Unterminated PS4 param.sfo value for {entry.Key}.");
            var value = Encoding.UTF8.GetString(text[..^1]);
            switch (entry.Key)
            {
                case "TITLE": title = value; break;
                case "TITLE_ID": titleId = value; break;
                case "APP_VER": version = value; break;
            }
        }
        return (title, titleId, version);
    }

    public static int[] ReadUserDefinedParams(ReadOnlySpan<byte> data)
    {
        var values = new int[UserDefinedParamCount];
        foreach (var entry in Entries(data))
        {
            if (entry.Key.Length != UserDefinedParamPrefix.Length + 1 ||
                !entry.Key.StartsWith(UserDefinedParamPrefix, StringComparison.Ordinal) ||
                entry.Key[^1] is < '1' or > (char)('0' + UserDefinedParamCount) ||
                entry.Format != 0x404 || entry.Length != sizeof(int) ||
                entry.Offset > (ulong)data.Length - sizeof(int))
                continue;
            values[entry.Key[^1] - '1'] = BinaryPrimitives.ReadInt32LittleEndian(data[(int)entry.Offset..]);
        }
        return values;
    }

    public static int ReadCpuMode(ReadOnlySpan<byte> data)
    {
        foreach (var entry in Entries(data))
        {
            if (entry.Key != "ATTRIBUTE" || entry.Format != 0x404 || entry.Length != sizeof(uint) ||
                entry.Capacity < sizeof(uint) || entry.Offset > (ulong)data.Length - sizeof(uint))
                continue;
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)entry.Offset..]);
            return ((attributes >> 15) & 3) switch
            {
                3 => 2,
                2 => 5,
                _ => 0,
            };
        }
        return 0;
    }

    private static List<Entry> Entries(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || !data[..4].SequenceEqual("\0PSF"u8))
            throw new InvalidDataException("Invalid PS4 param.sfo header.");
        var keys = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var values = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var entriesEnd = 20UL + (ulong)count * 16;
        if (entriesEnd > keys || keys > values || values > data.Length)
            throw new InvalidDataException("Invalid PS4 param.sfo table bounds.");

        var entries = new List<Entry>((int)count);
        for (var i = 0u; i < count; i++)
        {
            var entry = data.Slice(checked(20 + (int)i * 16), 16);
            var keyOffset = (ulong)keys + BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (keyOffset >= values)
                throw new InvalidDataException("Invalid PS4 param.sfo key offset.");
            var keyBytes = data.Slice((int)keyOffset, checked((int)(values - keyOffset)));
            var terminator = keyBytes.IndexOf((byte)0);
            if (terminator < 0)
                throw new InvalidDataException("Unterminated PS4 param.sfo key.");
            entries.Add(new Entry(Encoding.UTF8.GetString(keyBytes[..terminator]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                (ulong)values + BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])));
        }
        return entries;
    }
}
