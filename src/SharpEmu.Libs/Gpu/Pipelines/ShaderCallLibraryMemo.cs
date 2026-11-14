// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Reuse of call-library work across dispatches, on by default: a ray-traced pass derives its call library again at
// every dispatch, hundreds of times per frame. SHARPEMU_CALL_LIBRARY_REUSE=0, or any value but 1, turns it off. Unite
// still tracks the union's changes while reuse is off, so a result kept earlier is never reused over a union the path
// without reuse changed meanwhile.
//
// A run-time-record caller's library is derived again at every dispatch. Its records (which change as often as every
// frame) are this dispatch's own, but its topology (the sites, the callees each site names and their code, and what is
// derived from them: the callee order and slots, the layout identity, the union and the chosen callees) changes rarely:
//   - Topology: the per-record scan of the tables stays fresh (dedup, first occurrence, unreadable targets, current sites,
//     code hashes from the function cache's validated lookups). A run-time library whose sites and distinct callees (by
//     address, in first occurrence, each with the decoded program of the function cache's result) equal a previous read's
//     reuses that read's sorted sites, slots, identity and bodies, with this dispatch's records and current code hashes.
//   - Unite and WithSiteCallees depend on their input library only through its topology and carry its records, current
//     code hashes and current sites through; their results are kept by topology (and the union's version, or the chosen
//     callees map) and reused with the current dispatch's fields. A Unite that changed the union is not kept.
// The tables and functions are read at every dispatch as without reuse (ShaderCallLibrary.Read). Whole libraries are
// not cached by their exact input: the input words change at every dispatch.
internal sealed class ShaderCallLibraryMemo
{
    internal enum Mode { Off, On }

    internal static readonly Mode Configured = Environment.GetEnvironmentVariable("SHARPEMU_CALL_LIBRARY_REUSE") switch
    {
        null or "" or "1" => Mode.On,
        _ => Mode.Off,
    };

    // Whether reuse is on. Code-hash text follows it (ShaderCallLibrary.CodeKey).
    internal static bool Enabled => Configured != Mode.Off;

    // Tests make a memo that always or never reuses (active true or false), whatever the configuration (the variable is
    // read once per process). Null follows the configuration.
    private readonly bool? _active;

    internal ShaderCallLibraryMemo(bool? active = null)
    {
        _active = active;
    }

    private bool IsActive => _active ?? Enabled;

    private const int MaxTopologies = 4096;

    private enum Counter
    {
        TopologyHit, TopologyMiss, UniteHit, UniteMiss, UniteChanged, UniteUntokened, SitesHit, SitesMiss, SitesUntokened,
        Off, Count,
    }

    private readonly long[] _counters = new long[(int)Counter.Count];

    private void Count(Counter counter) => _counters[(int)counter]++;

    // Tests: a counter's total since the memo was made, by name.
    internal long Counted(string counter) => _counters[(int)Enum.Parse<Counter>(counter)];

    // ---- Read ------------------------------------------------------------------------------------------------------------

    // The library of the caller's call sites, as ShaderCallLibrary.Read returns it.
    internal ShaderCallLibrary Read(Gen5ShaderProgram caller, MemoryAccessTable memory, ResourceSnapshot snapshot, GuestWordReader read,
        ShaderFunctionCache? functionCache, bool runtimeArguments, uint userDataBase, ResidentGuestBytesReader? readBytes)
    {
        var active = IsActive;
        if (!active) Count(Counter.Off);
        return ShaderCallLibrary.Read(caller, memory, snapshot, read, functionCache, runtimeArguments, userDataBase, readBytes,
            active ? this : null);
    }

    // ---- Topology --------------------------------------------------------------------------------------------------------

