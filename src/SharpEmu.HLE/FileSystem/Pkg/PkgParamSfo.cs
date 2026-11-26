// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A tiny param.sfo reader for package metadata. HLE cannot reference the GnmMode loader, so this
// reads just enough to look up string values by key. Anything malformed yields null.
public static class PkgParamSfo
{
    private const uint Magic = 0x46535000;
    private const int HeaderSize = 0x14;
    private const int IndexEntrySize = 16;

    public static string? ReadString(ReadOnlySpan<byte> sfo, string key)
    {
        if (sfo.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(sfo) != Magic)
        {
            return null;
        }

        var keyTable = BinaryPrimitives.ReadUInt32LittleEndian(sfo[8..]);
        var dataTable = BinaryPrimitives.ReadUInt32LittleEndian(sfo[0xC..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(sfo[0x10..]);
        if (keyTable > sfo.Length || dataTable > sfo.Length || count > (uint)((sfo.Length - HeaderSize) / IndexEntrySize))
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            var entry = sfo.Slice(HeaderSize + i * IndexEntrySize, IndexEntrySize);
            var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var format = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);

            var keyStart = (long)keyTable + keyOffset;
            if (keyStart >= sfo.Length)
            {
                return null;
            }

            var keyBytes = sfo[(int)keyStart..];
            var keyEnd = keyBytes.IndexOf((byte)0);
            if (keyEnd < 0)
            {
                return null;
            }

            if (!Encoding.ASCII.GetString(keyBytes[..keyEnd]).Equals(key, StringComparison.Ordinal))
            {
                continue;
            }

            if (format is not (0x0204 or 0x0004))
            {
                return null;
            }

            var valueStart = (long)dataTable + dataOffset;
            if (valueStart + length > sfo.Length)
            {
                return null;
            }

            var value = sfo.Slice((int)valueStart, (int)length);
            var nul = value.IndexOf((byte)0);
            if (nul >= 0)
            {
                value = value[..nul];
            }

            return Encoding.UTF8.GetString(value);
        }

        return null;
    }
}
