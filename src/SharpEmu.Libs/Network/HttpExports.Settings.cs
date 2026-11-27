// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

// Shared bodies for the libSceHttp setters and getters in HttpExports.Handles.cs, and the non-blocking (epoll)
// calls. A value set on a template, connection or request is read back from it and, failing that, from its
// parents, as requests inherit template settings. There is no network: no request is ever pending, so a wait
// ends by timeout, abort or destruction.
public static partial class HttpExports
{
    private const int HttpErrorAborted = unchecked((int)0x80431080);

    private static readonly ConcurrentDictionary<(int Id, string Key), ulong> Settings = new();
    private static readonly ConcurrentDictionary<ulong, HttpEpoll> Epolls = new();
    private static ulong _nextEpollHandle;

    private enum NodeKind
    {
        Template,
        Connection,
        Request,
    }

    private sealed class HttpEpoll(int contextId)
    {
        public int ContextId { get; } = contextId;
        public bool Aborted;
        public bool Destroyed;
    }

    private static int CheckNode(int id, NodeKind? kind) => kind switch
    {
        NodeKind.Template => Templates.ContainsKey(id) ? 0 : HttpErrorInvalidId,
        NodeKind.Connection => Connections.ContainsKey(id) ? 0 : HttpErrorInvalidId,
        NodeKind.Request => Requests.ContainsKey(id) ? 0 : HttpErrorInvalidId,
        _ => Templates.ContainsKey(id) || Connections.ContainsKey(id) || Requests.ContainsKey(id) ? 0 : HttpErrorInvalidId,
    };

    private static int ParentOf(int id) =>
        Requests.TryGetValue(id, out var request) ? request.ConnectionId :
        Connections.TryGetValue(id, out var connection) ? connection.TemplateId : 0;

