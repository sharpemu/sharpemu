// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;

namespace SharpEmu.Debugger.Server;

/// <summary>
/// A network front-end that exposes a debugger session to remote clients.
/// </summary>
public interface IDebuggerServer : IAsyncDisposable
{
    /// <summary>True once the listener is accepting connections.</summary>
    bool IsListening { get; }

    /// <summary>The endpoint the server is bound to, or null before start.</summary>
    IPEndPoint? Endpoint { get; }

    /// <summary>Binds and begins accepting client connections.</summary>
    void Start();

    /// <summary>Stops accepting connections and closes active clients.</summary>
    Task StopAsync();
}
