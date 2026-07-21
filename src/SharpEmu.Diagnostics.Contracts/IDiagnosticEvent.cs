// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Contracts;

/// <summary>
/// Base interface for all diagnostic events. Events are immutable records
/// that flow through the EventBus. The Version field lets old plugins
/// gracefully ignore events from newer emulators.
/// </summary>
public interface IDiagnosticEvent
{
    /// <summary>Monotonic timestamp (Stopwatch ticks).</summary>
    long Timestamp { get; }

    /// <summary>Event schema version (default 1). Increment when fields change.</summary>
    int Version { get; }

    /// <summary>Event category: "cpu", "memory", "thread", "import", "boot", "crash".</summary>
    string Category { get; }

    /// <summary>Event type within the category.</summary>
    string Type { get; }
}