    private static int SetSetting(CpuContext ctx, NodeKind? kind, string key, ulong value)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var error = CheckNode(id, kind);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        Settings[(id, key)] = value;
        return ctx.SetReturn(0);
    }

    private static int SetBoolSetting(CpuContext ctx, string key)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var value = unchecked((uint)ctx[CpuRegister.Rsi]);
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        if (value > 1)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Settings[(id, key)] = value;
        TraceHttp("set_" + key, id, value, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    private static int GetBoolSetting(CpuContext ctx, string key, int defaultValue)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var value = defaultValue;
        for (var current = id; current != 0; current = ParentOf(current))
        {
            if (Settings.TryGetValue((current, key), out var stored))
            {
                value = unchecked((int)stored);
                break;
            }
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(outputAddress, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    // Timeouts reject 0; the other numeric options take any value.
    private static int SetUIntSetting(CpuContext ctx, string key, bool rejectZero)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var value = ctx[CpuRegister.Rsi];
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        if (rejectZero && unchecked((uint)value) == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Settings[(id, key)] = value;
        TraceHttp("set_" + key, id, value, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    private static int SetSslFlags(CpuContext ctx, bool clear)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var flags = unchecked((uint)ctx[CpuRegister.Rsi]);
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        Settings.AddOrUpdate((id, "sslflags"), clear ? 0 : flags, (_, current) => clear ? current & ~(ulong)flags : current | flags);
        return ctx.SetReturn(0);
    }

    // Callbacks are kept but never invoked: no request makes progress offline.
    private static int SetCallbackSetting(CpuContext ctx, NodeKind? kind, string key)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var error = CheckNode(id, kind);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        // A null function clears the registration.
        if (ctx[CpuRegister.Rsi] == 0)
        {
            Settings.TryRemove((id, key), out _);
            Settings.TryRemove((id, key + ".arg"), out _);
        }
        else
        {
            Settings[(id, key)] = ctx[CpuRegister.Rsi];
            Settings[(id, key + ".arg")] = ctx[CpuRegister.Rdx];
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "hvG6GfBMXg8",
        ExportName = "sceHttpAbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpAbortRequest(CpuContext ctx) =>
        ctx.SetReturn(CheckNode(unchecked((int)ctx[CpuRegister.Rdi]), NodeKind.Request));

    // (requestId, int* errorNumber). No OS error exists offline.
    [SysAbiExport(
        Nid = "0onIrKx9NIE",
        ExportName = "sceHttpGetLastErrno",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetLastErrno(CpuContext ctx)
    {
        var error = CheckNode(unchecked((int)ctx[CpuRegister.Rdi]), NodeKind.Request);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        Span<byte> zero = stackalloc byte[sizeof(int)];
        zero.Clear();
        return ctx[CpuRegister.Rsi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], zero)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    // (contextId, HttpEpollHandle* handle)
    [SysAbiExport(
        Nid = "6381dWF+xsQ",
        ExportName = "sceHttpCreateEpoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpCreateEpoll(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handleAddress = ctx[CpuRegister.Rsi];
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (handleAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var handle = Interlocked.Increment(ref _nextEpollHandle);
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, handle);
        if (!ctx.Memory.TryWrite(handleAddress, bytes))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Epolls[handle] = new HttpEpoll(contextId);
        TraceHttp("create_epoll", contextId, handle, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    // (contextId, HttpEpollHandle handle)
    [SysAbiExport(
        Nid = "wYhXVfS2Et4",
        ExportName = "sceHttpDestroyEpoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDestroyEpoll(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var handle = ctx[CpuRegister.Rsi];
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (handle == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!Epolls.TryRemove(handle, out var epoll))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (epoll)
        {
            epoll.Destroyed = true;
            Monitor.PulseAll(epoll);
        }

        return ctx.SetReturn(0);
    }

    // (id, HttpEpollHandle handle, void* userArg)
    [SysAbiExport(
        Nid = "-xm7kZQNpHI",
        ExportName = "sceHttpSetEpoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetEpoll(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var handle = ctx[CpuRegister.Rsi];
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        if (handle == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!Epolls.ContainsKey(handle))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        Settings[(id, "epoll")] = handle;
        Settings[(id, "epoll.arg")] = ctx[CpuRegister.Rdx];
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "59tL1AQBb8U",
        ExportName = "sceHttpUnsetEpoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUnsetEpoll(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var error = CheckNode(id, null);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        Settings.TryRemove((id, "epoll"), out _);
        Settings.TryRemove((id, "epoll.arg"), out _);
        return ctx.SetReturn(0);
    }

    // (HttpEpollHandle handle, HttpNBEvent* events, int maxEvents, int timeoutMicroseconds). No request is ever
    // pending, so the wait reports no events once the timeout passes, or ends by abort or destruction. A negative
    // timeout waits without limit.
    [SysAbiExport(
        Nid = "qISjDHrxONc",
        ExportName = "sceHttpWaitRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpWaitRequest(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var maxEvents = unchecked((int)ctx[CpuRegister.Rdx]);
        var timeoutMicroseconds = unchecked((int)ctx[CpuRegister.Rcx]);
        if (handle == 0 || ctx[CpuRegister.Rsi] == 0 || maxEvents <= 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!Epolls.TryGetValue(handle, out var epoll))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (epoll)
        {
            var deadline = timeoutMicroseconds < 0
                ? (DateTime?)null
                : DateTime.UtcNow.AddTicks((long)timeoutMicroseconds * TimeSpan.TicksPerMicrosecond);
            while (!epoll.Aborted && !epoll.Destroyed)
            {
                if (deadline is null)
                {
                    Monitor.Wait(epoll);
                    continue;
                }

                var remaining = deadline.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(epoll, remaining))
                {
                    break;
                }
            }

            return ctx.SetReturn(epoll.Aborted ? HttpErrorAborted : 0);
        }
    }

    // An abort sticks: every later wait on the handle reports ABORTED.
    [SysAbiExport(
        Nid = "sWQiqKvYTVA",
        ExportName = "sceHttpAbortWaitRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpAbortWaitRequest(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!Epolls.TryGetValue(handle, out var epoll))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (epoll)
        {
            epoll.Aborted = true;
            Monitor.PulseAll(epoll);
        }

        return ctx.SetReturn(0);
    }
}
