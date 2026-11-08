// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Diagnostics;

public static class DispatchRunTally
{
    public enum End
    {
        Sync,
        Wait,
        Draw,
        Program,
        Shape,
        Queue,
        Count,
    }

    private static readonly string[] EndNames = ["sync", "wait", "draw", "program", "shape", "queue"];
    private static readonly int[] Bounds = [1, 7, 31, 255];
    private static readonly long[] _lengths = new long[Bounds.Length + 1];
    private static readonly long[] _dispatchesByLength = new long[Bounds.Length + 1];
    private static readonly long[] _ends = new long[(int)End.Count];
    private static long _dispatches;
    private static long _indirect;
    private static long _longRunsWithVaryingUserData;
    private static long _longRunsWithVaryingDims;
    private static long _longRuns;
    private static long _longRunsWithVaryingProgram;
    private static long _longRunsWithVaryingImages;
    private static long _longRunsWithoutTableBlock;
    private static long _longRunsWithGlobalDataShare;
    private static long _longRunsWithConsumeCandidates;
    private static long _longRunsWithKnownWrites;
    private static long _longRunsWithIndirect;
    private static long _longRunsWithSkippedMembers;
    private static long _longRunsMergeable;
    private static long _mergeableDispatches;

    private static bool _memberPending;
    private static bool _resolved;
    private static ulong _programId;
    private static ulong _imageHash;
    private static bool _programVaries;
    private static bool _imagesVary;
    private static bool _withoutTableBlock;
    private static bool _globalDataShare;
    private static bool _consumeCandidates;
    private static bool _knownWrites;
    private static bool _indirectMembers;
    private static bool _skippedMembers;

    private static object? _queue;
    private static ulong _address;
    private static uint _threads;
    private static uint _localDataShare;
    private static int _length;
    private static bool _userDataVaries;
    private static bool _dimsVary;
    private static ulong _userDataHash;
    private static ulong _dims;
    private static End _pendingEnd = End.Program;

    public static void NoteDispatch(object queue, ulong address, uint threadsX, uint threadsY, uint threadsZ, uint localDataShare,
        ReadOnlySpan<uint> userData, uint groupsX, uint groupsY, uint groupsZ, bool indirect)
    {
        _dispatches++;
        if (indirect)
        {
            _indirect++;
        }

        FinishMember();

        var threads = threadsX | (threadsY << 10) | (threadsZ << 20);
        var hash = Hash(userData);
        var dims = groupsX | ((ulong)groupsY << 21) | ((ulong)groupsZ << 42);
        if (_length != 0 && _pendingEnd == End.Program)
        {
            if (!ReferenceEquals(queue, _queue))
            {
                _pendingEnd = End.Queue;
            }
            else if (address != _address)
            {
                _pendingEnd = End.Program;
            }
            else if (threads != _threads || localDataShare != _localDataShare)
            {
                _pendingEnd = End.Shape;
            }
            else
            {
                _length++;
                _userDataVaries |= hash != _userDataHash;
                _dimsVary |= dims != _dims;
                _userDataHash = hash;
                _dims = dims;
                _indirectMembers |= indirect;
                _memberPending = true;
                return;
            }
        }

        Close();
        _queue = queue;
        _address = address;
        _threads = threads;
        _localDataShare = localDataShare;
        _length = 1;
        _userDataVaries = false;
        _dimsVary = false;
        _userDataHash = hash;
        _dims = dims;
        _pendingEnd = End.Program;
        _resolved = false;
        _programVaries = false;
        _imagesVary = false;
        _withoutTableBlock = false;
        _globalDataShare = false;
        _consumeCandidates = false;
        _knownWrites = false;
        _indirectMembers = indirect;
        _skippedMembers = false;
        _memberPending = true;
    }

    public static void NoteResolved(ulong programId, uint[][] images, uint[][] samplers, bool tableBlock, bool globalDataShare, bool consumeCandidate, bool knownWrite)
    {
        if (!_memberPending)
        {
            return;
        }

        _memberPending = false;
        var imageHash = Hash(images) * 31 + Hash(samplers);
        if (_resolved)
        {
            _programVaries |= programId != _programId;
            _imagesVary |= imageHash != _imageHash;
        }

        _resolved = true;
        _programId = programId;
        _imageHash = imageHash;
        _withoutTableBlock |= !tableBlock;
        _globalDataShare |= globalDataShare;
        _consumeCandidates |= consumeCandidate;
        _knownWrites |= knownWrite;
    }

    private static void FinishMember()
    {
        if (_memberPending)
        {
            _skippedMembers = true;
            _memberPending = false;
        }
    }

    private static readonly string[] ConsumeKindNames = ["metadata", "constant_fill", "bounded_fill", "bounded_copy", "image_clear"];
    private static readonly long[] _consumeCandidatesByKind = new long[ConsumeKindNames.Length];
    private static readonly long[] _consumedByKind = new long[ConsumeKindNames.Length];
    private static readonly object _consumeLock = new();
    private static readonly Dictionary<ulong, (long Candidates, long Consumed, int Kinds)> _consumePrograms = new();

    public static void NoteConsume(ulong programHash, int kinds, bool consumed)
    {
        for (var kind = 0; kind < ConsumeKindNames.Length; kind++)
        {
            if ((kinds & (1 << kind)) != 0)
            {
                Interlocked.Increment(ref _consumeCandidatesByKind[kind]);
                if (consumed)
                {
                    Interlocked.Increment(ref _consumedByKind[kind]);
                }
            }
        }

        lock (_consumeLock)
        {
            _consumePrograms.TryGetValue(programHash, out var entry);
            _consumePrograms[programHash] = (entry.Candidates + 1, entry.Consumed + (consumed ? 1 : 0), entry.Kinds | kinds);
        }
    }

