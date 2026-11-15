// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The read of a run-time-record caller's tables in one pass per table. A ray tracer's hit table reserves far more
// records than a frame fills (megabytes of records, a few thousand of them live), and every pass reads it. The
// per-record read keeps a set of records per site and a ShaderCallTarget per record, then derives the library's records
// and each site's distinct callees from those (WithRuntimeArguments). This read skips zero targets four records at a
// time and keeps only what the library holds: each site's distinct callees by address in first occurrence, and the
// distinct records of all sites in first occurrence, site by site. The library it returns equals the per-record read's
// (records, sites, code hashes and runs, current code hashes in the same order, current sites); each function is read
// and validated once per address and read, as the per-record read does, and every failure throws the same exception.
// On by default; SHARPEMU_CALL_TABLE_SCAN=0, or any value but 1, turns it off.
internal sealed partial record ShaderCallLibrary
{
    internal enum TableScanMode { Off, On }

    internal static readonly TableScanMode ConfiguredTableScan =
        Environment.GetEnvironmentVariable("SHARPEMU_CALL_TABLE_SCAN") switch
        {
            null or "" or "1" => TableScanMode.On,
            _ => TableScanMode.Off,
        };

    // Tests: this thread's mode, whatever the configuration.
    [ThreadStatic] internal static TableScanMode? TableScanOverride;

    internal static TableScanMode TableScan => TableScanOverride ?? ConfiguredTableScan;

    // The host reads the tables and the functions they name as it reads any shader code and resource table, so bytes
    // the GPU wrote are brought back and an image the GPU wrote over memory the game has since reused does not refuse
    // the game's own bytes.
    // With memo (call-library reuse), a run-time-record library whose topology a previous read derived reuses it
    // (ShaderCallLibraryMemo.Runtime); the tables and functions are read as without it.
    // With runtimeArguments and the table scan on, the tables are read in one pass each (see above).
    public static ShaderCallLibrary Read(Gen5ShaderProgram caller, MemoryAccessTable memory,
        ResourceSnapshot snapshot, GuestWordReader read, ShaderFunctionCache? functionCache = null, bool runtimeArguments = false,
        uint userDataBase = 0, ResidentGuestBytesReader? readBytes = null, ShaderCallLibraryMemo? memo = null)
    {
        var mode = runtimeArguments && BitConverter.IsLittleEndian ? TableScan : TableScanMode.Off;
        if (mode == TableScanMode.Off)
            return ReadOriginal(caller, memory, snapshot, read, functionCache, runtimeArguments, userDataBase, readBytes, memo);
        return ReadScanned(caller, memory, snapshot, read, functionCache, userDataBase, readBytes, memo);
    }

