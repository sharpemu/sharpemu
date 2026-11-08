// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
// distinct nonzero record arguments, Gen5ShaderCallLinker.CallEntryArguments) from each dispatch.
internal sealed record ShaderCallLibrary(string Identity, IReadOnlyList<ShaderCallSite> Calls, string LayoutIdentity,
    ulong[] Arguments)
{
    private const ulong MaxTableBytes = 65536;

    // With runtimeArguments a site links each callee once, so a table's size sets only how many records the host reads and
    // resolves at each dispatch.
    private const ulong MaxRuntimeTableBytes = 1 << 20;

    // Read with runtimeArguments, a site's Calls hold each callee once (in the order of their code, so the program does not
    // depend on the tables' order), LayoutIdentity names only the sites and the callees' code, and Records holds every
    // record of the tables for the host to resolve at each dispatch (CallRecordSelector).
    public (ulong Target, ulong Argument)[] Records { get; init; } = [];

    // The distinct callee code bodies of the tables (by content), when read with runtimeArguments.
    public int Bodies { get; init; }

    // Each callee's code hash, by address, when read with runtimeArguments.
    public Dictionary<ulong, byte[]> CodeHashes { get; init; } = [];

    // The words each callee's decode read, by address, when read with runtimeArguments.
    public Dictionary<ulong, (ulong Address, byte[] Bytes)[]> CodeRuns { get; init; } = [];

    // The hash of the code at each address the tables name, as this dispatch read it; CodeHashes holds the code the library
    // links, which for a kept or united callee can be older.
    public Dictionary<ulong, byte[]> CurrentCodeHashes { get; init; } = [];

    // The sites whose tables name each address at this dispatch.
    public Dictionary<ulong, HashSet<uint>> CurrentSites { get; init; } = [];

    // The host reads the tables and the functions they name as it reads any shader code and resource table, so bytes
    // the GPU wrote are brought back and an image the GPU wrote over memory the game has since reused does not refuse
    // the game's own bytes.
    public static ShaderCallLibrary Read(Gen5ShaderProgram caller, MemoryAccessTable memory,
        ResourceSnapshot snapshot, GuestWordReader read, ShaderFunctionCache? functionCache = null, bool runtimeArguments = false)
    {
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var layout = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var calls = new List<ShaderCallSite>();
        var functions = new Dictionary<ulong, Gen5ShaderProgram>();
        var codeHashes = new Dictionary<ulong, byte[]>();
        var codeRuns = new Dictionary<ulong, (ulong Address, byte[] Bytes)[]>();
        var currentSites = new Dictionary<ulong, HashSet<uint>>();
        foreach (var (call, index) in caller.Instructions.Select((instruction, index) => (instruction, index)))
        {
            if (call.Opcode != "SSwappcB64") continue;
            if (call.Sources.Count != 1 || call.Destinations.Count != 1 ||
                call.Sources[0].Kind != Gen5OperandKind.ScalarRegister || call.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                throw new InvalidOperationException("The shader call does not use a supported table record.");
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
            var access = memory.Find(load.Pc);
            if (access is null || access.Resource >= snapshot.Buffers.Length)
                throw new InvalidOperationException("The shader call table has no materialized buffer.");
            var descriptor = snapshot.Buffers[access.Resource];
            var address = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
            var stride = (descriptor[1] >> 16) & 0x3FFF;
            var size = (ulong)descriptor[2] * Math.Max(stride, 1u);
            var returnAddress = unchecked(caller.Address + call.ProgramOffset + (ulong)call.Words.Count * sizeof(uint));
            layout.AppendData(BitConverter.GetBytes(call.Pc));
            layout.AppendData(BitConverter.GetBytes(argumentRegister));
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
            var table = ShaderCallDump.ReadPrefix(address, (int)size, read);
            if ((ulong)table.Length != size)
                throw new InvalidOperationException(
                    $"The shader call table is not CPU-readable at this submission: address=0x{address:X} bytes={size} readable={table.Length}.");
            identity.AppendData(table);
            var targets = new List<ShaderCallTarget>();
            var records = new HashSet<(ulong, ulong)>();
            var firstRecord = checked((int)loadControl.ImmediateOffsetBytes);
            if (firstRecord < 0 || (firstRecord & 15) != 0 || firstRecord > table.Length - 16)
                throw new InvalidOperationException("The shader call record is outside the table or is not aligned.");
            var recordEnd = loadControl.DynamicOffsetRegister.HasValue ? table.Length : firstRecord + 16;
            for (var offset = firstRecord; offset < recordEnd; offset += 16)
            {
                var target = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(offset));
                var argument = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(offset + 8));
                if (target == 0 || !records.Add((target, argument))) continue;
                if (!functions.TryGetValue(target, out var function))
                {
                    var result = functionCache?.Read(target, read) ?? ShaderFunctionReader.Read(target, read);
                    if (!result.Complete) throw new ShaderFunctionReadException(target, result);
                    function = result.Program;
                    identity.AppendData(result.Bytes);
                    layout.AppendData(result.Bytes);
                    functions.Add(target, function);
                    if (runtimeArguments)
                    {
                        codeHashes.Add(target, SHA256.HashData(result.Bytes));
                        codeRuns.Add(target, result.Runs);
                    }
                }
                targets.Add(new(target, argument, function));
                if (runtimeArguments)
                {
                    if (!currentSites.TryGetValue(target, out var namedAt))
                    {
                        currentSites.Add(target, namedAt = []);
                    }

                    namedAt.Add(call.Pc);
                }
                layout.AppendData(BitConverter.GetBytes(target));
            }
            layout.AppendData(BitConverter.GetBytes(targets.Count));
            calls.Add(new(call.Pc, call.Sources[0].Value, argumentRegister, call.Destinations[0].Value, returnAddress, targets));
        }
        identity.AppendData(BitConverter.GetBytes(caller.Address));
        if (runtimeArguments)
            return WithRuntimeArguments(caller, Convert.ToHexString(identity.GetHashAndReset()), calls, codeHashes, codeRuns) with
            {
                CurrentSites = currentSites,
            };
        layout.AppendData(BitConverter.GetBytes(caller.Address));
        ulong[] recordArguments = [.. calls.SelectMany(site => site.Targets).Select(target => target.Argument)];
        ulong[] arguments = [.. recordArguments.Where(argument => argument != 0).Distinct()];
        var slotOf = arguments.Select((argument, slot) => (argument, slot)).ToDictionary(entry => entry.argument, entry => entry.slot);
        foreach (var argument in recordArguments)
            layout.AppendData(BitConverter.GetBytes(argument == 0 ? -1 : slotOf[argument]));
        return new(Convert.ToHexString(identity.GetHashAndReset()), calls, Convert.ToHexString(layout.GetHashAndReset()), arguments);
    }

    private static ShaderCallLibrary WithRuntimeArguments(Gen5ShaderProgram caller, string identity, List<ShaderCallSite> calls,
        Dictionary<ulong, byte[]> codeHashes, Dictionary<ulong, (ulong Address, byte[] Bytes)[]> codeRuns)
    {
        var sites = new List<ShaderCallSite>(calls.Count);
        var records = new HashSet<(ulong Target, ulong Argument)>();
        foreach (var site in calls)
        {
            foreach (var target in site.Targets) records.Add((target.Address, target.Argument));
            sites.Add(site with
            {
                Targets = [.. site.Targets.DistinctBy(static target => target.Address).Select(static target => target with { Argument = 0 })],
            });
        }

        return WithRuntimeSites(caller.Address, identity, sites, [.. records], codeHashes, codeRuns) with { CurrentCodeHashes = codeHashes };
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
            Bodies = sites.SelectMany(static site => site.Targets).Select(target => Convert.ToHexString(codeHashes[target.Address]))
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
    public ShaderCallLibrary Unite(ulong callerAddress, Dictionary<uint, Dictionary<ulong, UnitedCallee>> union, int maxCallees)
    {
        // Callees whose code is the same share one decoded program, so the module can share their function: the translator
        // compares instructions whose controls hold register lists by reference (SameBody).
        var programs = new Dictionary<string, Gen5ShaderProgram>();
        foreach (var known in union.Values.SelectMany(static callees => callees.Values))
        {
            programs.TryAdd(Convert.ToHexString(known.Code), known.Target.Program);
        }

        UnitedCallee Current(ShaderCallTarget target)
        {
            var code = CodeHashes[target.Address];
            var program = programs.TryGetValue(Convert.ToHexString(code), out var shared) ? shared : target.Program;
            programs.TryAdd(Convert.ToHexString(code), program);
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
                if (callees.Remove(from) && !callees.ContainsKey(to))
                {
                    callees[to] = Current(named[to]);
                }
            }
        }

        foreach (var site in Calls)
        {
            if (!union.TryGetValue(site.Pc, out var callees))
            {
                callees = [];
                union.Add(site.Pc, callees);
            }

            if (site.Targets.Count(target => !callees.ContainsKey(target.Address)) + callees.Count > maxCallees)
            {
                callees.Clear();
            }

            foreach (var target in site.Targets)
            {
                if (!callees.ContainsKey(target.Address))
                {
                    callees[target.Address] = Current(target);
                }
            }
        }

        return WithSiteCallees(callerAddress, union);
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
