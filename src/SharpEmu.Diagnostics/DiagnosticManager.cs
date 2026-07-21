// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Diagnostics.Contracts;
using SharpEmu.Diagnostics.Core;
using SharpEmu.Diagnostics.Export;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Top-level diagnostic manager. Created once per emulator session.
/// Owns the EventBus, PluginRegistry, and export pipeline.
///
/// Usage:
///   using var mgr = new DiagnosticManager(gameId, dir);
///   mgr.Start();
///   mgr.Publish(new BootEvent(...));
///   // at shutdown:
///   mgr.Dispose(); // flushes everything
/// </summary>
public sealed class DiagnosticManager : IDisposable
{
    private EventBus? _bus;
    private DiagnosticContext? _context;
    private PluginRegistry? _registry;
    private readonly string _gameId;
    private readonly string _sessionDir;
    private bool _active;

    public bool IsActive => _active;

    public DiagnosticManager(string gameId, string sessionDirectory)
    {
        _gameId = gameId;
        _sessionDir = sessionDirectory;
    }

    /// <summary>Initialize the bus, registry, and auto-register plugins based on config.</summary>
    public void Start()
    {
        var config = DiagnosticConfig.Load();
        _active = config.IsAnyEnabled;
        if (!_active) return;

        Directory.CreateDirectory(_sessionDir);
        _bus = new EventBus();
        _context = new DiagnosticContext(_gameId, _sessionDir, _bus);
        _registry = new PluginRegistry(_bus, _context);

        // Plugins are registered by the caller (e.g. CLI) after Start().
        // DiagnosticManager itself does not know about specific plugin types.
    }

    /// <summary>Register a plugin by type. Must be called after Start().</summary>
    public T? RegisterPlugin<T>() where T : class, IDiagnosticPlugin, new()
    {
        if (!_active || _registry == null) return null;
        return _registry.Register<T>();
    }

    /// <summary>Publish an event to all registered plugins.</summary>
    public void Publish(IDiagnosticEvent e)
    {
        if (_active && _bus != null) _bus.Publish(e);
    }

    /// <summary>Collect data from all plugins and write to disk.</summary>
    public void Flush()
    {
        if (!_active || _bus == null) return;
        var data = _bus.FlushAll();
        DiagnosticExporter.ExportJson(_sessionDir, data);
        DiagnosticExporter.ExportText(_sessionDir, data);
        DiagnosticExporter.ExportMarkdown(_sessionDir, _gameId, data);
    }

    /// <summary>Shutdown all plugins and release resources.</summary>
    public void Stop()
    {
        Flush();
        _active = false;
    }

    public void Dispose() => Stop();
}