    // ReadOriginal with runtimeArguments, in one pass per table. The checks, their order and their exceptions are the
    // original's; the records, sites, code hashes and runs and current sites it derives are the ones the original's
    // collections yield.
    private static ShaderCallLibrary ReadScanned(Gen5ShaderProgram caller, MemoryAccessTable memory, ResourceSnapshot snapshot,
        GuestWordReader read, ShaderFunctionCache? functionCache, uint userDataBase, ResidentGuestBytesReader? readBytes,
        ShaderCallLibraryMemo? memo)
    {
        var scan = _tableScan ??= new TableScanState();
        scan.Begin(caller.Address, functionCache, read);
        try
        {
            var calls = new List<ShaderCallSite>();
            foreach (var callSite in CallSites(caller, userDataBase, (uint)snapshot.UserData.Length))
            {
                if (callSite.Refusal is { } refusal) throw new InvalidOperationException(refusal);
                var call = caller.Instructions[callSite.Index];
                if (callSite.UserData is { } userData)
                {
                    // A function pointer the dispatch passes in user data: one target, read as the original's Target reads it.
                    var userTarget = UserDataPair(snapshot, userDataBase, userData.Target);
                    var userArgument = UserDataPair(snapshot, userDataBase, userData.Argument);
                    var userArgumentRegister = call.Sources[0].Value + 2;
                    var userReturn = unchecked(caller.Address + call.ProgramOffset + (ulong)call.Words.Count * sizeof(uint));
                    var userTargets = new List<ShaderCallTarget>(1);
                    if (userTarget != 0)
                    {
                        scan.BeginSite(call.Pc, userTargets, 0);
                        scan.VisitUserTarget(userTarget, userArgument);
                    }

                    calls.Add(new(call.Pc, call.Sources[0].Value, userArgumentRegister, call.Destinations[0].Value, userReturn, userTargets));
                    continue;
                }

                var load = callSite.Load!;
                var loadControl = (Gen5ScalarMemoryControl)load.Control!;
                var argumentRegister = callSite.ArgumentRegister;
                var access = memory.Find(load.Pc);
                if (access is null || access.Resource >= snapshot.Buffers.Length)
                    throw new InvalidOperationException("The shader call table has no materialized buffer.");
                var descriptor = snapshot.Buffers[access.Resource];
                var address = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
                var stride = (descriptor[1] >> 16) & 0x3FFF;
                var size = (ulong)descriptor[2] * Math.Max(stride, 1u);
                var returnAddress = unchecked(caller.Address + call.ProgramOffset + (ulong)call.Words.Count * sizeof(uint));
                if (size == 0)
                {
                    calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value, returnAddress, []));
                    continue;
                }

                if (size > MaxRuntimeTableBytes || (size & 15) != 0 || (descriptor[1] & 0x80000000) != 0)
                    throw new InvalidOperationException(
                        $"The shader call table extent or layout is not supported: address=0x{address:X} stride={stride} records={descriptor[2]} " +
                        $"swizzle={descriptor[1] >> 31} dynamic={loadControl.DynamicOffsetRegister.HasValue} offset={loadControl.ImmediateOffsetBytes}.");
                ReadOnlySpan<byte> table = ReadTable(address, (int)size, read, readBytes);
                if ((ulong)table.Length != size)
                    throw new InvalidOperationException(
                        $"The shader call table is not CPU-readable at this submission: address=0x{address:X} bytes={size} readable={table.Length}.");
                var targets = new List<ShaderCallTarget>();
                var firstRecord = checked((int)loadControl.ImmediateOffsetBytes);
                if (firstRecord < 0 || (firstRecord & 15) != 0 || firstRecord > table.Length - 16)
                    throw new InvalidOperationException("The shader call record is outside the table or is not aligned.");
                var recordEnd = loadControl.DynamicOffsetRegister.HasValue ? table.Length : firstRecord + 16;
                scan.BeginSite(call.Pc, targets, address);
                ScanRecords(scan, MemoryMarshal.Cast<byte, ulong>(table[firstRecord..recordEnd]), firstRecord / 16);
                calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value, returnAddress, targets));
            }

            var records = scan.TakeRecords();
            return (memo?.Runtime(caller, calls, scan.CodeHashes, scan.CodeRuns, records) ??
                WithRuntimeArguments(caller, calls, scan.CodeHashes, scan.CodeRuns, records)) with
            {
                CurrentSites = scan.CurrentSites,
            };
        }
        finally
        {
            scan.End();
        }
    }

    // Each record of a table whose target is nonzero, in order (words: the records' target and argument words, little
    // endian; first: the first record's index in the table). A zero target names no call; four records whose targets are
    // all zero are skipped together.
    private static void ScanRecords(TableScanState scan, ReadOnlySpan<ulong> words, int first)
    {
        var index = 0;
        if (Vector256.IsHardwareAccelerated && words.Length >= 8)
        {
            ref var start = ref MemoryMarshal.GetReference(words);
            var targets = Vector256.Create(ulong.MaxValue, 0UL, ulong.MaxValue, 0UL);
            for (; index <= words.Length - 8; index += 8)
            {
                var low = Vector256.LoadUnsafe(ref start, (nuint)index);
                var high = Vector256.LoadUnsafe(ref start, (nuint)(index + 4));
                if (((low | high) & targets) == Vector256<ulong>.Zero) continue;
                for (var word = index; word < index + 8; word += 2)
                {
                    if (words[word] != 0) scan.Visit(words[word], words[word + 1], first + (word >> 1));
                }
            }
        }

        for (; index < words.Length; index += 2)
        {
            if (words[index] != 0) scan.Visit(words[index], words[index + 1], first + (index >> 1));
        }
    }

    [ThreadStatic] private static TableScanState? _tableScan;

    // One thread's state of a scanned read. The collections a library holds are made for each read; the rest is kept.
    private sealed class TableScanState
    {
        // A callee the tables named in this read: its target as a library's site holds it (argument 0), null until its
        // function is read; whether a table record could not read it (the original's unreadable set: its later table
        // records are left out, also when a user-data site reads it after all); and the last site that named it.
        private sealed class Callee
        {
            public ShaderCallTarget? Target;
            public bool Unreadable;
            public int Site;
        }

        private readonly Dictionary<ulong, Callee> _callees = new();
        private readonly HashSet<(ulong Target, ulong Argument)> _seen = new();
        private (ulong Target, ulong Argument)[] _records = new (ulong, ulong)[256];
        private int _recordCount;
        private int _site;
        private uint _sitePc;
        private ulong _table;
        private ulong _caller;
        private List<ShaderCallTarget>? _siteTargets;
        private ShaderFunctionCache? _functionCache;
        private GuestWordReader? _read;

        public Dictionary<ulong, byte[]> CodeHashes = [];
        public Dictionary<ulong, (ulong Address, byte[] Bytes)[]> CodeRuns = [];
        public Dictionary<ulong, HashSet<uint>> CurrentSites = [];

        public void Begin(ulong caller, ShaderFunctionCache? functionCache, GuestWordReader read)
        {
            _caller = caller;
            _functionCache = functionCache;
            _read = read;
            CodeHashes = [];
            CodeRuns = [];
            CurrentSites = [];
            _site = 0;
            _recordCount = 0;
        }

        public void End()
        {
            _callees.Clear();
            _seen.Clear();
            _recordCount = 0;
            _siteTargets = null;
            _functionCache = null;
            _read = null;
        }

        public void BeginSite(uint pc, List<ShaderCallTarget> targets, ulong table)
        {
            _site++;
            _sitePc = pc;
            _siteTargets = targets;
            _table = table;
        }

        // A table record with a nonzero target, as the original's loop takes it: a target whose function cannot be read
        // leaves the record out, and every later table record naming it in this read.
        public void Visit(ulong target, ulong argument, int record)
        {
            if (!_callees.TryGetValue(target, out var callee))
            {
                callee = new Callee();
                _callees.Add(target, callee);
                var result = ReadFunction(target);
                if (!result.Complete)
                {
                    callee.Unreadable = true;
                    if (Interlocked.Increment(ref _unreadableTargetReports) <= 8)
                    {
                        Console.Error.WriteLine(
                            $"[GPU][WARN][LINKED_PROGRAM] A call table record names a function that cannot be read; its calls return at once: " +
                            $"caller=0x{_caller:X} table=0x{_table:X} record={record} target=0x{target:X} error={result.Error}.");
                    }

                    return;
                }

                Resolve(callee, target, result);
            }
            else if (callee.Unreadable)
            {
                return;
            }

            Name(callee, target);
            AddRecord(target, argument);
        }

        // A user-data site's target, as the original's Target reads it: a function a table record could not read is read
        // again, and a failure throws.
        public void VisitUserTarget(ulong target, ulong argument)
        {
            if (!_callees.TryGetValue(target, out var callee) || callee.Target is null)
            {
                var result = ReadFunction(target);
                if (!result.Complete) throw new ShaderFunctionReadException(target, result);
                if (callee is null)
                {
                    callee = new Callee();
                    _callees.Add(target, callee);
                }

                Resolve(callee, target, result);
            }

            Name(callee, target);
            AddRecord(target, argument);
        }

        public (ulong Target, ulong Argument)[] TakeRecords() => _records.AsSpan(0, _recordCount).ToArray();

        private ShaderFunctionReadResult ReadFunction(ulong address) =>
            _functionCache?.Read(address, _read!) ?? ShaderFunctionReader.Read(address, _read!);

        private void Resolve(Callee callee, ulong address, ShaderFunctionReadResult result)
        {
            callee.Target = new ShaderCallTarget(address, 0, result.Program);
            CodeHashes.Add(address, CodeDigest(result));
            CodeRuns.Add(address, result.Runs);
        }

        // The site holds each callee once, in first occurrence, and the callee's current sites name the site.
        private void Name(Callee callee, ulong address)
        {
            if (callee.Site == _site) return;
            callee.Site = _site;
            _siteTargets!.Add(callee.Target!);
            if (!CurrentSites.TryGetValue(address, out var namedAt))
            {
                CurrentSites.Add(address, namedAt = []);
            }

            namedAt.Add(_sitePc);
        }

        private void AddRecord(ulong target, ulong argument)
        {
            if (!_seen.Add((target, argument))) return;
            if (_recordCount == _records.Length) Array.Resize(ref _records, _records.Length * 2);
            _records[_recordCount++] = (target, argument);
        }
    }
}
