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

public interface IGuestThreadScheduler
{
    bool SupportsGuestContextTransfer { get; }

    bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error);

    bool TryJoinThread(
        CpuContext callerContext,
        ulong threadHandle,
        out ulong returnValue,
        out string? error);

    void Pump(CpuContext callerContext, string reason);

    int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue);

    IReadOnlyList<GuestThreadSnapshot> SnapshotThreads();

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong arg2,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out string? error);

    bool TryCallGuestContinuation(
        CpuContext callerContext,
        GuestCpuContinuation continuation,
        string reason,
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
    ulong R12,
    ulong R13,
    ulong R14,
    ulong R15);

public static class GuestThreadExecution
{
    [ThreadStatic]
    private static ulong _currentGuestThreadHandle;

    [ThreadStatic]
    private static ulong _currentFiberAddress;

    [ThreadStatic]
    private static string? _pendingBlockReason;

    [ThreadStatic]
    private static bool _pendingBlockContinuationValid;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingBlockContinuation;

    [ThreadStatic]
    private static string? _pendingBlockWakeKey;

    [ThreadStatic]
    private static Func<int>? _pendingBlockResumeHandler;

    [ThreadStatic]
    private static Func<bool>? _pendingBlockWakeHandler;

    [ThreadStatic]
    private static long _pendingBlockDeadlineTimestamp;

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

    // Process-level (not thread-local) exit state. exit()/_exit() terminate the whole
    // guest process, so the requested status must survive independently of which guest
    // thread issued the call and of the numeric value the entry stub happens to return.
    private static readonly object _processExitGate = new();
    private static bool _processExitRequested;
    private static int _processExitStatus;
    private static string? _processExitReason;

    private static IGuestThreadScheduler? _scheduler;
    private static int _threadWatchdogStarted;

