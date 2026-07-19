// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE;

public readonly record struct GuestThreadStartRequest(
    ulong ThreadHandle,
    ulong EntryPoint,
    ulong Argument,
    ulong AttributeAddress,
    string Name,
    int Priority,
    ulong AffinityMask);

public readonly record struct GuestThreadSnapshot(
    ulong ThreadHandle,
    string Name,
    string State,
    long ImportCount,
    string? LastImportNid,
    ulong LastReturnRip,
    string? BlockReason);

/// <summary>
/// Continuation state for a blocked guest thread, replacing the closure pair a blocking
/// wait used to allocate. TryWake runs under the scheduler's guest-thread gate and
/// returns true when the waiter has a final result and the thread should be re-readied;
/// false leaves it parked. Resume runs later on the woken thread outside that gate, and
/// its return value becomes the guest's RAX for the resumed call.
/// </summary>
public interface IGuestThreadScheduler
{
    bool SupportsGuestContextTransfer { get; }

    /// <summary>
    /// Associates a pthread identity created on the primary guest executor
    /// with its live CPU context. Primary execution does not pass through
    /// TryStartThread, but kernel exception delivery must still be able to
    /// target it.
    /// </summary>
    void RegisterGuestThreadContext(ulong threadHandle, CpuContext context);

    bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error);

    bool TryJoinThread(
        CpuContext callerContext,
        ulong threadHandle,
        out ulong returnValue,
        out string? error);

    /// <summary>
    /// Applies a new guest scheduling priority to a live thread, mapping it
    /// onto the host thread if one is running. Returns false when the thread
    /// handle is unknown.
    /// </summary>
    bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority);

    /// <summary>
    /// Records a new affinity mask for a guest thread and re-applies it to
    /// the host thread where the platform supports it.
    /// </summary>
    bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask);

    IReadOnlyList<GuestThreadSnapshot> SnapshotThreads();

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out string? error);

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong arg2,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out ulong returnValue,
        out string? error);

    bool TryCallGuestContinuation(
        CpuContext callerContext,
        GuestCpuContinuation continuation,
        string reason,
        out string? error);

    /// <summary>
    /// Asynchronously invokes an installed kernel exception handler as the
    /// target guest thread. This is used by IL2CPP's stop-the-world collector:
    /// the handler acknowledges suspension and may remain blocked until the
    /// collecting thread resumes it.
    /// </summary>
    bool TryRaiseGuestException(
        CpuContext callerContext,
        ulong threadHandle,
        ulong handler,
        int exceptionType,
        out string? error);
}

public readonly record struct GuestImportCallFrame(
    bool IsValid,
    ulong ReturnRip,
    ulong ResumeRsp,
    ulong ReturnSlotAddress);

public readonly record struct GuestCpuContinuation(
    ulong Rip,
    ulong Rsp,
    ulong ReturnSlotAddress,
    ulong Rflags,
    ulong FsBase,
    ulong GsBase,
    ulong Rax,
    ulong Rcx,
    ulong Rdx,
    ulong Rbx,
    ulong Rbp,
    ulong Rsi,
    ulong Rdi,
    ulong R8,
    ulong R9,
    ulong R10,
    ulong R11,
    ulong R12,
    ulong R13,
    ulong R14,
    ulong R15,
    ushort FpuControlWord,
    uint Mxcsr,
    bool RestoreFullFpuState);

public static class GuestThreadExecution
{
    [ThreadStatic]
    private static ulong _currentGuestThreadHandle;

    [ThreadStatic]
    private static ulong _currentFiberAddress;

    [ThreadStatic]
    private static bool _pendingEntryExit;

    [ThreadStatic]
    private static ulong _pendingEntryExitValue;

    [ThreadStatic]
    private static string? _pendingEntryExitReason;

    [ThreadStatic]
    private static bool _pendingContextTransfer;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingContextTransferTarget;

