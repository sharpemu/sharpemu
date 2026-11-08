// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The callees of one runtime-record caller that its calls reach (CallMarks). The caller's programs hold only those: the
// callees its tables name but no call reaches cost no compile time, so a first visit compiles the few that the place calls
// instead of every callee the tables hold. A callee counts once a mark snapshot taken after the caller first named its code
// at that address holds its bit; until then its calls return at once, as a call does for any callee a program lacks. While
// the program of the chosen callees is not yet ready the choice stays, so the caller compiles one program at a time and the
// callees that calls reach meanwhile join the next one. A new choice also waits until no callee has become called for a few
// snapshots: the calls of a new view reach its callees over a few frames, and each choice costs a compile.
internal sealed class ShaderCalleeTiers
{
    // Marks count from this many snapshots after the code was named: the copy in flight then and the one after it can hold
    // the marks of dispatches recorded before.
    private const long SnapshotMargin = 2;

    // A choice waits this many snapshots after the last callee became called, and at most MaxSettle after the first one that
    // the chosen program lacks.
    private const long Settle = 8;
    private const long MaxSettle = 64;

    private readonly HashSet<string> _called;
    private readonly Dictionary<ulong, (byte[] Code, long Snapshot)> _named = [];
    private Dictionary<uint, Dictionary<ulong, UnitedCallee>>? _chosen;
    private long _lastCalled = -1;
    private long _firstUnchosen = -1;

    // The callees an earlier session kept for the caller count as called, and its first program holds exactly those, whose
    // pipeline the linked cache holds. The union also names the other addresses the game loaded the same code at in this
    // session, and a first program that held them too would be another compile of minutes.
    public ShaderCalleeTiers(IReadOnlyDictionary<uint, Dictionary<ulong, UnitedCallee>> kept)
    {
        _called = [.. kept.Values.SelectMany(static callees => callees.Values).Select(static callee => Convert.ToHexString(callee.Code))];
        if (kept.Count != 0)
        {
            _chosen = kept.ToDictionary(static site => site.Key, static site => site.Value);
        }
    }

    public int Called => _called.Count;

    // The generation of the program linked from the chosen callees, once the caller has linked it.
    public long? LinkedGeneration { get; set; }

    // Counts the callees this dispatch names whose marks show a call since the caller named them, and returns how many.
    public int Observe(ShaderCallLibrary library, ulong callerHash, IShaderPipelineHost host)
    {
        var snapshots = host.CallMarkSnapshots;
        var added = 0;
        foreach (var (address, code) in library.CurrentCodeHashes)
        {
            var key = Convert.ToHexString(code);
            if (_called.Contains(key))
            {
                continue;
            }

            if (!_named.TryGetValue(address, out var named) || !named.Code.AsSpan().SequenceEqual(code))
            {
                _named[address] = (code, snapshots);
            }
            else if (host.CallMarkedAt(CallMarks.Bit(address, callerHash)) > named.Snapshot + SnapshotMargin && _called.Add(key))
            {
                added++;
            }
        }

        if (added != 0)
        {
            _lastCalled = snapshots;
            if (_firstUnchosen < 0)
            {
                _firstUnchosen = snapshots;
            }
        }

        return added;
    }

    // A dispatch named the code of a chosen callee at an address no slot of the program serves (the game holds more copies of
    // that code than the program has slots): the next choice adds the copy, as it adds a callee that became called.
    public void NoteUnservedCopy(long snapshots)
    {
        if (_firstUnchosen < 0)
        {
            _firstUnchosen = snapshots;
        }
    }

    // The callees each site links: the union's called callees, chosen again once the program of the last choice is ready
    // (fallbackGeneration, the generation of the caller's ready program, reaches its generation), a callee became called or a
    // copy went unserved since that choice, and the calls have settled (snapshots, the host's mark snapshot count). A copy
    // the game moved needs no new program: a slot serves the address that now holds its code.
    public IReadOnlyDictionary<uint, Dictionary<ulong, UnitedCallee>> Choose(Dictionary<uint, Dictionary<ulong, UnitedCallee>> union,
        long fallbackGeneration, long snapshots)
    {
        if (_chosen is not null && (LinkedGeneration is not { } generation || fallbackGeneration < generation || _firstUnchosen < 0 ||
            snapshots - _lastCalled < Settle && snapshots - _firstUnchosen < MaxSettle))
        {
            return _chosen;
        }

        _firstUnchosen = -1;

        var chosen = new Dictionary<uint, Dictionary<ulong, UnitedCallee>>(union.Count);
        foreach (var (pc, callees) in union)
        {
            var linked = new Dictionary<ulong, UnitedCallee>();
            foreach (var (address, callee) in callees)
            {
                if (_called.Contains(Convert.ToHexString(callee.Code)))
                {
                    linked.Add(address, callee);
                }
            }

            chosen.Add(pc, linked);
        }

        if (_chosen is null || !SameChoice(_chosen, chosen))
        {
            _chosen = chosen;
            LinkedGeneration = null;
        }

        return _chosen;
    }

    private static bool SameChoice(Dictionary<uint, Dictionary<ulong, UnitedCallee>> left, Dictionary<uint, Dictionary<ulong, UnitedCallee>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (pc, callees) in left)
        {
            if (!right.TryGetValue(pc, out var other) || other.Count != callees.Count)
            {
                return false;
            }

            foreach (var (address, callee) in callees)
            {
                if (!other.TryGetValue(address, out var same) || !ReferenceEquals(same, callee))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
