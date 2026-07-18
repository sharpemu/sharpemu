// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SharpEmu.Debugger.Protocol;
using SharpEmu.Debugger.Session;
using SharpEmu.Logging;

namespace SharpEmu.Debugger.Server;

/// <summary>
/// A TCP server that exposes an <see cref="IDebuggerSession"/> to remote
/// clients over a pluggable <see cref="IDebugProtocol"/>. Every connection sees
/// the same session, so multiple clients (for example a UI and a scripted
/// probe) observe a consistent view of the target.
/// </summary>
public sealed class DebuggerServer : IDebuggerServer
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.Debugger");

    private readonly IDebuggerSession _session;
    private readonly DebuggerServerOptions _options;
    private readonly Func<IDebugProtocol> _protocolFactory;
    private readonly ConcurrentDictionary<DebuggerClientConnection, Task> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public DebuggerServer(
        IDebuggerSession session,
        DebuggerServerOptions? options = null,
        Func<IDebugProtocol>? protocolFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new DebuggerServerOptions();
        _protocolFactory = protocolFactory ?? (static () => new JsonLineDebugProtocol());
    }

    public bool IsListening => _listener is not null;

    public IPEndPoint? Endpoint { get; private set; }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Start(_options.MaxClients);
        _listener = listener;
        Endpoint = (IPEndPoint?)listener.LocalEndpoint;
        Log.Info($"Debug server listening on {Endpoint} (protocol {_protocolFactory().Name})");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _shutdown.Token));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var connection = new DebuggerClientConnection(client, _session, _protocolFactory());
            var task = Task.Run(() => ServeAsync(connection, cancellationToken), cancellationToken);
            _connections[connection] = task;
        }
    }

    private async Task ServeAsync(DebuggerClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener = null;

        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop.ConfigureAwait(false);
            }

            await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Expected while tearing connections down.
        }

        _connections.Clear();
        Log.Info("Debug server stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