    [ThreadStatic]
    private static bool _hasCurrentImportCallFrame;

    [ThreadStatic]
    private static ulong _currentImportReturnRip;

    [ThreadStatic]
    private static ulong _currentImportResumeRsp;

    [ThreadStatic]
    private static ulong _currentImportReturnSlotAddress;

    public static IGuestThreadScheduler? Scheduler { get; set; }

    public static bool IsGuestThread => _currentGuestThreadHandle != 0;

    public static ulong CurrentGuestThreadHandle => _currentGuestThreadHandle;

    public static ulong CurrentFiberAddress => _currentFiberAddress;

    public static ulong EnterGuestThread(ulong threadHandle)
    {
        var previous = _currentGuestThreadHandle;
        _currentGuestThreadHandle = threadHandle;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
        return previous;
    }

    public static void RestoreGuestThread(ulong previousThreadHandle)
    {
        _currentGuestThreadHandle = previousThreadHandle;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
    }

    public static ulong EnterFiber(ulong fiberAddress)
    {
        var previous = _currentFiberAddress;
        _currentFiberAddress = fiberAddress;
        return previous;
    }

    public static void RestoreFiber(ulong previousFiberAddress)
    {
        _currentFiberAddress = previousFiberAddress;
    }

    public static long ComputeDeadlineTimestamp(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return Stopwatch.GetTimestamp();
        }

        var ticks = timeout.TotalSeconds >= long.MaxValue / (double)Stopwatch.Frequency
            ? long.MaxValue
            : (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        if (long.MaxValue - now <= ticks)
        {
            return long.MaxValue;
        }

        return now + Math.Max(1, ticks);
    }

    public static void RequestCurrentEntryExit(string reason, int status)
    {
        RequestCurrentEntryExit(reason, unchecked((ulong)(long)status));
    }

    public static void RequestCurrentEntryExit(string reason, ulong value)
    {
        _pendingEntryExit = true;
        _pendingEntryExitValue = value;
        _pendingEntryExitReason = string.IsNullOrWhiteSpace(reason) ? "guest_entry_exit" : reason;
    }

    public static bool TryConsumeCurrentEntryExit(out ulong value, out string reason)
    {
        value = _pendingEntryExitValue;
        reason = _pendingEntryExitReason ?? string.Empty;
        if (!_pendingEntryExit)
        {
            return false;
        }

        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        return true;
    }

    public static void RequestCurrentContextTransfer(GuestCpuContinuation target)
    {
        _pendingContextTransferTarget = target;
        _pendingContextTransfer = true;
    }

    public static bool TryConsumeCurrentContextTransfer(out GuestCpuContinuation target)
    {
        target = _pendingContextTransferTarget;
        if (!_pendingContextTransfer)
        {
            return false;
        }

        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        return true;
    }

    public static GuestImportCallFrame EnterImportCallFrame(
        ulong returnRip,
        ulong resumeRsp,
        ulong returnSlotAddress)
    {
        var previous = new GuestImportCallFrame(
            _hasCurrentImportCallFrame,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        _hasCurrentImportCallFrame = true;
        _currentImportReturnRip = returnRip;
        _currentImportResumeRsp = resumeRsp;
        _currentImportReturnSlotAddress = returnSlotAddress;
        return previous;
    }

    public static void RestoreImportCallFrame(GuestImportCallFrame previous)
    {
        _hasCurrentImportCallFrame = previous.IsValid;
        _currentImportReturnRip = previous.ReturnRip;
        _currentImportResumeRsp = previous.ResumeRsp;
        _currentImportReturnSlotAddress = previous.ReturnSlotAddress;
    }

    public static bool TryGetCurrentImportCallFrame(out GuestImportCallFrame frame)
    {
        if (!_hasCurrentImportCallFrame)
        {
            frame = default;
            return false;
        }

        frame = new GuestImportCallFrame(
            true,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        return true;
    }
}
