// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.Debugger.Protocol;

/// <summary>
/// A newline-delimited JSON protocol: one JSON object per line in each
/// direction. Requests carry a <c>command</c>; replies carry <c>ok</c> plus
/// <c>data</c>/<c>error</c>; events carry an <c>event</c> name. It is trivial to
/// drive from a socket, <c>nc</c>, or a small script, which suits bring-up and
/// tooling while a richer protocol is layered on later.
/// </summary>
public sealed class JsonLineDebugProtocol : IDebugProtocol
{
    /// <summary>The command assigned to a request that failed to parse.</summary>
    public const string ParseErrorCommand = "$parse-error";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public string Name => "json-lines/1";

    public async Task<DebugRequest?> ReadRequestAsync(TextReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (DebugRequest.TryParse(line, out var request, out var error))
            {
                return request;
            }

            // Surface the parse failure as a synthetic request so the connection
            // loop can reply with an error rather than dropping the client.
            var envelope = $"{{\"command\":\"{ParseErrorCommand}\",\"message\":{JsonSerializer.Serialize(error)}}}";
            if (DebugRequest.TryParse(envelope, out var errorRequest, out _))
            {
                return errorRequest;
            }
        }
    }

    public async Task WriteResponseAsync(TextWriter writer, DebugResponse response, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = response.Ok,
        };

        if (response.Command is not null)
        {
            payload["command"] = response.Command;
        }

        if (response.Data is not null)
        {
            payload["data"] = response.Data;
        }

        if (response.Error is not null)
        {
            payload["error"] = response.Error;
        }

        await WriteLineAsync(writer, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteEventAsync(
        TextWriter writer,
        string eventName,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(data.Count + 1)
        {
            ["event"] = eventName,
        };

        foreach (var (key, value) in data)
        {
            payload[key] = value;
        }

        await WriteLineAsync(writer, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteLineAsync(
        TextWriter writer,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
