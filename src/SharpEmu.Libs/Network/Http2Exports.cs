// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class Http2Exports
{
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);

    private static readonly ConcurrentDictionary<int, Http2Context> _contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> _templates = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x2000;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed record Http2Template(int ContextId, ulong UserAgentAddress, int HttpVersion, bool AutoProxyConfig);

    [SysAbiExport(
        Nid = "3JCe3lCbQ8A",
        ExportName = "sceHttp2Init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Init(CpuContext ctx)
    {
        var netId = unchecked((int)ctx[CpuRegister.Rdi]);
        var sslId = unchecked((int)ctx[CpuRegister.Rsi]);
        var poolSize = ctx[CpuRegister.Rdx];
        var maxRequests = unchecked((int)ctx[CpuRegister.Rcx]);

        if (poolSize == 0 || maxRequests <= 0)
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new Http2Context(netId, sslId, poolSize, maxRequests);

        TraceHttp2("init", id, unchecked((ulong)netId), unchecked((ulong)sslId), poolSize, unchecked((ulong)maxRequests));
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "YiBUtz-pGkc",
        ExportName = "sceHttp2Term",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Term(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        foreach (var pair in _templates)
        {
            if (pair.Value.ContextId == id)
            {
                _templates.TryRemove(pair.Key, out _);
            }
        }

        TraceHttp2("term", id, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "+wCt7fCijgk",
        ExportName = "sceHttp2CreateTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        var userAgentAddress = ctx[CpuRegister.Rsi];
        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        var autoProxyConfig = ctx[CpuRegister.Rcx] != 0;
        var id = Interlocked.Increment(ref _nextTemplateId);
        _templates[id] = new Http2Template(contextId, userAgentAddress, httpVersion, autoProxyConfig);

        TraceHttp2("create_template", id, unchecked((ulong)contextId), userAgentAddress, unchecked((ulong)httpVersion), autoProxyConfig ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "pDom5-078DA",
        ExportName = "sceHttp2DeleteTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceHttp2("delete_template", templateId, 0, 0, 0, 0);
        return _templates.TryRemove(templateId, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);
    }

    // Per-template setters: no request is ever issued HLE-side, so accepting
    // the value for a known template is all the behavior the callers need.
    // Returning success for an id that does not exist is dangerous: the guest
    // then dereferences its (nonexistent) template object and crashes at NULL
    // right after the call (run 130053, sceHttp2SetSslCallback).
    private static int SetTemplateOption(CpuContext ctx, string operation)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceHttp2(operation, templateId, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], 0, 0);
        return _templates.ContainsKey(templateId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "YrWX+DhPHQY",
        ExportName = "sceHttp2SetSslCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetSslCallback(CpuContext ctx) => SetTemplateOption(ctx, "set_ssl_callback");

    [SysAbiExport(
        Nid = "-HIO4VT87v8",
        ExportName = "sceHttp2SetConnectTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetConnectTimeOut(CpuContext ctx) => SetTemplateOption(ctx, "set_connect_timeout");

    [SysAbiExport(
        Nid = "XPtW45xiLHk",
        ExportName = "sceHttp2SetSendTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetSendTimeOut(CpuContext ctx) => SetTemplateOption(ctx, "set_send_timeout");

    [SysAbiExport(
        Nid = "izvHhqgDt44",
        ExportName = "sceHttp2SetRecvTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRecvTimeOut(CpuContext ctx) => SetTemplateOption(ctx, "set_recv_timeout");

    [SysAbiExport(
        Nid = "jjFahkBPCYs",
        ExportName = "sceHttp2SetAuthEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetAuthEnabled(CpuContext ctx) => SetTemplateOption(ctx, "set_auth_enabled");

    [SysAbiExport(
        Nid = "BJgi0CH7al4",
        ExportName = "sceHttp2SetRedirectCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRedirectCallback(CpuContext ctx) => SetTemplateOption(ctx, "set_redirect_callback");

    [SysAbiExport(
        Nid = "EWcwMpbr5F8",
        ExportName = "sceHttp2SslEnableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SslEnableOption(CpuContext ctx) => SetTemplateOption(ctx, "ssl_enable_option");

    [SysAbiExport(
        Nid = "B37SruheQ5Y",
        ExportName = "sceHttp2SslDisableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SslDisableOption(CpuContext ctx) => SetTemplateOption(ctx, "ssl_disable_option");

    private static void TraceHttp2(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] http2.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
    }
}
