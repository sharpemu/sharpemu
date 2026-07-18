// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Protocol;

/// <summary>
/// The reply to a <see cref="DebugRequest"/>: either success with an optional
/// data payload, or a failure with a human-readable message.
/// </summary>
public sealed class DebugResponse
{
    private DebugResponse(bool ok, string? command, IReadOnlyDictionary<string, object?>? data, string? error)
    {
        Ok = ok;
        Command = command;
        Data = data;
        Error = error;
    }

    public bool Ok { get; }

    /// <summary>Echoes the command the reply answers, when known.</summary>
    public string? Command { get; }

    public IReadOnlyDictionary<string, object?>? Data { get; }

    public string? Error { get; }

    public static DebugResponse Success(string command, IReadOnlyDictionary<string, object?>? data = null)
        => new(ok: true, command, data, error: null);

    public static DebugResponse Failure(string command, string error)
        => new(ok: false, command, data: null, error);
}
