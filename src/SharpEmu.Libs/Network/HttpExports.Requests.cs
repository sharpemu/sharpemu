// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static partial class HttpExports
{
    private const int HttpErrorBeforeSend = unchecked((int)0x80431065);
    private const int HttpErrorTimeout = unchecked((int)0x80431068);
    private const int HttpErrorUnknownMethod = unchecked((int)0x8043106B);

    private sealed record HttpHeader(string Name, string Value);

    private sealed record HttpRequest(
        int ConnectionId,
        string Method,
        string Path,
        ulong ContentLength,
        HttpHeader[] Headers,
        bool SendAttempted,
        int SendResult,
        uint StatusCode);

    [SysAbiExport(
        Nid = "P6A3ytpsiYc",
        ExportName = "sceHttpDeleteConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteConnection(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (LifecycleGate)
        {
            if (!Connections.TryRemove(connectionId, out _))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            RemoveConnectionRequestsLocked(connectionId);
        }

        TraceHttp("delete_connection", connectionId, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "tsGVru3hCe8",
        ExportName = "sceHttpCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateRequest(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        var method = unchecked((int)ctx[CpuRegister.Rsi]);
        var pathAddress = ctx[CpuRegister.Rdx];
        var contentLength = ctx[CpuRegister.Rcx];
        var methodName = method switch
        {
            0 => "GET",
            1 => "POST",
            2 => "HEAD",
            3 => "OPTIONS",
            4 => "PUT",
            5 => "DELETE",
            6 => "TRACE",
            7 => "CONNECT",
            _ => null,
        };

        if (methodName is null)
        {
            return ctx.SetReturn(HttpErrorUnknownMethod);
        }

        if (pathAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var pathResult = ReadUriSource(ctx.Memory, pathAddress, out var path);
        if (pathResult != 0)
        {
            return ctx.SetReturn(pathResult);
        }

        int requestId;
        lock (LifecycleGate)
        {
            if (!Connections.ContainsKey(connectionId))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            requestId = Interlocked.Increment(ref _nextRequestId);
            Requests[requestId] = new HttpRequest(
                connectionId,
                methodName,
                path,
                contentLength,
                [],
                false,
                HttpErrorBeforeSend,
                0);
        }

        TraceHttp(
            "create_request",
            requestId,
            unchecked((ulong)connectionId),
            unchecked((ulong)method),
            pathAddress,
            contentLength);
        ctx[CpuRegister.Rax] = unchecked((ulong)requestId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qe7oZ+v4PWA",
        ExportName = "sceHttpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (LifecycleGate)
        {
            if (!Requests.TryRemove(requestId, out _))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }
        }

        TraceHttp("delete_request", requestId, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PTiFIUxCpJc",
        ExportName = "sceHttpSetRequestContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetRequestContentLength(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var contentLength = ctx[CpuRegister.Rsi];
        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            Requests[requestId] = request with { ContentLength = contentLength };
        }

        TraceHttp("set_request_content_length", requestId, contentLength, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "EY28T2bkN7k",
        ExportName = "sceHttpAddRequestHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpAddRequestHeader(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var nameAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        var mode = unchecked((uint)ctx[CpuRegister.Rcx]);
        if (nameAddress == 0 || valueAddress == 0 || mode > 1)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var nameResult = ReadUriSource(ctx.Memory, nameAddress, out var name);
        if (nameResult != 0)
        {
            return ctx.SetReturn(nameResult);
        }

        var valueResult = ReadUriSource(ctx.Memory, valueAddress, out var value);
        if (valueResult != 0)
        {
            return ctx.SetReturn(valueResult);
        }

        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            var headers = mode == 0
                ? request.Headers
                    .Where(header => !string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                    .Append(new HttpHeader(name, value))
                    .ToArray()
                : [.. request.Headers, new HttpHeader(name, value)];
            Requests[requestId] = request with { Headers = headers };
        }

        TraceHttp("add_request_header", requestId, nameAddress, valueAddress, mode, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "1e2BNwI-XzE",
        ExportName = "sceHttpSendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSendRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            // Guest HTTP state is emulated locally, but guest applications are not
            // allowed to initiate host-network traffic. Report a deterministic
            // timeout while retaining coherent response-query state.
            Requests[requestId] = request with
            {
                SendAttempted = true,
                SendResult = HttpErrorTimeout,
                StatusCode = 0,
            };
        }

        TraceHttp("send_timeout", requestId, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], 0, 0);
        return ctx.SetReturn(HttpErrorTimeout);
    }

    [SysAbiExport(
        Nid = "0a2TBNfE3BU",
        ExportName = "sceHttpGetStatusCode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetStatusCode(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusCodeAddress = ctx[CpuRegister.Rsi];
        if (statusCodeAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        HttpRequest request;
        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out request!))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }
        }

        if (!ctx.TryWriteUInt32(statusCodeAddress, request.StatusCode))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(request.SendAttempted ? request.SendResult : HttpErrorBeforeSend);
    }

    [SysAbiExport(
        Nid = "aCYPMSUIaP8",
        ExportName = "sceHttpGetAllResponseHeaders",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetAllResponseHeaders(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var headerAddress = ctx[CpuRegister.Rsi];
        var headerSizeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || headerSizeAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        HttpRequest request;
        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out request!))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }
        }

        if (!ctx.TryWriteUInt64(headerAddress, 0) || !ctx.TryWriteUInt64(headerSizeAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(request.SendAttempted ? request.SendResult : HttpErrorBeforeSend);
    }

    [SysAbiExport(
        Nid = "yuO2H2Uvnos",
        ExportName = "sceHttpGetResponseContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetResponseContentLength(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        var contentLengthAddress = ctx[CpuRegister.Rdx];
        if (resultAddress == 0 || contentLengthAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        HttpRequest request;
        lock (LifecycleGate)
        {
            if (!Requests.TryGetValue(requestId, out request!))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }
        }

        var sendResult = request.SendAttempted ? request.SendResult : HttpErrorBeforeSend;
        var queryResult = sendResult == 0 ? 0u : uint.MaxValue;
        if (!ctx.TryWriteUInt32(resultAddress, queryResult) ||
            !ctx.TryWriteUInt64(contentLengthAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(sendResult);
    }

    internal static bool TryGetRequestForTests(
        int requestId,
        out string method,
        out string path,
        out ulong contentLength,
        out int headerCount)
    {
        lock (LifecycleGate)
        {
            if (Requests.TryGetValue(requestId, out var request))
            {
                method = request.Method;
                path = request.Path;
                contentLength = request.ContentLength;
                headerCount = request.Headers.Length;
                return true;
            }
        }

        method = string.Empty;
        path = string.Empty;
        contentLength = 0;
        headerCount = 0;
        return false;
    }

    private static void RemoveConnectionRequestsLocked(int connectionId)
    {
        foreach (var request in Requests)
        {
            if (request.Value.ConnectionId == connectionId)
            {
                Requests.TryRemove(request.Key, out _);
            }
        }
    }
}
