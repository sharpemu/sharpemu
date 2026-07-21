// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// ============================================================================
// DIAGNOSTIC RUNTIME ADAPTER (v5)
//
// Static bridge between Emulator Core and Diagnostics subsystem.
// Core/Libs/HLE call DiagnosticAdapter.Publish() — if a handler is
// registered (by DiagnosticManager at startup), the event is forwarded.
// If no handler is registered, the call returns immediately (zero overhead).
//
// This file has ZERO dependencies on SharpEmu.Core/Libs/HLE/Logging.
// ============================================================================

using SharpEmu.Diagnostics.Contracts.Events;

namespace SharpEmu.Diagnostics.Contracts;

/// <summary>
/// Static adapter that the Emulator Core/Libs/HLE use to publish diagnostic events.
/// This is the ONLY entry point from Core/Libs/HLE into the Diagnostics subsystem.
/// </summary>
public static class DiagnosticAdapter
{
    private static volatile bool _isActive;
    private static Action<IDiagnosticEvent>? _handler;

    /// <summary>
    /// True if diagnostics are active (a handler has been registered).
    /// Core/Libs/HLE should check this before constructing events.
    /// </summary>
    public static bool IsActive => _isActive;

    /// <summary>
    /// Registers a publish handler. Called by DiagnosticManager.Start().
    /// </summary>
    public static void RegisterHandler(Action<IDiagnosticEvent> handler)
    {
        _handler = handler;
        _isActive = true;
    }

    /// <summary>
    /// Unregisters the handler. Called by DiagnosticManager.Stop().
    /// </summary>
    public static void UnregisterHandler()
    {
        _isActive = false;
        _handler = null;
    }

    /// <summary>
    /// Publishes a diagnostic event. Returns immediately if no handler is registered.
    /// </summary>
    public static void Publish(IDiagnosticEvent e)
    {
        if (!_isActive) return;
        _handler?.Invoke(e);
    }

    // --- Convenience methods for common event types ---

    /// <summary>Publishes a boot stage event.</summary>
    public static void NotifyBootStage(string stageName, bool success, string? detail = null)
    {
        if (!_isActive) return;
        Publish(new BootEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            stageName, success, detail));
    }

    /// <summary>Publishes an import call event.</summary>
    public static void NotifyImport(string nid, string? exportName, string? library, int result, long durationMicros = 0)
    {
        if (!_isActive) return;
        Publish(new ImportEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            nid, exportName, library, result, durationMicros));
    }

    /// <summary>Publishes a crash event.</summary>
    public static void NotifyCrash(ulong rip, ulong faultAddress, int signal, string crashType, Dictionary<string, ulong>? registers = null)
    {
        if (!_isActive) return;
        Publish(new CrashEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            rip, faultAddress, signal, crashType, registers));
    }

    /// <summary>Publishes a thread lifecycle event.</summary>
    public static void NotifyThreadEvent(ulong threadId, string operation, string? detail = null)
    {
        if (!_isActive) return;
        Publish(new ThreadEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            threadId, operation, detail));
    }

    /// <summary>Publishes a memory event.</summary>
    public static void NotifyMemoryEvent(string operation, ulong address, ulong size, string? detail = null)
    {
        if (!_isActive) return;
        Publish(new MemoryEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            operation, address, size, detail));
    }

    /// <summary>Publishes a GPU event.</summary>
    public static void NotifyGpuEvent(string operation, ulong? address = null, string? detail = null)
    {
        if (!_isActive) return;
        Publish(new GpuEvent(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            operation, address, detail));
    }
}
