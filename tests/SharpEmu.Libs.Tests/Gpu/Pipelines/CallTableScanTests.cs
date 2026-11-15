// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Mode = SharpEmu.Libs.Gpu.Pipelines.ShaderCallLibrary.TableScanMode;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The one-pass table read (ShaderCallLibrary.TableScan.cs): on the same memory it returns the library the original read
// returns (ShaderCallLibraryMemo.Difference) and throws the original's exceptions.
public sealed class CallTableScanTests
{
    private const ulong TableA = 0x10_0000_0000;
    private const ulong TableB = 0x20_0000_0000;
    private const ulong Code = 0x200000;

    // Site 1 reads table A (records selected by s20), site 2 calls the pair user data s4..s7 hold, site 3 reads table B
    // (records selected by s21).
    private static Gen5ShaderProgram ThreeSites() => Program(
        Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
        ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
        Vop1(12, "VMovB32", 111, Operand(0)),
        new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
        MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
        Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
        MoveScalarRegister(40, 14, 4), MoveScalarRegister(44, 15, 5),
        MoveScalarRegister(48, 16, 6), MoveScalarRegister(52, 17, 7),
        Vop1(56, "VMovB32", 5, Operand(0)),
        Sop1(60, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
        Sop2(64, "SLshlB32", 21, Gen5Operand.Scalar(21), Operand(4)),
        ScalarBufferLoad(68, 8, 24, 4, dynamicOffsetRegister: 21),
        Vop1(76, "VMovB32", 111, Operand(0)),
        new(80, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        MoveScalarRegister(84, 14, 24), MoveScalarRegister(88, 15, 25),
        MoveScalarRegister(92, 16, 26), MoveScalarRegister(96, 17, 27),
        Sop1(100, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(104));

    // One site reading the record at a fixed offset.
    private static Gen5ShaderProgram FixedRecord(int offset) => Program(
        ScalarBufferLoad(0, 0, 16, 4, immediateOffset: offset),
        Vop1(8, "VMovB32", 111, Operand(0)),
        new(12, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        MoveScalarRegister(16, 14, 16), MoveScalarRegister(20, 15, 17),
        MoveScalarRegister(24, 16, 18), MoveScalarRegister(28, 17, 19),
        Sop1(32, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(36));

    private static MemoryAccessTable Access(params uint[] loadPcs)
    {
        var access = new MemoryAccessTable();
        for (var index = 0; index < loadPcs.Length; index++)
            access.Add(new() { Pc = loadPcs[index], Kind = MemoryResourceKind.ScalarBuffer, Resource = (uint)index });
        return access;
    }

    private static uint[] Descriptor(ulong address, int records) =>
        [unchecked((uint)address), (uint)(address >> 32) | (16u << 16), (uint)records, 0];

    // Guest memory: the tables, the callees' code (callee n at Code + n * 0x100, 1 + n % 5 nops then a return) and
    // addresses whose code cannot be read; a flaky address fails its first function read of each read pass.
    private sealed class Guest
    {
        public readonly Dictionary<ulong, byte[]> Tables = new();
        public readonly HashSet<ulong> Unreadable = [];
        public ulong Flaky = ulong.MaxValue;
        public bool FlakyFailed;
        public int Callees = 12;
        public bool TablesCopy = true;

        public bool ReadWord(ulong address, out uint word)
        {
            word = 0;
            foreach (var (start, bytes) in Tables)
            {
                if (address >= start && address - start + 4 <= (ulong)bytes.Length)
                {
                    word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(address - start)));
                    return true;
                }
            }

            if (address < Code || address >= Code + (ulong)Callees * 0x100) return false;
            var function = address - (address - Code) % 0x100;
            if (Unreadable.Contains(function)) return false;
            if (function == Flaky && !FlakyFailed)
            {
                FlakyFailed = true;
                return false;
            }

            var callee = (int)((function - Code) / 0x100);
            var index = (int)((address - function) / 4);
            var nops = 1 + callee % 5;
            word = index < nops ? 0xBF800000 : index == nops ? 0xBE80200E : 0;
            return index <= nops;
        }

        public bool ReadBytes(ulong address, Span<byte> destination, bool clean)
        {
            if (!TablesCopy) return false;
            foreach (var (start, bytes) in Tables)
            {
                if (address >= start && address - start + (ulong)destination.Length <= (ulong)bytes.Length)
                {
                    bytes.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
                    return true;
                }
            }

            return false;
        }
    }

    private static ShaderCallLibrary Read(Mode mode, Gen5ShaderProgram caller, MemoryAccessTable access, ResourceSnapshot snapshot,
        Guest guest, ShaderFunctionCache? functions, ShaderCallLibraryMemo? memo)
    {
        ShaderCallLibrary.TableScanOverride = mode;
        try
        {
            guest.FlakyFailed = false;
            return ShaderCallLibrary.Read(caller, access, snapshot, guest.ReadWord, functions, runtimeArguments: true, userDataBase: 0,
                readBytes: guest.ReadBytes, memo: memo);
        }
        finally
        {
            ShaderCallLibrary.TableScanOverride = null;
        }
    }

    private static Exception? Throws(Mode mode, Gen5ShaderProgram caller, MemoryAccessTable access, ResourceSnapshot snapshot,
        Guest guest, ShaderFunctionCache? functions = null)
    {
        try
        {
            Read(mode, caller, access, snapshot, guest, functions, null);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    // ShaderCallLibraryMemo.Difference without program identity, for reads with their own function caches.
    private static void SameShape(ShaderCallLibrary expected, ShaderCallLibrary actual)
    {
        static string Shape(ShaderCallSite site) => $"{site.Pc}:{site.AddressRegister}:{site.ArgumentRegister}:{site.ReturnAddress}:" +
            string.Join(",", site.Targets.Select(static target => $"{target.Address:X}/{target.Argument:X}/{target.Program.Instructions.Count}"));
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.LayoutIdentity, actual.LayoutIdentity);
        Assert.Equal(expected.Bodies, actual.Bodies);
        Assert.Equal(expected.Records, actual.Records);
        Assert.Equal(expected.Calls.Select(Shape), actual.Calls.Select(Shape));
        Assert.Equal(expected.CurrentCodeHashes.Keys, actual.CurrentCodeHashes.Keys);
        Assert.Equal(expected.CodeHashes.Keys.Order(), actual.CodeHashes.Keys.Order());
        Assert.Equal(expected.CurrentSites.Keys.Order(), actual.CurrentSites.Keys.Order());
        foreach (var (address, sites) in expected.CurrentSites) Assert.True(sites.SetEquals(actual.CurrentSites[address]));
    }

    private static void Record(byte[] table, int index, ulong target, ulong argument)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(index * 16), target);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(index * 16 + 8), argument);
    }

    // Random tables: most records zero, the rest naming twelve callees (two of them unreadable) with arguments from a small
    // pool, so records repeat within and across tables; user data names one of them.
    private static (Guest Guest, ResourceSnapshot Snapshot) RandomTables(int seed, int recordsA, int recordsB)
    {
        var random = new System.Random(seed);
        var guest = new Guest();
        guest.Unreadable.Add(Code + 3 * 0x100);
        guest.Unreadable.Add(Code + 7 * 0x100);
        var a = new byte[recordsA * 16];
        var b = new byte[recordsB * 16];
        foreach (var table in new[] { a, b })
        {
            for (var index = 0; index < table.Length / 16; index++)
            {
                if (random.Next(10) < 7) continue;
                Record(table, index, Code + (ulong)random.Next(guest.Callees) * 0x100, 0x5000ul + (ulong)random.Next(20) * 0x40);
            }
        }

        guest.Tables[TableA] = a;
        guest.Tables[TableB] = b;
        var user = Code + (ulong)random.Next(guest.Callees) * 0x100;
        if (guest.Unreadable.Contains(user)) user += 0x100;
        var snapshot = new ResourceSnapshot
        {
            Buffers = [Descriptor(TableA, recordsA), Descriptor(TableB, recordsB)],
            UserData = [0, 0, 0, 0, (uint)user, (uint)(user >> 32), 0x7000, 0x12],
        };
        return (guest, snapshot);
    }

    [Theory]
    [InlineData(1, 7, 3)]
    [InlineData(2, 64, 64)]
    [InlineData(3, 333, 1)]
    [InlineData(4, 2049, 517)]
    [InlineData(5, 40000, 9)]
    [InlineData(6, 1, 40000)]
    public void TheScanReturnsTheOriginalsLibrary(int seed, int recordsA, int recordsB)
    {
        var caller = ThreeSites();
        var access = Access(4, 68);
        var (guest, snapshot) = RandomTables(seed, recordsA, recordsB);
        foreach (var copy in new[] { true, false })
        {
            guest.TablesCopy = copy;
            var functions = new ShaderFunctionCache(guest.ReadBytes);
            var original = Read(Mode.Off, caller, access, snapshot, guest, functions, null);
            var scanned = Read(Mode.On, caller, access, snapshot, guest, functions, null);
            Assert.Null(ShaderCallLibraryMemo.Difference(scanned, original));
            Assert.Equal(3, original.Calls.Count);
            Assert.NotEmpty(original.Records);

            // With call-library reuse, a scan that derives the topology and one that reuses it.
            var memo = new ShaderCallLibraryMemo(active: true);
            var first = Read(Mode.On, caller, access, snapshot, guest, functions, memo);
            var reused = Read(Mode.On, caller, access, snapshot, guest, functions, memo);
            Assert.Null(ShaderCallLibraryMemo.Difference(first, original));
            Assert.Null(ShaderCallLibraryMemo.Difference(reused, original));
            Assert.Equal(1, memo.Counted("TopologyHit"));
        }
    }

    // A record's order and the site that first names a callee decide the records and current code hashes: the same records
    // in another order give the original's other order.
    [Fact]
    public void RecordsKeepTheOriginalsOrderAcrossSites()
    {
        var caller = ThreeSites();
        var access = Access(4, 68);
        var guest = new Guest();
        var a = new byte[8 * 16];
        var b = new byte[8 * 16];
        Record(a, 5, Code + 0x200, 0x5040);
        Record(a, 1, Code + 0x100, 0x5000);
        Record(a, 6, Code + 0x100, 0x5000);
        Record(b, 0, Code + 0x200, 0x5040);
        Record(b, 2, Code + 0x400, 0x5000);
        Record(b, 7, Code + 0x100, 0x6000);
        guest.Tables[TableA] = a;
        guest.Tables[TableB] = b;
        var snapshot = new ResourceSnapshot
        {
            Buffers = [Descriptor(TableA, 8), Descriptor(TableB, 8)],
            UserData = [0, 0, 0, 0, (uint)(Code + 0x500), 0, 0x7000, 0],
        };
        var functions = new ShaderFunctionCache(guest.ReadBytes);
        var original = Read(Mode.Off, caller, access, snapshot, guest, functions, null);
        var scanned = Read(Mode.On, caller, access, snapshot, guest, functions, null);
        Assert.Null(ShaderCallLibraryMemo.Difference(scanned, original));
        Assert.Equal(new (ulong, ulong)[]
        {
            (Code + 0x100, 0x5000), (Code + 0x200, 0x5040), (Code + 0x500, 0x7000), (Code + 0x400, 0x5000), (Code + 0x100, 0x6000),
        }, scanned.Records);
        Assert.Equal(new[] { Code + 0x100, Code + 0x200, Code + 0x500, Code + 0x400 }, scanned.CurrentCodeHashes.Keys);
        Assert.Equal(new uint[] { 36, 100 }, scanned.CurrentSites[Code + 0x100].Order());
    }

    // A table record whose function cannot be read leaves out every later table record naming it, also after a user-data
    // site reads that function; the user-data site keeps it.
    [Fact]
    public void AFunctionATableCouldNotReadStaysOutOfLaterTables()
    {
        var caller = ThreeSites();
        var access = Access(4, 68);
        var guest = new Guest { Flaky = Code + 0x200 };
        var a = new byte[4 * 16];
        var b = new byte[4 * 16];
        Record(a, 0, Code + 0x200, 0x5000);
        Record(a, 1, Code + 0x100, 0x5000);
        Record(b, 0, Code + 0x200, 0x6000);
        Record(b, 1, Code + 0x100, 0x6000);
        guest.Tables[TableA] = a;
        guest.Tables[TableB] = b;
        var snapshot = new ResourceSnapshot
        {
            Buffers = [Descriptor(TableA, 4), Descriptor(TableB, 4)],
            UserData = [0, 0, 0, 0, (uint)(Code + 0x200), 0, 0x7000, 0],
        };
        var original = Read(Mode.Off, caller, access, snapshot, guest, new ShaderFunctionCache(guest.ReadBytes), null);
        var scanned = Read(Mode.On, caller, access, snapshot, guest, new ShaderFunctionCache(guest.ReadBytes), null);
        SameShape(original, scanned);
        Assert.Contains((Code + 0x200, 0x7000ul), scanned.Records);
        Assert.DoesNotContain((Code + 0x200, 0x5000ul), scanned.Records);
        Assert.DoesNotContain((Code + 0x200, 0x6000ul), scanned.Records);
    }

    // Every refusal and failure throws the original's exception.
    [Fact]
    public void FailuresThrowTheOriginalsExceptions()
    {
        static void Same(Exception? original, Exception? scanned)
        {
            Assert.NotNull(original);
            Assert.NotNull(scanned);
            Assert.Equal(original!.GetType(), scanned!.GetType());
            Assert.Equal(original.Message, scanned.Message);
        }

        var caller = ThreeSites();
        var access = Access(4, 68);
        var (guest, snapshot) = RandomTables(9, 100, 100);

        // A user-data function that cannot be read.
        var unreadable = new ResourceSnapshot { Buffers = snapshot.Buffers, UserData = [0, 0, 0, 0, (uint)(Code + 3 * 0x100), 0, 0x7000, 0] };
        Same(Throws(Mode.Off, caller, access, unreadable, guest), Throws(Mode.On, caller, access, unreadable, guest));
        Assert.IsType<ShaderFunctionReadException>(Throws(Mode.On, caller, access, unreadable, guest));

        // A table the host can read only in part.
        var partial = new Guest { TablesCopy = false };
        partial.Tables[TableA] = new byte[64 * 16];
        partial.Tables[TableB] = new byte[16];
        var past = new ResourceSnapshot { Buffers = [Descriptor(TableA, 64), Descriptor(TableB, 8)], UserData = snapshot.UserData };
        Same(Throws(Mode.Off, caller, access, past, partial), Throws(Mode.On, caller, access, past, partial));

        // An unsupported layout, a table past 16 MiB, no buffer for a load.
        var swizzled = new ResourceSnapshot
        {
            Buffers = [[unchecked((uint)TableA), (uint)(TableA >> 32) | (16u << 16) | 0x80000000u, 100, 0], Descriptor(TableB, 100)],
            UserData = snapshot.UserData,
        };
        Same(Throws(Mode.Off, caller, access, swizzled, guest), Throws(Mode.On, caller, access, swizzled, guest));
        var huge = new ResourceSnapshot { Buffers = [Descriptor(TableA, (16 << 20) / 16 + 1), Descriptor(TableB, 100)], UserData = snapshot.UserData };
        Same(Throws(Mode.Off, caller, access, huge, guest), Throws(Mode.On, caller, access, huge, guest));
        var noBuffer = new ResourceSnapshot { Buffers = [Descriptor(TableA, 100)], UserData = snapshot.UserData };
        Same(Throws(Mode.Off, caller, access, noBuffer, guest), Throws(Mode.On, caller, access, noBuffer, guest));

        // A fixed record past the table's end.
        var fixedCaller = FixedRecord(64 * 16);
        var fixedAccess = Access(0);
        var small = new ResourceSnapshot { Buffers = [Descriptor(TableA, 64)], UserData = snapshot.UserData };
        Same(Throws(Mode.Off, fixedCaller, fixedAccess, small, guest), Throws(Mode.On, fixedCaller, fixedAccess, small, guest));
    }

    // A fixed record, an empty table and a zero user-data target.
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(1024)]
    public void FixedRecordsEmptyTablesAndNullTargets(int offset)
    {
        var (guest, snapshot) = RandomTables(11, 200, 0);
        Record(guest.Tables[TableA], offset / 16, Code + 0x100, 0x5100);
        var fixedCaller = FixedRecord(offset);
        var fixedSnapshot = new ResourceSnapshot { Buffers = [Descriptor(TableA, 200)], UserData = snapshot.UserData };
        var functions = new ShaderFunctionCache(guest.ReadBytes);
        Assert.Null(ShaderCallLibraryMemo.Difference(
            Read(Mode.On, fixedCaller, Access(0), fixedSnapshot, guest, functions, null),
            Read(Mode.Off, fixedCaller, Access(0), fixedSnapshot, guest, functions, null)));

        var empty = new ResourceSnapshot
        {
            Buffers = [Descriptor(TableA, 200), Descriptor(TableB, 0)],
            UserData = [0, 0, 0, 0, 0, 0, 0x7000, 0],
        };
        var caller = ThreeSites();
        var original = Read(Mode.Off, caller, Access(4, 68), empty, guest, functions, null);
        Assert.Null(ShaderCallLibraryMemo.Difference(Read(Mode.On, caller, Access(4, 68), empty, guest, functions, null), original));
        Assert.Empty(original.Calls[1].Targets);
        Assert.Empty(original.Calls[2].Targets);
    }

