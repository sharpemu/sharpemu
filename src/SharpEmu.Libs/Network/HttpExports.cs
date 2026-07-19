// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace SharpEmu.Libs.Network;

public static class HttpExports
{
    private const int HttpErrorInvalidId = unchecked((int)0x80431100);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80431170);
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int MaxUriBytes = 4096;

    private static readonly ConcurrentDictionary<int, HttpContext> Contexts = new();
    private static readonly ConcurrentDictionary<int, HttpTemplate> Templates = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x1000;

    private sealed record HttpContext(int NetMemoryId, int SslContextId, ulong PoolSize);

    private sealed record HttpTemplate(int ContextId, ulong UserAgentAddress, int HttpVersion, bool AutoProxyConfig);

    [SysAbiExport(
        Nid = "A9cVMUtEp4Y",
        ExportName = "sceHttpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpInit(CpuContext ctx)
    {
        var netMemoryId = unchecked((int)ctx[CpuRegister.Rdi]);
        var sslContextId = unchecked((int)ctx[CpuRegister.Rsi]);
        var poolSize = ctx[CpuRegister.Rdx];
        if (poolSize == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        Contexts[id] = new HttpContext(netMemoryId, sslContextId, poolSize);
        TraceHttp("init", id, unchecked((ulong)netMemoryId), unchecked((ulong)sslContextId), poolSize, 0);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0gYjPTR-6cY",
        ExportName = "sceHttpCreateTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var userAgentAddress = ctx[CpuRegister.Rsi];
        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        var autoProxyConfig = ctx[CpuRegister.Rcx] != 0;
        var id = Interlocked.Increment(ref _nextTemplateId);
        Templates[id] = new HttpTemplate(contextId, userAgentAddress, httpVersion, autoProxyConfig);
        TraceHttp("create_template", id, unchecked((ulong)contextId), userAgentAddress, unchecked((ulong)httpVersion), autoProxyConfig ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4I8vEpuEhZ8",
        ExportName = "sceHttpDeleteTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        return Templates.TryRemove(templateId, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "Ik-KpLTlf7Q",
        ExportName = "sceHttpTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpTerm(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryRemove(contextId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var pair in Templates)
        {
            if (pair.Value.ContextId == contextId)
            {
                Templates.TryRemove(pair.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    // SceHttpUriElement layout (0x48 bytes): opaque i32 at 0x00, then scheme,
    // username, password, hostname, path, query, fragment pointers at
    // 0x08..0x38, port u16 at 0x40. Component strings live in the caller pool.
    [SysAbiExport(
        Nid = "IWalAn-guFs",
        ExportName = "sceHttpUriParse",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriParse(CpuContext ctx)
    {
        var elementAddress = ctx[CpuRegister.Rdi];
        var uriAddress = ctx[CpuRegister.Rsi];
        var poolAddress = ctx[CpuRegister.Rdx];
        var requireAddress = ctx[CpuRegister.Rcx];
        var prepare = ctx[CpuRegister.R8];
        if (uriAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        if (!TryReadNullTerminatedAscii(ctx, uriAddress, MaxUriBytes, out var uri) ||
            !TryParseUri(uri, out var parsed))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        string?[] components = [parsed.Scheme, parsed.Username, parsed.Password, parsed.Hostname, parsed.Path, parsed.Query, parsed.Fragment];
        var required = 0UL;
        foreach (var component in components)
        {
            if (component is not null)
            {
                required += (ulong)component.Length + 1;
            }
        }

        if (requireAddress != 0 && !TryWriteUInt64(ctx, requireAddress, required))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Two-phase callers pass a null element/pool first to size the pool.
        if (elementAddress == 0 || poolAddress == 0)
        {
            TraceHttp("uri_parse_size", 0, uriAddress, required, 0, 0);
            return ctx.SetReturn(0);
        }

        // prepare=0 is observed in the wild with a caller-managed pool: treat
        // it as "unchecked capacity" rather than failing the parse.
        if (prepare != 0 && prepare < required)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        Span<byte> element = stackalloc byte[0x48];
        element.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(element, parsed.Opaque ? 1u : 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(element[0x40..], parsed.Port);

        var cursor = poolAddress;
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component is null)
            {
                continue;
            }

            var bytes = Encoding.ASCII.GetBytes(component + '\0');
            if (!ctx.Memory.TryWrite(cursor, bytes))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            BinaryPrimitives.WriteUInt64LittleEndian(element[(0x08 + (i * 8))..], cursor);
            cursor += (ulong)bytes.Length;
        }

        if (!ctx.Memory.TryWrite(elementAddress, element))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceHttp("uri_parse", 0, uriAddress, poolAddress, required, parsed.Port);
        return ctx.SetReturn(0);
    }

    private readonly record struct ParsedUri(
        bool Opaque,
        string Scheme,
        string? Username,
        string? Password,
        string? Hostname,
        string? Path,
        string? Query,
        string? Fragment,
        ushort Port);

    private static bool TryParseUri(string uri, out ParsedUri parsed)
    {
        parsed = default;
        var schemeEnd = uri.IndexOf(':');
        if (schemeEnd <= 0)
        {
            return false;
        }

        var scheme = uri[..schemeEnd];
        var rest = uri[(schemeEnd + 1)..];

        var fragment = (string?)null;
        var fragmentStart = rest.IndexOf('#');
        if (fragmentStart >= 0)
        {
            fragment = rest[(fragmentStart + 1)..];
            rest = rest[..fragmentStart];
        }

        var query = (string?)null;
        var queryStart = rest.IndexOf('?');
        if (queryStart >= 0)
        {
            query = rest[(queryStart + 1)..];
            rest = rest[..queryStart];
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            parsed = new ParsedUri(
                Opaque: true, scheme, Username: null, Password: null, Hostname: null,
                Path: rest.Length == 0 ? null : rest, query, fragment, Port: 0);
            return true;
        }

        var authority = rest[2..];
        var path = (string?)null;
        var pathStart = authority.IndexOf('/');
        if (pathStart >= 0)
        {
            path = authority[pathStart..];
            authority = authority[..pathStart];
        }

        var username = (string?)null;
        var password = (string?)null;
        var userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            var userInfo = authority[..userInfoEnd];
            authority = authority[(userInfoEnd + 1)..];
            var passwordStart = userInfo.IndexOf(':');
            if (passwordStart >= 0)
            {
                username = userInfo[..passwordStart];
                password = userInfo[(passwordStart + 1)..];
            }
            else
            {
                username = userInfo;
            }
        }

        var port = scheme.ToLowerInvariant() switch
        {
            "http" => (ushort)80,
            "https" => (ushort)443,
            _ => (ushort)0,
        };
        var portStart = authority.LastIndexOf(':');
        if (portStart >= 0 && authority.IndexOf(']') < portStart)
        {
            if (!ushort.TryParse(authority[(portStart + 1)..], out port))
            {
                return false;
            }

            authority = authority[..portStart];
        }

        parsed = new ParsedUri(
            Opaque: false, scheme, username, password, authority,
            path ?? "/", query, fragment, port);
        return true;
    }

    private static bool TryReadNullTerminatedAscii(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        Span<byte> one = stackalloc byte[1];
        var builder = new StringBuilder();
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                value = builder.ToString();
                return true;
            }

            builder.Append((char)one[0]);
        }

        return false;
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static void TraceHttp(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] http.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
    }
}
