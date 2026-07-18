// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Protocol;

/// <summary>
/// Frames debugger traffic on a connection. A protocol turns bytes into
/// <see cref="DebugRequest"/> objects and serialises <see cref="DebugResponse"/>
/// replies plus asynchronous events (stops, resumes, termination) back to the
/// client. Swapping the implementation (line-delimited JSON today, a GDB remote
/// serial stub later) leaves the session and server untouched.
/// </summary>
public interface IDebugProtocol
{
    /// <summary>A short protocol name reported in the handshake.</summary>
    string Name { get; }

    /// <summary>
    /// Reads the next request, or null at end of stream. Parse failures are
    /// surfaced as a request with a reserved error command rather than throwing.
    /// </summary>
    Task<DebugRequest?> ReadRequestAsync(TextReader reader, CancellationToken cancellationToken);

    /// <summary>Writes a reply to a request.</summary>
    Task WriteResponseAsync(TextWriter writer, DebugResponse response, CancellationToken cancellationToken);

    /// <summary>Writes an unsolicited event (for example a stop notification).</summary>
    Task WriteEventAsync(
        TextWriter writer,
        string eventName,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken cancellationToken);
}
