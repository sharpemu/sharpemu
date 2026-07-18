// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;

namespace SharpEmu.Debugger.Protocol;

/// <summary>
/// A parsed client request: a <see cref="Command"/> verb plus a bag of named
/// arguments backed by the original JSON. Numeric arguments accept either JSON
/// numbers or <c>"0x"</c>-prefixed hex strings so addresses read naturally on
/// the wire.
/// </summary>
public sealed class DebugRequest
{
    private readonly JsonElement _root;

    private DebugRequest(string command, JsonElement root)
    {
        Command = command;
        _root = root;
    }

    /// <summary>The lower-cased command verb.</summary>
    public string Command { get; }

    /// <summary>
    /// Parses a single JSON object into a request. Returns false when the text is
    /// not a JSON object or is missing a string <c>command</c> field.
    /// </summary>
    public static bool TryParse(string json, out DebugRequest request, out string error)
    {
        request = null!;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Request must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("command", out var commandElement) ||
                commandElement.ValueKind != JsonValueKind.String)
            {
                error = "Request is missing a string 'command'.";
                return false;
            }

            var command = commandElement.GetString() ?? string.Empty;
            request = new DebugRequest(command.Trim().ToLowerInvariant(), root);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Malformed JSON: {ex.Message}";
            return false;
        }
    }

    public bool TryGetString(string name, out string value)
    {
        if (_root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public bool TryGetUInt64(string name, out ulong value)
    {
        value = 0;
        if (!_root.TryGetProperty(name, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetUInt64(out value);
            case JsonValueKind.String:
                return TryParseNumber(element.GetString(), out value);
            default:
                return false;
        }
    }

    public bool TryGetInt32(string name, out int value)
    {
        value = 0;
        if (!_root.TryGetProperty(name, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out value);
            case JsonValueKind.String when TryParseNumber(element.GetString(), out var parsed) && parsed <= int.MaxValue:
                value = (int)parsed;
                return true;
            default:
                return false;
        }
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (!_root.TryGetProperty(name, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseNumber(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
