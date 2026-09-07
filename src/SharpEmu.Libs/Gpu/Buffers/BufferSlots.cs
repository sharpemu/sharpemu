// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

// Generation 0 never names a live slot, so the default value is the invalid id.
public readonly record struct BufferSlot(uint Index, uint Generation)
{
    public static BufferSlot Invalid => default;

    public bool IsValid => Generation != 0;
}

// Stable slot storage; an erased slot disposes its buffer and bumps the generation.
public sealed class BufferSlots
{
    private struct Slot
    {
        public GpuBuffer? Value;
        public uint Generation;
    }

    private readonly List<Slot> _slots = new();
    private readonly List<uint> _free = new();

    public int Count { get; private set; }

    public GpuBuffer this[BufferSlot id] =>
        TryGet(id) ?? throw SubmissionScheduler.Fatal($"Buffer slot {id.Index}:{id.Generation} is not allocated.");

    public GpuBuffer? TryGet(BufferSlot id) => IsAllocated(id) ? _slots[(int)id.Index].Value : null;

    public bool IsAllocated(BufferSlot id) =>
        id.IsValid && id.Index < _slots.Count && _slots[(int)id.Index].Generation == id.Generation && _slots[(int)id.Index].Value != null;

    public BufferSlot Insert(GpuBuffer buffer)
    {
        uint index;
        if (_free.Count == 0)
        {
            index = (uint)_slots.Count;
            _slots.Add(new Slot { Value = buffer, Generation = 1 });
        }
        else
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
            var slot = _slots[(int)index];
            if (slot.Value != null)
            {
                throw SubmissionScheduler.Fatal("The free list contains an allocated buffer slot.");
            }

            slot.Value = buffer;
            _slots[(int)index] = slot;
        }

        Count++;
        return new BufferSlot(index, _slots[(int)index].Generation);
    }

    public void Erase(BufferSlot id)
    {
        if (!IsAllocated(id))
        {
            throw SubmissionScheduler.Fatal($"Buffer slot {id.Index}:{id.Generation} is not allocated.");
        }

        var slot = _slots[(int)id.Index];
        slot.Value!.Dispose();
        slot.Value = null;
        if (++slot.Generation == 0)
        {
            slot.Generation = 1;
        }

        _slots[(int)id.Index] = slot;
        _free.Add(id.Index);
        Count--;
    }

    public void ForEach(Action<BufferSlot, GpuBuffer> visit)
    {
        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index].Value is { } buffer)
            {
                visit(new BufferSlot((uint)index, _slots[index].Generation), buffer);
            }
        }
    }
}
