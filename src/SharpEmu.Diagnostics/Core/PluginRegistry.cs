// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Diagnostics.Contracts;

namespace SharpEmu.Diagnostics.Core;

/// <summary>
/// Manages plugin registration. Plugins are registered by type;
/// the manager creates and initializes them.
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<IDiagnosticPlugin> _plugins = new();
    private readonly EventBus _bus;
    private readonly DiagnosticContext _context;

    public PluginRegistry(EventBus bus, DiagnosticContext context)
    {
        _bus = bus;
        _context = context;
    }

    public T Register<T>() where T : IDiagnosticPlugin, new()
    {
        var plugin = new T();
        plugin.Initialize(_context);
        _plugins.Add(plugin);
        _bus.Register(plugin);
        return plugin;
    }

    public IDiagnosticPlugin? GetPlugin(string name) =>
        _plugins.FirstOrDefault(p => p.Metadata.Name == name);

    public IReadOnlyList<IDiagnosticPlugin> All => _plugins;

    public void ShutdownAll()
    {
        foreach (var p in _plugins)
        {
            try { p.Shutdown(); }
            catch { /* best effort */ }
        }
    }
}
