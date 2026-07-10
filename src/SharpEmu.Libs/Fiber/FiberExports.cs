// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Fiber;

public static class FiberExports
{
    private const int MaxNameLength = 31;
    private const int FiberInfoSize = 128;
    private const int FiberContextMinimumSize = 512;
    private const uint FiberSignature0 = 0xDEF1649C;
    private const uint FiberSignature1 = 0xB37592A0;
    private const uint FiberOptSignature = 0xBB40E64D;
    private const ulong FiberStackSignature = 0x7149F2CA7149F2CAUL;
    private const ulong FiberStackSizeCheck = 0xDEADBEEFDEADBEEFUL;
    private const uint FiberStateRun = 1;
    private const uint FiberStateIdle = 2;
    private const uint FiberStateTerminated = 3;
    private const uint FiberFlagContextSizeCheck = 0x10;

    private const int FiberErrorNull = unchecked((int)0x80590001);
    private const int FiberErrorAlignment = unchecked((int)0x80590002);
    private const int FiberErrorRange = unchecked((int)0x80590003);
    private const int FiberErrorInvalid = unchecked((int)0x80590004);
    private const int FiberErrorPermission = unchecked((int)0x80590005);
    private const int FiberErrorState = unchecked((int)0x80590006);

    private const int FiberMagicStartOffset = 0;
    private const int FiberStateOffset = 4;
    private const int FiberEntryOffset = 8;
    private const int FiberArgOnInitializeOffset = 16;
    private const int FiberContextAddressOffset = 24;
    private const int FiberContextSizeOffset = 32;
    private const int FiberNameOffset = 40;
    private const int FiberContextPointerOffset = 72;
    private const int FiberFlagsOffset = 80;
    private const int FiberContextStartOffset = 88;
    private const int FiberContextEndOffset = 96;
    private const int FiberMagicEndOffset = 104;

    private static int _contextSizeCheck;

    [ThreadStatic]
    private static ulong _currentFiberAddress;

    private static readonly object _fiberGate = new();
    private static readonly ConcurrentDictionary<ulong, FiberContinuation> _continuations = new();
    private static readonly ConcurrentDictionary<ulong, FiberReturnTarget> _returnTargets = new();
    private static readonly ConcurrentDictionary<ulong, FiberStackRange> _stackRanges = new();

