// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Diagnostics.Contracts;

namespace SharpEmu.Diagnostics.Core;

/// <summary>
/// Implementation of <see cref="IDiagnosticContext"/>. Given to each plugin
/// at initialization so it knows where to write files and can publish events.
/// </summary>
public sealed class DiagnosticContext : IDiagnosticContext
{
    private readonly EventBus _bus;

    public string GameId { get; }
    public string SessionDirectory { get; }

    public DiagnosticContext(string gameId, string sessionDirectory, EventBus bus)
    {
        GameId = gameId;
        SessionDirectory = sessionDirectory;
        _bus = bus;
    }

    public void Publish(IDiagnosticEvent e) => _bus.Publish(e);
}
