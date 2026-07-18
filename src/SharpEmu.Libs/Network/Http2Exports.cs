// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class Http2Exports
{
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);
    private const int MethodCStringCapacity = 64;
    private const int UrlCStringCapacity = 4096;

    private static readonly object _stateGate = new();
    private static readonly ConcurrentDictionary<int, Http2Context> _contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> _templates = new();
    private static readonly ConcurrentDictionary<int, Http2RequestState> _requests = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x100;
    private static int _nextRequestId = 0x200;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed record Http2Template(int ContextId, ulong UserAgentAddress, int HttpVersion, int AutoProxy);

    internal readonly record struct Http2RequestState(
        int TemplateId,
        string Method,
        string Url,
        ulong ContentLength);

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

        int id;
        lock (_stateGate)
        {
            id = Interlocked.Increment(ref _nextContextId);
            _contexts[id] = new Http2Context(netId, sslId, poolSize, maxRequests);
        }

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
        var removedTemplateIds = new HashSet<int>();
        var removedRequestCount = 0;
        lock (_stateGate)
        {
            if (!_contexts.TryRemove(id, out _))
            {
                return ctx.SetReturn(Http2ErrorInvalidId);
            }

            foreach (var pair in _templates)
            {
                if (pair.Value.ContextId == id && _templates.TryRemove(pair.Key, out _))
                {
                    removedTemplateIds.Add(pair.Key);
                }
            }

            if (removedTemplateIds.Count != 0)
            {
                foreach (var pair in _requests)
                {
                    if (removedTemplateIds.Contains(pair.Value.TemplateId) &&
                        _requests.TryRemove(pair.Key, out _))
                    {
                        removedRequestCount++;
                    }
                }
            }
        }

        TraceHttp2(
            "term",
            id,
            unchecked((ulong)removedTemplateIds.Count),
            unchecked((ulong)removedRequestCount),
            0,
            0);
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
        var userAgentAddress = ctx[CpuRegister.Rsi];
        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        var autoProxy = unchecked((int)ctx[CpuRegister.Rcx]);

        int id;
        lock (_stateGate)
        {
            if (!_contexts.ContainsKey(contextId))
            {
                return ctx.SetReturn(Http2ErrorInvalidId);
            }

            id = Interlocked.Increment(ref _nextTemplateId);
            _templates[id] = new Http2Template(contextId, userAgentAddress, httpVersion, autoProxy);
        }

        TraceHttp2("template.create", id, unchecked((ulong)contextId), userAgentAddress, unchecked((ulong)httpVersion), unchecked((ulong)autoProxy));
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mmyOCxQMVYQ",
        ExportName = "sceHttp2CreateRequestWithURL",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateRequestWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        var methodAddress = ctx[CpuRegister.Rsi];
        var urlAddress = ctx[CpuRegister.Rdx];
        var contentLength = ctx[CpuRegister.Rcx];

        lock (_stateGate)
        {
            if (!_templates.ContainsKey(templateId))
            {
                return ctx.SetReturn(Http2ErrorInvalidId);
            }
        }

        if (!TryReadRequiredCString(ctx, methodAddress, MethodCStringCapacity, out var method) ||
            !TryReadRequiredCString(ctx, urlAddress, UrlCStringCapacity, out var url))
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        int id;
        lock (_stateGate)
        {
            if (!_templates.ContainsKey(templateId))
            {
                return ctx.SetReturn(Http2ErrorInvalidId);
            }

            id = Interlocked.Increment(ref _nextRequestId);
            _requests[id] = new Http2RequestState(templateId, method, url, contentLength);
        }

        TraceHttp2(
            "request.create",
            id,
            unchecked((ulong)templateId),
            methodAddress,
            urlAddress,
            contentLength);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static bool TryGetRequestState(int requestId, out Http2RequestState state) =>
        _requests.TryGetValue(requestId, out state);

    internal static void ResetForTests()
    {
        lock (_stateGate)
        {
            _contexts.Clear();
            _templates.Clear();
            _requests.Clear();
            _nextContextId = 0;
            _nextTemplateId = 0x100;
            _nextRequestId = 0x200;
        }
    }

    private static bool TryReadRequiredCString(
        CpuContext ctx,
        ulong address,
        int capacity,
        out string value)
    {
        value = string.Empty;
        if (address == 0 || capacity <= 0)
        {
            return false;
        }

        const int ReadChunkLength = 128;
        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var length = 0;
            Span<byte> singleByte = stackalloc byte[1];
            while (length < capacity)
            {
                var currentAddress = address + unchecked((ulong)length);
                if (currentAddress < address)
                {
                    return false;
                }

                var chunkLength = Math.Min(ReadChunkLength, capacity - length);
                var chunk = rented.AsSpan(length, chunkLength);
                if (ctx.Memory.TryRead(currentAddress, chunk))
                {
                    var terminator = chunk.IndexOf((byte)0);
                    if (terminator >= 0)
                    {
                        var stringLength = length + terminator;
                        if (stringLength == 0)
                        {
                            return false;
                        }

                        value = Encoding.UTF8.GetString(rented, 0, stringLength);
                        return true;
                    }

                    length += chunkLength;
                    continue;
                }

                for (var i = 0; i < chunkLength; i++)
                {
                    var byteAddress = currentAddress + unchecked((ulong)i);
                    if (byteAddress < currentAddress ||
                        !ctx.Memory.TryRead(byteAddress, singleByte))
                    {
                        return false;
                    }

                    if (singleByte[0] == 0)
                    {
                        var stringLength = length + i;
                        if (stringLength == 0)
                        {
                            return false;
                        }

                        value = Encoding.UTF8.GetString(rented, 0, stringLength);
                        return true;
                    }

                    rented[length + i] = singleByte[0];
                }

                length += chunkLength;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
