// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Only collection capacity survives a lease. Results and graph references do not.
internal sealed class RuntimeEvaluationScratch : IDisposable
{
    [ThreadStatic]
    private static RuntimeEvaluationScratch? _available;

    private RuntimeEvaluationScratch? _nextAvailable;
    private bool _rented;

    internal ScalarValueCache Values { get; } = new();
    internal CompiledValueCache CompiledValues { get; } = new();
    internal List<ScalarValue> Visiting { get; } = [];
    internal Stack<int> PendingBranches { get; } = new();
    private bool[] _visitedBranches = [];

    internal bool[] PrepareBranches(int count)
    {
        if (_visitedBranches.Length < count) _visitedBranches = new bool[count];
        else Array.Clear(_visitedBranches, 0, count);
        PendingBranches.Clear();
        return _visitedBranches;
    }

    internal static RuntimeEvaluationScratch Rent()
    {
        var scratch = _available;
        if (scratch is null)
            scratch = new RuntimeEvaluationScratch();
        else
            _available = scratch._nextAvailable;

        scratch._nextAvailable = null;
        scratch._rented = true;
        return scratch;
    }

    public void Dispose()
    {
        if (!_rented) return;
        Values.Reset();
        CompiledValues.Reset();
        Visiting.Clear();
        PendingBranches.Clear();
        _rented = false;
        _nextAvailable = _available;
        _available = this;
    }
}

// Plan-local indices avoid hashing graph nodes on every dependency evaluation.
internal sealed class CompiledValueCache
{
    private ulong[] _values = [];
    private uint[] _stamps = [];
    private uint _generation = 2;

    internal void EnsureCapacity(int count)
    {
        if (_values.Length >= count) return;
        Array.Resize(ref _values, count);
        Array.Resize(ref _stamps, count);
    }

    internal int Begin(int index, out ulong result)
    {
        result = 0;
        if (_stamps[index] == _generation)
        {
            result = _values[index];
            return 1;
        }
        if (_stamps[index] == _generation + 1) return -1;
        _stamps[index] = _generation + 1;
        return 0;
    }

    internal bool Contains(int index) => _stamps[index] == _generation;

    internal void Store(int index, ulong value)
    {
        _values[index] = value;
        _stamps[index] = _generation;
    }

    internal void End(int index)
    {
        if (_stamps[index] == _generation + 1) _stamps[index] = 0;
    }

    internal void Reset()
    {
        _generation = unchecked(_generation + 2);
        if (_generation != 0) return;
        Array.Clear(_stamps);
        _generation = 2;
    }
}
