// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMappingRangeTests
{
    [Theory]
    [InlineData(0x0800UL, 0x0800UL)]
    [InlineData(0x1000UL, 0x1000UL)]
    [InlineData(0x1800UL, 0x0100UL)]
    [InlineData(0x2000UL, 0x1000UL)]
    [InlineData(0x2800UL, 0x2000UL)]
    [InlineData(0x3000UL, 0x1000UL)]
    [InlineData(0x5000UL, 0x1000UL)]
    [InlineData(0x1800UL, 0x6000UL)]
    public void QueryPreservesBoundariesAndAllOverlappingRecords(ulong address, ulong size)
    {
        using var table = new MappingTableScope();
        MappingRecord[] records =
        [
            new(0x1000, 0x2000, 0x33, false, true, 0x8000, 0x8000, false),
            new(0x3000, 0x2000, 3, true, false, 0, 0x4000000, false),
            new(0x6000, 0x1000, 0, false, false, 0, 0, true),
            new(0x7000, 0x1000, 1, false, false, 0, 0, false),
        ];
        table.Set(records);
        table.AssertQueryMatches(records, address, size);
    }

    [Fact]
    public void EmptyTableHasNoOverlaps()
    {
        using var table = new MappingTableScope();
        table.AssertQueryMatches([], 0x1000, 0x4000);
    }

    [Fact]
    public void GeneratedQueriesMatchTheFullScan()
    {
        using var table = new MappingTableScope();
        var random = new System.Random(613);
        var records = new List<MappingRecord>();
        ulong currentAddress = 0x10000;
        for (var index = 0; index < 5000; index++)
        {
            currentAddress += (ulong)random.Next(0, 4) * 0x1000;
            var length = (ulong)random.Next(1, 9) * 0x1000;
            records.Add(new MappingRecord(currentAddress, length, index % 4,
                index % 4 == 1, index % 4 == 0, (ulong)index * 0x10000,
                (ulong)index * 0x10000, index % 4 == 2));
            currentAddress += length;
        }
        table.Set(records);
        for (var index = 0; index < 1000; index++)
        {
            var address = (ulong)random.NextInt64((long)currentAddress + 0x10000);
            var size = (ulong)random.Next(1, 0x40000);
            table.AssertQueryMatches(records, address, size);
            table.AssertGapMatches(records, address, size, 0x4000);
        }
        table.AssertQueryMatches(records, records[2500].Address + 8, 0);
        table.AssertQueryMatches(records, ulong.MaxValue - 8, 16);
    }

    [Theory]
    [InlineData(0x3000UL, 0x2000UL)]
    [InlineData(0x3000UL, 0x1000UL)]
    [InlineData(0x3800UL, 0x0800UL)]
    [InlineData(0x3000UL, 0x4000UL)]
    public void ReplacementPreservesNeighborsAndUpdatesAllFields(ulong address, ulong length)
    {
        using var table = new MappingTableScope();
        MappingRecord[] original =
        [
            new(0x1000, 0x2000, 3, false, true, 0x9000, 0x9000, false),
            new(0x3000, 0x2000, 3, false, true, 0xB000, 0xB000, false),
            new(0x5000, 0x2000, 3, false, true, 0xD000, 0xD000, false),
        ];
        table.Set(original);
        MappingRecord[] replacements =
        [
            new(address, length, 0, false, false, 0, 0, true),
            new(address, length, 1, true, false, 0, 0x4000000, false),
            new(address, length, 0x33, false, true, 0xA0000, 0xA0000, false),
        ];
        foreach (var replacement in replacements)
        {
            table.Replace(replacement);
            var expected = new List<MappingRecord>();
            foreach (var region in original)
            {
                var end = region.Address + region.Length;
                if (end <= address || region.Address >= address + length)
                    expected.Add(region);
                else
                {
                    if (region.Address < address)
                        expected.Add(region.Slice(region.Address, address));
                    if (end > address + length)
                        expected.Add(region.Slice(address + length, end));
                }
            }
            expected.Add(replacement);
            table.AssertQueryMatches(expected.OrderBy(region => region.Address), 0, 0x10000);
            table.AssertDirectQueryMatches(expected, 0, 0x100000);
        }
    }

    [Fact]
    public void GeneratedReplacementsAndRemovalsPreserveRangeQueries()
    {
        using var table = new MappingTableScope();
        var random = new System.Random(917);
        var expected = new List<MappingRecord>();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var address = (ulong)random.Next(1, 128) * 0x1000;
            var length = (ulong)random.Next(1, 8) * 0x1000;
            var end = address + length;
            var remaining = new List<MappingRecord>();
            foreach (var region in expected)
            {
                var regionEnd = region.Address + region.Length;
                if (regionEnd <= address || region.Address >= end)
                    remaining.Add(region);
                else
                {
                    if (region.Address < address)
                        remaining.Add(region.Slice(region.Address, address));
                    if (regionEnd > end)
                        remaining.Add(region.Slice(end, regionEnd));
                }
            }
            if (random.Next(3) == 0)
                table.Remove(address, length);
            else
            {
                var directStart = (ulong)random.Next(0, 32) * 0x1000;
                var isDirect = iteration % 4 != 0;
                var replacement = new MappingRecord(address, length, iteration % 4,
                    !isDirect, isDirect, isDirect ? directStart : 0, directStart, false);
                table.Replace(replacement);
                remaining.Add(replacement);
            }
            expected = remaining.OrderBy(region => region.Address).ToList();
            table.AssertQueryMatches(expected, 0, 0x100000);
            table.AssertQueryMatches(expected, address + 1, length);
            table.AssertDirectQueryMatches(expected, 0, 0x100000);
            table.AssertDirectQueryMatches(expected, (ulong)random.Next(0, 40) * 0x1000, length);
            table.AssertDirectQueryMatches(expected, 0x1000, 0);
            table.AssertGapMatches(expected, address, length, 0x4000);
            table.AssertGapMatches(expected, 0x1000, length, 0x1000);
        }
    }

    [Fact]
    public void GapSearchPreservesInvalidRecordFallbackAndClear()
    {
        using var table = new MappingTableScope();
        MappingRecord[] records =
        [
            new(0x1000, 0, 0, false, false, 0, 0, false),
            new(ulong.MaxValue - 8, 16, 0, false, false, 0, 0, false),
        ];
        table.Set(records);
        table.AssertGapMatches(records, 0x1000, 0x1000, 0x1000);
        table.Set([]);
        table.AssertGapMatches([], 0x1000, 0x1000, 0x1000);
        table.AssertGapMatches([], ulong.MaxValue - 8, 16, 0x1000);
    }

    private readonly record struct MappingRecord(ulong Address, ulong Length, int Protection,
        bool IsFlexible, bool IsDirect, ulong DirectStart, ulong BackingOffset, bool IsReserved)
    {
        public MappingRecord Slice(ulong start, ulong end) => this with
        {
            Address = start,
            Length = end - start,
            DirectStart = IsDirect ? DirectStart + start - Address : 0,
            BackingOffset = IsDirect || IsFlexible ? BackingOffset + start - Address : 0,
        };
    }

    private sealed class MappingTableScope : IDisposable
    {
        private static readonly Type OwnerType = typeof(KernelMemoryCompatExports);
        private static readonly ConstructorInfo RegionConstructor = OwnerType
            .GetNestedType("MappedRegion", BindingFlags.NonPublic)!.GetConstructors().Single();
        private static readonly Func<ulong, ulong, bool, Array> Query = OwnerType
            .GetMethod("GetMappingSlices", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ulong, ulong, bool, Array>>();
        private readonly object _mappingGate;
        private readonly object _mappings;
        private readonly object[] _previousMappings;
        private static readonly Type TableType = OwnerType.GetNestedType("MappedRegionTable", BindingFlags.NonPublic)!;
        private static readonly PropertyInfo Values = TableType.GetProperty("Values")!;
        private static readonly PropertyInfo Item = TableType.GetProperty("Item")!;
        private static readonly MethodInfo Clear = TableType.GetMethod("Clear")!;

        public MappingTableScope()
        {
            _mappingGate = OwnerType.GetField("_memoryGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _mappings = OwnerType.GetField("_mappedRegions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Monitor.Enter(_mappingGate);
            try
            {
                _previousMappings = ((IEnumerable)Values.GetValue(_mappings)!).Cast<object>().ToArray();
                Clear.Invoke(_mappings, null);
            }
            catch
            {
                Monitor.Exit(_mappingGate);
                throw;
            }
        }

        public void Set(IEnumerable<MappingRecord> records)
        {
            Clear.Invoke(_mappings, null);
            foreach (var record in records)
                Item.SetValue(_mappings, CreateNativeRecord(record), [record.Address]);
        }

        public void Replace(MappingRecord replacement) => OwnerType
            .GetMethod("ReplaceMappedRegionRangeLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [CreateNativeRecord(replacement)]);

        public void Remove(ulong address, ulong length) => OwnerType
            .GetMethod("RemoveMappingLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [address, length]);

        public void AssertQueryMatches(IEnumerable<MappingRecord> records, ulong address, ulong size)
        {
            foreach (var clip in new[] { false, true })
            {
                var expected = records
                    .Where(record => record.Address < address + size && address < record.Address + record.Length)
                    .Select(record => clip ? record.Slice(Math.Max(address, record.Address),
                        Math.Min(address + size, record.Address + record.Length)) : record)
                    .Select(CreateNativeRecord).ToArray();
                Assert.Equal(expected, Query(address, size, clip).Cast<object>().ToArray());
            }
        }

        public void AssertDirectQueryMatches(IEnumerable<MappingRecord> records, ulong start, ulong length)
        {
            var expected = records.Where(record => length != 0 && record.IsDirect &&
                    record.DirectStart < start + length && start < record.DirectStart + record.Length)
                .OrderBy(record => record.Address).Select(CreateNativeRecord).ToArray();
            var actual = (Array)TableType.GetMethod("FindDirectOverlaps")!
                .Invoke(_mappings, [start, length])!;
            Assert.Equal(expected, actual.Cast<object>().ToArray());
        }

        public void AssertGapMatches(IEnumerable<MappingRecord> records, ulong start, ulong size, ulong alignment)
        {
            var expected = Scan();
            var actual = (ulong)OwnerType.GetMethod("FindAvailableMappingAddress",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [start, size, alignment])!;
            Assert.Equal(expected, actual);

            ulong Scan()
            {
                var padding = (alignment - start % alignment) % alignment;
                if (padding > ulong.MaxValue - start)
                    return 0;
                var candidate = start + padding;
                foreach (var record in records.OrderBy(record => record.Address))
                {
                    if (size > ulong.MaxValue - candidate)
                        return 0;
                    if (record.Address >= candidate + size)
                        break;
                    var end = unchecked(record.Address + record.Length);
                    if (end <= candidate)
                        continue;
                    padding = (alignment - end % alignment) % alignment;
                    if (padding > ulong.MaxValue - end)
                        return 0;
                    candidate = end + padding;
                }
                return size <= ulong.MaxValue - candidate ? candidate : 0;
            }
        }

        private static object CreateNativeRecord(MappingRecord record) => RegionConstructor.Invoke(
            [record.Address, record.Length, record.Protection, record.IsFlexible,
                record.IsDirect, record.DirectStart, record.BackingOffset, record.IsReserved]);

        public void Dispose()
        {
            try
            {
                Clear.Invoke(_mappings, null);
                foreach (var entry in _previousMappings)
                    Item.SetValue(_mappings, entry, [entry.GetType().GetProperty("Address")!.GetValue(entry)]);
            }
            finally
            {
                Monitor.Exit(_mappingGate);
            }
        }
    }
}
