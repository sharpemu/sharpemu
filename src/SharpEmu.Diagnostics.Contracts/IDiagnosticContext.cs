// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Contracts;

/// <summary>
/// Context given to each plugin at initialization. Provides access to
/// the session directory, game ID, and the event bus for publishing.
/// Plugins should NOT write files directly — they should return data
/// from Shutdown() and let the Export layer handle IO.
/// </summary>
public interface IDiagnosticContext
{
    string GameId { get; }
    string SessionDirectory { get; }
    void Publish(IDiagnosticEvent e);
}
