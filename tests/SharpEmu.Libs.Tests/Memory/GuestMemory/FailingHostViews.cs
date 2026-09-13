// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

// Delegates to the real host. One chosen call fails without reaching the host.
internal sealed class FailingHostViews : IHostViewMemory
{
    public enum Op
    {
        CreateBacking,
        ReserveHole,
        SplitHole,
        JoinHoles,
        FreeHole,
        MapView,
        UnmapView,
        CommitPrivate,
        ReleasePrivate,
        ChangeAccess,
        FreeOwnedRange,
    }

    private readonly IHostViewMemory _inner;
    private Op? _failOp;
    private int _failAfter;
    private HostViewFailure _failure;

    public FailingHostViews(IHostViewMemory inner)
    {
        _inner = inner;
    }

    public List<Op> Log { get; } = new();

    public Action? AfterMapView { get; set; }

    public Action<ulong, ulong>? AfterUnmapView { get; set; }

    public Action<ulong, ulong>? BeforeReserveHole { get; set; }

    public ulong PageSize => _inner.PageSize;

    public ulong Granularity => _inner.Granularity;

    public void FailNext(Op op, int afterCalls = 0, HostViewFailure failure = HostViewFailure.FixedMapFailed)
    {
        _failOp = op;
        _failAfter = afterCalls;
        _failure = failure;
    }

    public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
    {
        if (ShouldFail(Op.CreateBacking))
        {
            backing = null;
            failure = HostViewFailure.BackingUnavailable;
            return false;
        }

        return _inner.TryCreateBacking(size, out backing, out failure);
    }

    public ulong ReserveHole(ulong address, ulong size)
    {
        BeforeReserveHole?.Invoke(address, size);
        return ShouldFail(Op.ReserveHole) ? 0 : _inner.ReserveHole(address, size);
    }

    public bool SplitHole(ulong address, ulong size) =>
        !ShouldFail(Op.SplitHole) && _inner.SplitHole(address, size);

    public bool JoinHoles(ulong address, ulong size) =>
        !ShouldFail(Op.JoinHoles) && _inner.JoinHoles(address, size);

    public bool FreeHole(ulong address, ulong size) =>
        !ShouldFail(Op.FreeHole) && _inner.FreeHole(address, size);

    public bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
    {
        if (ShouldFail(Op.MapView))
        {
            failure = _failure;
            return false;
        }

        var mapped = _inner.TryMapView(backing, address, offset, size, protection, out failure);
        AfterMapView?.Invoke();
        return mapped;
    }

    public bool UnmapView(ulong address, ulong size)
    {
        if (ShouldFail(Op.UnmapView) || !_inner.UnmapView(address, size)) return false;
        AfterUnmapView?.Invoke(address, size);
        return true;
    }

    public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection) =>
        !ShouldFail(Op.CommitPrivate) && _inner.CommitPrivate(address, size, protection);

    public bool ReleasePrivate(ulong address, ulong size) =>
        !ShouldFail(Op.ReleasePrivate) && _inner.ReleasePrivate(address, size);

    public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection) =>
        !ShouldFail(Op.ChangeAccess) && _inner.ChangeAccess(address, size, protection);

    public bool FreeOwnedRange(ulong address, ulong size) =>
        !ShouldFail(Op.FreeOwnedRange) && _inner.FreeOwnedRange(address, size);

    private bool ShouldFail(Op op)
    {
        Log.Add(op);
        if (_failOp != op)
        {
            return false;
        }

        if (_failAfter-- > 0)
        {
            return false;
        }

        _failOp = null;
        return true;
    }
}