    [SysAbiExport(
        Nid = "hVYD7Ou2pCQ",
        ExportName = "_sceFiberInitializeImpl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberInitialize(CpuContext ctx)
    {
        var optParam = ReadStackArg64(ctx, 0);
        return FiberInitializeCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            optParam,
            flags: 0);
    }

    [SysAbiExport(
        Nid = "7+OJIpko9RY",
        ExportName = "_sceFiberInitializeWithInternalOptionImpl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberInitializeWithInternalOption(CpuContext ctx)
    {
        var optParam = ReadStackArg64(ctx, 0);
        var flags = unchecked((uint)ReadStackArg64(ctx, 1));
        return FiberInitializeCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            optParam,
            flags);
    }

    [SysAbiExport(
        Nid = "asjUJJ+aa8s",
        ExportName = "sceFiberOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberOptParamInitialize(CpuContext ctx)
    {
        var optParam = ctx[CpuRegister.Rdi];
        if (optParam == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if ((optParam & 7) != 0)
        {
            return SetReturn(ctx, FiberErrorAlignment);
        }

        return ctx.TryWriteUInt32(optParam, FiberOptSignature)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "JeNX5F-NzQU",
        ExportName = "sceFiberFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberFinalize(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!ctx.TryReadUInt32(fiber + FiberStateOffset, out var state))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (state != FiberStateIdle)
        {
            return SetReturn(ctx, FiberErrorState);
        }

        _continuations.TryRemove(fiber, out _);
        _returnTargets.TryRemove(fiber, out _);
        _stackRanges.TryRemove(fiber, out _);
        _ = ctx.TryWriteUInt32(fiber + FiberStateOffset, FiberStateTerminated);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "a0LLrZWac0M",
        ExportName = "sceFiberRun",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberRun(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            attachContextAddress: 0,
            attachContextSize: 0,
            reason: "sceFiberRun",
            isSwitch: false);
    }

    [SysAbiExport(
        Nid = "avfGJ94g36Q",
        ExportName = "_sceFiberAttachContextAndRun",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberAttachContextAndRun(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            attachContextAddress: ctx[CpuRegister.Rsi],
            attachContextSize: ctx[CpuRegister.Rdx],
            reason: "_sceFiberAttachContextAndRun",
            isSwitch: false);
    }

    [SysAbiExport(
        Nid = "PFT2S-tJ7Uk",
        ExportName = "sceFiberSwitch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberSwitch(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            attachContextAddress: 0,
            attachContextSize: 0,
            reason: "sceFiberSwitch",
            isSwitch: true);
    }

    [SysAbiExport(
        Nid = "ZqhZFuzKT6U",
        ExportName = "_sceFiberAttachContextAndSwitch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberAttachContextAndSwitch(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            attachContextAddress: ctx[CpuRegister.Rsi],
            attachContextSize: ctx[CpuRegister.Rdx],
            reason: "_sceFiberAttachContextAndSwitch",
            isSwitch: true);
    }

    [SysAbiExport(
        Nid = "B0ZX2hx9DMw",
        ExportName = "sceFiberReturnToThread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberReturnToThread(CpuContext ctx)
    {
        var fiberAddress = ResolveCurrentFiberAddress(ctx);
        if (fiberAddress == 0)
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        if (GuestThreadExecution.Scheduler is not { SupportsGuestContextTransfer: true } ||
            !GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        var returnArgument = ctx[CpuRegister.Rdi];
        var argOnRunAddress = ctx[CpuRegister.Rsi];
        if (argOnRunAddress != 0 && !ctx.TryWriteUInt64(argOnRunAddress, 0))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        GuestCpuContinuation transferTarget;
        ulong previousFiber;
        lock (_fiberGate)
        {
            _continuations[fiberAddress] = new FiberContinuation(
                CaptureContinuation(ctx, frame.ReturnRip, frame.ResumeRsp, frame.ReturnSlotAddress),
                argOnRunAddress);

            if (!_returnTargets.TryRemove(fiberAddress, out var returnTarget))
            {
                _continuations.TryRemove(fiberAddress, out _);
                return SetReturn(ctx, FiberErrorPermission);
            }

            previousFiber = returnTarget.PreviousFiber;
            if (previousFiber != 0)
            {
                if (!_continuations.TryRemove(previousFiber, out var previousContinuation) ||
                    !TryWriteResumeArgument(ctx, previousContinuation, returnArgument) ||
                    !ctx.TryWriteUInt32(previousFiber + FiberStateOffset, FiberStateRun))
                {
                    _continuations.TryRemove(fiberAddress, out _);
                    return SetReturn(ctx, FiberErrorState);
                }

                transferTarget = previousContinuation.Context with { Rax = 0 };
            }
            else
            {
                if (!returnTarget.ThreadContinuation.HasValue ||
                    !TryWriteResumeArgument(ctx, returnTarget.ThreadContinuation.Value, returnArgument))
                {
                    _continuations.TryRemove(fiberAddress, out _);
                    return SetReturn(ctx, FiberErrorState);
                }

                transferTarget = returnTarget.ThreadContinuation.Value.Context with { Rax = 0 };
            }

            if (!ctx.TryWriteUInt32(fiberAddress + FiberStateOffset, FiberStateIdle))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }
        }

        _currentFiberAddress = previousFiber;
        _ = GuestThreadExecution.EnterFiber(previousFiber);
        GuestThreadExecution.RequestCurrentContextTransfer(transferTarget);
        TraceFiber(
            $"return fiber=0x{fiberAddress:X16} to=0x{previousFiber:X16} " +
            $"resume=0x{transferTarget.Rip:X16} rsp=0x{transferTarget.Rsp:X16} arg=0x{returnArgument:X16}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "p+zLIOg27zU",
        ExportName = "sceFiberGetSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetSelf(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        var fiberAddress = ResolveCurrentFiberAddress(ctx);
        if (fiberAddress == 0)
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        return ctx.TryWriteUInt64(outAddress, fiberAddress)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "uq2Y5BFz0PE",
        ExportName = "sceFiberGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetInfo(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        var info = ctx[CpuRegister.Rsi];
        if (info == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!ctx.TryReadUInt64(info, out var size) || size != FiberInfoSize)
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!TryReadFiberFields(ctx, fiber, out var fields))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!ctx.TryWriteUInt64(info + 8, fields.Entry) ||
            !ctx.TryWriteUInt64(info + 16, fields.ArgOnInitialize) ||
            !ctx.TryWriteUInt64(info + 24, fields.ContextAddress) ||
            !ctx.TryWriteUInt64(info + 32, fields.ContextSize) ||
            !TryWriteName(ctx, info + 40, fields.Name) ||
            !ctx.TryWriteUInt64(info + 72, ulong.MaxValue))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JzyT91ucGDc",
        ExportName = "sceFiberRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberRename(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (nameAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (!ctx.TryReadNullTerminatedUtf8(nameAddress, MaxNameLength + 1, out var name))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return TryWriteName(ctx, fiber + FiberNameOffset, name)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "Lcqty+QNWFc",
        ExportName = "sceFiberStartContextSizeCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberStartContextSizeCheck(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return Interlocked.Exchange(ref _contextSizeCheck, 1) == 0
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorState);
    }

    [SysAbiExport(
        Nid = "Kj4nXMpnM8Y",
        ExportName = "sceFiberStopContextSizeCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberStopContextSizeCheck(CpuContext ctx)
    {
        return Interlocked.Exchange(ref _contextSizeCheck, 0) == 1
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorState);
    }

    [SysAbiExport(
        Nid = "0dy4JtMUcMQ",
        ExportName = "_sceFiberGetThreadFramePointerAddress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetThreadFramePointerAddress(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (ResolveCurrentFiberAddress(ctx) == 0)
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        return ctx.TryWriteUInt64(outAddress, ctx[CpuRegister.Rbp])
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    private static int FiberInitializeCore(
        CpuContext ctx,
        ulong fiber,
        ulong nameAddress,
        ulong entry,
        ulong argOnInitialize,
        ulong contextAddress,
        ulong contextSize,
        ulong optParam,
        uint flags)
    {
        if (fiber == 0 || nameAddress == 0 || entry == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if ((fiber & 7) != 0 ||
            (contextAddress & 15) != 0 ||
            (optParam & 7) != 0)
        {
            return SetReturn(ctx, FiberErrorAlignment);
        }

        if (contextSize != 0 && contextSize < FiberContextMinimumSize)
        {
            return SetReturn(ctx, FiberErrorRange);
        }

        if ((contextSize & 15) != 0 ||
            (contextAddress == 0 && contextSize != 0) ||
            (contextAddress != 0 && contextSize == 0))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (optParam != 0 &&
            (!ctx.TryReadUInt32(optParam, out var optMagic) || optMagic != FiberOptSignature))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!ctx.TryReadNullTerminatedUtf8(nameAddress, MaxNameLength + 1, out var name))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (Volatile.Read(ref _contextSizeCheck) != 0)
        {
            flags |= FiberFlagContextSizeCheck;
        }

        if (!ctx.TryWriteUInt32(fiber + FiberMagicStartOffset, FiberSignature0) ||
            !ctx.TryWriteUInt32(fiber + FiberStateOffset, FiberStateIdle) ||
            !ctx.TryWriteUInt64(fiber + FiberEntryOffset, entry) ||
            !ctx.TryWriteUInt64(fiber + FiberArgOnInitializeOffset, argOnInitialize) ||
            !ctx.TryWriteUInt64(fiber + FiberContextAddressOffset, contextAddress) ||
            !ctx.TryWriteUInt64(fiber + FiberContextSizeOffset, contextSize) ||
            !TryWriteName(ctx, fiber + FiberNameOffset, name) ||
            !ctx.TryWriteUInt64(fiber + FiberContextPointerOffset, 0) ||
            !ctx.TryWriteUInt32(fiber + FiberFlagsOffset, flags) ||
            !ctx.TryWriteUInt64(fiber + FiberContextStartOffset, contextAddress) ||
            !ctx.TryWriteUInt64(fiber + FiberContextEndOffset, contextAddress == 0 ? 0 : contextAddress + contextSize) ||
            !ctx.TryWriteUInt32(fiber + FiberMagicEndOffset, FiberSignature1))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (contextAddress != 0)
        {
            if (!ctx.TryWriteUInt64(contextAddress, FiberStackSignature))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if ((flags & FiberFlagContextSizeCheck) != 0)
            {
                FillContextSizeCheck(ctx, contextAddress + sizeof(ulong), contextSize - sizeof(ulong));
            }
        }

        if (contextAddress != 0 && contextSize != 0)
        {
            _stackRanges[fiber] = new FiberStackRange(contextAddress, contextSize);
        }

        TraceFiber($"init fiber=0x{fiber:X16} entry=0x{entry:X16} ctx=0x{contextAddress:X16} size=0x{contextSize:X} name='{name}'");
        return SetReturn(ctx, 0);
    }

    private static int FiberRunCore(
        CpuContext ctx,
        ulong fiber,
        ulong argOnRun,
        ulong outArgumentAddress,
        ulong attachContextAddress,
        ulong attachContextSize,
        string reason,
        bool isSwitch)
    {
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!TryReadFiberFields(ctx, fiber, out var fields))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (attachContextAddress != 0 || attachContextSize != 0)
        {
            var attachResult = AttachContext(ctx, fiber, attachContextAddress, attachContextSize, ref fields);
            if (attachResult != 0)
            {
                return SetReturn(ctx, attachResult);
            }
        }

        var previousFiber = ResolveCurrentFiberAddress(ctx);
        if ((isSwitch && previousFiber == 0) ||
            (!isSwitch && previousFiber != 0))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }
        if (previousFiber == fiber)
        {
            return SetReturn(ctx, FiberErrorState);
        }
        if (GuestThreadExecution.Scheduler is not { SupportsGuestContextTransfer: true } ||
            !GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        GuestCpuContinuation transferTarget;
        var resumed = false;
        lock (_fiberGate)
        {
            if (!TryReadFiberFields(ctx, fiber, out fields))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }
            if (fields.State != FiberStateIdle)
            {
                TraceFiber($"run-state-error reason={reason} fiber=0x{fiber:X16} state=0x{fields.State:X8}");
                return SetReturn(ctx, FiberErrorState);
            }

            FiberContinuation targetContinuation;
            if (_continuations.TryGetValue(fiber, out var savedContinuation))
            {
                targetContinuation = savedContinuation;
                resumed = true;
            }
            else if (!TryCreateInitialContinuation(ctx, fields, argOnRun, out targetContinuation))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if (resumed && !TryWriteResumeArgument(ctx, targetContinuation, argOnRun))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            var callerContinuation = new FiberContinuation(
                CaptureContinuation(ctx, frame.ReturnRip, frame.ResumeRsp, frame.ReturnSlotAddress),
                outArgumentAddress);

            if (previousFiber != 0)
            {
                if (!ctx.TryReadUInt32(previousFiber + FiberStateOffset, out var previousState) ||
                    previousState != FiberStateRun ||
                    !ctx.TryWriteUInt32(previousFiber + FiberStateOffset, FiberStateIdle))
                {
                    return SetReturn(ctx, FiberErrorState);
                }

                _continuations[previousFiber] = callerContinuation;
                _returnTargets[fiber] = new FiberReturnTarget(previousFiber, null);
            }
            else
            {
                _returnTargets[fiber] = new FiberReturnTarget(0, callerContinuation);
            }

            if (!ctx.TryWriteUInt32(fiber + FiberStateOffset, FiberStateRun))
            {
                if (previousFiber != 0)
                {
                    _continuations.TryRemove(previousFiber, out _);
                    _ = ctx.TryWriteUInt32(previousFiber + FiberStateOffset, FiberStateRun);
                }
                _returnTargets.TryRemove(fiber, out _);
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if (resumed)
            {
                _continuations.TryRemove(fiber, out _);
            }

            transferTarget = targetContinuation.Context with { Rax = 0 };
        }

        _currentFiberAddress = fiber;
        _ = GuestThreadExecution.EnterFiber(fiber);
        GuestThreadExecution.RequestCurrentContextTransfer(transferTarget);
        TraceFiber(
            $"transfer reason={reason} from=0x{previousFiber:X16} to=0x{fiber:X16} resume={resumed} " +
            $"rip=0x{transferTarget.Rip:X16} rsp=0x{transferTarget.Rsp:X16} arg=0x{argOnRun:X16}");
        return SetReturn(ctx, 0);
    }

    private static bool TryCreateInitialContinuation(
        CpuContext ctx,
        FiberFields fields,
        ulong argOnRun,
        out FiberContinuation continuation)
    {
        continuation = default;
        if (fields.ContextAddress == 0 || fields.ContextSize < FiberContextMinimumSize)
        {
            return false;
        }

        var stackEnd = fields.ContextAddress + fields.ContextSize;
        var entryRsp = (stackEnd & ~15UL) - sizeof(ulong);
        if (!ctx.TryWriteUInt64(entryRsp, 0))
        {
            return false;
        }

        continuation = new FiberContinuation(
            new GuestCpuContinuation(
                fields.Entry,
                entryRsp,
                entryRsp,
                ctx.Rflags == 0 ? 0x202UL : ctx.Rflags,
                ctx.FsBase,
                ctx.GsBase,
                0,
                0,
                0,
                0,
                0,
                argOnRun,
                fields.ArgOnInitialize,
                0,
                0,
                0,
                0,
                0,
                0),
            0);
        return true;
    }

    private static bool TryWriteResumeArgument(
        CpuContext ctx,
        FiberContinuation continuation,
        ulong argument) =>
        continuation.ArgOnRunAddress == 0 ||
        ctx.TryWriteUInt64(continuation.ArgOnRunAddress, argument);

    private static GuestCpuContinuation CaptureContinuation(
        CpuContext ctx,
        ulong resumeRip,
        ulong resumeRsp,
        ulong returnSlotAddress) =>
        new(
            resumeRip,
            resumeRsp,
            returnSlotAddress,
            ctx.Rflags == 0 ? 0x202UL : ctx.Rflags,
            ctx.FsBase,
            ctx.GsBase,
            0,
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rbx],
            ctx[CpuRegister.Rbp],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            ctx[CpuRegister.R12],
            ctx[CpuRegister.R13],
            ctx[CpuRegister.R14],
            ctx[CpuRegister.R15]);

    private static ulong ResolveCurrentFiberAddress(CpuContext ctx)
    {
        if (_currentFiberAddress != 0)
        {
            return _currentFiberAddress;
        }

        if (GuestThreadExecution.CurrentFiberAddress != 0)
        {
            return GuestThreadExecution.CurrentFiberAddress;
        }

        return TryFindFiberByStack(ctx, out var fiberAddress) ? fiberAddress : 0;
    }

    internal static ulong GetCurrentFiberAddressForDiagnostics(CpuContext ctx) =>
        ResolveCurrentFiberAddress(ctx);

    private static int AttachContext(
        CpuContext ctx,
        ulong fiber,
        ulong contextAddress,
        ulong contextSize,
        ref FiberFields fields)
    {
        if ((contextAddress & 15) != 0)
        {
            return FiberErrorAlignment;
        }

        if (contextSize != 0 && contextSize < FiberContextMinimumSize)
        {
            return FiberErrorRange;
        }

        if ((contextSize & 15) != 0 ||
            contextAddress == 0 ||
            contextSize == 0 ||
            fields.ContextAddress != 0)
        {
            return FiberErrorInvalid;
        }

        if (!ctx.TryWriteUInt64(fiber + FiberContextAddressOffset, contextAddress) ||
            !ctx.TryWriteUInt64(fiber + FiberContextSizeOffset, contextSize) ||
            !ctx.TryWriteUInt64(fiber + FiberContextStartOffset, contextAddress) ||
            !ctx.TryWriteUInt64(fiber + FiberContextEndOffset, contextAddress + contextSize) ||
            !ctx.TryWriteUInt64(contextAddress, FiberStackSignature))
        {
            return FiberErrorInvalid;
        }

        fields = fields with
        {
            ContextAddress = contextAddress,
            ContextSize = contextSize,
        };
        _stackRanges[fiber] = new FiberStackRange(contextAddress, contextSize);
        return 0;
    }

    private static bool TryFindFiberByStack(CpuContext ctx, out ulong fiber)
    {
        if (TryFindFiberByStackAddress(ctx[CpuRegister.Rsp], out fiber))
        {
            return true;
        }

        return TryFindFiberByStackAddress(ctx[CpuRegister.Rbp], out fiber);
    }

    private static bool TryFindFiberByStackAddress(ulong address, out ulong fiber)
    {
        if (address != 0)
        {
            foreach (var (candidate, range) in _stackRanges)
            {
                if (range.Contains(address))
                {
                    fiber = candidate;
                    return true;
                }
            }
        }

        fiber = 0;
        return false;
    }

    private static bool TryValidateFiber(CpuContext ctx, ulong fiber, out int error)
    {
        if (fiber == 0)
        {
            error = FiberErrorNull;
            return false;
        }

        if ((fiber & 7) != 0)
        {
            error = FiberErrorAlignment;
            return false;
        }

        if (!ctx.TryReadUInt32(fiber + FiberMagicStartOffset, out var magicStart) ||
            !ctx.TryReadUInt32(fiber + FiberMagicEndOffset, out var magicEnd) ||
            magicStart != FiberSignature0 ||
            magicEnd != FiberSignature1)
        {
            error = FiberErrorInvalid;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryReadFiberFields(CpuContext ctx, ulong fiber, out FiberFields fields)
    {
        fields = default;
        if (!ctx.TryReadUInt32(fiber + FiberStateOffset, out var state) ||
            !ctx.TryReadUInt64(fiber + FiberEntryOffset, out var entry) ||
            !ctx.TryReadUInt64(fiber + FiberArgOnInitializeOffset, out var argOnInitialize) ||
            !ctx.TryReadUInt64(fiber + FiberContextAddressOffset, out var contextAddress) ||
            !ctx.TryReadUInt64(fiber + FiberContextSizeOffset, out var contextSize) ||
            !ctx.TryReadUInt32(fiber + FiberFlagsOffset, out var flags) ||
            !TryReadInlineName(ctx, fiber + FiberNameOffset, out var name))
        {
            return false;
        }

        fields = new FiberFields(
            state,
            entry,
            argOnInitialize,
            contextAddress,
            contextSize,
            flags,
            name);
        return true;
    }

    private static void FillContextSizeCheck(CpuContext ctx, ulong address, ulong size)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(value, FiberStackSizeCheck);
        var end = address + size;
        for (var current = address; current + sizeof(ulong) <= end; current += sizeof(ulong))
        {
            _ = ctx.Memory.TryWrite(current, value);
        }
    }

    private static ulong ReadStackArg64(CpuContext ctx, int index)
    {
        if (ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong)), out var value))
        {
            return value;
        }

        return 0;
    }

    private static bool TryWriteName(CpuContext ctx, ulong address, string name)
    {
        Span<byte> buffer = stackalloc byte[MaxNameLength + 1];
        var bytes = Encoding.UTF8.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, MaxNameLength)).CopyTo(buffer);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadInlineName(CpuContext ctx, ulong address, out string value)
    {
        Span<byte> buffer = stackalloc byte[MaxNameLength + 1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = string.Empty;
            return false;
        }

        var length = buffer.IndexOf((byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        value = Encoding.UTF8.GetString(buffer[..length]);
        return true;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceFiber(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FIBER"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] fiber.{message}");
        }
    }

    private readonly record struct FiberFields(
        uint State,
        ulong Entry,
        ulong ArgOnInitialize,
        ulong ContextAddress,
        ulong ContextSize,
        uint Flags,
        string Name);

    private readonly record struct FiberContinuation(
        GuestCpuContinuation Context,
        ulong ArgOnRunAddress);

    private readonly record struct FiberReturnTarget(
        ulong PreviousFiber,
        FiberContinuation? ThreadContinuation);

    private readonly record struct FiberStackRange(ulong Start, ulong Size)
    {
        public bool Contains(ulong address) =>
            Size != 0 && address >= Start && address < Start + Size;
    }
}
