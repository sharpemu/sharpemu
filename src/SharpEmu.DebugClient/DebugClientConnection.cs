// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SharpEmu.DebugClient;

/// <summary>
/// A thin TCP wrapper around the server's line-delimited JSON protocol: it
/// writes request lines and runs a background loop that prints incoming
/// responses and events as they arrive. Because the stream interleaves replies
/// with asynchronous stop/resume events, a single reader printing everything is
/// simpler and more robust than correlating request/response pairs.
/// </summary>
internal sealed class DebugClientConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private DebugClientConnection(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _reader = new StreamReader(stream, Utf8NoBom);
        _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
    }

    public static async Task<DebugClientConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new DebugClientConnection(client, client.GetStream());
    }

    /// <summary>Continuously prints incoming lines until the stream closes.</summary>
    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    Console.WriteLine();
                    Console.WriteLine("[connection closed by server]");
                    return;
                }

                Print(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            Console.WriteLine();
            Console.WriteLine("[connection lost]");
        }
    }

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void Print(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var isEvent = root.TryGetProperty("event", out _);
            var prefix = isEvent ? "event>" : "reply>";
            var pretty = JsonSerializer.Serialize(root, PrettyOptions);
            Console.WriteLine();
            Console.WriteLine($"{prefix}\n{pretty}");
        }
        catch (JsonException)
        {
            Console.WriteLine();
            Console.WriteLine(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _writeLock.Dispose();
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
