// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextLibraryContextHandle;
    private static int _nextPushEventHandle;
    private static int _nextPushEventCallbackHandle;
    private static int _nextUserContextHandle = 1000;
    private static readonly object _contextGate = new();
    private static readonly HashSet<int> _libraryContexts = [];
    private static readonly HashSet<int> _userContexts = [];
    private static readonly HashSet<int> _pushEventFilters = [];

    public static void ResetRuntimeState()
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _nextLibraryContextHandle, 0);
        Interlocked.Exchange(ref _nextPushEventHandle, 0);
        Interlocked.Exchange(ref _nextPushEventCallbackHandle, 0);
        Interlocked.Exchange(ref _nextUserContextHandle, 1000);
        lock (_contextGate)
        {
            _libraryContexts.Clear();
            _userContexts.Clear();
            _pushEventFilters.Clear();
        }
    }

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "MsaFhR+lPE4",
        ExportName = "sceNpWebApi2PushEventCreateFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var filterHandle = Interlocked.Increment(ref _nextPushEventHandle);
        lock (_contextGate)
        {
            _pushEventFilters.Add(filterHandle);
        }

        TraceNpWebApi2("push-event-create-filter", libraryContextId, (ulong)filterHandle);
        return ctx.SetReturn(filterHandle);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var handle = CreatePushEventHandle();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", libraryContextId, 0);
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteHandle(CpuContext ctx)
    {
        TraceNpWebApi2(
            "push-event-delete-handle",
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((uint)ctx[CpuRegister.Rsi]));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        TraceNpWebApi2(
            "create-user-context",
            libraryContextId,
            unchecked((uint)userId));

        if (Volatile.Read(ref _initialized) == 0 ||
            !IsValidLibraryContextId(libraryContextId) ||
            userId == -1)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContextHandle);
        lock (_contextGate)
        {
            _userContexts.Add(userContextId);
        }

        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(
        Nid = "fY3QqeNkF8k",
        ExportName = "sceNpWebApi2PushEventRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var filterId = unchecked((int)ctx[CpuRegister.Rsi]);
        var callback = ctx[CpuRegister.Rdx];
        if (callback == 0 || !IsRegisteredPushEventPair(userContextId, filterId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var callbackId = Interlocked.Increment(ref _nextPushEventCallbackHandle);
        TraceNpWebApi2("push-event-register-callback", userContextId, (ulong)callbackId);
        return ctx.SetReturn(callbackId);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        RemoveLibraryContextId(libraryContextId);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    // Titles call this every frame to expire requests that ran past their timeout. No request
    // is ever in flight here, so there is nothing to expire.
    [SysAbiExport(
        Nid = "3Tt9zL3tkoc",
        ExportName = "sceNpWebApi2CheckTimeout",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CheckTimeout(CpuContext ctx) => ctx.SetReturn(0);

    private static int CreateLibraryContextId()
    {
        var handle = Interlocked.Increment(ref _nextLibraryContextHandle);
        lock (_contextGate)
        {
            _libraryContexts.Add(handle);
        }

        return handle;
    }

    private static int CreatePushEventHandle()
    {
        return Interlocked.Increment(ref _nextPushEventHandle);
    }

    [SysAbiExport(
        Nid = "QafxeZM3WK4",
        ExportName = "sceNpWebApi2PushEventDeletePushContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeletePushContext(CpuContext ctx) => ctx.SetReturn(0);

    private static bool IsValidLibraryContextId(int libraryContextId)
    {
        if (libraryContextId <= 0 || libraryContextId >= 0x8000)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId);
        }
    }

    private static void RemoveLibraryContextId(int libraryContextId)
    {
        lock (_contextGate)
        {
            _libraryContexts.Remove(libraryContextId);
            if (_libraryContexts.Count == 0)
            {
                Interlocked.Exchange(ref _initialized, 0);
                _userContexts.Clear();
                _pushEventFilters.Clear();
            }
        }
    }

    private static bool IsRegisteredPushEventPair(int userContextId, int filterId)
    {
        if (Volatile.Read(ref _initialized) == 0 || userContextId <= 0 || filterId <= 0)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _userContexts.Contains(userContextId) && _pushEventFilters.Contains(filterId);
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