    // A run-time library's topology: the caller, and per site its registers, its return address and the set of distinct
    // callees its records name (by address), each with the program the function cache's result decoded. The static library
    // sorts each site's callees by code and address, so the order the records name them in is not part of it.
    private sealed class Topology(ulong[] words, object[] programs, ShaderCallLibrary library)
    {
        public readonly ulong[] Words = words;
        public readonly object[] Programs = programs;
        public readonly ShaderCallLibrary Library = library;
    }

    private readonly Dictionary<(object Instructions, ulong Hash), List<Topology>> _topologies = new();
    private int _topologyCount;
    private readonly List<ulong> _topologyWords = new(256);
    private readonly List<object> _topologyPrograms = new(128);
    private readonly Dictionary<ulong, object> _callees = new();
    private readonly List<ulong> _sorted = new();

    // ShaderCallLibrary.WithRuntimeArguments for a read's sites and callees, reusing a previous read's topology. Called by
    // ShaderCallLibrary.Read; the caller attaches the read's current sites. records: the read's records when it collected
    // them itself (a one-pass read, whose sites hold each callee once); null collects them from the sites.
    internal ShaderCallLibrary Runtime(Gen5ShaderProgram caller, List<ShaderCallSite> calls, Dictionary<ulong, byte[]> codeHashes,
        Dictionary<ulong, (ulong Address, byte[] Bytes)[]> codeRuns, (ulong Target, ulong Argument)[]? records = null)
    {
        var words = _topologyWords;
        var programs = _topologyPrograms;
        words.Clear();
        programs.Clear();
        words.Add(caller.Address);
        foreach (var site in calls)
        {
            words.Add(site.Pc);
            words.Add(site.AddressRegister | (ulong)site.ArgumentRegister << 32);
            words.Add(site.ReturnRegister);
            words.Add(site.ReturnAddress);
            _callees.Clear();
            foreach (var target in site.Targets)
            {
                _callees.TryAdd(target.Address, target.Program);
            }

            words.Add((ulong)_callees.Count);
            _sorted.Clear();
            _sorted.AddRange(_callees.Keys);
            _sorted.Sort();
            foreach (var address in _sorted)
            {
                words.Add(address);
                programs.Add(_callees[address]);
            }
        }

        var hash = Hash(CollectionsMarshal.AsSpan(words));
        foreach (var program in CollectionsMarshal.AsSpan(programs)) hash = (hash ^ (ulong)RuntimeHelpers.GetHashCode(program)) * 0x100000001B3UL;
        var key = ((object)caller.Instructions, hash);
        if (_topologies.TryGetValue(key, out var candidates))
        {
            foreach (var topology in candidates)
            {
                if (!topology.Words.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(words)) ||
                    !SameReferences(topology.Programs, CollectionsMarshal.AsSpan(programs)))
                {
                    continue;
                }

                Count(Counter.TopologyHit);
                // The records, as WithRuntimeArguments collects them: every site's distinct target and argument pairs, in
                // first occurrence.
                var current = records ?? ShaderCallLibrary.RuntimeRecords(calls);
                return topology.Library with { Records = current, CurrentCodeHashes = codeHashes };
            }
        }

        Count(Counter.TopologyMiss);
        if (_topologyCount >= MaxTopologies)
        {
            _topologies.Clear();
            _topologyCount = 0;
        }

