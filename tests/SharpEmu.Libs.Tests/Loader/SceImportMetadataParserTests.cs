// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class SceImportMetadataParserTests
{
    private const long NeededModuleTag = 0x6100000F;
    private const long ImportLibraryTag = 0x61000015;
    private const long NeededModuleGen5Tag = 0x61000045;
    private const long ImportLibraryGen5Tag = 0x61000049;

    [Fact]
    public void Parse_ResolvesLibraryAndModuleFromEncodedSymbolSuffix()
    {
        var stringTable = Encoding.ASCII.GetBytes("\0libScePad\0libScePad.prx\0");
        Span<byte> dynamicTable = stackalloc byte[48];
        WriteDynamicEntry(dynamicTable, 0, ImportLibraryTag, PackReference(nameOffset: 1, id: 10));
        WriteDynamicEntry(dynamicTable, 16, NeededModuleTag, PackReference(nameOffset: 11, id: 10));

        var names = SceImportMetadataParser.Parse(dynamicTable, stringTable);

        Assert.True(SceImportMetadataParser.TryResolve(
            "AcslpN1jHR8#K#K",
            names,
            out var nid,
            out var metadata));
        Assert.Equal("AcslpN1jHR8", nid);
        Assert.Equal("libScePad", metadata.LibraryName);
        Assert.Equal("libScePad.prx", metadata.ModuleName);
        Assert.Equal(" library=libScePad module=libScePad.prx", metadata.DiagnosticSuffix);
    }

    [Fact]
    public void Parse_ResolvesGen5DynamicTags()
    {
        const int nameOffset = 0x2727;
        var stringTable = new byte[nameOffset + 10];
        Encoding.ASCII.GetBytes("libScePad\0").CopyTo(stringTable, nameOffset);
        Span<byte> dynamicTable = stackalloc byte[48];
        // Values observed in a Gen5 ELF: module version 1.1 and library version 1,
        // both with encoded id K (numeric id 10) and the same string-table offset.
        WriteDynamicEntry(dynamicTable, 0, NeededModuleGen5Tag, 0x000A_0101_0000_2727);
        WriteDynamicEntry(dynamicTable, 16, ImportLibraryGen5Tag, 0x000A_0001_0000_2727);

        var names = SceImportMetadataParser.Parse(dynamicTable, stringTable);

        Assert.True(SceImportMetadataParser.TryResolve(
            "AcslpN1jHR8#K#K",
            names,
            out _,
            out var metadata));
        Assert.Equal("libScePad", metadata.LibraryName);
        Assert.Equal("libScePad", metadata.ModuleName);
    }

    [Fact]
    public void Parse_KeepsDistinctLibraryAndModuleIdsInSymbolOrder()
    {
        var stringTable = Encoding.ASCII.GetBytes("\0libkernel\0libkernel.prx\0");
        Span<byte> dynamicTable = stackalloc byte[48];
        WriteDynamicEntry(dynamicTable, 0, NeededModuleGen5Tag, PackReference(nameOffset: 11, id: 13));
        WriteDynamicEntry(dynamicTable, 16, ImportLibraryGen5Tag, PackReference(nameOffset: 1, id: 36));

        var names = SceImportMetadataParser.Parse(dynamicTable, stringTable);

        Assert.True(SceImportMetadataParser.TryResolve(
            "12wOHk8ywb0#k#N",
            names,
            out _,
            out var metadata));
        Assert.Equal("libkernel", metadata.LibraryName);
        Assert.Equal("libkernel.prx", metadata.ModuleName);
    }

    [Theory]
    [InlineData("MjQ5oH6b620", "MjQ5oH6b620")]
    [InlineData("MjQ5oH6b620#K", "MjQ5oH6b620")]
    [InlineData("MjQ5oH6b620##K", "MjQ5oH6b620")]
    public void TryResolve_IncompleteMetadata_PreservesNidWithoutInventingNames(
        string symbolName,
        string expectedNid)
    {
        Assert.False(SceImportMetadataParser.TryResolve(
            symbolName,
            SceImportNameTables.Empty,
            out var nid,
            out var metadata));
        Assert.Equal(expectedNid, nid);
        Assert.Equal(string.Empty, metadata.DiagnosticSuffix);
    }

    private static ulong PackReference(uint nameOffset, ushort id) =>
        nameOffset | ((ulong)id << 48);

    private static void WriteDynamicEntry(
        Span<byte> table,
        int offset,
        long tag,
        ulong value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(table[offset..], tag);
        BinaryPrimitives.WriteUInt64LittleEndian(table[(offset + sizeof(long))..], value);
    }
}
