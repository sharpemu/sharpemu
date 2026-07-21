// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Diagnostics.Contracts;

namespace SharpEmu.Diagnostics.Core;

/// <summary>
/// Central event bus. Thread-safe: uses a concurrent bag of plugins
/// and dispatches events to all registered plugins.
/// No plugin calls another plugin directly — all communication flows
/// through the bus.
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentBag<IDiagnosticPlugin> _plugins = new();
    private long _eventCount;

    public long EventCount => Interlocked.Read(ref _eventCount);
    public int PluginCount => _plugins.Count;

    public void Register(IDiagnosticPlugin plugin) => _plugins.Add(plugin);

    public void Publish(IDiagnosticEvent e)
    {
        Interlocked.Increment(ref _eventCount);
        // Dispatch to all plugins. Each plugin's OnEvent must be fast;
        // long work should be queued internally by the plugin.
        foreach (var plugin in _plugins)
        {
            try { plugin.OnEvent(e); }
            catch { /* a plugin failure must never crash the emulator */ }
        }
    }

    /// <summary>Flush all plugins and return their collected data.</summary>
    public Dictionary<string, object?> FlushAll()
    {
        var results = new Dictionary<string, object?>();
        foreach (var plugin in _plugins)
        {
            try
            {
                var data = plugin.Shutdown();
                results[plugin.Metadata.Name] = data;
            }
            catch { /* best effort */ }
        }
        return results;
    }
}
