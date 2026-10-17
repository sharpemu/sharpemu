// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Least-recently-used order of slots keyed by the tick of their last use.
public sealed class RecencyQueue<TSlotIdentifier> where TSlotIdentifier : struct
{
    private sealed class Item
    {
        public TSlotIdentifier Slot;
        public ulong Tick;
        public Item? Next;
        public Item? Previous;
    }

    private readonly List<Item> _items = new();
    private readonly Queue<int> _freeEntryIndices = new();
    private Item? _first;
    private Item? _last;

    public int Insert(TSlotIdentifier slot, ulong tick)
    {
        var entryIndex = AllocateItem();
        var item = _items[entryIndex];
        item.Slot = slot;
        item.Tick = tick;
        AppendItem(item);
        return entryIndex;
    }

    public void Touch(int entryIndex, ulong tick)
    {
        var item = _items[entryIndex];
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

    public void Free(int entryIndex)
    {
        var item = _items[entryIndex];
        UnlinkItem(item);
        item.Next = null;
        item.Previous = null;
        _freeEntryIndices.Enqueue(entryIndex);
    }

    // Visits items whose tick is at most the given one; a true result stops early.
    public void ForEachItemAtOrBeforeTick(ulong tick, Func<TSlotIdentifier, bool> visit)
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
        if (_freeEntryIndices.Count == 0)
        {
            _items.Add(new Item());
            return _items.Count - 1;
        }

        return _freeEntryIndices.Dequeue();
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
