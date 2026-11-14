// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Network;

// Connections and requests. There is no network: objects are created as on a console, and sending a request fails
// because the host name never resolves, so no response exists.
public static partial class HttpExports
{
    private const int HttpErrorBeforeSend = unchecked((int)0x80431065);
    private const int HttpErrorResolverNoDns = unchecked((int)0x80436002);

    private static readonly ConcurrentDictionary<int, HttpConnection> Connections = new();
    private static readonly ConcurrentDictionary<int, HttpRequest> Requests = new();

    private sealed record HttpConnection(int TemplateId, ulong ServerNameAddress, ushort Port);

    private sealed record HttpRequest(int ConnectionId, int Method, ulong ContentLength);

    [SysAbiExport(
        Nid = "Kiwv9r4IZCc",
        ExportName = "sceHttpCreateConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateConnection(CpuContext ctx)
    {
        // tmplId, serverName, scheme, port, isEnableKeepalive.
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        var serverNameAddress = ctx[CpuRegister.Rsi];
        if (!Templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (serverNameAddress == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextTemplateId);
        var port = unchecked((ushort)ctx[CpuRegister.Rcx]);
        Connections[id] = new HttpConnection(templateId, serverNameAddress, port);
        TraceHttp("create_connection", id, unchecked((ulong)templateId), serverNameAddress, port, ctx[CpuRegister.R8]);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "P6A3ytpsiYc",
        ExportName = "sceHttpDeleteConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteConnection(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.TryRemove(connectionId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var pair in Requests)
        {
            if (pair.Value.ConnectionId == connectionId)
            {
                Requests.TryRemove(pair.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "tsGVru3hCe8",
        ExportName = "sceHttpCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateRequest(CpuContext ctx)
    {
        // connId, method, path, contentLength.
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.ContainsKey(connectionId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextTemplateId);
        var method = unchecked((int)ctx[CpuRegister.Rsi]);
        Requests[id] = new HttpRequest(connectionId, method, ctx[CpuRegister.Rcx]);
        TraceHttp("create_request", id, unchecked((ulong)connectionId), unchecked((ulong)method), ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qe7oZ+v4PWA",
        ExportName = "sceHttpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteRequest(CpuContext ctx) =>
        Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidId);

    // Headers apply to a template, a connection or a request; nothing is ever sent, so they are not kept.
    [SysAbiExport(
        Nid = "EY28T2bkN7k",
        ExportName = "sceHttpAddRequestHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpAddRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.ContainsKey(id) && !Connections.ContainsKey(id) && !Requests.ContainsKey(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0
            ? ctx.SetReturn(HttpErrorInvalidValue)
            : ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PTiFIUxCpJc",
        ExportName = "sceHttpSetRequestContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetRequestContentLength(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.TryGetValue(id, out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        Requests[id] = request with { ContentLength = ctx[CpuRegister.Rsi] };
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "1e2BNwI-XzE",
        ExportName = "sceHttpSendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSendRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.ContainsKey(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        TraceHttp("send_request_no_network", id, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], 0, 0);
        return ctx.SetReturn(HttpErrorResolverNoDns);
    }

    [SysAbiExport(
        Nid = "0a2TBNfE3BU",
        ExportName = "sceHttpGetStatusCode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetStatusCode(CpuContext ctx) => NoResponse(ctx);

    [SysAbiExport(
        Nid = "yuO2H2Uvnos",
        ExportName = "sceHttpGetResponseContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetResponseContentLength(CpuContext ctx) => NoResponse(ctx);

    [SysAbiExport(
        Nid = "aCYPMSUIaP8",
        ExportName = "sceHttpGetAllResponseHeaders",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetAllResponseHeaders(CpuContext ctx) => NoResponse(ctx);

    [SysAbiExport(
        Nid = "P5pdoykPYTk",
        ExportName = "sceHttpReadData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpReadData(CpuContext ctx) => NoResponse(ctx);

    private static int NoResponse(CpuContext ctx) =>
        Requests.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
            ? ctx.SetReturn(HttpErrorBeforeSend)
            : ctx.SetReturn(HttpErrorInvalidId);
}