    public static IGuestThreadScheduler? Scheduler
    {
        get => _scheduler;
        set
        {
            _scheduler = value;
            // Opt-in stall diagnostics: periodically dump every guest thread's
            // state/block-reason so an idle deadlock (e.g. HN2's UE4 render-thread
            // init hang) shows what each thread is waiting on. See
            // [[dreamcore-ue-rendering]].
            if (value is not null &&
                string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_THREADS"),
                    "1",
                    StringComparison.Ordinal) &&
                System.Threading.Interlocked.Exchange(ref _threadWatchdogStarted, 1) == 0)
            {
                StartThreadStateWatchdog();
            }
        }
    }

    private static void StartThreadStateWatchdog()
    {
        var thread = new System.Threading.Thread(() =>
        {
            while (true)
            {
                System.Threading.Thread.Sleep(5000);
                var scheduler = _scheduler;
                if (scheduler is null)
                {
                    continue;
                }

                try
                {
                    Console.Error.WriteLine("[LOADER][WATCHDOG] === guest thread states ===");
                    foreach (var s in scheduler.SnapshotThreads())
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WATCHDOG] name='{s.Name}' state={s.State} " +
                            $"block={s.BlockReason ?? "none"} imports={s.ImportCount} " +
                            $"nid={s.LastImportNid ?? "none"} ret=0x{s.LastReturnRip:X16}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LOADER][WATCHDOG] dump failed: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpEmuThreadWatchdog",
        };
        thread.Start();
    }

    public static bool IsGuestThread => _currentGuestThreadHandle != 0;

    public static ulong CurrentGuestThreadHandle => _currentGuestThreadHandle;

    public static ulong CurrentFiberAddress => _currentFiberAddress;

    public static ulong EnterGuestThread(ulong threadHandle)
    {
        var previous = _currentGuestThreadHandle;
        _currentGuestThreadHandle = threadHandle;
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockResumeHandler = null;
        _pendingBlockWakeHandler = null;
        _pendingBlockDeadlineTimestamp = 0;
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
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockResumeHandler = null;
        _pendingBlockWakeHandler = null;
        _pendingBlockDeadlineTimestamp = 0;
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

    public static bool RequestCurrentThreadBlock(string reason) => RequestCurrentThreadBlock(null, reason);

    public static bool RequestCurrentThreadBlock(
        CpuContext? context,
        string reason,
        string? wakeKey = null,
        Func<int>? resumeHandler = null,
        Func<bool>? wakeHandler = null,
        long blockDeadlineTimestamp = 0)
    {
        if (!IsGuestThread)
        {
            return false;
        }

        _pendingBlockReason = string.IsNullOrWhiteSpace(reason) ? "guest_thread_blocked" : reason;
        _pendingBlockWakeKey = string.IsNullOrWhiteSpace(wakeKey) ? _pendingBlockReason : wakeKey;
        _pendingBlockResumeHandler = resumeHandler;
        _pendingBlockWakeHandler = wakeHandler;
        _pendingBlockDeadlineTimestamp = blockDeadlineTimestamp;
        if (context is not null && TryCaptureCurrentBlockContinuation(context, out var continuation))
        {
            _pendingBlockContinuation = continuation;
            _pendingBlockContinuationValid = true;
        }
        else
        {
            _pendingBlockContinuation = default;
            _pendingBlockContinuationValid = false;
        }

        return true;
    }

    public static bool TryConsumeCurrentThreadBlock(out string reason)
    {
        return TryConsumeCurrentThreadBlock(out reason, out _, out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out _,
            out _,
            out _,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out Func<int>? resumeHandler,
        out Func<bool>? wakeHandler)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out wakeKey,
            out resumeHandler,
            out wakeHandler,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out Func<int>? resumeHandler,
        out Func<bool>? wakeHandler,
        out long blockDeadlineTimestamp)
    {
        reason = _pendingBlockReason ?? string.Empty;
        if (string.IsNullOrEmpty(reason))
        {
            continuation = default;
            hasContinuation = false;
            wakeKey = string.Empty;
            resumeHandler = null;
            wakeHandler = null;
            blockDeadlineTimestamp = 0;
            return false;
        }

        continuation = _pendingBlockContinuation;
        hasContinuation = _pendingBlockContinuationValid;
        wakeKey = _pendingBlockWakeKey ?? reason;
        resumeHandler = _pendingBlockResumeHandler;
        wakeHandler = _pendingBlockWakeHandler;
        blockDeadlineTimestamp = _pendingBlockDeadlineTimestamp;
        _pendingBlockReason = null;
        _pendingBlockContinuation = default;
        _pendingBlockContinuationValid = false;
        _pendingBlockWakeKey = null;
        _pendingBlockResumeHandler = null;
        _pendingBlockWakeHandler = null;
        _pendingBlockDeadlineTimestamp = 0;
        return true;
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

    private static bool TryCaptureCurrentBlockContinuation(CpuContext context, out GuestCpuContinuation continuation)
    {
        if (!TryGetCurrentImportCallFrame(out var frame) ||
            frame.ReturnRip < 65536 ||
            frame.ResumeRsp == 0 ||
            frame.ReturnSlotAddress == 0)
        {
            continuation = default;
            return false;
        }

        continuation = new GuestCpuContinuation(
            frame.ReturnRip,
            frame.ResumeRsp,
            frame.ReturnSlotAddress,
            context.Rflags,
            context.FsBase,
            context.GsBase,
            0,
            context[CpuRegister.Rcx],
            context[CpuRegister.Rdx],
            context[CpuRegister.Rbx],
            context[CpuRegister.Rbp],
            context[CpuRegister.Rsi],
            context[CpuRegister.Rdi],
            context[CpuRegister.R8],
            context[CpuRegister.R9],
            context[CpuRegister.R12],
            context[CpuRegister.R13],
            context[CpuRegister.R14],
            context[CpuRegister.R15]);
        return true;
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

    // Records a whole-process exit (exit/_exit/main-return). Unlike the per-thread entry
    // exit, this is consulted after the guest unwinds to decide the final session result,
    // so a clean exit(0) is not misreported as a trap based on a stale LastError or the
    // arbitrary value the entry stub returns.
    public static void RecordProcessExit(string reason, int status)
    {
        lock (_processExitGate)
        {
            _processExitRequested = true;
            _processExitStatus = status;
            _processExitReason = string.IsNullOrWhiteSpace(reason) ? "exit" : reason;
        }
    }

    public static bool TryGetProcessExit(out int status, out string reason)
    {
        lock (_processExitGate)
        {
            status = _processExitStatus;
            reason = _processExitReason ?? string.Empty;
            return _processExitRequested;
        }
    }

    public static void ResetProcessExit()
    {
        lock (_processExitGate)
        {
            _processExitRequested = false;
            _processExitStatus = 0;
            _processExitReason = null;
        }
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
