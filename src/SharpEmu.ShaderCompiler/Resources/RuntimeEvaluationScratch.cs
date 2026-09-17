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

    internal Dictionary<ScalarValue, ulong> Values { get; } = [];
    internal List<ScalarValue> Visiting { get; } = [];

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
        Values.Clear();
        Visiting.Clear();
        _rented = false;
        _nextAvailable = _available;
        _available = this;
    }
}
