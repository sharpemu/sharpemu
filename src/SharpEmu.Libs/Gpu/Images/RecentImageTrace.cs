// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

internal sealed class RecentImageTrace(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, string Text)>> _entries = new();
    private readonly LinkedList<(string Key, string Text)> _order = new();
    private long _evicted;

    internal void Record(string key, string text)
    {
        if (_entries.Remove(key, out var previous)) _order.Remove(previous);
        else if (_order.Count == capacity)
        {
            _entries.Remove(_order.First!.Value.Key);
            _order.RemoveFirst();
            _evicted++;
        }
        _entries.Add(key, _order.AddLast((key, text)));
    }

    internal void WriteTo(TextWriter writer, string name)
    {
        writer.WriteLine($"[GPU][TRACE] {name} recent={_order.Count} evicted={_evicted}");
        foreach (var entry in _order) writer.WriteLine(entry.Text);
        _entries.Clear();
        _order.Clear();
        _evicted = 0;
    }
}