        var library = ShaderCallLibrary.WithRuntimeArguments(caller, calls, codeHashes, codeRuns,
            records ?? ShaderCallLibrary.RuntimeRecords(calls)) with { Topology = new object() };
        if (!_topologies.TryGetValue(key, out candidates)) _topologies[key] = candidates = new List<Topology>(1);
        candidates.Add(new Topology(words.ToArray(), programs.ToArray(), library));
        _topologyCount++;
        return library;
    }

    private static bool SameReferences(object[] left, ReadOnlySpan<object> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (!ReferenceEquals(left[index], right[index])) return false;
        return true;
    }

    // ---- Unite and site callees, by topology ------------------------------------------------------------------------------

    private sealed class UniteResult(object union, long version, ulong callerAddress, int maxCallees, ShaderCallLibrary output)
    {
        public readonly object Union = union;
        public readonly long Version = version;
        public readonly ulong CallerAddress = callerAddress;
        public readonly int MaxCallees = maxCallees;
        public readonly ShaderCallLibrary Output = output;
    }

    private sealed class SitesResult(object callees, ulong callerAddress, ShaderCallLibrary output)
    {
        public readonly object Callees = callees;
        public readonly ulong CallerAddress = callerAddress;
        public readonly ShaderCallLibrary Output = output;
    }

    private readonly ConditionalWeakTable<object, UniteResult> _unites = new();
    private readonly ConditionalWeakTable<object, StrongBox<long>> _unionVersions = new();
    private readonly ConditionalWeakTable<object, SitesResult> _sites = new();

    // A kept result with this dispatch's own fields.
    private static ShaderCallLibrary WithCurrent(ShaderCallLibrary kept, ShaderCallLibrary current) => kept with
    {
        Records = current.Records,
        CurrentCodeHashes = current.CurrentCodeHashes,
        CurrentSites = current.CurrentSites,
    };

    // ShaderCallLibrary.Unite (which updates union): a call that changed nothing is kept by the input's topology and the
    // union's version, and reused while both hold.
    internal ShaderCallLibrary Unite(ShaderCallLibrary library, ulong callerAddress, Dictionary<uint, Dictionary<ulong, UnitedCallee>> union,
        int maxCallees) => Unite(library, callerAddress, union, maxCallees, out _);

    // changed: whether this call changed the union (its version moved).
    internal ShaderCallLibrary Unite(ShaderCallLibrary library, ulong callerAddress, Dictionary<uint, Dictionary<ulong, UnitedCallee>> union,
        int maxCallees, out bool changed)
    {
        changed = false;
        var version = _unionVersions.GetValue(union, static _ => new StrongBox<long>());
        if (library.Topology is not { } topology || !IsActive)
        {
            Count(Counter.UniteUntokened);
            var original = library.Unite(callerAddress, union, maxCallees, out var originalChanged);
            if (originalChanged)
            {
                version.Value++;
                changed = true;
            }

            return original;
        }

        if (_unites.TryGetValue(topology, out var kept) && ReferenceEquals(kept.Union, union) && kept.Version == version.Value &&
            kept.CallerAddress == callerAddress && kept.MaxCallees == maxCallees)
        {
            Count(Counter.UniteHit);
            return WithCurrent(kept.Output, library);
        }

        Count(Counter.UniteMiss);
        var output = library.Unite(callerAddress, union, maxCallees, out var unionChanged);
        if (unionChanged)
        {
            Count(Counter.UniteChanged);
            version.Value++;
            changed = true;
            return output;
        }

        output = output with { Topology = new object() };
        _unites.AddOrUpdate(topology, new UniteResult(union, version.Value, callerAddress, maxCallees, output));
        return output;
    }

    // ShaderCallLibrary.WithSiteCallees, kept by the input's topology and the callees map (a chosen map is never changed).
    internal ShaderCallLibrary WithSiteCallees(ShaderCallLibrary library, ulong callerAddress,
        IReadOnlyDictionary<uint, Dictionary<ulong, UnitedCallee>> callees)
    {
        if (library.Topology is not { } topology || !IsActive)
        {
            Count(Counter.SitesUntokened);
            return library.WithSiteCallees(callerAddress, callees);
        }

        if (_sites.TryGetValue(topology, out var kept) && ReferenceEquals(kept.Callees, callees) && kept.CallerAddress == callerAddress)
        {
            Count(Counter.SitesHit);
            return WithCurrent(kept.Output, library);
        }

        Count(Counter.SitesMiss);
        var output = library.WithSiteCallees(callerAddress, callees) with { Topology = new object() };
        _sites.AddOrUpdate(topology, new SitesResult(callees, callerAddress, output));
        return output;
    }

    // ---- Common ----------------------------------------------------------------------------------------------------------

    private static ulong Hash(ReadOnlySpan<ulong> words)
    {
        var hash = 0xCBF29CE484222325UL ^ (ulong)words.Length;
        foreach (var word in words)
        {
            hash = (hash ^ word) * 0x100000001B3UL;
            hash ^= hash >> 29;
        }

        return hash;
    }

    // The first difference between two libraries, or null when they are the same: identities, sites with their registers
    // and targets (address, argument, the same decoded program), records in order, arguments, bodies, callee code and runs
    // (by address), and the current code hashes (in order) and sites of the dispatch.
    internal static string? Difference(ShaderCallLibrary left, ShaderCallLibrary right)
    {
        if (left.Identity != right.Identity) return "identity";
        if (left.LayoutIdentity != right.LayoutIdentity) return "layout identity";
        if (!left.Arguments.AsSpan().SequenceEqual(right.Arguments)) return "arguments";
        if (!left.Records.AsSpan().SequenceEqual(right.Records)) return "records";
        if (left.Bodies != right.Bodies) return "bodies";
        if (left.Calls.Count != right.Calls.Count) return "site count";
        for (var index = 0; index < left.Calls.Count; index++)
        {
            var (a, b) = (left.Calls[index], right.Calls[index]);
            if (a.Pc != b.Pc || a.AddressRegister != b.AddressRegister || a.ArgumentRegister != b.ArgumentRegister ||
                a.ReturnRegister != b.ReturnRegister || a.ReturnAddress != b.ReturnAddress || a.Targets.Count != b.Targets.Count)
                return $"site {index}";
            for (var target = 0; target < a.Targets.Count; target++)
            {
                var (x, y) = (a.Targets[target], b.Targets[target]);
                if (x.Address != y.Address || x.Argument != y.Argument || !ReferenceEquals(x.Program, y.Program))
                    return $"site {index} target {target}";
            }
        }

        if (!SameBytesByKey(left.CodeHashes, right.CodeHashes)) return "code hashes";
        if (!SameBytes(left.CurrentCodeHashes, right.CurrentCodeHashes)) return "current code hashes";
        if (left.CodeRuns.Count != right.CodeRuns.Count) return "code runs";
        foreach (var (address, runs) in left.CodeRuns)
        {
            if (!right.CodeRuns.TryGetValue(address, out var other) || runs.Length != other.Length) return "code runs";
            for (var run = 0; run < runs.Length; run++)
                if (runs[run].Address != other[run].Address || !runs[run].Bytes.AsSpan().SequenceEqual(other[run].Bytes)) return "code runs";
        }

        if (left.CurrentSites.Count != right.CurrentSites.Count) return "current sites";
        foreach (var (address, sites) in left.CurrentSites)
            if (!right.CurrentSites.TryGetValue(address, out var other) || !sites.SetEquals(other)) return "current sites";
        return null;
    }

    // Same keys with equal bytes, in any order: the static code hashes are looked up by address, never enumerated for a
    // decision, and a reused topology holds them in the order of the read that derived it.
    private static bool SameBytesByKey(Dictionary<ulong, byte[]> left, Dictionary<ulong, byte[]> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (address, bytes) in left)
        {
            if (!right.TryGetValue(address, out var other) || !bytes.AsSpan().SequenceEqual(other)) return false;
        }

        return true;
    }

    // Same keys with equal bytes, in the same order.
    private static bool SameBytes(Dictionary<ulong, byte[]> left, Dictionary<ulong, byte[]> right)
    {
        if (left.Count != right.Count) return false;
        using var first = left.GetEnumerator();
        using var second = right.GetEnumerator();
        while (first.MoveNext() && second.MoveNext())
        {
            if (first.Current.Key != second.Current.Key || !first.Current.Value.AsSpan().SequenceEqual(second.Current.Value)) return false;
        }

        return true;
    }
}
