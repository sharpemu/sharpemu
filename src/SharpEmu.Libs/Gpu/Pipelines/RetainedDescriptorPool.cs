// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A slice has exclusive use of this pool until its last submission completes or the slice is discarded.
internal sealed class RetainedDescriptorPool(DescriptorPool pool)
{
    private const int MaxSets = 64;
    internal const int MaxKeyWords = 256;
    internal DescriptorPool Pool => pool;
    private readonly Dictionary<(ulong Pipeline, ulong Layout), List<Slot>> _layouts = [];
    private readonly DescriptorSet[] _allocation = new DescriptorSet[1];
    private DescriptorSetDemand _remaining = DescriptorHeap.PoolCapacity;
    private uint _remainingSets = DescriptorHeap.SetsPerPool;
    private int _count;
    private ulong _used;
    private bool _full;

    private sealed class Slot(DescriptorSet set, int index)
    {
        internal readonly DescriptorSet Set = set;
        internal readonly ulong Bit = 1UL << index;
        internal ulong[] Words = [];
        internal int Length;
        internal long Generation;
    }

    // Only overwrite a native set when every array is written from element zero.
    internal static unsafe bool CoversLayout(in DescriptorSetDemand demand, ReadOnlySpan<WriteDescriptorSet> writes)
    {
        ulong bindings = 0;
        uint buffers = 0, sampled = 0, storage = 0, samplers = 0;
        foreach (ref readonly var write in writes)
        {
            if (write.DstBinding >= 64 || write.DstArrayElement != 0 || write.PNext != null || write.PTexelBufferView != null)
                return false;
            var bit = 1UL << (int)write.DstBinding;
            if ((bindings & bit) != 0) return false;
            bindings |= bit;
            switch (write.DescriptorType)
            {
                case DescriptorType.StorageBuffer: buffers += write.DescriptorCount; break;
                case DescriptorType.SampledImage: sampled += write.DescriptorCount; break;
                case DescriptorType.StorageImage: storage += write.DescriptorCount; break;
                case DescriptorType.Sampler: samplers += write.DescriptorCount; break;
                default: return false;
            }
        }
        return new DescriptorSetDemand(buffers, sampled, storage, samplers) == demand;
    }

    internal bool TryCommit(GpuDeviceInfo device, ulong pipeline, DescriptorSetLayout layout, in DescriptorSetDemand demand,
        ReadOnlySpan<ulong> key, Span<WriteDescriptorSet> writes, out DescriptorSet set)
    {
        var generation = device.DescriptorGeneration;
        var id = (pipeline, layout.Handle);
        Slot? writable = null;
        if (_layouts.TryGetValue(id, out var slots))
        {
            foreach (var slot in slots)
            {
                if (slot.Generation == generation && slot.Length == key.Length && key.SequenceEqual(slot.Words.AsSpan(0, slot.Length)))
                {
                    _used |= slot.Bit;
                    set = slot.Set;
                    return true;
                }
                if (writable is null && (_used & slot.Bit) == 0) writable = slot;
            }
        }

        if (writable is null)
        {
            uint one = 1;
            if (_count == MaxSets || !DescriptorHeap.TryAllocateBatch(device, pool, layout, in demand, _allocation,
                ref one, ref _remainingSets, ref _remaining))
            {
                _full = true;
                set = default;
                return false;
            }

            if (slots is null) _layouts.Add(id, slots = []);
            slots.Add(writable = new Slot(_allocation[0], _count++));
        }

        _used |= writable.Bit;
        set = writable.Set;
        writable.Length = 0;
        if (writable.Words.Length < key.Length) writable.Words = new ulong[key.Length];
        DescriptorHeap.WriteSet(device, set, writes);
        key.CopyTo(writable.Words);
        writable.Generation = generation;
        writable.Length = key.Length;
        return true;
    }

    internal bool FinishSlice()
    {
        // A saturated pool of mostly unused layouts would force each new layout onto the fallback path.
        var keep = !_full || BitOperations.PopCount(_used) >= (_count + 1) / 2;
        _used = 0;
        _full = false;
        return keep;
    }
}
