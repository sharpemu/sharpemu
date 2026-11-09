// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

// Generation 0 never identifies a live slot, so the default slot identifier is invalid.
public readonly record struct ResourceSlotIdentifier(uint Index, uint Generation)
{
    public static ResourceSlotIdentifier Invalid => default;

    public bool IsValid => Generation != 0;
}

// An erased slot disposes its value and increases its generation.
public sealed class SlotTable<TResource> where TResource : class, IDisposable
{
    private struct Slot
    {
        public TResource? Value;
        public uint Generation;
    }

    private readonly List<Slot> _slots = new();
    private readonly List<uint> _freeSlotIndices = new();

    public int Count { get; private set; }

    public TResource this[ResourceSlotIdentifier slotIdentifier] =>
        TryGet(slotIdentifier) ?? throw SubmissionScheduler.Fatal($"The slot is not allocated: index={slotIdentifier.Index} generation={slotIdentifier.Generation}.");

    public TResource? TryGet(ResourceSlotIdentifier slotIdentifier) => IsAllocated(slotIdentifier) ? _slots[(int)slotIdentifier.Index].Value : null;

    public bool IsAllocated(ResourceSlotIdentifier slotIdentifier) =>
        slotIdentifier.IsValid && slotIdentifier.Index < _slots.Count && _slots[(int)slotIdentifier.Index].Generation == slotIdentifier.Generation && _slots[(int)slotIdentifier.Index].Value != null;

    public ResourceSlotIdentifier Insert(TResource value)
    {
        uint index;
        if (_freeSlotIndices.Count == 0)
        {
            index = (uint)_slots.Count;
            _slots.Add(new Slot { Value = value, Generation = 1 });
        }
        else
        {
            index = _freeSlotIndices[^1];
            _freeSlotIndices.RemoveAt(_freeSlotIndices.Count - 1);
            var slot = _slots[(int)index];
            if (slot.Value != null)
            {
                throw SubmissionScheduler.Fatal("The free list contains an allocated slot.");
            }

            slot.Value = value;
            _slots[(int)index] = slot;
        }

        Count++;
        return new ResourceSlotIdentifier(index, _slots[(int)index].Generation);
    }

    public void Erase(ResourceSlotIdentifier slotIdentifier)
    {
        if (!IsAllocated(slotIdentifier))
        {
            throw SubmissionScheduler.Fatal($"The slot is not allocated: index={slotIdentifier.Index} generation={slotIdentifier.Generation}.");
        }

        var slot = _slots[(int)slotIdentifier.Index];
        slot.Value!.Dispose();
        slot.Value = null;
        if (++slot.Generation == 0)
        {
            slot.Generation = 1;
        }

        _slots[(int)slotIdentifier.Index] = slot;
        _freeSlotIndices.Add(slotIdentifier.Index);
        Count--;
    }

    public void ForEach(Action<ResourceSlotIdentifier, TResource> visit)
    {
        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index].Value is { } value)
            {
                visit(new ResourceSlotIdentifier((uint)index, _slots[index].Generation), value);
            }
        }
    }
}
