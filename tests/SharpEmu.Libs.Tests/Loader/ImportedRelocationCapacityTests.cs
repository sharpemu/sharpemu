// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class ImportedRelocationCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(8192)]
    public void SparseAndFullImportsRetainOnlyRequiredCapacityAndAllRecordFields(int imports)
    {
        var rows = new Descriptor[8192]; var expected = new ImportedSymbolRelocation[imports];
        for (var index = 0; index < rows.Length; index++)
        {
            var target = (ulong)(index % 23); var addend = (long)index - 8192;
            // Duplicate NIDs/targets and negative addends are intentionally kept.
            var nid = index < imports ? "repeat-nid" : index % 2 == 0 ? null : "not-a-pointer";
            var kind = index < imports || index % 2 == 0 ? 0 : 3;
            rows[index] = new(target, addend, nid, kind, index % 2 != 0);
            if (index < imports) expected[index] = new(target, addend, "repeat-nid", index % 2 != 0);
        }
        var result = Build(rows);
        Assert.Equal(expected, result);
        if (imports == 0) Assert.Same(Array.Empty<ImportedSymbolRelocation>(), result);
        else Assert.Equal(imports, Assert.IsType<List<ImportedSymbolRelocation>>(result).Capacity);
    }

    [Fact]
    public void MixedKindsPreserveInputOrderDuplicatesEmptyNidAndDataClassification()
    {
        var result = Build([
            new(0x20, -7, "duplicate", 0, false),
            new(0x99, 3, "tls-module-skip", 1, true),
            new(0x30, 9, null, 0, true),
            new(0x20, -7, "duplicate", 0, true),
            new(0x40, 2, "", 0, true),
            new(0x70, -2, "pc-relative-skip", 3, false),
            new(0x80, 5, "symbol-size-skip", 4, false),
            new(0x50, 11, "last", 0, false),
        ]);
        Assert.Equal(new ImportedSymbolRelocation[] {
            new(0x20, -7, "duplicate", false),
            new(0x20, -7, "duplicate", true),
            new(0x40, 2, "", true),
            new(0x50, 11, "last", false),
        }, result);
        Assert.Equal(4, Assert.IsType<List<ImportedSymbolRelocation>>(result).Capacity);
    }

    [Fact]
    public void EmptyInputUsesExistingSharedEmptyResult() => Assert.Same(Array.Empty<ImportedSymbolRelocation>(), Build([]));

    private readonly record struct Descriptor(ulong Target, long Addend, string? Nid, int Kind, bool Data);

    private static IReadOnlyList<ImportedSymbolRelocation> Build(Descriptor[] rows)
    {
        // Exercise the production private method without widening Core's public
        // contract or adding extracted-package friend access for a loader test.
        var loader = typeof(SelfLoader);
        var descriptor = loader.GetNestedType("RelocationDescriptor", BindingFlags.NonPublic)!;
        var valueKind = loader.GetNestedType("RelocationValueKind", BindingFlags.NonPublic)!;
        var writeKind = loader.GetNestedType("RelocationWriteKind", BindingFlags.NonPublic)!;
        var values = Array.CreateInstance(descriptor, rows.Length);
        var constructor = descriptor.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 8);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            values.SetValue(constructor.Invoke([row.Target, row.Addend, row.Nid, 0ul, Enum.ToObject(valueKind, row.Kind), row.Data, Enum.ToObject(writeKind, 0), false]), index);
        }
        return (IReadOnlyList<ImportedSymbolRelocation>)loader.GetMethod("BuildImportedRelocations", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [values])!;
    }
}
