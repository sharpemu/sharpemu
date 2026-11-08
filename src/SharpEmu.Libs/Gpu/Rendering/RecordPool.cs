// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed class RecordPool
{
    private const int Capacity = 8192;

    private readonly ConcurrentQueue<ResolvedDraw> _draws = new();
    private readonly ConcurrentQueue<ResolvedDispatch> _dispatches = new();

    public ResolvedDraw RentDraw() => _draws.TryDequeue(out var record) ? record : new ResolvedDraw();

    public ResolvedDispatch RentDispatch() => _dispatches.TryDequeue(out var record) ? record : new ResolvedDispatch();

    public void Return(ResolvedDraw record)
    {
        if (_draws.Count < Capacity)
        {
            record.Reset();
            _draws.Enqueue(record);
        }
    }

    public void Return(ResolvedDispatch record)
    {
        if (_dispatches.Count < Capacity)
        {
            record.Reset();
            _dispatches.Enqueue(record);
        }
    }
}
