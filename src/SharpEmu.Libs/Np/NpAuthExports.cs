// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpAuthExports
{
    private const int RequestLimit = 16;
    private const int RequestIdOffset = 0x10000000;
    private const int CreateAsyncParameterSize = 24;
    private const int AuthorizationCodeParameterSize = 32;
    private const int IdTokenParameterSize = 40;
    private const int AuthorizationCodeSize = 136;
    private const int IdTokenSize = 4104;

    private const int NpErrorSignedOut = unchecked((int)0x80550006);
    private const int NpAuthErrorInvalidArgument = unchecked((int)0x80550301);
    private const int NpAuthErrorInvalidSize = unchecked((int)0x80550302);
    private const int NpAuthErrorAborted = unchecked((int)0x80550304);
    private const int NpAuthErrorRequestMax = unchecked((int)0x80550305);
    private const int NpAuthErrorRequestNotFound = unchecked((int)0x80550306);
    private const int NpAuthErrorInvalidId = unchecked((int)0x80550307);

    private static readonly object RequestGate = new();
    private static readonly AuthRequest[] Requests = new AuthRequest[RequestLimit];
    private static int _activeRequestCount;

    private enum RequestState : byte
    {
        None,
        Ready,
        Aborted,
        Complete,
    }

    private struct AuthRequest
    {
        public RequestState State;
        public bool Async;
        public int Result;
        public int ResolveRetry;
        public uint ResolveTimeout;
        public uint ConnectTimeout;
        public uint SendTimeout;
        public uint ReceiveTimeout;
    }

    [SysAbiExport(
        Nid = "6bwFkosYRQg",
        ExportName = "sceNpAuthCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateRequest(CpuContext ctx)
    {
        return ctx.SetReturn(CreateRequest(async: false));
    }

    [SysAbiExport(
        Nid = "N+mr7GjTvr8",
        ExportName = "sceNpAuthCreateAsyncRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateAsyncRequest(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        Span<byte> parameter = stackalloc byte[CreateAsyncParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(parameter);
        if (size != CreateAsyncParameterSize)
        {
            return ctx.SetReturn(NpAuthErrorInvalidSize);
        }

        var requestId = CreateRequest(async: true);
        TraceNpAuth(
            $"create_async size={size} affinity=0x{BinaryPrimitives.ReadUInt64LittleEndian(parameter[8..]):X16} " +
            $"priority={BinaryPrimitives.ReadInt32LittleEndian(parameter[16..])} result=0x{unchecked((uint)requestId):X8}");
        return ctx.SetReturn(requestId);
    }

    [SysAbiExport(
        Nid = "H8wG9Bk-nPc",
        ExportName = "sceNpAuthDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthDeleteRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (RequestGate)
        {
            if (!TryGetRequestIndexLocked(requestId, out var index))
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            Requests[index] = default;
            _activeRequestCount--;
        }

        TraceNpAuth($"delete request=0x{unchecked((uint)requestId):X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cE7wIsqXdZ8",
        ExportName = "sceNpAuthAbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthAbortRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (RequestGate)
        {
            if (!TryGetRequestIndexLocked(requestId, out var index))
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            ref var request = ref Requests[index];
            if (request.State != RequestState.Complete)
            {
                request.State = RequestState.Aborted;
                request.Result = NpAuthErrorAborted;
            }
        }

        TraceNpAuth($"abort request=0x{unchecked((uint)requestId):X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        ExportName = "sceNpAuthSetTimeout",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthSetTimeout(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (RequestGate)
        {
            if (!TryGetRequestIndexLocked(requestId, out var index))
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            ref var request = ref Requests[index];
            request.ResolveRetry = unchecked((int)ctx[CpuRegister.Rsi]);
            request.ResolveTimeout = unchecked((uint)ctx[CpuRegister.Rdx]);
            request.ConnectTimeout = unchecked((uint)ctx[CpuRegister.Rcx]);
            request.SendTimeout = unchecked((uint)ctx[CpuRegister.R8]);
            request.ReceiveTimeout = unchecked((uint)ctx[CpuRegister.R9]);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "SK-S7daqJSE",
        ExportName = "sceNpAuthWaitAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthWaitAsync(CpuContext ctx)
    {
        return WaitOrPollAsync(ctx, "wait");
    }

    [SysAbiExport(
        Nid = "gjSyfzSsDcE",
        ExportName = "sceNpAuthPollAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthPollAsync(CpuContext ctx)
    {
        return WaitOrPollAsync(ctx, "poll");
    }

    [SysAbiExport(
        Nid = "KI4dHLlTNl0",
        ExportName = "sceNpAuthGetAuthorizationCodeV3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeV3(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        var authorizationCodeAddress = ctx[CpuRegister.Rdx];
        var issuerIdAddress = ctx[CpuRegister.Rcx];

        if (parameterAddress == 0 || authorizationCodeAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        Span<byte> parameter = stackalloc byte[AuthorizationCodeParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(parameter) != AuthorizationCodeParameterSize)
        {
            return ctx.SetReturn(NpAuthErrorInvalidSize);
        }

        var userId = BinaryPrimitives.ReadInt32LittleEndian(parameter[8..]);
        var clientIdAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameter[16..]);
        var scopeAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameter[24..]);
        if (userId == -1 || clientIdAddress == 0 || scopeAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        if (!TryClear(ctx, authorizationCodeAddress, AuthorizationCodeSize) ||
            (issuerIdAddress != 0 && !ctx.TryWriteInt32(issuerIdAddress, 0)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(CompleteOfflineRequest(requestId));
    }

    [SysAbiExport(
        Nid = "RdsFVsgSpZY",
        ExportName = "sceNpAuthGetIdTokenV3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdTokenV3(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        var tokenAddress = ctx[CpuRegister.Rdx];

        if (parameterAddress == 0 || tokenAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        Span<byte> parameter = stackalloc byte[IdTokenParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(parameter) != IdTokenParameterSize)
        {
            return ctx.SetReturn(NpAuthErrorInvalidSize);
        }

        var userId = BinaryPrimitives.ReadInt32LittleEndian(parameter[8..]);
        var clientIdAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameter[16..]);
        var clientSecretAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameter[24..]);
        var scopeAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameter[32..]);
        if (userId == -1 || clientIdAddress == 0 || clientSecretAddress == 0 || scopeAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        if (!TryClear(ctx, tokenAddress, IdTokenSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(CompleteOfflineRequest(requestId));
    }

    [SysAbiExport(
        ExportName = "sceNpAuthGetAuthorizedAppCode",
        Target = Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizedAppCode(CpuContext ctx)
    {
        return ctx.SetReturn(CompleteOfflineRequest(unchecked((int)ctx[CpuRegister.Rdi])));
    }

    private static int CreateRequest(bool async)
    {
        lock (RequestGate)
        {
            if (_activeRequestCount >= RequestLimit)
            {
                return NpAuthErrorRequestMax;
            }

            for (var index = 0; index < Requests.Length; index++)
            {
                if (Requests[index].State != RequestState.None)
                {
                    continue;
                }

                Requests[index] = new AuthRequest
                {
                    State = RequestState.Ready,
                    Async = async,
                    Result = 0,
                };
                _activeRequestCount++;
                return RequestIdOffset + index + 1;
            }
        }

        return NpAuthErrorRequestMax;
    }

    private static int CompleteOfflineRequest(int requestId)
    {
        lock (RequestGate)
        {
            if (!TryGetRequestIndexLocked(requestId, out var index))
            {
                return NpAuthErrorRequestNotFound;
            }

            ref var request = ref Requests[index];
            if (request.State == RequestState.Complete)
            {
                return NpAuthErrorInvalidArgument;
            }

            if (request.State == RequestState.Aborted)
            {
                request.Result = NpAuthErrorAborted;
                return NpAuthErrorAborted;
            }

            request.State = RequestState.Complete;
            request.Result = NpErrorSignedOut;
            return request.Async ? 0 : NpErrorSignedOut;
        }
    }

    private static int WaitOrPollAsync(CpuContext ctx, string operation)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(NpAuthErrorInvalidArgument);
        }

        int result;
        lock (RequestGate)
        {
            if (!TryGetRequestIndexLocked(requestId, out var index))
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            var request = Requests[index];
            if (!request.Async || request.State == RequestState.Ready)
            {
                return ctx.SetReturn(NpAuthErrorInvalidId);
            }

            result = request.Result;
        }

        if (!ctx.TryWriteInt32(resultAddress, result))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNpAuth(
            $"{operation} request=0x{unchecked((uint)requestId):X8} result=0x{unchecked((uint)result):X8}");
        return ctx.SetReturn(0);
    }

    private static bool TryGetRequestIndexLocked(int requestId, out int index)
    {
        var candidate = (long)requestId - RequestIdOffset - 1;
        if (candidate < 0 || candidate >= Requests.Length)
        {
            index = -1;
            return false;
        }

        index = (int)candidate;
        return Requests[index].State != RequestState.None;
    }

    private static bool TryClear(CpuContext ctx, ulong address, int size)
    {
        var clear = new byte[size];
        return ctx.Memory.TryWrite(address, clear);
    }

    private static void TraceNpAuth(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np_auth.{message}");
    }

    public static void ResetRuntimeState()
    {
        lock (RequestGate)
        {
            Array.Clear(Requests);
            _activeRequestCount = 0;
        }
    }

    internal static void ResetForTests() => ResetRuntimeState();
}
