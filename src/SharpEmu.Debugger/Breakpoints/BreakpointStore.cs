// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Breakpoints;

/// <summary>
/// A thread-safe registry of breakpoints. The debug server mutates it from
/// client-servicing threads while the emulation thread queries it at frame
/// boundaries, so every operation takes the same lock.
/// </summary>
public sealed class BreakpointStore
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Breakpoint> _breakpoints = new();
    private int _nextId = 1;

    /// <summary>Adds a breakpoint and returns the created entry with its id.</summary>
    public Breakpoint Add(BreakpointKind kind, ulong address, ulong length = 1)
    {
        lock (_sync)
        {
            var effectiveLength = kind == BreakpointKind.Execute ? 1UL : Math.Max(1UL, length);
            var breakpoint = new Breakpoint(_nextId++, kind, address, effectiveLength);
            _breakpoints[breakpoint.Id] = breakpoint;
            return breakpoint;
        }
    }

    /// <summary>Removes a breakpoint by id. Returns false when it did not exist.</summary>
    public bool Remove(int id)
    {
        lock (_sync)
        {
            return _breakpoints.Remove(id);
        }
    }

    /// <summary>Enables or disables a breakpoint by id.</summary>
    public bool SetEnabled(int id, bool enabled)
    {
        lock (_sync)
        {
            if (!_breakpoints.TryGetValue(id, out var breakpoint))
            {
                return false;
            }

            _breakpoints[id] = breakpoint.WithEnabled(enabled);
            return true;
        }
    }

    /// <summary>Removes every breakpoint.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _breakpoints.Clear();
        }
    }

    /// <summary>Returns a point-in-time copy of all breakpoints.</summary>
    public IReadOnlyList<Breakpoint> Snapshot()
    {
        lock (_sync)
        {
            return _breakpoints.Values.ToArray();
        }
    }

    /// <summary>
    /// Finds the first enabled execution breakpoint covering <paramref name="address"/>,
    /// or null when none applies.
    /// </summary>
    public Breakpoint? FindExecuteHit(ulong address)
    {
        lock (_sync)
        {
            foreach (var breakpoint in _breakpoints.Values)
            {
                if (breakpoint.Enabled && breakpoint.Kind == BreakpointKind.Execute && breakpoint.Covers(address))
                {
                    return breakpoint;
                }
            }

            return null;
        }
    }
}
