// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Contracts;

/// <summary>
/// The minimal interface every diagnostic plugin must implement.
/// Plugins receive events through <see cref="OnEvent"/> and must not
/// call other plugins directly. All communication flows through the EventBus.
/// </summary>
public interface IDiagnosticPlugin
{
    /// <summary>Plugin metadata (name, version, description, env var).</summary>
    PluginMetadata Metadata { get; }

    /// <summary>Called once after registration. Subscribe to event types here.</summary>
    void Initialize(IDiagnosticContext context);

    /// <summary>Called when the session is closing. Return collected data for the exporter.</summary>
    /// <returns>A serializable object or null if nothing to export.</returns>
    object? Shutdown();

    /// <summary>Called for every event published to the bus. Return quickly.</summary>
    void OnEvent(IDiagnosticEvent e);
}
