// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;
using System.Text;
using SharpEmu.Debugger.Protocol;
using SharpEmu.Debugger.Session;
using SharpEmu.Logging;

namespace SharpEmu.Debugger.Server;

/// <summary>
/// Services a single connected client: reads requests, dispatches them against
/// the shared session, and pushes session lifecycle events. Writes from the
/// request loop and from event callbacks are serialised through one lock so the
/// two never interleave a half-written line.
/// </summary>
internal sealed class DebuggerClientConnection : IAsyncDisposable
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.Debugger");
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TcpClient _client;
    private readonly IDebuggerSession _session;
    private readonly IDebugProtocol _protocol;
    private readonly DebugCommandDispatcher _dispatcher;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TextWriter? _writer;
    private CancellationToken _cancellationToken;

    public DebuggerClientConnection(TcpClient client, IDebuggerSession session, IDebugProtocol protocol)
    {
        _client = client;
        _session = session;
        _protocol = protocol;
        _dispatcher = new DebugCommandDispatcher(session);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        var endpoint = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Log.Info($"Debugger client connected: {endpoint}");

        using var stream = _client.GetStream();
        using var reader = new StreamReader(stream, Utf8NoBom);
        await using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
        _writer = writer;

        _session.Stopped += OnStopped;
        _session.Resumed += OnResumed;
        _session.Terminated += OnTerminated;
        try
        {
            await SendEventAsync("hello", new Dictionary<string, object?>
            {
                ["protocol"] = _protocol.Name,
                ["state"] = _session.State.ToString(),
            }).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await _protocol.ReadRequestAsync(reader, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                var response = _dispatcher.Dispatch(request);
                await WriteResponseAsync(response).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (IOException)
        {
            // Client dropped the connection.
        }
        catch (Exception ex)
        {
            Log.Warn($"Debugger client error ({endpoint}): {ex.Message}");
        }
        finally
        {
            _session.Stopped -= OnStopped;
            _session.Resumed -= OnResumed;
            _session.Terminated -= OnTerminated;
            _writer = null;
            Log.Info($"Debugger client disconnected: {endpoint}");
        }
    }

    private void OnStopped(object? sender, DebugStopEvent stop)
        => _ = SendEventAsync("stopped", DebugCommandDispatcher.DescribeStop(stop));

    private void OnResumed(object? sender, EventArgs e)
        => _ = SendEventAsync("resumed", EmptyData);

    private void OnTerminated(object? sender, EventArgs e)
        => _ = SendEventAsync("terminated", EmptyData);

    private async Task WriteResponseAsync(DebugResponse response)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        await _writeLock.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            await _protocol.WriteResponseAsync(writer, response, _cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendEventAsync(string name, IReadOnlyDictionary<string, object?> data)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                await _protocol.WriteEventAsync(writer, name, data, _cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away between the event firing and the write.
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyData = new Dictionary<string, object?>();
}
