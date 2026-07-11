// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>
/// Minimal Discord Rich Presence client. Speaks Discord's local IPC
/// protocol (framed JSON over the discord-ipc-N named pipe) directly, so no
/// external dependency is needed. All failures are silent: no Discord, no
/// presence, no impact on the launcher.
/// </summary>
public sealed class DiscordRichPresence : IDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int MaxFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _clientId;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;

    private NamedPipeClientStream? _pipe;
    private PresenceState? _desired;
    private PresenceState? _sent;
    private volatile bool _disposed;

    private sealed record PresenceState(string Details, string? State, long? StartUnixSeconds);

    public DiscordRichPresence(string clientId)
    {
        _clientId = clientId;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DiscordRichPresence",
        };
        _worker.Start();
    }

    /// <summary>
    /// Requests the given presence; the worker applies it asynchronously and
    /// retries (with reconnection) until it is delivered or replaced.
    /// </summary>
    public void SetPresence(string details, string? state = null, long? startUnixSeconds = null)
    {
        lock (_gate)
        {
            _desired = new PresenceState(details, state, startUnixSeconds);
        }

        Trace($"queued details=\"{details}\" state=\"{state}\"");
        _signal.Set();
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        ClosePipe();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            // Woken immediately on presence changes; the periodic tick
            // retries after connection failures (e.g. Discord starts later).
            _signal.WaitOne(TimeSpan.FromSeconds(15));
            if (_disposed || _clientId.Length == 0)
            {
                continue;
            }

            PresenceState? desired;
            lock (_gate)
            {
                desired = _desired;
            }

            if (desired is null || desired == _sent)
            {
                continue;
            }

            try
            {
                EnsureConnected();
                SendActivity(desired);
                _sent = desired;
                Trace($"sent details=\"{desired.Details}\" state=\"{desired.State}\"");
            }
            catch (Exception exception)
            {
                // Connection died (or never existed); reconnect on a later tick.
                Trace($"send failed: {exception.Message}");
                ClosePipe();
                _sent = null;
            }
        }
    }

    private void EnsureConnected()
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        ClosePipe();
        Exception? lastError = null;
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                $"discord-ipc-{index}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(500);
                _pipe = pipe;
                WriteFrame(OpHandshake, JsonSerializer.Serialize(new
                {
                    v = 1,
                    client_id = _clientId,
                }));

                // Discord replies with a READY (or error) frame; consume it
                // so the pipe buffer stays drained.
                _ = ReadFrame();
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                pipe.Dispose();
                _pipe = null;
            }
        }

        throw lastError ?? new IOException("Discord IPC pipe not found.");
    }

    private void SendActivity(PresenceState presence)
    {
        var payload = new Dictionary<string, object?>
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = Guid.NewGuid().ToString(),
            ["args"] = new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = new Dictionary<string, object?>
                {
                    ["details"] = presence.Details,
                    ["state"] = presence.State,
                    ["timestamps"] = presence.StartUnixSeconds is { } start
                        ? new Dictionary<string, object?> { ["start"] = start }
                        : null,
                    ["assets"] = new Dictionary<string, object?>
                    {
                        ["large_image"] = "logo",
                        ["large_text"] = "SharpEmu",
                    },
                },
            },
        };

        WriteFrame(OpFrame, JsonSerializer.Serialize(payload, JsonOptions));

        // Consume Discord's acknowledgement to keep the pipe drained.
        _ = ReadFrame();
    }

    private void WriteFrame(int opcode, string json)
    {
        var pipe = _pipe ?? throw new IOException("Discord IPC pipe is not connected.");
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private string ReadFrame()
    {
        var pipe = _pipe ?? throw new IOException("Discord IPC pipe is not connected.");
        var header = ReadExactly(pipe, 8);
        var length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new IOException($"Discord IPC frame length {length} is out of range.");
        }

        return Encoding.UTF8.GetString(ReadExactly(pipe, length));
    }

    private static byte[] ReadExactly(NamedPipeClientStream pipe, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = pipe.Read(buffer, read, count - read);
            if (chunk <= 0)
            {
                throw new IOException("Discord IPC pipe closed.");
            }

            read += chunk;
        }

        return buffer;
    }

    private static void Trace(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISCORD"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[DISCORD] {message}");
        }
    }

    private void ClosePipe()
    {
        var pipe = _pipe;
        _pipe = null;
        try
        {
            pipe?.Dispose();
        }
        catch (Exception)
        {
            // Best effort; the pipe may already be broken.
        }
    }
}
