// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    // The call linker gives each record of a call site's hit-group table its own copy of the callee's code. The copies
    // of one callee at one site are the same instructions, and only the record's argument (and so the descriptors the
    // body loads through it) differs. The module keeps the last copy: every record's entry stores its slot and runs
    // that copy, and an instruction whose memory records differ between the records is emitted once for each distinct
    // set of records, selected by the slot. The resource plan still holds every copy, so bindings do not change.
    private sealed class SharedCallBodies
    {
        public required Dictionary<uint, uint> SlotByEntryPc { get; init; }

        // The kept copy of each shared body: its pc range and every record's body start, in slot order.
        public required List<(uint Start, uint End, uint[] Bases)> Bodies { get; init; }

        // The start of each earlier site's copy of a body whose function serves those sites too, by the body's start.
        public required Dictionary<uint, uint[]> SiteStarts { get; init; }

        // Each site's records, by the pc of the site's first test, which finds the caller's record (EmitSiteLookup).
        public required Dictionary<uint, SiteRecords> Lookups { get; init; }

        // The program before the earlier sites' copies became calls. The per-instruction analyses run on it, so their
        // facts are known at every site's copy.
        public required Gen5ShaderProgram AnalysisProgram { get; init; }

        public bool TryGetRecordPcs(uint pc, out uint[] pcs)
        {
            if (TryFindBody(pc, out var body))
            {
                pcs = new uint[body.Bases.Length];
                for (var slot = 0; slot < pcs.Length; slot++)
                {
                    pcs[slot] = body.Bases[slot] + (pc - body.Start);
                }

                return true;
            }

            pcs = [];
            return false;
        }

        // The same instruction in the copies of the other sites that the body's function serves.
        public bool TryGetSitePcs(uint pc, out uint[] pcs)
        {
            if (TryFindBody(pc, out var body) && SiteStarts.TryGetValue(body.Start, out var starts))
            {
                pcs = new uint[starts.Length];
                for (var index = 0; index < pcs.Length; index++)
                {
                    pcs[index] = starts[index] + (pc - body.Start);
                }

                return true;
            }

            pcs = [];
            return false;
        }

        private bool TryFindBody(uint pc, out (uint Start, uint End, uint[] Bases) body)
        {
            var low = 0;
            var high = Bodies.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                body = Bodies[middle];
                if (pc < body.Start)
                {
                    high = middle - 1;
                }
                else if (pc >= body.End)
                {
                    low = middle + 1;
                }
                else
                {
                    return true;
                }
            }

            body = default;
            return false;
        }
    }

    // A call of a shared body's function from a site whose own copy the module does not keep.
    private sealed record SharedBodyCallControl(uint BodyPc) : Gen5InstructionControl;

    // A site's records in the linker's order: the target and argument each matches, its callee's group at the site and its
    // slot in that callee's body.
    private sealed record SiteRecords(uint AddressRegister, uint ArgumentRegister,
        (ulong Address, ulong Argument, uint Group, uint Slot)[] Records);

    // The argument a test takes for a callee whose every record matches (ShaderCallMatchControl.RuntimeArgument): no guest
    // address is this large.
    private const ulong AnyCallArgument = ulong.MaxValue;

    // The key under which copies share a function: their body's length and instruction words, which SameBody then confirms
    // copy by copy. Callees at different addresses share a function when their code is the same: a game can load one function
    // at several addresses.
    private static (int Length, int Words) BodyKey(IReadOnlyList<Gen5ShaderInstruction> instructions, int start, int end)
    {
        var words = new HashCode();
        for (var index = start; index < end; index++)
        {
            words.Add(instructions[index].Opcode);
            foreach (var word in instructions[index].Words)
            {
                words.Add(word);
            }
        }

        return (end - start, words.ToHashCode());
    }

    private static ShaderCompileRequest ShareLinkedCallBodies(ShaderCompileRequest request, out SharedCallBodies? shared)
    {
        shared = null;
        var instructions = request.Program.Instructions;
        if (!instructions.Any(static instruction => instruction.Control is ShaderCallEntryControl))
        {
            return request;
        }

        // A body runs from its entry to the next record's test or to the site's fault.
        var bodies = new List<(int Entry, int Start, int End, int Site, ulong Target)>();
        var site = 0;
        ulong target = 0;
        for (var index = 0; index < instructions.Count; index++)
        {
            var control = instructions[index].Control;
            if (control is ShaderCallMatchControl match)
            {
                target = match.Address;
            }
            else if (control is ShaderCallFaultControl)
            {
                site++;
            }
            else if (control is ShaderCallEntryControl)
            {
                var end = index + 1;
                while (end < instructions.Count && instructions[end].Control is not (ShaderCallMatchControl or ShaderCallFaultControl))
                {
                    end++;
                }

                if (end < instructions.Count && end > index + 1)
                {
                    bodies.Add((index, index + 1, end, site, target));
                }

                index = end - 1;
            }
        }

        // The copies of one callee at one site: the last copy stays, so every other record's entry branches forward
        // into it and the program gains no back edge.
        var copies = new List<(int Site, ulong Target, int Start, int End, uint[] Entries, uint[] Bases, int Records)>();
        var replaced = new Dictionary<int, (int End, uint Target)>();
        foreach (var group in bodies.GroupBy(static body => (body.Site, body.Target)))
        {
            var kept = group.Last();
            var members = group.Where(member => member == kept ||
                SameBody(instructions, kept.Start, kept.End, member.Start, member.End)).ToList();
            var start = instructions[kept.Start].Pc;
            foreach (var member in members)
            {
                if (member != kept)
                {
                    replaced.Add(member.Start, (member.End, start));
                }
            }

            copies.Add((group.Key.Site, group.Key.Target, kept.Start, kept.End,
                members.Select(member => instructions[member.Entry].Pc).ToArray(),
                members.Select(member => instructions[member.Start].Pc).ToArray(), group.Count()));
        }

        if (copies.Count == 0)
        {
            return request;
        }

        // A compute body runs in a DontInline function of its own (PlanOutlinedCallBodies), so the function of a
        // callee's last copy can serve its earlier sites as well: their copy becomes a call of it followed by the branch
        // to their own return. Their records take the slots after the function's own.
        var acrossSites = request.WaveSize == 32 &&
            request.Stage is not (ShaderStage.Vertex or ShaderStage.Pixel or ShaderStage.Mesh);
        var slotByEntryPc = new Dictionary<uint, uint>();
        var shares = new List<(uint Start, uint End, uint[] Bases)>();
        var siteStarts = new Dictionary<uint, uint[]>();
        var calls = new Dictionary<int, (int End, uint Body, uint Return)>();
        // A copy can call the function of another copy when their bodies are the same, each leaving only to its own site's return.
        bool SharesFunction(int functionStart, int functionEnd, int copyStart, int copyEnd) =>
            copyEnd - copyStart > 1 && SiteReturnPc(instructions, functionEnd) is { } toFunction &&
            SiteReturnPc(instructions, copyEnd) is { } toCopy &&
            SameBody(instructions, functionStart, functionEnd, copyStart, copyEnd, (toFunction, toCopy));

        // The copies of one key share the function of the last of them. A copy whose body differs from it (a hash collision,
        // or a callee that reads its own address) joins the next class whose function it matches, or starts its own.
        var classes = new List<List<(int Site, ulong Target, int Start, int End, uint[] Entries, uint[] Bases, int Records)>>();
        foreach (var byKey in copies.GroupBy(copy => BodyKey(instructions, copy.Start, copy.End)))
        {
            var first = classes.Count;
            foreach (var copy in byKey.OrderByDescending(static copy => copy.Start))
            {
                var home = acrossSites
                    ? classes.Skip(first).FirstOrDefault(members => SharesFunction(members[0].Start, members[0].End, copy.Start, copy.End))
                    : null;
                if (home is null)
                {
                    classes.Add([copy]);
                }
                else
                {
                    home.Add(copy);
                }
            }
        }

        foreach (var members in classes)
        {
            var ordered = members.OrderBy(static copy => copy.Start).ToList();
            var function = ordered[^1];
            var bases = new List<uint>(function.Bases);
            var starts = new List<uint>();
            AddSlots(function.Entries, 0);
            foreach (var copy in ordered.Take(ordered.Count - 1))
            {
                if (acrossSites && SharesFunction(function.Start, function.End, copy.Start, copy.End) &&
                    SiteReturnPc(instructions, copy.End) is { } toCopy)
                {
                    AddSlots(copy.Entries, (uint)bases.Count);
                    bases.AddRange(copy.Bases);
                    starts.Add(instructions[copy.Start].Pc);
                    calls.Add(copy.Start, (copy.End, instructions[function.Start].Pc, toCopy));
                    continue;
                }

                AddSlots(copy.Entries, 0);
                shares.Add((instructions[copy.Start].Pc, instructions[copy.End].Pc, copy.Bases));
            }

            shares.Add((instructions[function.Start].Pc, instructions[function.End].Pc, bases.ToArray()));
            if (starts.Count != 0)
            {
                siteStarts.Add(instructions[function.Start].Pc, starts.ToArray());
            }
        }

        // A site whose records all run a kept copy keeps one test per callee, in the order of their copies. The
        // site's first test finds the caller's record, so the entries no longer store a slot.
        var lookups = new Dictionary<uint, SiteRecords>();
        var endByStart = new Dictionary<uint, uint>();
        var sites = new Dictionary<int, (int First, int Fault, List<Gen5ShaderInstruction> Tests, List<(int Start, int End)> Copies)>();
        foreach (var bySite in bodies.GroupBy(static body => body.Site))
        {
            var records = bySite.ToList();
            var groups = copies.Where(copy => copy.Site == bySite.Key).OrderBy(static copy => copy.Start).ToList();
            var groupOf = new Dictionary<uint, uint>();
            for (var group = 0; group < groups.Count; group++)
            {
                foreach (var entry in groups[group].Entries)
                {
                    groupOf.Add(entry, (uint)group);
                }
            }

            var first = records[0].Entry - 1;
            var fault = records[^1].End;
            if (groups.Count >= 0xFFFF || records.Any(record => record.Entry < 1 ||
                    instructions[record.Entry - 1].Control is not ShaderCallMatchControl ||
                    !groupOf.ContainsKey(instructions[record.Entry].Pc) ||
                    slotByEntryPc.GetValueOrDefault(instructions[record.Entry].Pc, 0xFFFFu) >= 0xFFFFu) ||
                instructions[fault].Control is not ShaderCallFaultControl)
            {
                continue;
            }

            var match = (ShaderCallMatchControl)instructions[first].Control!;
            lookups.Add(instructions[first].Pc, new SiteRecords(match.AddressRegister, match.ArgumentRegister, [.. records.Select(record =>
            {
                var test = (ShaderCallMatchControl)instructions[record.Entry - 1].Control!;
                var entry = instructions[record.Entry].Pc;
                return (test.Address, test.RuntimeArgument ? AnyCallArgument : test.Argument, groupOf[entry], slotByEntryPc[entry]);
            })]));
            var tests = new List<Gen5ShaderInstruction>();
            for (var group = 0; group < groups.Count; group++)
            {
                var pc = group == 0 ? instructions[first].Pc : instructions[groups[group].Start - 2].Pc;
                var next = group + 1 < groups.Count ? instructions[groups[group + 1].Start - 2].Pc : instructions[fault].Pc;
                tests.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchCallMismatch", [0], [], [],
                    new ShaderCallGroupControl(next, (uint)group)));
                // The copy now ends at the next callee's test or the fault: the next record's test is gone.
                endByStart.Add(instructions[groups[group].Start].Pc, next);
            }

            foreach (var record in records)
            {
                slotByEntryPc.Remove(instructions[record.Entry].Pc);
            }

            sites.Add(first, (first, fault, tests, [.. groups.Select(static group => (group.Start, group.End))]));
        }

        void AddSlots(uint[] entries, uint first)
        {
            for (var slot = 0; slot < entries.Length; slot++)
            {
                slotByEntryPc.Add(entries[slot], first + (uint)slot);
            }
        }

        // A copy keeps its test and entry; its body becomes a branch into the kept copy, and the kept copy of an
        // earlier site becomes the call of the shared function and the branch to its own return.
        List<Gen5ShaderInstruction> Rewrite(bool withCalls)
        {
            var program = new List<Gen5ShaderInstruction>(instructions.Count);
            void AddCopy(int start, int end)
            {
                if (withCalls && calls.TryGetValue(start, out var call))
                {
                    var pc = instructions[start].Pc;
                    program.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sop1, "ShaderSharedCall", [0], [], [],
                        new SharedBodyCallControl(call.Body)));
                    program.Add(new Gen5ShaderInstruction(pc + sizeof(uint), Gen5ShaderEncoding.Sopp, "SBranch", [0xBF820000], [], [],
                        new ShaderLinkedBranchControl(call.Return)));
                    return;
                }

                for (var index = start; index < end; index++)
                {
                    program.Add(instructions[index]);
                }
            }

            for (var index = 0; index < instructions.Count; index++)
            {
                // A site with a lookup: each callee's test, the entry of its kept copy and the copy.
                if (sites.TryGetValue(index, out var site))
                {
                    for (var group = 0; group < site.Copies.Count; group++)
                    {
                        program.Add(site.Tests[group]);
                        program.Add(instructions[site.Copies[group].Start - 1]);
                        AddCopy(site.Copies[group].Start, site.Copies[group].End);
                    }

                    index = site.Fault - 1;
                    continue;
                }

                if (replaced.TryGetValue(index, out var copy))
                {
                    program.Add(new Gen5ShaderInstruction(instructions[index].Pc, Gen5ShaderEncoding.Sopp, "SBranch", [0xBF820000], [], [],
                        new ShaderLinkedBranchControl(copy.Target)));
                    index = copy.End - 1;
                    continue;
                }

                if (withCalls && calls.TryGetValue(index, out var call))
                {
                    var pc = instructions[index].Pc;
                    program.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sop1, "ShaderSharedCall", [0], [], [],
                        new SharedBodyCallControl(call.Body)));
                    program.Add(new Gen5ShaderInstruction(pc + sizeof(uint), Gen5ShaderEncoding.Sopp, "SBranch", [0xBF820000], [], [],
                        new ShaderLinkedBranchControl(call.Return)));
                    index = call.End - 1;
                    continue;
                }

                program.Add(instructions[index]);
            }

            return program;
        }

        var analysis = request.Program with { Instructions = Rewrite(withCalls: false) };
        for (var index = 0; index < shares.Count; index++)
        {
            if (endByStart.TryGetValue(shares[index].Start, out var end))
            {
                shares[index] = (shares[index].Start, end, shares[index].Bases);
            }
        }

        shares.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        shared = new SharedCallBodies { SlotByEntryPc = slotByEntryPc, Bodies = shares, SiteStarts = siteStarts, Lookups = lookups, AnalysisProgram = analysis };
        return request.WithProgram(calls.Count == 0 ? analysis : request.Program with { Instructions = Rewrite(withCalls: true) });
    }

    // The pc a site's copies return to: the caller's instruction after the site's fault.
    private static uint? SiteReturnPc(IReadOnlyList<Gen5ShaderInstruction> instructions, int end)
    {
        for (var index = end; index + 1 < instructions.Count; index++)
        {
            if (instructions[index].Control is ShaderCallFaultControl)
            {
                return instructions[index + 1].Pc;
            }
        }

        return null;
    }

    // The same instructions at the same offsets; a branch inside the body keeps its offset, one leaving it its target. Copies
    // at different sites leave only to their own site's return (exits).
    private static bool SameBody(IReadOnlyList<Gen5ShaderInstruction> instructions, int firstStart, int firstEnd, int copyStart, int copyEnd,
        (uint First, uint Copy)? exits = null)
    {
        if (firstEnd - firstStart != copyEnd - copyStart)
        {
            return false;
        }

        var firstPc = instructions[firstStart].Pc;
        var firstEndPc = instructions[firstEnd].Pc;
        var copyPc = instructions[copyStart].Pc;
        for (var offset = 0; offset < firstEnd - firstStart; offset++)
        {
            var left = instructions[firstStart + offset];
            var right = instructions[copyStart + offset];
            if (left.Pc - firstPc != right.Pc - copyPc || left.Opcode != right.Opcode || left.Encoding != right.Encoding ||
                left.AddressOffset != right.AddressOffset || !left.Words.SequenceEqual(right.Words) ||
                !left.Sources.SequenceEqual(right.Sources) || !left.Destinations.SequenceEqual(right.Destinations))
            {
                return false;
            }

            if (left.Control is ShaderLinkedBranchControl leftBranch && right.Control is ShaderLinkedBranchControl rightBranch)
            {
                var inside = leftBranch.TargetPc >= firstPc && leftBranch.TargetPc < firstEndPc;
                if (inside
                    ? rightBranch.TargetPc - copyPc != leftBranch.TargetPc - firstPc
                    : exits is { } exit
                        ? leftBranch.TargetPc != exit.First || rightBranch.TargetPc != exit.Copy
                        : rightBranch.TargetPc != leftBranch.TargetPc)
                {
                    return false;
                }
            }
            else if (!Equals(left.Control, right.Control))
            {
                return false;
            }
        }

        return true;
    }

    private sealed partial class CompilationContext
    {
        private readonly SharedCallBodies? _sharedCalls;
        private uint _callSlot;
        private bool _emittingCallRecord;

        // The last block and the function of each outlined body, by its first block; and its first block and function by
        // its start pc, for the calls of the sites it serves (TryEmitSharedBodyCall).
        private readonly Dictionary<int, (int Last, uint Function)> _outlinedBodies = [];
        private readonly Dictionary<uint, (int First, uint Function)> _outlinedBodyByPc = [];
        private bool _emittingOutlinedBody;

        // Each shared body of a compute program runs in its own DontInline function, which the driver compiles once.
        // Inline, the driver's compile time grew much faster than the module (a 31 MB module of 6 call sites did not
        // finish in 30 minutes), and the driver inlines any function the module does not mark. A body is outlined when
        // control enters it only at its first block, every branch out of it goes forward past it, and no loop crosses
        // its boundary; the program counter then carries control in and out exactly as it does inline.
        private void PlanOutlinedCallBodies(IReadOnlyList<ShaderBlock> blocks)
        {
            if (_sharedCalls is null || _stage != Gen5SpirvStage.Compute)
            {
                return;
            }

            foreach (var (start, end, _) in _sharedCalls.Bodies)
            {
                if (TryFindBlock(blocks, start, out var first) && TryFindBlock(blocks, end, out var after) &&
                    after > first && IsSingleEntryRegion(blocks, first, after - 1))
                {
                    var function = _module.AllocateId();
                    _outlinedBodies.Add(first, (after - 1, function));
                    _outlinedBodyByPc.Add(start, (first, function));
                }
            }
        }

        private bool IsSingleEntryRegion(IReadOnlyList<ShaderBlock> blocks, int first, int last)
        {
            foreach (var (header, latch) in _loopLatchByHeader)
            {
                if (!(header >= first && latch <= last) && !(latch < first || header > last) && !(header < first && latch > last))
                {
                    return false;
                }
            }

            var instructions = _request.Program.Instructions;
            for (var index = 0; index < blocks.Count; index++)
            {
                var terminator = instructions[blocks[index].EndIndex - 1];
                if (!IsBranch(terminator.Opcode) || !TryGetBranchTargetPc(terminator, out var targetPc) ||
                    IsExitBranchTarget(instructions, targetPc))
                {
                    continue;
                }

                if (!TryFindBlock(blocks, targetPc, out var target))
                {
                    return false;
                }

                var fromInside = index >= first && index <= last;
                if (fromInside ? target < first : target > first && target <= last)
                {
                    return false;
                }
            }

            return true;
        }

        // The analyses' program: the one the module is emitted from, unless earlier sites call a shared body.
        private Gen5ShaderProgram AnalysisProgram => _sharedCalls?.AnalysisProgram ?? _request.Program;

        // A body that serves several sites is emitted once, so it may rely only on what holds at every site's copy.
        private void CombineSiteFacts(uint pc)
        {
            if (_sharedCalls is not { } shared || !shared.TryGetSitePcs(pc, out var sitePcs))
            {
                return;
            }

            foreach (var sitePc in sitePcs)
            {
                _execKnownFull &= IsExecKnownFull(sitePc);
                if (_stage != Gen5SpirvStage.Mesh && ObservationPlan() is { } plan)
                {
                    var other = plan.UnobservedWrites(sitePc);
                    _unobservedWrites = new Ir.VectorRegisterSet(_unobservedWrites.Low & other.Low, _unobservedWrites.High & other.High);
                }
            }
        }

        // The M0 values a relative move may see; in a body that serves several sites, those of every site's copy.
        private bool TryGetMoveRelativeOffsets(uint pc, out uint[] offsets)
        {
            _moveRelativeOffsets ??= Ir.Gen5MoveRelativeOffsets.Analyze(AnalysisProgram);
            if (!_moveRelativeOffsets.TryGetValue(pc, out var own))
            {
                offsets = [];
                return false;
            }

            offsets = own;
            if (_sharedCalls is { } shared && shared.TryGetSitePcs(pc, out var sitePcs))
            {
                var union = new SortedSet<uint>(own);
                foreach (var sitePc in sitePcs)
                {
                    if (!_moveRelativeOffsets.TryGetValue(sitePc, out var other))
                    {
                        offsets = [];
                        return false;
                    }

                    union.UnionWith(other);
                }

                offsets = [.. union];
            }

            return true;
        }

        private uint _callGroup;

        // The slot of each nonzero record argument among the call arguments, when the host pushes the arguments.
        private readonly Dictionary<ulong, int>? _callArgumentSlots;

        // The slot of each callee's address among the call targets, when the host pushes the targets.
        private readonly Dictionary<ulong, int>? _callTargetSlots;

        // A callee's address: the host's call target for it, or the link-time address without pushed targets.
        private uint CallTarget(ulong address)
        {
            if (_callTargetSlots is null || !_callTargetSlots.TryGetValue(address, out var slot))
            {
                return ULong(address);
            }

            return LoadCallData(_request.Bindings.CallTargetsDword + (uint)slot * 2);
        }

        // A pair of call data dwords. The layout sizes the shader data for them, so the load needs no bounds check.
        private uint LoadCallData(uint dword)
        {
            var (low, high) = LoadCallDataWords(dword);
            return Pair64(low, high);
        }

        private (uint Low, uint High) LoadCallDataWords(uint dword)
        {
            uint Word(uint index) => _request.Bindings.UsesPushData
                ? LoadShaderDataDword(UInt(index))
                : Load(_uintType, BlockWordPointer(_shaderData, UInt(index)));
            return (Word(dword), Word(dword + 1));
        }

        // A record's argument is compared with the host's argument for its slot, so the module does not depend on where
        // the game placed its argument data; a null argument stays the constant. The argument register pair is compared
        // a dword at a time, and each argument is loaded once in the current block of code.
        private uint CallArgumentMatches((uint Low, uint High) argument, ulong recordArgument, Dictionary<int, (uint Low, uint High)> loaded)
        {
            if (recordArgument == AnyCallArgument)
            {
                return _module.ConstantBool(true);
            }

            (uint Low, uint High) value;
            if (recordArgument == 0 || _callArgumentSlots is null || !_callArgumentSlots.TryGetValue(recordArgument, out var slot))
            {
                value = (UInt((uint)recordArgument), UInt((uint)(recordArgument >> 32)));
            }
            else if (!loaded.TryGetValue(slot, out value))
            {
                value = LoadCallDataWords(_request.Bindings.CallArgumentsDword + (uint)slot * 2);
                loaded.Add(slot, value);
            }

            return LogicalAnd(
                _module.AddInstruction(SpirvOp.IEqual, _boolType, argument.Low, value.Low),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, argument.High, value.High));
        }

        // The record a site's caller selects: the first whose target and argument match, as the per-record tests found
        // it. Its group selects the callee's test and its slot the record's variants; none matching leaves every test
        // failing, so the site faults as before.
        private void EmitSiteLookup(SiteRecords site)
        {
            var address = Pair64(LoadS(site.AddressRegister), LoadS(site.AddressRegister + 1));
            var argument = (LoadS(site.ArgumentRegister), LoadS(site.ArgumentRegister + 1));
            var loaded = new Dictionary<int, (uint, uint)>();
            var addressMatches = new Dictionary<ulong, uint>();
            var found = UInt(uint.MaxValue);
            for (var index = site.Records.Length - 1; index >= 0; index--)
            {
                var (recordAddress, recordArgument, group, slot) = site.Records[index];
                if (!addressMatches.TryGetValue(recordAddress, out var addressMatch))
                {
                    addressMatch = _module.AddInstruction(SpirvOp.IEqual, _boolType, address, CallTarget(recordAddress));
                    addressMatches.Add(recordAddress, addressMatch);
                }

                var matches = LogicalAnd(addressMatch, CallArgumentMatches(argument, recordArgument, loaded));
                found = _module.AddInstruction(SpirvOp.Select, _uintType, matches, UInt((group << 16) | slot), found);
            }

            Store(_callGroup, ShiftRightLogical(found, UInt(16)));
            Store(_callSlot, BitwiseAnd(found, UInt(0xFFFF)));
        }

        // A site whose copy the module does not keep calls the function of the last site's copy. It enters at the
        // body's first block, as main does there; the branch after the call returns to this site.
        private bool TryEmitSharedBodyCall(SharedBodyCallControl call, out string error)
        {
            error = string.Empty;
            if (!_outlinedBodyByPc.TryGetValue(call.BodyPc, out var body))
            {
                error = $"the shared call body at pc=0x{call.BodyPc:X} has no function";
                return false;
            }

            Store(_programCounter, UInt((uint)body.First));
            _module.AddInstruction(SpirvOp.FunctionCall, _voidType, body.Function);
            return true;
        }

        // The functions of a module's call bodies share the program's private state, which the NVIDIA compiler then keeps in
        // memory and takes for uniform: a branch on the EXEC flag does not narrow its convergence mask, and its warp syncs
        // inside the branch wait for the lanes that skipped it. A branch on this lane's bit of the condition's ballot is
        // divergent to it.
        private bool _divergentConditions;

        private uint DivergentCondition(uint condition)
        {
            if (!_divergentConditions)
            {
                return condition;
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var ballot = _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type, UInt(3), condition);
            var word = _module.AddInstruction(SpirvOp.VectorExtractDynamic, _uintType, ballot, ShiftRightLogical(lane, UInt(5)));
            return IsNotZero(BitwiseAnd(ShiftRightLogical(word, BitwiseAnd(lane, UInt(31))), UInt(1)));
        }

        // Before main's first block of a module with outlined bodies: the invocations past the dispatch's thread count end
        // here, so the active flag the block guards test is the same for every lane that runs; the program's other lane
        // branches go through DivergentCondition. Paired and emulated wave64 lanes keep theirs, since their host lanes
        // work together.
        private void BeginOutlinedCallBodies()
        {
            if (_outlinedBodies.Count == 0 || _subgroupInvocationIdInput == 0)
            {
                return;
            }

            _divergentConditions = true;
            if (_pairedLanes || _waveLowering is EmulatedWave64Lowering)
            {
                return;
            }

            var active = Load(_boolType, _programActive);
            var finished = _module.AllocateId();
            var running = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, running, 0);
            _module.AddStatement(SpirvOp.BranchConditional, active, running, finished);
            _module.AddLabel(finished);
            _module.AddStatement(SpirvOp.Return);
            _module.AddLabel(running);
        }

        private void EmitOutlinedBodyCall(int first, uint function)
        {
            var enters = LogicalAnd(
                Load(_boolType, _programActive),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _programCounter), UInt((uint)first)));
            var call = _module.AllocateId();
            var merge = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, merge, 0);
            _module.AddStatement(SpirvOp.BranchConditional, enters, call, merge);
            _module.AddLabel(call);
            _module.AddInstruction(SpirvOp.FunctionCall, _voidType, function);
            _module.AddStatement(SpirvOp.Branch, merge);
            _module.AddLabel(merge);
        }

        private bool TryEmitOutlinedCallBodies(IReadOnlyList<ShaderBlock> blocks, out string error)
        {
            error = string.Empty;
            if (_outlinedBodies.Count == 0)
            {
                return true;
            }

            const uint dontInline = 2;
            var functionType = _module.TypeFunction(_voidType);
            _emittingOutlinedBody = true;
            try
            {
                foreach (var (first, (last, function)) in _outlinedBodies.OrderBy(static body => body.Key))
                {
                    _module.BeginFunction(_voidType, functionType, dontInline, function);
                    _module.AddLabel();
                    // The block lengths main keeps are values of main.
                    InitializeStorageBlockLengths();
                    InitializeDeviceAddressFallback();
                    CaptureBufferTableValues();
                    if (!TryEmitStructuredRange(blocks, first, last, -1, out error))
                    {
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Return);
                    _module.EndFunction();
                }
            }
            finally
            {
                _emittingOutlinedBody = false;
            }

            return true;
        }

        // An instruction of a shared body: once when every record reads the same resources, else once per distinct set.
        private bool TryEmitSharedCallInstruction(Gen5ShaderInstruction instruction, uint[] pcs, out string error)
        {
            error = string.Empty;
            var signatures = new string[pcs.Length];
            for (var slot = 0; slot < pcs.Length; slot++)
            {
                signatures[slot] = CallRecordSignature(pcs[slot]);
            }

            _emittingCallRecord = true;
            try
            {
                if (signatures.All(signature => signature == signatures[0]))
                {
                    return TryEmitInstruction(instruction, out error);
                }

                if (TryEmitShiftedFlatInstruction(instruction, pcs, out var shiftedEmitted, out error))
                {
                    return shiftedEmitted;
                }

                var slotValue = Load(_uintType, _callSlot);
                var emitted = true;
                var recordError = string.Empty;
                foreach (var group in Enumerable.Range(0, pcs.Length).GroupBy(slot => signatures[slot]))
                {
                    var condition = _module.ConstantBool(false);
                    foreach (var slot in group)
                    {
                        condition = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, condition,
                            _module.AddInstruction(SpirvOp.IEqual, _boolType, slotValue, UInt((uint)slot)));
                    }

                    var record = instruction with { Pc = pcs[group.First()] };
                    EmitConditional(condition, () =>
                    {
                        if (emitted && !TryEmitInstruction(record, out var groupError))
                        {
                            emitted = false;
                            recordError = groupError;
                        }
                    });
                }

                error = recordError;
                return emitted;
            }
            finally
            {
                _emittingCallRecord = false;
            }
        }

        // The record's amount added to the flattened slots of the instruction being emitted, and those slots.
        private uint _flatSlotShift;
        private HashSet<uint>? _shiftedFlatSlots;
        private bool _shiftedFlatSlotsReserved;
        private readonly Dictionary<string, uint> _flatShiftTables = [];

        // The records of a call body flatten the descriptor words they load through their argument into slots of their
        // own, laid out alike: such a load differs between records only by one amount per record. It is emitted once,
        // its slots moved by the record's amount, instead of once per record under a test of the slot.
        private bool TryEmitShiftedFlatInstruction(Gen5ShaderInstruction instruction, uint[] pcs, out bool emitted, out string error)
        {
            emitted = false;
            error = string.Empty;
            var request = _request;
            var reference = CallRecordSignature(pcs[0], flatSlots: false);
            for (var record = 1; record < pcs.Length; record++)
            {
                if (CallRecordSignature(pcs[record], flatSlots: false) != reference)
                {
                    return false;
                }
            }

            var first = new List<uint>();
            for (uint component = 0; request.Memory.TryGetIndex(pcs[0], component, out var index); component++)
            {
                if (!request.FlattenedSlotByMemoryIndex.TryGetValue(index, out var slot))
                {
                    return false;
                }

                first.Add(slot);
            }

            if (first.Count == 0)
            {
                return false;
            }

            var shifts = new uint[pcs.Length];
            var reserved = true;
            for (var record = 0; record < pcs.Length; record++)
            {
                for (var component = 0; component < first.Count; component++)
                {
                    if (!request.Memory.TryGetIndex(pcs[record], (uint)component, out var index) ||
                        !request.FlattenedSlotByMemoryIndex.TryGetValue(index, out var slot))
                    {
                        return false;
                    }

                    var shift = unchecked(slot - first[component]);
                    if (component == 0)
                    {
                        shifts[record] = shift;
                    }
                    else if (shift != shifts[record])
                    {
                        return false;
                    }

                    reserved &= slot < request.FlattenedTableReservedWords;
                }
            }

            var key = string.Join(',', shifts);
            if (!_flatShiftTables.TryGetValue(key, out var table))
            {
                var type = _module.TypeArray(_uintType, (uint)shifts.Length);
                table = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Private, type),
                    SpirvStorageClass.Private,
                    _module.ConstantComposite(type, shifts.Select(shift => UInt(shift)).ToArray()));
                _module.AddName(table, "flatSlotShifts");
                _interfaces.Add(table);
                _flatShiftTables.Add(key, table);
            }

            _flatSlotShift = Load(_uintType, _module.AddInstruction(SpirvOp.AccessChain, _privateUintPointer, table, Load(_uintType, _callSlot)));
            _shiftedFlatSlots = [.. first];
            _shiftedFlatSlotsReserved = reserved;
            try
            {
                emitted = TryEmitInstruction(instruction with { Pc = pcs[0] }, out error);
            }
            finally
            {
                _flatSlotShift = 0;
                _shiftedFlatSlots = null;
            }

            return true;
        }

        // What the emitters read from the plan for the instruction at pc; equal strings emit the same code. Without
        // flatSlots, a flattened read counts as one whatever its slot.
        private string CallRecordSignature(uint pc, bool flatSlots = true)
        {
            var request = _request;
            var info = request.Resources.Info;
            var text = new StringBuilder();
            for (uint component = 0; request.Memory.TryGetIndex(pc, component, out var index); component++)
            {
                var entry = request.Memory[index];
                text.Append(entry.Kind).Append(',').Append(entry.Access).Append(',').Append(entry.Offset).Append(',').Append(entry.Dmask)
                    .Append(',').Append(entry.DataDwords).Append(',').Append(entry.DataBits).Append(',').Append(entry.ComponentCount)
                    .Append(',').Append(entry.DataFormat).Append(',').Append(entry.NumberFormat).Append(',').Append(entry.ImageSampleFlags)
                    .Append(',').Append(entry.ImageDimension).Append(',').Append(entry.ImageClass).Append(',').Append(entry.NeedsSampler)
                    .Append(',').Append(entry.AddressIsFull).Append(',').Append(entry.DataSigned).Append(',').Append(entry.Typed)
                    .Append(',').Append(entry.Formatted).Append(',').Append(entry.ImageHasMip).Append(',').Append(entry.ImageR128)
                    .Append(',').Append(entry.Glc).Append(',').Append(entry.Slc).Append(',').Append(entry.IndexEnabled)
                    .Append(',').Append(entry.OffsetEnabled).Append(',').Append(entry.PlanningOnly).Append(',').Append(entry.DeviceDescriptor)
                    .Append(',').Append(entry.AddressSpace).Append(',').Append(entry.BufferDescriptor?.Provenance)
                    .Append(',').Append(entry.BufferDescriptor?.StaticResource);
                if (entry.Kind == MemoryResourceKind.Image && request.CallRecordImageByMemoryIndex.TryGetValue(index, out var recordImage))
                {
                    // Each site's copy of a callee binds its own placeholder; the record's field decides the code.
                    text.Append(",record=").Append(recordImage.Field).Append(',').Append(recordImage.Slot);
                }
                else if (entry.Kind == MemoryResourceKind.Image && entry.Resource < (uint)info.Images.Count && _stage != Gen5SpirvStage.Pixel)
                {
                    // An image binds through its canonical element; its view facts decide the code.
                    var image = info.Images[(int)entry.Resource];
                    text.Append(",img=").Append(info.GetCanonicalImageBinding(entry.Resource)).Append(',').Append(image.ResourceClass)
                        .Append(',').Append(image.NumericClass).Append(',').Append(image.Dimension).Append(',').Append(image.MipMode)
                        .Append(',').Append(image.MipCount).Append(',').Append(image.ConversionFormat).Append(',').Append(image.ShaderSwizzle)
                        .Append(',').Append(image.Read).Append(',').Append(image.Written).Append(',').Append(image.Atomic)
                        .Append(',').Append(image.Atomic64).Append(',').Append(image.DepthCompare).Append(',')
                        .Append(image.EmulatedCompareFunction).Append(',').Append(image.Cube).Append(',').Append(image.R128)
                        .Append(',').Append(image.IndirectRoot).Append(',').Append(image.IndirectMappingOffset).Append(',')
                        .Append(image.IndirectSearchIterations).Append(',').Append(string.Join('/', image.IndirectResources))
                        .Append(',').Append(image.MipStatistics);
                }
                else
                {
                    text.Append(",res=").Append(entry.Resource);
                }

                var sampler = request.Resources.SamplerByMemoryIndex.TryGetValue(index, out var mappedSampler) ? mappedSampler : entry.Sampler;
                if (request.CallRecordSamplerByMemoryIndex.TryGetValue(index, out var recordSampler))
                {
                    text.Append(",recordsmp=").Append(recordSampler.Field).Append(',').Append(recordSampler.Slot);
                }
                else if (sampler != MemoryAccessInfo.NoResource && sampler < (uint)info.Samplers.Count)
                {
                    var samplerInfo = info.Samplers[(int)sampler];
                    text.Append(",smp=").Append(info.SamplerBinding(sampler)).Append(',').Append(samplerInfo.ForcePointFiltering)
                        .Append(',').Append(samplerInfo.DepthCompare);
                }
                else
                {
                    text.Append(",smp=").Append(sampler);
                }

                if (request.FlattenedSlotByMemoryIndex.TryGetValue(index, out var flattened)) text.Append(",flat=").Append(flatSlots ? flattened : 0);
                // A key read stores its value for a later image access of the same record, so each record keeps its own.
                if (request.IndirectKeyMemoryIndices.Contains(index)) text.Append(",key=").Append(index);
                if (request.IndirectOffsetKeyMemoryIndices.Contains(index)) text.Append(",offsetkey=").Append(index);
                if (request.IndirectRootByMemoryIndex.TryGetValue(index, out var root)) text.Append(",root=").Append(root);
                if (request.BufferCandidateTableByMemoryIndex.TryGetValue(index, out var table)) text.Append(",table=").Append(table);
                if (request.WrittenRangeSlotByMemoryIndex.TryGetValue(index, out var written)) text.Append(",written=").Append(written);
                text.Append(';');
            }

            return text.ToString();
        }
    }
}
