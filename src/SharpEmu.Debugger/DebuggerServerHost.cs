// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger.Server;
using SharpEmu.Debugger.Session;

namespace SharpEmu.Debugger;

/// <summary>
/// One-call wiring of the live debugger: it owns a <see cref="DebuggerSession"/>
/// and a <see cref="DebuggerServer"/>, exposes the <see cref="Hook"/> to attach
/// to <c>SharpEmuRuntimeOptions.DebugHook</c>, and starts/stops the network
/// front-end. A host constructs one, hands <see cref="Hook"/> to the runtime,
/// calls <see cref="Start"/>, and calls <see cref="NotifyRunCompleted"/> once the
/// runtime returns.
/// </summary>
public sealed class DebuggerServerHost : IAsyncDisposable
{
    private readonly DebuggerSession _session;
    private readonly DebuggerServer _server;

    public DebuggerServerHost(
        DebuggerServerOptions? serverOptions = null,
        DebuggerSessionOptions? sessionOptions = null)
    {
        _session = new DebuggerSession(sessionOptions);
        _server = new DebuggerServer(_session, serverOptions);
    }

    /// <summary>The session driving the target.</summary>
    public IDebuggerSession Session => _session;

    /// <summary>
    /// The dispatcher hook to hand to the runtime so guest frames route through
    /// the debugger.
    /// </summary>
    public ICpuDebugHook Hook => _session.Hook;

    /// <summary>The endpoint the server bound to, or null before <see cref="Start"/>.</summary>
    public IPEndPoint? Endpoint => _server.Endpoint;

    /// <summary>Begins accepting debugger clients.</summary>
    public void Start() => _server.Start();

    /// <summary>
    /// Releases a parked emulation thread and marks the target terminated. Call
    /// after the runtime's run returns so any attached client is notified and the
    /// guest thread is never left blocked in the debugger.
    /// </summary>
    public void NotifyRunCompleted() => _session.NotifyTerminated();

    public async ValueTask DisposeAsync()
    {
        _session.NotifyTerminated();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
