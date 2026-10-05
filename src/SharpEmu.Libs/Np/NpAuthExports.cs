// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpAuthExports
{
    private const int MaximumRequestCount = 16;
    private const int NpAuthErrorInvalidSize = unchecked((int)0x80550302);
    private const int NpAuthErrorAborted = unchecked((int)0x80550304);
    private const int NpAuthErrorRequestMaximum = unchecked((int)0x80550305);
    private const int NpAuthErrorRequestNotFound = unchecked((int)0x80550306);
    private const int NpAuthErrorServiceDown = unchecked((int)0x80550401);
    private const ulong AsyncParameterSize = 24;

    private static readonly ConcurrentDictionary<int, AuthRequest> Requests = new();
    private static readonly object RequestGate = new();
    private static int _nextRequestId;

    private sealed class AuthRequest
    {
        public bool Aborted { get; set; }

        public int Result { get; set; }

        public int ResolveRetry { get; set; }

        public uint ResolveTimeout { get; set; }

        public uint ConnectTimeout { get; set; }

        public uint SendTimeout { get; set; }

        public uint ReceiveTimeout { get; set; }
    }

    public static void ResetRuntimeState()
    {
        Requests.Clear();
        _nextRequestId = 0;
    }

    [SysAbiExport(
        ExportName = "sceNpAuthCreateRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateRequest(CpuContext ctx) => CreateRequest(ctx);

    [SysAbiExport(
        ExportName = "sceNpAuthCreateAsyncRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateAsyncRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0 ||
            !ctx.TryReadUInt64(parameterAddress, out var size) ||
            size != AsyncParameterSize)
        {
            return ctx.SetReturn(NpAuthErrorInvalidSize);
        }

        return CreateRequest(ctx);
    }

    [SysAbiExport(
        ExportName = "sceNpAuthDeleteRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthDeleteRequest(CpuContext ctx) =>
        Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NpAuthErrorRequestNotFound);

    [SysAbiExport(
        ExportName = "sceNpAuthAbortRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthAbortRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(NpAuthErrorRequestNotFound);
        }

        lock (request)
        {
            request.Aborted = true;
            request.Result = NpAuthErrorAborted;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        ExportName = "sceNpAuthSetTimeout",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthSetTimeout(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(NpAuthErrorRequestNotFound);
        }

        lock (request)
        {
            request.ResolveRetry = unchecked((int)ctx[CpuRegister.Rsi]);
            request.ResolveTimeout = unchecked((uint)ctx[CpuRegister.Rdx]);
            request.ConnectTimeout = unchecked((uint)ctx[CpuRegister.Rcx]);
            request.SendTimeout = unchecked((uint)ctx[CpuRegister.R8]);
            request.ReceiveTimeout = unchecked((uint)ctx[CpuRegister.R9]);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        ExportName = "sceNpAuthWaitAsync",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthWaitAsync(CpuContext ctx) => CompleteAsyncRequest(ctx);

    [SysAbiExport(
        ExportName = "sceNpAuthPollAsync",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthPollAsync(CpuContext ctx) => CompleteAsyncRequest(ctx);

    [SysAbiExport(
        ExportName = "sceNpAuthGetAuthorizationCodeV3",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeV3(CpuContext ctx) => RejectOnlineOperation(ctx);

    [SysAbiExport(
        ExportName = "sceNpAuthGetIdTokenV3",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdTokenV3(CpuContext ctx) => RejectOnlineOperation(ctx);

    [SysAbiExport(
        ExportName = "sceNpAuthGetAuthorizedAppCode",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizedAppCode(CpuContext ctx) => RejectOnlineOperation(ctx);

    private static int CreateRequest(CpuContext ctx)
    {
        lock (RequestGate)
        {
            if (Requests.Count >= MaximumRequestCount)
            {
                return ctx.SetReturn(NpAuthErrorRequestMaximum);
            }

            while (true)
            {
                var id = Interlocked.Increment(ref _nextRequestId);
                if (Requests.TryAdd(id, new AuthRequest()))
                {
                    return ctx.SetReturn(id);
                }
            }
        }
    }

    private static int RejectOnlineOperation(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(NpAuthErrorRequestNotFound);
        }

        lock (request)
        {
            request.Result = request.Aborted ? NpAuthErrorAborted : NpAuthErrorServiceDown;
            return ctx.SetReturn(request.Result);
        }
    }

    private static int CompleteAsyncRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(NpAuthErrorRequestNotFound);
        }

        var resultAddress = ctx[CpuRegister.Rsi];
        int result;
        lock (request)
        {
            result = request.Aborted ? NpAuthErrorAborted : request.Result;
        }

        if (resultAddress == 0 || !ctx.TryWriteUInt32(resultAddress, unchecked((uint)result)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // No network work can remain pending. Poll and wait both report a completed request.
        return ctx.SetReturn(0);
    }
}
