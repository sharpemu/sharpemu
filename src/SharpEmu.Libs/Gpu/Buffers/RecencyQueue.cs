// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Least-recently-used order of buffer slots keyed by the tick of their last use.
public sealed class RecencyQueue
{
    private sealed class Item
    {
        public BufferSlot Slot;
        public ulong Tick;
        public Item? Next;
        public Item? Previous;
    }

    private readonly List<Item> _items = new();
    private readonly Queue<int> _free = new();
    private Item? _first;
    private Item? _last;

    public int Insert(BufferSlot slot, ulong tick)
    {
        var id = AllocateItem();
        var item = _items[id];
        item.Slot = slot;
        item.Tick = tick;
        AppendItem(item);
        return id;
    }

    public void Touch(int id, ulong tick)
    {
        var item = _items[id];
        if (item.Tick >= tick)
        {
            return;
        }

        item.Tick = tick;
        if (item != _last)
        {
            UnlinkItem(item);
            AppendItem(item);
        }
    }

    public void Free(int id)
    {
        var item = _items[id];
        UnlinkItem(item);
        item.Next = null;
        item.Previous = null;
        _free.Enqueue(id);
    }

    // Visits items whose tick is at most the given one; a true result stops early.
    public void ForEachItemAtOrBeforeTick(ulong tick, Func<BufferSlot, bool> visit)
    {
        for (var item = _first; item != null;)
        {
            if (item.Tick > tick)
            {
                return;
            }

            var next = item.Next;
            if (visit(item.Slot))
            {
                return;
            }

            item = next;
        }
    }

    private int AllocateItem()
    {
        if (_free.Count == 0)
        {
            _items.Add(new Item());
            return _items.Count - 1;
        }

        return _free.Dequeue();
    }

    private void AppendItem(Item item)
    {
        _first ??= item;
        if (_last == null)
        {
            _last = item;
            return;
        }

        item.Previous = _last;
        _last.Next = item;
        item.Next = null;
        _last = item;
    }

    private void UnlinkItem(Item item)
    {
        if (item.Previous != null)
        {
            item.Previous.Next = item.Next;
        }

        if (item.Next != null)
        {
            item.Next.Previous = item.Previous;
        }

        if (_first == item)
        {
            _first = item.Next;
        }

        if (_last == item)
        {
            _last = item.Previous;
        }
    }
}
