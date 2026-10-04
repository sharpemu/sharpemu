// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Network;

public static partial class HttpExports
{
    private const int HttpErrorInvalidId = unchecked((int)0x80431100);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);

    private static readonly ConcurrentDictionary<int, HttpContext> Contexts = new();
    private static readonly ConcurrentDictionary<int, HttpTemplate> Templates = new();
    private static readonly ConcurrentDictionary<int, HttpConnection> Connections = new();
    private static readonly ConcurrentDictionary<int, HttpRequest> Requests = new();
    private static readonly object LifecycleGate = new();
    private static int _nextContextId;
    private static int _nextTemplateId = 0x1000;
    private static int _nextConnectionId = 0x2000;
    private static int _nextRequestId = 0x3000;

    private sealed record HttpContext(int NetMemoryId, int SslContextId, ulong PoolSize);

    private sealed record HttpTemplate(
        int ContextId,
        ulong UserAgentAddress,
        int HttpVersion,
        bool AutoProxyConfig,
        uint ConnectTimeoutMicroseconds = 30_000_000);

    private sealed record HttpConnection(
        int TemplateId,
        string ServerName,
        string Scheme,
        ushort Port,
        bool KeepAlive,
        uint ConnectTimeoutMicroseconds);

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

        int id;
        lock (LifecycleGate)
        {
            id = Interlocked.Increment(ref _nextContextId);
            Contexts[id] = new HttpContext(netMemoryId, sslContextId, poolSize);
        }
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
        var userAgentAddress = ctx[CpuRegister.Rsi];
        var httpVersion = unchecked((int)ctx[CpuRegister.Rdx]);
        var autoProxyConfig = ctx[CpuRegister.Rcx] != 0;
        int id;
        lock (LifecycleGate)
        {
            if (!Contexts.ContainsKey(contextId))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            id = Interlocked.Increment(ref _nextTemplateId);
            Templates[id] = new HttpTemplate(contextId, userAgentAddress, httpVersion, autoProxyConfig);
        }

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
        lock (LifecycleGate)
        {
            if (!Templates.TryRemove(templateId, out _))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            RemoveTemplateConnectionsLocked(templateId);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Kiwv9r4IZCc",
        ExportName = "sceHttpCreateConnection",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateConnection(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        var serverNameAddress = ctx[CpuRegister.Rsi];
        var schemeAddress = ctx[CpuRegister.Rdx];
        var port = unchecked((ushort)ctx[CpuRegister.Rcx]);
        var keepAlive = ctx[CpuRegister.R8] != 0;

        if (serverNameAddress == 0 || schemeAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var serverResult = ReadUriSource(ctx.Memory, serverNameAddress, out var serverName);
        if (serverResult != 0)
        {
            return ctx.SetReturn(serverResult);
        }

        var schemeResult = ReadUriSource(ctx.Memory, schemeAddress, out var scheme);
        if (schemeResult != 0)
        {
            return ctx.SetReturn(schemeResult);
        }

        if (serverName.Length == 0 || scheme.Length == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        int id;
        lock (LifecycleGate)
        {
            if (!Templates.TryGetValue(templateId, out var template))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            id = Interlocked.Increment(ref _nextConnectionId);
            Connections[id] = new HttpConnection(
                templateId,
                serverName,
                scheme,
                port,
                keepAlive,
                template.ConnectTimeoutMicroseconds);
        }
        TraceHttp(
            "create_connection",
            id,
            unchecked((ulong)templateId),
            port,
            keepAlive ? 1UL : 0UL,
            0);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0S9tTH0uqTU",
        ExportName = "sceHttpSetConnectTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetConnectTimeOut(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var timeoutMicroseconds = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (LifecycleGate)
        {
            if (Templates.TryGetValue(id, out var template))
            {
                Templates[id] = template with { ConnectTimeoutMicroseconds = timeoutMicroseconds };
            }
            else if (Connections.TryGetValue(id, out var connection))
            {
                Connections[id] = connection with { ConnectTimeoutMicroseconds = timeoutMicroseconds };
            }
            else
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }
        }

        TraceHttp("set_connect_timeout", id, timeoutMicroseconds, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Ik-KpLTlf7Q",
        ExportName = "sceHttpTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpTerm(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (LifecycleGate)
        {
            if (!Contexts.TryRemove(contextId, out _))
            {
                return ctx.SetReturn(HttpErrorInvalidId);
            }

            foreach (var pair in Templates)
            {
                if (pair.Value.ContextId == contextId)
                {
                    Templates.TryRemove(pair.Key, out _);
                    RemoveTemplateConnectionsLocked(pair.Key);
                }
            }
        }

        return ctx.SetReturn(0);
    }

    public static void ResetRuntimeState()
    {
        lock (LifecycleGate)
        {
            Requests.Clear();
            Connections.Clear();
            Templates.Clear();
            Contexts.Clear();
            _nextRequestId = 0x3000;
            _nextConnectionId = 0x2000;
            _nextTemplateId = 0x1000;
            _nextContextId = 0;
        }
    }

    internal static bool TryGetConnectTimeout(int id, out uint timeoutMicroseconds)
    {
        lock (LifecycleGate)
        {
            if (Templates.TryGetValue(id, out var template))
            {
                timeoutMicroseconds = template.ConnectTimeoutMicroseconds;
                return true;
            }

            if (Connections.TryGetValue(id, out var connection))
            {
                timeoutMicroseconds = connection.ConnectTimeoutMicroseconds;
                return true;
            }

            timeoutMicroseconds = 0;
            return false;
        }
    }

    private static void RemoveTemplateConnectionsLocked(int templateId)
    {
        foreach (var connection in Connections)
        {
            if (connection.Value.TemplateId == templateId)
            {
                RemoveConnectionRequestsLocked(connection.Key);
                Connections.TryRemove(connection.Key, out _);
            }
        }
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