    // The scan reads each function once per read, as the original does, and allocates a small part of the original's
    // bytes for a large table.
    [Fact]
    public void TheScanReadsFunctionsOnceAndAllocatesLess()
    {
        var caller = ThreeSites();
        var access = Access(4, 68);
        var (guest, snapshot) = RandomTables(17, 60000, 16);
        var random = new System.Random(17);
        for (var index = 0; index < 60000; index += 3)
        {
            var callee = random.Next(guest.Callees);
            if (callee is 3 or 7) callee++;
            Record(guest.Tables[TableA], index, Code + (ulong)callee * 0x100, 0x100000ul + (ulong)index * 0x40);
        }

        var functions = new ShaderFunctionCache(guest.ReadBytes);
        Read(Mode.Off, caller, access, snapshot, guest, functions, null);
        Read(Mode.On, caller, access, snapshot, guest, functions, null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Read(Mode.Off, caller, access, snapshot, guest, functions, null);
        var original = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        Read(Mode.On, caller, access, snapshot, guest, functions, null);
        var scanned = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(scanned * 4 < original, $"scanned {scanned} bytes, original {original}");

        var reads = new Dictionary<ulong, int>();
        bool Counting(ulong address, out uint word)
        {
            if (address >= Code && address < Code + 0x10000 && (address - Code) % 0x100 == 0)
                reads[address] = reads.GetValueOrDefault(address) + 1;
            return guest.ReadWord(address, out word);
        }

        ShaderCallLibrary.TableScanOverride = Mode.On;
        try
        {
            ShaderCallLibrary.Read(caller, access, snapshot, Counting, null, runtimeArguments: true, userDataBase: 0,
                readBytes: guest.ReadBytes);
        }
        finally
        {
            ShaderCallLibrary.TableScanOverride = null;
        }

        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Equal(guest.Callees, reads.Count);
    }

    [Fact]
    public void TheScanIsOnUnlessConfigured()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_CALL_TABLE_SCAN") is not null) return;
        Assert.Equal(Mode.On, ShaderCallLibrary.TableScan);
    }
}
