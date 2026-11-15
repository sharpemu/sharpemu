// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A callee a runtime-record caller's site has named: its target, the hash of its code, and the words its decode read
// (ShaderFunctionReadResult.Runs), from which a later session decodes it again (ShaderCallUnionStore).
internal sealed record UnitedCallee(ShaderCallTarget Target, byte[] Code, (ulong Address, byte[] Bytes)[] Runs);

// Identity names the tables and code exactly. LayoutIdentity names them up to the values of the records' arguments: it
// keeps which records share an argument and which have none. A game rebuilds its tables with the argument data
// elsewhere as often as every frame; such a table links to the same program, whose plan and module read Arguments (the
// distinct nonzero record arguments, Gen5ShaderCallLinker.CallEntryArguments) from each dispatch. A library read with
// runtimeArguments holds its records as dispatch data (Records): its Identity is its LayoutIdentity.
internal sealed partial record ShaderCallLibrary(string Identity, IReadOnlyList<ShaderCallSite> Calls, string LayoutIdentity,
    ulong[] Arguments)
{
    private const ulong MaxTableBytes = 65536;

    // With runtimeArguments a site links each callee once, so a table's size sets only how many records the host reads and
    // resolves at each dispatch. A ray tracer's hit table can reserve hundreds of thousands of records: up to 16 MiB.
    private const ulong MaxRuntimeTableBytes = 16 << 20;

    private static int _unreadableTargetReports;

    // Each callee's code hash, computed once for the decoded function the function cache returns while its code is unchanged.
    private static readonly ConditionalWeakTable<ShaderFunctionReadResult, byte[]> CodeDigests = new();

    private static byte[] CodeDigest(ShaderFunctionReadResult function) =>
        CodeDigests.GetValue(function, static result => SHA256.HashData(result.Bytes));

    // Read with runtimeArguments, a site's Calls hold each callee once (in the order of their code, so the program does not
    // depend on the tables' order), LayoutIdentity names only the sites and the callees' code, and Records holds every
    // record of the tables for the host to resolve at each dispatch (CallRecordSelector).
    public (ulong Target, ulong Argument)[] Records { get; init; } = [];

    // The distinct callee code bodies of the tables (by content), when read with runtimeArguments.
    public int Bodies { get; init; }

    // Each callee's code hash, by address, when read with runtimeArguments.
    public Dictionary<ulong, byte[]> CodeHashes { get; init; } = [];

    // The words each callee's decode read, by address; also retained for exact prewarm replay of static links.
    public Dictionary<ulong, (ulong Address, byte[] Bytes)[]> CodeRuns { get; init; } = [];

    // The hash of the code at each address the tables name, as this dispatch read it; CodeHashes holds the code the library
    // links, which for a kept or united callee can be older.
    public Dictionary<ulong, byte[]> CurrentCodeHashes { get; init; } = [];

    // The sites whose tables name each address at this dispatch.
    public Dictionary<ulong, HashSet<uint>> CurrentSites { get; init; } = [];

    // With call-library reuse on, the token of this library's topology: every field but Records, CurrentCodeHashes and
    // CurrentSites, which are this dispatch's own (ShaderCallLibraryMemo). Null otherwise.
    internal object? Topology { get; init; }

    // Read without the one-pass table scan (ShaderCallLibrary.TableScan.cs), which is also the scan's reference.
    private static ShaderCallLibrary ReadOriginal(Gen5ShaderProgram caller, MemoryAccessTable memory,
        ResourceSnapshot snapshot, GuestWordReader read, ShaderFunctionCache? functionCache, bool runtimeArguments,
        uint userDataBase, ResidentGuestBytesReader? readBytes, ShaderCallLibraryMemo? memo)
    {
        // A runtime-argument library names its sites from the callees' code hashes (WithRuntimeSites), so its read hashes
        // neither the tables nor the code.
        using var identity = runtimeArguments ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var layout = runtimeArguments ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var calls = new List<ShaderCallSite>();
        var functions = new Dictionary<ulong, Gen5ShaderProgram>();
        var codeHashes = new Dictionary<ulong, byte[]>();
        var codeRuns = new Dictionary<ulong, (ulong Address, byte[] Bytes)[]>();
        var currentSites = new Dictionary<ulong, HashSet<uint>>();

        var unreadable = new HashSet<ulong>();

        // A target a site names: its function, read once per library, joins both identities.
        ShaderCallTarget Target(uint site, ulong address, ulong argument) =>
            TryTarget(site, address, argument, out var target, out var failure)
                ? target
                : throw new ShaderFunctionReadException(address, failure!);

        bool TryTarget(uint site, ulong address, ulong argument, out ShaderCallTarget target, out ShaderFunctionReadResult? failure)
        {
            target = null!;
            failure = null;
            if (!functions.TryGetValue(address, out var function))
            {
                var result = functionCache?.Read(address, read) ?? ShaderFunctionReader.Read(address, read);
                if (!result.Complete)
                {
                    failure = result;
                    return false;
                }

                function = result.Program;
                identity?.AppendData(result.Bytes);
                layout?.AppendData(result.Bytes);
                functions.Add(address, function);
                if (runtimeArguments)
                {
                    codeHashes.Add(address, CodeDigest(result));
                }
                codeRuns.Add(address, result.Runs);
            }

            if (runtimeArguments)
            {
                if (!currentSites.TryGetValue(address, out var namedAt))
                {
                    currentSites.Add(address, namedAt = []);
                }

                namedAt.Add(site);
            }

            layout?.AppendData(BitConverter.GetBytes(address));
            target = new(address, argument, function);
            return true;
        }

        foreach (var callSite in CallSites(caller, userDataBase, (uint)snapshot.UserData.Length))
        {
            if (callSite.Refusal is { } refusal) throw new InvalidOperationException(refusal);
            var call = caller.Instructions[callSite.Index];
            if (callSite.UserData is { } userData)
            {
                // A function pointer the dispatch passes in user data: one target, which the linked program still checks
                // against the call registers when the call runs. Its argument is a record argument like a table's.
                var userTarget = UserDataPair(snapshot, userDataBase, userData.Target);
                var userArgument = UserDataPair(snapshot, userDataBase, userData.Argument);
                var userArgumentRegister = call.Sources[0].Value + 2;
                var userReturn = unchecked(caller.Address + call.ProgramOffset + (ulong)call.Words.Count * sizeof(uint));
                identity?.AppendData(BitConverter.GetBytes(userTarget));
                identity?.AppendData(BitConverter.GetBytes(userArgument));
                layout?.AppendData(BitConverter.GetBytes(call.Pc));
                layout?.AppendData(BitConverter.GetBytes(userArgumentRegister));
                List<ShaderCallTarget> userTargets = userTarget == 0 ? [] : [Target(call.Pc, userTarget, userArgument)];
                layout?.AppendData(BitConverter.GetBytes(userTargets.Count));
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
            layout?.AppendData(BitConverter.GetBytes(call.Pc));
            layout?.AppendData(BitConverter.GetBytes(argumentRegister));
            if (size == 0)
            {
                // A buffer without records reads zero for every load: a null target, so the call has no target.
                calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value, returnAddress, []));
                continue;
            }
            if (size > (runtimeArguments ? MaxRuntimeTableBytes : MaxTableBytes) || (size & 15) != 0 || (descriptor[1] & 0x80000000) != 0)
                throw new InvalidOperationException(
                    $"The shader call table extent or layout is not supported: address=0x{address:X} stride={stride} records={descriptor[2]} " +
                    $"swizzle={descriptor[1] >> 31} dynamic={loadControl.DynamicOffsetRegister.HasValue} offset={loadControl.ImmediateOffsetBytes}.");
            ReadOnlySpan<byte> table = ReadTable(address, (int)size, read, readBytes);
            if ((ulong)table.Length != size)
                throw new InvalidOperationException(
                    $"The shader call table is not CPU-readable at this submission: address=0x{address:X} bytes={size} readable={table.Length}.");
            identity?.AppendData(table);
            var targets = new List<ShaderCallTarget>();
            var records = new HashSet<(ulong, ulong)>();
            var firstRecord = checked((int)loadControl.ImmediateOffsetBytes);
            if (firstRecord < 0 || (firstRecord & 15) != 0 || firstRecord > table.Length - 16)
                throw new InvalidOperationException("The shader call record is outside the table or is not aligned.");
            var recordEnd = loadControl.DynamicOffsetRegister.HasValue ? table.Length : firstRecord + 16;
            for (var offset = firstRecord; offset < recordEnd; offset += 16)
            {
                var target = BinaryPrimitives.ReadUInt64LittleEndian(table[offset..]);
                var argument = BinaryPrimitives.ReadUInt64LittleEndian(table[(offset + 8)..]);
                if (target == 0 || !records.Add((target, argument)) || unreadable.Contains(target)) continue;
                if (!runtimeArguments)
                {
                    targets.Add(Target(call.Pc, target, argument));
                    continue;
                }

                // A table can hold records past the ones the game wrote for this dispatch, naming functions it has since
                // unloaded, and the GPU never calls those. A record whose function cannot be read is left out: its call
                // returns at once, as one to a callee the program does not hold does.
                if (TryTarget(call.Pc, target, argument, out var linked, out var failure))
                {
                    targets.Add(linked);
                    continue;
                }

                unreadable.Add(target);
                if (Interlocked.Increment(ref _unreadableTargetReports) <= 8)
                {
                    Console.Error.WriteLine(
                        $"[GPU][WARN][LINKED_PROGRAM] A call table record names a function that cannot be read; its calls return at once: " +
                        $"caller=0x{caller.Address:X} table=0x{address:X} record={offset / 16} target=0x{target:X} error={failure!.Error}.");
                }
            }
            layout?.AppendData(BitConverter.GetBytes(targets.Count));
            calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value, returnAddress, targets));
        }
        identity?.AppendData(BitConverter.GetBytes(caller.Address));
        if (runtimeArguments)
            return (memo?.Runtime(caller, calls, codeHashes, codeRuns) ?? WithRuntimeArguments(caller, calls, codeHashes, codeRuns)) with
            {
                CurrentSites = currentSites,
            };
        var identityHash = identity!;
        var layoutHash = layout!;
        layoutHash.AppendData(BitConverter.GetBytes(caller.Address));
        ulong[] recordArguments = [.. calls.SelectMany(site => site.Targets).Select(target => target.Argument)];
        ulong[] arguments = [.. recordArguments.Where(argument => argument != 0).Distinct()];
        var slotOf = arguments.Select((argument, slot) => (argument, slot)).ToDictionary(entry => entry.argument, entry => entry.slot);
        foreach (var argument in recordArguments)
            layoutHash.AppendData(BitConverter.GetBytes(argument == 0 ? -1 : slotOf[argument]));
        return new(Convert.ToHexString(identityHash.GetHashAndReset()), calls, Convert.ToHexString(layoutHash.GetHashAndReset()), arguments)
        { CodeRuns = codeRuns };
    }

    // A code hash as text. With call-library reuse on, one string per code array: the arrays are never written.
    internal static string CodeKey(byte[] code) => ShaderCallLibraryMemo.Enabled
        ? CodeKeys.GetValue(code, static bytes => Convert.ToHexString(bytes))
        : Convert.ToHexString(code);

    private static readonly ConditionalWeakTable<byte[], string> CodeKeys = new();

    // The table bytes of the call site being read; valid until the next table read on this thread.
    [ThreadStatic]
    private static byte[]? _tableScratch;

    // A table is copied at once when the host can read it without first bringing back bytes the GPU wrote; otherwise it is
    // read a word at a time, as the host reads any shader resource. Either way into one buffer per thread: a ray tracer's
    // table is megabytes, read at each of its dispatches, and a new array each time is a large-object allocation.
    private static ReadOnlySpan<byte> ReadTable(ulong address, int size, GuestWordReader read, ResidentGuestBytesReader? readBytes)
    {
        var scratch = _tableScratch;
        if (scratch is null || scratch.Length < size)
        {
            _tableScratch = scratch = new byte[(size + 0xFFFF) & ~0xFFFF];
        }

        var bytes = scratch.AsSpan(0, size);
        if (readBytes is not null && readBytes(address, bytes, false))
        {
            return bytes;
        }

        // As ShaderCallDump.ReadPrefix: whole words up to the first unreadable one.
        var offset = 0;
        while (offset + sizeof(uint) <= (size & ~3) && address <= ulong.MaxValue - (ulong)offset - 3 &&
            read(address + (ulong)offset, out var word))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], word);
            offset += sizeof(uint);
        }

        return bytes[..offset];
    }

    internal static ShaderCallLibrary WithRuntimeArguments(Gen5ShaderProgram caller, List<ShaderCallSite> calls,
        Dictionary<ulong, byte[]> codeHashes, Dictionary<ulong, (ulong Address, byte[] Bytes)[]> codeRuns) =>
        WithRuntimeArguments(caller, calls, codeHashes, codeRuns, RuntimeRecords(calls));

    // The records of a read's sites as a run-time library holds them: every site's distinct target and argument pairs, in
    // first occurrence, site by site.
    internal static (ulong Target, ulong Argument)[] RuntimeRecords(List<ShaderCallSite> calls)
    {
        var records = new HashSet<(ulong Target, ulong Argument)>();
        foreach (var site in calls)
        {
            foreach (var target in site.Targets) records.Add((target.Address, target.Argument));
        }

        return [.. records];
    }

    // records: RuntimeRecords of the read's sites. A one-pass read (ReadScanned) collects them itself, and its sites hold
    // each callee once.
    internal static ShaderCallLibrary WithRuntimeArguments(Gen5ShaderProgram caller, List<ShaderCallSite> calls,
        Dictionary<ulong, byte[]> codeHashes, Dictionary<ulong, (ulong Address, byte[] Bytes)[]> codeRuns,
        (ulong Target, ulong Argument)[] records)
    {
        var sites = new List<ShaderCallSite>(calls.Count);
        foreach (var site in calls)
        {
            sites.Add(site with
            {
                Targets = [.. site.Targets.DistinctBy(static target => target.Address).Select(static target => target with { Argument = 0 })],
            });
        }

        var library = WithRuntimeSites(caller.Address, "", sites, records, codeHashes, codeRuns);
        return library with { Identity = library.LayoutIdentity, CurrentCodeHashes = codeHashes };
    }

    // The library of sites whose callees are each named once, in the order of their code, and whose identity names only the
    // sites, that code and which callees share an address (a callee slot): the module reads the addresses from each dispatch,
    // so a library whose functions the game loaded elsewhere links to the same program.
    private static ShaderCallLibrary WithRuntimeSites(ulong callerAddress, string identity, IReadOnlyList<ShaderCallSite> calls,
        (ulong Target, ulong Argument)[] records, Dictionary<ulong, byte[]> codeHashes,
        Dictionary<ulong, (ulong Address, byte[] Bytes)[]> codeRuns)
    {
        using var layout = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        layout.AppendData("runtime-call-records-slots"u8);
        var sites = new List<ShaderCallSite>(calls.Count);
        var slots = new Dictionary<ulong, int>();
        foreach (var site in calls)
        {
            List<ShaderCallTarget> callees = [.. site.Targets
                .OrderBy(target => BinaryPrimitives.ReadUInt64LittleEndian(codeHashes[target.Address]))
                .ThenBy(static target => target.Address)];
            layout.AppendData(BitConverter.GetBytes(site.Pc));
            layout.AppendData(BitConverter.GetBytes(site.ArgumentRegister));
            layout.AppendData(BitConverter.GetBytes(callees.Count));
            foreach (var callee in callees)
            {
                if (!slots.TryGetValue(callee.Address, out var slot))
                {
                    slots.Add(callee.Address, slot = slots.Count);
                }

                layout.AppendData(BitConverter.GetBytes(slot));
                layout.AppendData(codeHashes[callee.Address]);
            }

            sites.Add(site with { Targets = callees });
        }

        layout.AppendData(BitConverter.GetBytes(callerAddress));
        return new(identity, sites, Convert.ToHexString(layout.GetHashAndReset()), [])
        {
            Records = records,
            CodeHashes = codeHashes,
            CodeRuns = codeRuns,
            Bodies = sites.SelectMany(static site => site.Targets).Select(target => CodeKey(codeHashes[target.Address]))
                .Distinct().Count(),
        };
    }

    // This runtime-argument library whose sites hold the given callees, by site pc; a site the map does not name holds none.
    public ShaderCallLibrary WithSiteCallees(ulong callerAddress, IReadOnlyDictionary<uint, Dictionary<ulong, UnitedCallee>> callees)
    {
        var codeHashes = new Dictionary<ulong, byte[]>(CodeHashes);
        var codeRuns = new Dictionary<ulong, (ulong Address, byte[] Bytes)[]>(CodeRuns);
        var sites = new List<ShaderCallSite>(Calls.Count);
        foreach (var site in Calls)
        {
            List<ShaderCallTarget> targets = [];
            if (callees.TryGetValue(site.Pc, out var named))
            {
                foreach (var (address, callee) in named)
                {
                    codeHashes[address] = callee.Code;
                    codeRuns[address] = callee.Runs;
                    targets.Add(callee.Target);
                }
            }

            sites.Add(site with { Targets = targets });
        }

        return WithRuntimeSites(callerAddress, Identity, sites, Records, codeHashes, codeRuns) with
        {
            CurrentCodeHashes = CurrentCodeHashes,
            CurrentSites = CurrentSites,
        };
    }

    // This runtime-argument library without the callee at an address, at every site: a call to it returns at once.
    public ShaderCallLibrary WithoutCallee(ulong callerAddress, ulong address) =>
        WithSiteCallees(callerAddress, SiteCallees().ToDictionary(static site => site.Key,
            site => site.Value.Where(callee => callee.Target.Address != address).ToDictionary(static callee => callee.Target.Address)));

    // Links the sites with run-time arguments. A callee the linker cannot copy (one that calls another function, say) is left
    // out at every site and the others still link: its records return at once, as the records of any callee a program lacks
    // do. The library becomes the one the program holds.
    public static Gen5ShaderProgram LinkLeavingOutUnlinkable(Gen5ShaderProgram caller, ref ShaderCallLibrary library,
        Action<ShaderCallLinkException> leftOut)
    {
        while (true)
        {
            try
            {
                return Gen5ShaderCallLinker.Link(caller, library.Calls, runtimeArguments: true);
            }
            catch (ShaderCallLinkException failure) when (library.Calls.Any(site =>
                site.Targets.Any(target => target.Address == failure.Target.Address)))
            {
                leftOut(failure);
                library = library.WithoutCallee(caller.Address, failure.Target.Address);
            }
        }
    }

    // This runtime-argument library without a callee at any site: its program returns from every call at once, as from a
    // record of a callee it does not hold, and compiles in seconds where the callees' code can take the driver minutes.
    public ShaderCallLibrary WithoutCallees(ulong callerAddress) =>
        WithSiteCallees(callerAddress, new Dictionary<uint, Dictionary<ulong, UnitedCallee>>());

    // Each site's callees with the code they were decoded from (ShaderCallUnionStore).
    public Dictionary<uint, UnitedCallee[]> SiteCallees() => Calls.ToDictionary(static site => site.Pc, site =>
        site.Targets.Select(target => new UnitedCallee(target, CodeHashes[target.Address], CodeRuns[target.Address])).ToArray());

    // This runtime-argument library with each site's callees joined to the ones it named before (union, by site pc and
    // address, which this updates): a callee that leaves the tables keeps its copy, so tables that drop and regain it link
    // to the same program. An address whose code changed takes the new code, and a site that would pass maxCallees starts
    // over from its current callees.
    public ShaderCallLibrary Unite(ulong callerAddress, Dictionary<uint, Dictionary<ulong, UnitedCallee>> union, int maxCallees) =>
        Unite(callerAddress, union, maxCallees, out _);

    // changed: whether the union changed. A call that changed nothing returns the same library again for the same union
    // (ShaderCallLibraryMemo keeps that result).
    public ShaderCallLibrary Unite(ulong callerAddress, Dictionary<uint, Dictionary<ulong, UnitedCallee>> union, int maxCallees,
        out bool changed)
    {
        changed = false;
        // Callees whose code is the same share one decoded program, so the module can share their function: the translator
        // compares instructions whose controls hold register lists by reference (SameBody).
        var programs = new Dictionary<string, Gen5ShaderProgram>();
        foreach (var known in union.Values.SelectMany(static callees => callees.Values))
        {
            programs.TryAdd(CodeKey(known.Code), known.Target.Program);
        }

        UnitedCallee Current(ShaderCallTarget target)
        {
            var code = CodeHashes[target.Address];
            var program = programs.TryGetValue(CodeKey(code), out var shared) ? shared : target.Program;
            programs.TryAdd(CodeKey(code), program);
            return new UnitedCallee(target with { Program = program }, code, CodeRuns[target.Address]);
        }

        // An address the tables name links the code they name at every site, also at a site whose own table does not name it
        // now: one address never links two bodies, whose records would share one callee slot.
        var named = new Dictionary<ulong, ShaderCallTarget>();
        foreach (var target in Calls.SelectMany(static site => site.Targets))
        {
            named.TryAdd(target.Address, target);
        }

        foreach (var callees in union.Values)
        {
            foreach (var (address, known) in callees.ToArray())
            {
                if (named.TryGetValue(address, out var target) && !known.Code.AsSpan().SequenceEqual(CodeHashes[address]))
                {
                    callees[address] = Current(target);
                    changed = true;
                }
            }
        }

        // A function the game loaded at another address (in another session, or a block streamed in elsewhere) keeps its copy:
        // an entry whose address the tables no longer name takes the address they name with the same code, at every site, so
        // the program and its pipeline stay the same.
        var moved = new Dictionary<ulong, ulong>();
        foreach (var site in Calls)
        {
            if (!union.TryGetValue(site.Pc, out var callees))
            {
                continue;
            }

            foreach (var target in site.Targets)
            {
                if (callees.ContainsKey(target.Address) || moved.ContainsValue(target.Address))
                {
                    continue;
                }

                var code = CodeHashes[target.Address];
                foreach (var (address, known) in callees)
                {
                    if (!named.ContainsKey(address) && !moved.ContainsKey(address) && known.Code.AsSpan().SequenceEqual(code))
                    {
                        moved.Add(address, target.Address);
                        break;
                    }
                }
            }
        }

        foreach (var callees in union.Values)
        {
            foreach (var (from, to) in moved)
            {
                if (callees.Remove(from))
                {
                    changed = true;
                    if (!callees.ContainsKey(to))
                    {
                        callees[to] = Current(named[to]);
                    }
                }
            }
        }

        foreach (var site in Calls)
        {
            if (!union.TryGetValue(site.Pc, out var callees))
            {
                callees = [];
                union.Add(site.Pc, callees);
                changed = true;
            }

            if (site.Targets.Count(target => !callees.ContainsKey(target.Address)) + callees.Count > maxCallees)
            {
                callees.Clear();
                changed = true;
            }

            foreach (var target in site.Targets)
            {
                if (!callees.ContainsKey(target.Address))
                {
                    callees[target.Address] = Current(target);
                    changed = true;
                }
            }
        }

        return WithSiteCallees(callerAddress, union);
    }

    // A caller may also copy its target and argument pairs from user data: each of the four call registers (the
    // target pair, then the argument pair) still holds a user-data register, reached through S_MOV_B32 and
    // S_MOV_B64 copies (a caller may save a pair at entry and reuse it after the call registers were overwritten).
    // A call site as the caller's code decides it: the user-data registers that hold its target and argument pairs, or the
    // instruction that loads its record, or why it is refused. Each read takes the values from its own dispatch.
    private sealed record CallSitePlan(int Index, string? Refusal = null, (uint Target, uint Argument)? UserData = null,
        Gen5ShaderInstruction? Load = null, uint ArgumentRegister = 0);

    private sealed record CallSitePlans(uint UserDataBase, uint UserDataCount, CallSitePlan[] Sites);

    private sealed class CallSitePlanSlot
    {
        public volatile CallSitePlans? Plans;
    }

    // By instruction list, which a copy of the program at another address shares.
    private static readonly ConditionalWeakTable<IReadOnlyList<Gen5ShaderInstruction>, CallSitePlanSlot> SitePlans = new();

    private static CallSitePlan[] CallSites(Gen5ShaderProgram caller, uint userDataBase, uint userDataCount)
    {
        var slot = SitePlans.GetValue(caller.Instructions, static _ => new CallSitePlanSlot());
        if (slot.Plans is { } plans && plans.UserDataBase == userDataBase && plans.UserDataCount == userDataCount)
        {
            return plans.Sites;
        }

        var planned = new CallSitePlans(userDataBase, userDataCount, PlanSites(caller, userDataBase, userDataCount));
        slot.Plans = planned;
        return planned.Sites;
    }

    private static CallSitePlan[] PlanSites(Gen5ShaderProgram caller, uint userDataBase, uint userDataCount)
    {
        var sites = new List<CallSitePlan>();
        for (var index = 0; index < caller.Instructions.Count; index++)
        {
            if (caller.Instructions[index].Opcode == "SSwappcB64")
            {
                sites.Add(PlanSite(caller, index, userDataBase, userDataCount));
            }
        }

        return [.. sites];
    }

    private static CallSitePlan PlanSite(Gen5ShaderProgram caller, int index, uint userDataBase, uint userDataCount)
    {
        try
        {
            return PlanSiteOrThrow(caller, index, userDataBase, userDataCount);
        }
        catch (InvalidOperationException refusal)
        {
            return new CallSitePlan(index, refusal.Message);
        }
    }

    private static CallSitePlan PlanSiteOrThrow(Gen5ShaderProgram caller, int index, uint userDataBase, uint userDataCount)
    {
        var call = caller.Instructions[index];
        if (call.Sources.Count != 1 || call.Destinations.Count != 1 ||
            call.Sources[0].Kind != Gen5OperandKind.ScalarRegister || call.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            throw new InvalidOperationException("The shader call does not use a supported table record.");
        if (TryFindUserDataCall(caller, index, userDataBase, userDataCount, out var userData))
            return new CallSitePlan(index, UserData: userData);
        Gen5ShaderInstruction load;
        Gen5ScalarMemoryControl loadControl;
        uint argumentRegister = 0;
        if (TryFindDirectRecordLoads(caller, index, out var targetLoad, out var directArgumentRegister))
        {
            load = targetLoad;
            loadControl = (Gen5ScalarMemoryControl)targetLoad.Control!;
            argumentRegister = directArgumentRegister;
        }
        else
        {
            if (index < 6) throw new InvalidOperationException("The shader call does not use a supported table record.");
            var loadIndex = index - 6;
            while (loadIndex >= 0 && caller.Instructions[loadIndex] is { Opcode: "VMovB32", Destinations.Count: 1 } vectorMove &&
                vectorMove.Destinations[0].Kind == Gen5OperandKind.VectorRegister) loadIndex--;
            if (loadIndex < 0) throw new InvalidOperationException("The shader call table load is missing.");
            load = caller.Instructions[loadIndex];
            if (load.Opcode != "SBufferLoadDwordx4" || load.Control is not Gen5ScalarMemoryControl recordControl ||
                load.Destinations.Count != 4 || caller.Instructions[index - 5].Opcode != "SWaitcnt")
                throw new InvalidOperationException("The shader call table load is not supported.");
            loadControl = recordControl;
            if (loadControl.DynamicOffsetRegister is { } selector)
            {
                var scale = caller.Instructions.Take(loadIndex).Reverse().FirstOrDefault(instruction =>
                    instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal) || instruction.Opcode is "SBranch" or "SSwappcB64" ||
                    instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister &&
                        selector >= destination.Value && selector - destination.Value < instruction.DestinationWidth));
                if (scale is null || scale.Opcode != "SLshlB32" || scale.Destinations.Count != 1 || scale.Destinations[0] != Gen5Operand.Scalar(selector) ||
                    scale.Sources.Count != 2 || scale.Sources[1] != new Gen5Operand(Gen5OperandKind.EncodedConstant, 132))
                    throw new InvalidOperationException("The shader call table does not use 16-byte records.");
            }
            for (var component = 0; component < 4; component++)
            {
                var move = caller.Instructions[index - 4 + component];
                if (move.Opcode != "SMovB32" || move.Sources.Count != 1 || move.Destinations.Count != 1 ||
                    move.Sources[0] != load.Destinations[component] || move.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                    throw new InvalidOperationException("The shader call record has an unsupported register transfer.");
                if (component == 2) argumentRegister = move.Destinations[0].Value;
                var expected = component < 2 ? call.Sources[0].Value + (uint)component : argumentRegister + (uint)component - 2;
                if (move.Destinations[0].Value != expected)
                    throw new InvalidOperationException("The shader call record does not use register pairs.");
            }
        }
        return new CallSitePlan(index, Load: load, ArgumentRegister: argumentRegister);
    }

    private static ulong UserDataPair(ResourceSnapshot snapshot, uint userDataBase, uint first) =>
        snapshot.UserData[first - userDataBase] | (ulong)snapshot.UserData[first - userDataBase + 1] << 32;

    private static bool TryFindUserDataCall(Gen5ShaderProgram caller, int index, uint userDataBase, uint userDataCount,
        out (uint Target, uint Argument) userData)
    {
        userData = default;
        var callRegister = caller.Instructions[index].Sources[0].Value;
        var sources = new uint[4];
        for (var slot = 0; slot < sources.Length; slot++)
            if (!TryTraceUserDataRegister(caller, index, callRegister + (uint)slot, userDataBase, userDataCount, out sources[slot]))
                return false;
        if (sources[1] != sources[0] + 1 || sources[3] != sources[2] + 1) return false;
        userData = (sources[0], sources[2]);
        return true;
    }

    // Follows one scalar register back from an instruction through register copies to the user-data register it
    // holds. Any other writer ends the trace; a vector instruction's scalar result counts as a lane-mask pair.
    private static bool TryTraceUserDataRegister(Gen5ShaderProgram caller, int before, uint register, uint userDataBase,
        uint userDataCount, out uint userDataRegister)
    {
        userDataRegister = 0;
        for (var cursor = before - 1; cursor >= 0; cursor--)
        {
            var instruction = caller.Instructions[cursor];
            var vector = instruction.Encoding is Gen5ShaderEncoding.Vopc or Gen5ShaderEncoding.Vop3 or Gen5ShaderEncoding.Vop1 or
                Gen5ShaderEncoding.Vop2;
            foreach (var destination in instruction.Destinations)
            {
                var width = vector ? 2u : Math.Max(instruction.DestinationWidth, 1u);
                if (destination.Kind != Gen5OperandKind.ScalarRegister || register < destination.Value ||
                    register - destination.Value >= width) continue;
                if (instruction.Opcode is not ("SMovB32" or "SMovB64") || instruction.Sources.Count != 1 ||
                    instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister) return false;
                register = instruction.Sources[0].Value + (register - destination.Value);
                goto Traced;
            }
            continue;
        Traced:;
        }
        if (register < userDataBase || register - userDataBase >= userDataCount) return false;
        userDataRegister = register;
        return true;
    }

    // A caller may also load one 16-byte record straight into the call registers: the target pair with an
    // S_BUFFER_LOAD_DWORDX2 and the argument pair with another one 8 bytes further on, often with a null
    // check of the target that branches past the call. Only waits, scalar compares and such a branch may stand
    // between the loads and the call, and no branch may land among them.
    private static bool TryFindDirectRecordLoads(Gen5ShaderProgram caller, int index, out Gen5ShaderInstruction targetLoad,
        out uint argumentRegister)
    {
        targetLoad = null!;
        argumentRegister = 0;
        var call = caller.Instructions[index];
        Gen5ShaderInstruction? argumentLoad = null;
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            var instruction = caller.Instructions[cursor];
            if (instruction is { Opcode: "SBufferLoadDwordx2", Control: Gen5ScalarMemoryControl { DynamicOffsetRegister: null },
                    Destinations: [{ Kind: Gen5OperandKind.ScalarRegister } low, { Kind: Gen5OperandKind.ScalarRegister } high] } &&
                high.Value == low.Value + 1)
            {
                if (low.Value == call.Sources[0].Value)
                {
                    targetLoad = instruction;
                    break;
                }
                if (argumentLoad is not null) return false;
                argumentLoad = instruction;
                continue;
            }
            if (instruction.Opcode is "SWaitcnt" or "SNop" || instruction.Encoding == Gen5ShaderEncoding.Sopc ||
                instruction.Opcode is "SCbranchScc0" or "SCbranchScc1" &&
                Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var skipTarget) && skipTarget > call.Pc)
                continue;
            return false;
        }
        if (targetLoad is null || argumentLoad is null || argumentLoad.Sources[0] != targetLoad.Sources[0] ||
            ((Gen5ScalarMemoryControl)argumentLoad.Control!).ImmediateOffsetBytes != ((Gen5ScalarMemoryControl)targetLoad.Control!).ImmediateOffsetBytes + 8)
            return false;
        var start = targetLoad.Pc;
        if (caller.Instructions.Any(instruction => Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
                target > start && target <= call.Pc))
            return false;
        argumentRegister = argumentLoad.Destinations[0].Value;
        return true;
    }
}
