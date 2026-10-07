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
        }
        table.AssertQueryMatches(records, records[2500].Address + 8, 0);
        table.AssertQueryMatches(records, ulong.MaxValue - 8, 16);
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
        private readonly IDictionary _mappings;
        private readonly DictionaryEntry[] _previousMappings;

        public MappingTableScope()
        {
            _mappingGate = OwnerType.GetField("_memoryGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            _mappings = (IDictionary)OwnerType.GetField("_mappedRegions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Monitor.Enter(_mappingGate);
            try
            {
                _previousMappings = _mappings.Keys.Cast<object>()
                    .Select(key => new DictionaryEntry(key, _mappings[key])).ToArray();
                _mappings.Clear();
            }
            catch
            {
                Monitor.Exit(_mappingGate);
                throw;
            }
        }

        public void Set(IEnumerable<MappingRecord> records)
        {
            _mappings.Clear();
            foreach (var record in records)
                _mappings.Add(record.Address, CreateNativeRecord(record));
        }

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

        private static object CreateNativeRecord(MappingRecord record) => RegionConstructor.Invoke(
            [record.Address, record.Length, record.Protection, record.IsFlexible,
                record.IsDirect, record.DirectStart, record.BackingOffset, record.IsReserved]);

        public void Dispose()
        {
            try
            {
                _mappings.Clear();
                foreach (var entry in _previousMappings)
                    _mappings.Add(entry.Key, entry.Value);
            }
            finally
            {
                Monitor.Exit(_mappingGate);
            }
        }
    }
}
