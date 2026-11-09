// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestPermissionLedgerTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    [Theory]
    [InlineData(17)]
    [InlineData(631)]
    public void RandomUpdatesMatchPermissionArray(int seed)
    {
        var ledger = new GuestPermissionLedger();
        var expected = Enumerable.Repeat(ReadWrite, 256).ToArray();
        var random = new System.Random(seed);
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var start = random.Next(expected.Length);
            var length = random.Next(1, expected.Length - start + 1);
            var protection = (GuestPageProtection)random.Next(8);
            if (random.Next(3) == 0)
            {
                ledger.Clear((ulong)start, (ulong)length);
                protection = ReadWrite;
            }
            else
                ledger.Set((ulong)start, (ulong)length, protection);
            Array.Fill(expected, protection, start, length);
            for (var address = 0; address < expected.Length; address++)
                Assert.Equal(expected[address], ledger.Lookup((ulong)address));
            // The end a lookup names never passes a change of answer.
            var answerEnd = expected[^1] == ReadWrite ? ulong.MaxValue : (ulong)expected.Length;
            for (var address = expected.Length - 1; address >= 0; address--)
            {
                if (address + 1 < expected.Length && expected[address + 1] != expected[address])
                    answerEnd = (ulong)address + 1;
                Assert.Equal(expected[address], ledger.Lookup((ulong)address, out var validEnd));
                Assert.InRange(validEnd, (ulong)address + 1, answerEnd);
            }
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong address = 0; address < 256; address++)
        {
            _ = ledger.Lookup(address);
            _ = ledger.Lookup(address, out _);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Set_SplitsTheSpansItOverlaps()
    {
        var ledger = new GuestPermissionLedger();
        ledger.Set(0x1000, 0x3000, GuestPageProtection.None);
        ledger.Set(0x2000, 0x1000, GuestPageProtection.Read);

        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1000));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1FFF));
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2000));
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2FFF));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x3000));
        Assert.Equal(ReadWrite, ledger.Lookup(0x4000));

        ledger.Set(0x0000, 0x8000, GuestPageProtection.Execute);
        Assert.Equal(GuestPageProtection.Execute, ledger.Lookup(0x2800));
    }

    [Fact]
    public void Clear_KeepsTheOuterParts()
    {
        var ledger = new GuestPermissionLedger();
        ledger.Set(0x1000, 0x3000, GuestPageProtection.None);

        ledger.Clear(0x2000, 0x1000);

        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1000));
        Assert.Equal(ReadWrite, ledger.Lookup(0x2000));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x3000));
    }

    [Fact]
    public void Lookup_DefaultsToReadWriteWithoutARecord()
    {
        var ledger = new GuestPermissionLedger();

        Assert.Equal(ReadWrite, ledger.Lookup(0));
        Assert.Equal(ReadWrite, ledger.Lookup(0x10000));
    }

    // A caller walking pages upward reuses one answer below the address the lookup names: the end of the
    // record that holds the address, or the start of the next record in a gap.
    [Fact]
    public void Lookup_NamesWhereItsAnswerStopsHolding()
    {
        var ledger = new GuestPermissionLedger();
        Assert.Equal(ReadWrite, ledger.Lookup(0x5000, out var end));
        Assert.Equal(ulong.MaxValue, end);

        ledger.Set(0x2000, 0x2000, GuestPageProtection.Read);
        ledger.Set(0x4000, 0x0800, GuestPageProtection.Execute);
        ledger.Set(0x6000, 0x1000, GuestPageProtection.None);

        // Before the first record, at both ends of a record, where two records touch, in a gap and past
        // the last record.
        Assert.Equal(ReadWrite, ledger.Lookup(0, out end));
        Assert.Equal(0x2000UL, end);
        Assert.Equal(ReadWrite, ledger.Lookup(0x1FFF, out end));
        Assert.Equal(0x2000UL, end);
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2000, out end));
        Assert.Equal(0x4000UL, end);
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x3FFF, out end));
        Assert.Equal(0x4000UL, end);
        Assert.Equal(GuestPageProtection.Execute, ledger.Lookup(0x4000, out end));
        Assert.Equal(0x4800UL, end);
        Assert.Equal(ReadWrite, ledger.Lookup(0x4800, out end));
        Assert.Equal(0x6000UL, end);
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x6FFF, out end));
        Assert.Equal(0x7000UL, end);
        Assert.Equal(ReadWrite, ledger.Lookup(0x7000, out end));
        Assert.Equal(ulong.MaxValue, end);

        // A split record names the end of its own part; a cleared range is a gap up to the next record.
        ledger.Set(0x2800, 0x0800, GuestPageProtection.None);
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2000, out end));
        Assert.Equal(0x2800UL, end);
        ledger.Clear(0x3000, 0x1000);
        Assert.Equal(ReadWrite, ledger.Lookup(0x3000, out end));
        Assert.Equal(0x4000UL, end);
    }

    // Lookups take no lock while map and mprotect events change the ledger, so each one must answer,
    // with the end of its record, from the records before or after a change. The writer splits
    // three-page records and joins them again (their outer pages are removed and set again, always
    // Read) and adds and removes records in the gaps between them, so the tree rebalances under the
    // readers.
    [Fact]
    public void Lookup_AnswersFromWholeChangesWhileTheLedgerChanges()
    {
        const ulong first = 0x10_0000;
        const ulong stride = 0x4000;
        const int records = 64;
        const int readerCount = 4;
        var ledger = new GuestPermissionLedger();
        for (var record = 0UL; record < records; record++)
            ledger.Set(first + record * stride, 0x3000, GuestPageProtection.Read);

        var started = 0;
        var done = false;
        var wrong = 0;
        var readers = Enumerable.Range(0, readerCount).Select(_ => new Thread(() =>
        {
            Interlocked.Increment(ref started);
            while (!Volatile.Read(ref done))
            {
                for (var record = 0UL; record < records; record++)
                {
                    var start = first + record * stride;
                    var next = record + 1 < records ? start + stride : ulong.MaxValue;
                    var outer = (ledger.Lookup(start, out var end), end);
                    var middle = (ledger.Lookup(start + 0x1800, out end), end);
                    var last = (ledger.Lookup(start + 0x2FFF, out end), end);
                    var gap = (ledger.Lookup(start + 0x3800, out end), end);
                    if ((outer != (GuestPageProtection.Read, start + 0x3000) && outer != (GuestPageProtection.Read, start + 0x1000)) ||
                        (middle != (GuestPageProtection.Read, start + 0x3000) && middle != (GuestPageProtection.None, start + 0x2000)) ||
                        last != (GuestPageProtection.Read, start + 0x3000) ||
                        (gap != (ReadWrite, next) && gap != (GuestPageProtection.Execute, start + 0x4000)))
                    {
                        Interlocked.Increment(ref wrong);
                    }
                }
            }
        })).ToArray();
        foreach (var reader in readers)
            reader.Start();

        while (Volatile.Read(ref started) != readerCount)
            Thread.Yield();
        var random = new System.Random(5);
        for (var round = 0; round < 20000; round++)
        {
            var start = first + (ulong)random.Next(records) * stride;
            if ((round & 1) == 0)
            {
                ledger.Set(start + 0x1000, 0x1000, GuestPageProtection.None);
                ledger.Set(start, 0x3000, GuestPageProtection.Read);
            }
            else
            {
                ledger.Set(start + 0x3000, 0x1000, GuestPageProtection.Execute);
                ledger.Clear(start + 0x3000, 0x1000);
            }
        }

        Volatile.Write(ref done, true);
        foreach (var reader in readers)
            reader.Join();
        Assert.Equal(0, wrong);
    }

    // A snapshot keeps the records it was taken with while the tree it came from changes.
    [Fact]
    public void Snapshot_KeepsItsRecordsWhileTheTreeChanges()
    {
        var tree = new PermissionRangeTree();
        for (var index = 0UL; index < 32; index++)
            tree[index * 0x10] = new PermissionRange(index * 0x10, 0x8, GuestPageProtection.Read);
        var snapshot = tree.Snapshot();
        var before = snapshot.ToArray();

        for (var index = 0UL; index < 32; index += 2)
            tree.Remove(new PermissionRange(index * 0x10, 0x8, GuestPageProtection.Read));
        for (var index = 32UL; index < 64; index++)
            tree[index * 0x10] = new PermissionRange(index * 0x10, 0x8, GuestPageProtection.None);
        tree[0x10] = new PermissionRange(0x10, 0x4, GuestPageProtection.Execute);

        Assert.Equal(before, snapshot.ToArray());
        var expected = Enumerable.Range(0, 64).Where(index => index >= 32 || index % 2 == 1)
            .Select(index => new PermissionRange((ulong)index * 0x10, index == 1 ? 0x4UL : 0x8UL,
                index == 1 ? GuestPageProtection.Execute : index < 32 ? GuestPageProtection.Read : GuestPageProtection.None));
        Assert.Equal(expected, tree.ToArray());
        Assert.Equal(new PermissionRange(0x10, 0x8, GuestPageProtection.Read), snapshot.FindAtOrBelow(0x17));
    }

    [Fact]
    public void Lookup_ReportsWhereTheAnswerStopsHolding()
    {
        var ledger = new GuestPermissionLedger();
        Assert.Equal(ReadWrite, ledger.Lookup(0x5000, out var emptyUntil));
        Assert.Equal(ulong.MaxValue, emptyUntil);

        ledger.Set(0x2000, 0x2000, GuestPageProtection.Read);
        ledger.Set(0x6000, 0x1000, GuestPageProtection.None);

        Assert.Equal(ReadWrite, ledger.Lookup(0x1000, out var beforeFirst));
        Assert.Equal(0x2000UL, beforeFirst);
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2000, out var firstStart));
        Assert.Equal(0x4000UL, firstStart);
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x3FFF, out var firstLast));
        Assert.Equal(0x4000UL, firstLast);
        Assert.Equal(ReadWrite, ledger.Lookup(0x4000, out var hole));
        Assert.Equal(0x6000UL, hole);
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x6000, out var second));
        Assert.Equal(0x7000UL, second);
        Assert.Equal(ReadWrite, ledger.Lookup(0x7000, out var afterLast));
        Assert.Equal(ulong.MaxValue, afterLast);

        ledger.Clear(0x2000, 0x2000);
        Assert.Equal(ReadWrite, ledger.Lookup(0x2000, out var cleared));
        Assert.Equal(0x6000UL, cleared);
    }

    [Fact]
    public void Lookup_ConcurrentWithSetSeesEitherTheOldOrTheNewProtection()
    {
        var ledger = new GuestPermissionLedger();
        ledger.Set(0x10000, 0x10000, GuestPageProtection.Read);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var round = 0; !stop.IsCancellationRequested; round++)
            {
                ledger.Set(0x10000 + (ulong)(round % 16) * 0x1000, 0x1000, round % 2 == 0 ? GuestPageProtection.None : GuestPageProtection.Read);
                ledger.Set(0x40000 + (ulong)(round % 8) * 0x1000, 0x1000, GuestPageProtection.Execute);
                ledger.Clear(0x40000 + (ulong)(round % 8) * 0x1000, 0x1000);
            }
        });

        var unexpected = 0;
        for (var index = 0; index < 200_000; index++)
        {
            var address = 0x10000 + (ulong)(index % 16) * 0x1000 + 0x800;
            var protection = ledger.Lookup(address, out var validUntil);
            if ((protection != GuestPageProtection.None && protection != GuestPageProtection.Read) || validUntil <= address)
            {
                unexpected++;
            }

            if (ledger.Lookup(0x30000) != ReadWrite)
            {
                unexpected++;
            }
        }

        stop.Cancel();
        writer.Wait();
        Assert.Equal(0, unexpected);
    }
}