    private static string TakeConsumeReport()
    {
        var kinds = string.Join(",", Enumerable.Range(0, ConsumeKindNames.Length).Select(index =>
            FormattableString.Invariant($"{ConsumeKindNames[index]}:{Interlocked.Exchange(ref _consumeCandidatesByKind[index], 0)}/{Interlocked.Exchange(ref _consumedByKind[index], 0)}")));
        string programs;
        int count;
        lock (_consumeLock)
        {
            count = _consumePrograms.Count;
            programs = string.Join(",", _consumePrograms.OrderByDescending(pair => pair.Value.Candidates).Take(8).Select(pair =>
                FormattableString.Invariant($"{pair.Key:X16}:{pair.Value.Candidates}/{pair.Value.Consumed}/k{pair.Value.Kinds}")));
            _consumePrograms.Clear();
        }

        return " consume_candidates(candidates/consumed)=" + kinds + FormattableString.Invariant($" consume_programs={count} top=") + programs;
    }

    public static void NoteSync() => Break(End.Sync);

    public static void NoteWait() => Break(End.Wait);

    public static void NoteDraw() => Break(End.Draw);

    private static void Break(End reason)
    {
        if (_length != 0 && _pendingEnd == End.Program)
        {
            _pendingEnd = reason;
        }
    }

    private static void Close()
    {
        FinishMember();
        if (_length == 0)
        {
            return;
        }

        var bucket = 0;
        while (bucket < Bounds.Length && _length > Bounds[bucket])
        {
            bucket++;
        }

        _lengths[bucket]++;
        _dispatchesByLength[bucket] += _length;
        _ends[(int)_pendingEnd]++;
        if (_length >= 8)
        {
            _longRuns++;
            if (_userDataVaries) _longRunsWithVaryingUserData++;
            if (_dimsVary) _longRunsWithVaryingDims++;
            if (_programVaries) _longRunsWithVaryingProgram++;
            if (_imagesVary) _longRunsWithVaryingImages++;
            if (_withoutTableBlock) _longRunsWithoutTableBlock++;
            if (_globalDataShare) _longRunsWithGlobalDataShare++;
            if (_consumeCandidates) _longRunsWithConsumeCandidates++;
            if (_knownWrites) _longRunsWithKnownWrites++;
            if (_indirectMembers) _longRunsWithIndirect++;
            if (_skippedMembers) _longRunsWithSkippedMembers++;
            if (!_programVaries && !_imagesVary && !_withoutTableBlock && !_globalDataShare && !_consumeCandidates && !_knownWrites && !_indirectMembers && !_skippedMembers)
            {
                _longRunsMergeable++;
                _mergeableDispatches += _length;
            }
        }

        _length = 0;
    }

    private static ulong Hash(uint[][] arrays)
    {
        var hash = 0xCBF29CE484222325ul;
        foreach (var words in arrays)
        {
            hash = (hash ^ Hash(words)) * 0x100000001B3ul;
        }

        return hash;
    }

    private static ulong Hash(ReadOnlySpan<uint> values)
    {
        var hash = 0xCBF29CE484222325ul;
        foreach (var value in values)
        {
            hash = (hash ^ value) * 0x100000001B3ul;
        }

        return hash;
    }

    public static string TakeReport()
    {
        var lengths = string.Join(",", Enumerable.Range(0, _lengths.Length).Select(index =>
            FormattableString.Invariant($"{Label(index)}:{Interlocked.Exchange(ref _lengths[index], 0)}/{Interlocked.Exchange(ref _dispatchesByLength[index], 0)}")));
        var ends = string.Join(",", Enumerable.Range(0, EndNames.Length).Select(index =>
            FormattableString.Invariant($"{EndNames[index]}:{Interlocked.Exchange(ref _ends[index], 0)}")));
        return FormattableString.Invariant($"[PERF][DISPATCH_RUNS] dispatches={Interlocked.Exchange(ref _dispatches, 0)} indirect={Interlocked.Exchange(ref _indirect, 0)} ") +
            "runs_by_length(runs/dispatches)=" + lengths + " ended=" + ends + " " +
            FormattableString.Invariant($"long_runs={Interlocked.Exchange(ref _longRuns, 0)} user_data_varies={Interlocked.Exchange(ref _longRunsWithVaryingUserData, 0)} dims_vary={Interlocked.Exchange(ref _longRunsWithVaryingDims, 0)} ") +
            FormattableString.Invariant($"program_varies={Interlocked.Exchange(ref _longRunsWithVaryingProgram, 0)} images_vary={Interlocked.Exchange(ref _longRunsWithVaryingImages, 0)} no_table={Interlocked.Exchange(ref _longRunsWithoutTableBlock, 0)} gds={Interlocked.Exchange(ref _longRunsWithGlobalDataShare, 0)} ") +
            FormattableString.Invariant($"consume={Interlocked.Exchange(ref _longRunsWithConsumeCandidates, 0)} known_write={Interlocked.Exchange(ref _longRunsWithKnownWrites, 0)} indirect={Interlocked.Exchange(ref _longRunsWithIndirect, 0)} skipped={Interlocked.Exchange(ref _longRunsWithSkippedMembers, 0)} ") +
            FormattableString.Invariant($"mergeable={Interlocked.Exchange(ref _longRunsMergeable, 0)}/{Interlocked.Exchange(ref _mergeableDispatches, 0)}") +
            TakeConsumeReport();
    }

    private static string Label(int index) => index switch
    {
        0 => "1",
        1 => "2-7",
        2 => "8-31",
        3 => "32-255",
        _ => "256+",
    };
}
