// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Core.Loader;

internal static class SceImportMetadataParser
{
    private const int DynamicEntrySize = 16;
    private const long DtNull = 0;
    private const long DtSceNeededModule = 0x6100000F;
    private const long DtSceImportLibrary = 0x61000015;
    private const long DtSceNeededModuleGen5 = 0x61000045;
    private const long DtSceImportLibraryGen5 = 0x61000049;
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";

    internal static SceImportNameTables Parse(
        ReadOnlySpan<byte> dynamicTable,
        ReadOnlySpan<byte> stringTable)
    {
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var offset = 0; offset + DynamicEntrySize <= dynamicTable.Length; offset += DynamicEntrySize)
        {
            var entry = dynamicTable[offset..];
            var tag = BinaryPrimitives.ReadInt64LittleEndian(entry);
            if (tag == DtNull)
            {
                break;
            }

            if (tag is not (DtSceNeededModule or DtSceImportLibrary or
                DtSceNeededModuleGen5 or DtSceImportLibraryGen5))
            {
                continue;
            }

            var value = BinaryPrimitives.ReadUInt64LittleEndian(entry[sizeof(long)..]);
            var nameOffset = (uint)value;
            var id = (ushort)(value >> 48);
            if (!TryReadNullTerminatedAscii(stringTable, nameOffset, out var name))
            {
                continue;
            }

            var encodedId = EncodeId(id);
            var destination = tag is DtSceImportLibrary or DtSceImportLibraryGen5
                ? libraries
                : modules;
            destination.TryAdd(encodedId, name);
        }

        return new SceImportNameTables(libraries, modules);
    }

    internal static bool TryResolve(
        string symbolName,
        SceImportNameTables names,
        out string nid,
        out ImportedSymbolMetadata metadata)
    {
        nid = string.Empty;
        metadata = default;
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return false;
        }

        var firstSeparator = symbolName.IndexOf('#');
        if (firstSeparator <= 0)
        {
            nid = symbolName;
            return false;
        }

        nid = symbolName[..firstSeparator];
        var secondSeparator = symbolName.IndexOf('#', firstSeparator + 1);
        if (secondSeparator <= firstSeparator + 1 || secondSeparator == symbolName.Length - 1)
        {
            return false;
        }

        var libraryId = symbolName[(firstSeparator + 1)..secondSeparator];
        var moduleId = symbolName[(secondSeparator + 1)..];
        names.Libraries.TryGetValue(libraryId, out var libraryName);
        names.Modules.TryGetValue(moduleId, out var moduleName);
        metadata = new ImportedSymbolMetadata(libraryName, moduleName);
        return libraryName is not null || moduleName is not null;
    }

    internal static string ExtractNid(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return string.Empty;
        }

        var separator = symbolName.IndexOf('#');
        return separator <= 0 ? symbolName : symbolName[..separator];
    }

    private static string EncodeId(ushort value)
    {
        Span<char> encoded = stackalloc char[3];
        var length = value switch
        {
            < 0x40 => 1,
            < 0x1000 => 2,
            _ => 3,
        };

        for (var index = length - 1; index >= 0; index--)
        {
            encoded[index] = IdAlphabet[value & 0x3F];
            value >>= 6;
        }

        return new string(encoded[..length]);
    }

    private static bool TryReadNullTerminatedAscii(
        ReadOnlySpan<byte> source,
        uint offset,
        out string value)
    {
        if (offset >= (uint)source.Length)
        {
            value = string.Empty;
            return false;
        }

        var slice = source[(int)offset..];
        var terminatorIndex = slice.IndexOf((byte)0);
        if (terminatorIndex < 0)
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.ASCII.GetString(slice[..terminatorIndex]);
        return true;
    }
}

internal sealed record SceImportNameTables(
    IReadOnlyDictionary<string, string> Libraries,
    IReadOnlyDictionary<string, string> Modules)
{
    internal static readonly SceImportNameTables Empty = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));
}
