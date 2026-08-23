// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class Http2Exports
{
    private const int Http2ErrorInvalidVersion = unchecked((int)0x817B106A);
    private const int Http2ErrorInvalidId = unchecked((int)0x817B1100);
    private const int Http2ErrorInvalidValue = unchecked((int)0x817B11FE);
    private const int Http2ErrorProhibited = unchecked((int)0x817B5224);

    private static readonly ConcurrentDictionary<int, Http2Context> Contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> Templates = new();
    private static readonly ConcurrentDictionary<int, Http2Request> Requests = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x1000;
    private static int _nextRequestId = 0x2000;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed record Http2Template(int ContextId, string UserAgent, int HttpVersion, bool AutoProxyConfig);

    private sealed record Http2Request(int TemplateId, string Method, string Url, ulong ContentLength);

    public static void ResetRuntimeState()
    {
        Contexts.Clear();
        Templates.Clear();
        Requests.Clear();
        _nextContextId = 0;
        _nextTemplateId = 0x1000;
        _nextRequestId = 0x2000;
    }

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
            return ctx.SetReturn(Http2ErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        Contexts[id] = new Http2Context(netId, sslId, poolSize, maxRequests);

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
        if (!Contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        foreach (var template in Templates)
        {
            if (template.Value.ContextId != id || !Templates.TryRemove(template.Key, out _))
            {
                continue;
            }

            RemoveTemplateRequests(template.Key);
        }

        TraceHttp2("term", id, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        ExportName = "sceHttp2CreateTemplate",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], 4096, out var userAgent) || string.IsNullOrEmpty(userAgent))
        {
            return ctx.SetReturn(Http2ErrorInvalidValue);
        }

        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        if (httpVersion is < 1 or > 3)
        {
            return ctx.SetReturn(Http2ErrorInvalidVersion);
        }

        var autoProxyConfig = unchecked((int)ctx[CpuRegister.Rcx]);
        if (autoProxyConfig is not 0 and not 1)
        {
            return ctx.SetReturn(Http2ErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextTemplateId);
        Templates[id] = new Http2Template(contextId, userAgent, httpVersion, autoProxyConfig != 0);
        TraceHttp2("create_template", id, unchecked((ulong)contextId), ctx[CpuRegister.Rsi], unchecked((ulong)httpVersion), unchecked((ulong)autoProxyConfig));
        return ctx.SetReturn(id);
    }

    [SysAbiExport(
        ExportName = "sceHttp2DeleteTemplate",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.TryRemove(templateId, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        RemoveTemplateRequests(templateId);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        ExportName = "sceHttp2CreateRequestWithURL",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateRequestWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], 64, out var method) || string.IsNullOrWhiteSpace(method) ||
            !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], 8192, out var url) || string.IsNullOrWhiteSpace(url))
        {
            return ctx.SetReturn(Http2ErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        Requests[id] = new Http2Request(templateId, method, url, ctx[CpuRegister.Rcx]);
        TraceHttp2("create_request", id, unchecked((ulong)templateId), ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
        return ctx.SetReturn(id);
    }

    [SysAbiExport(
        ExportName = "sceHttp2DeleteRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteRequest(CpuContext ctx) =>
        Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(Http2ErrorInvalidId);

    [SysAbiExport(
        ExportName = "sceHttp2SendRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SendRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.ContainsKey(requestId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        // SharpEmu does not let guest applications open host network sessions.
        // Request construction remains local, but transmission stops here.
        TraceHttp2("send_blocked", requestId, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], 0, 0);
        return ctx.SetReturn(Http2ErrorProhibited);
    }

    private static void RemoveTemplateRequests(int templateId)
    {
        foreach (var request in Requests)
        {
            if (request.Value.TemplateId == templateId)
            {
                Requests.TryRemove(request.Key, out _);
            }
        }
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        var bytes = new byte[maxLength];
        Span<byte> one = stackalloc byte[1];
        var count = 0;
        for (; count < maxLength; count++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, count);
                return true;
            }

            bytes[count] = one[0];
        }

        return false;
    }

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
