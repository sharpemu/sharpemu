// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// One entry per local VkDeviceMemory, including one for a whole slab. Free and priority
// updates share a lock so a delayed touch cannot change the priority of a recycled handle.
internal sealed class GpuResidency(Action<DeviceMemory, float> setPriority)
{
    // Scan every eight frames. Active bindings refresh their age on use.
    internal const long ColdFrames = 120;
    internal const string PriorityExtension = "VK_EXT_memory_priority";
    internal const string PageableExtension = "VK_EXT_pageable_device_local_memory";
    internal static unsafe bool QueryFeatures(Vk vk, PhysicalDevice physical, PhysicalDeviceMemoryPriorityFeaturesEXT* priority,
        PhysicalDevicePageableDeviceLocalMemoryFeaturesEXT* pageable)
    {
        *pageable = new() { SType = StructureType.PhysicalDevicePageableDeviceLocalMemoryFeaturesExt };
        *priority = new() { SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt, PNext = pageable };
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = priority };
        vk.GetPhysicalDeviceFeatures2(physical, &features);
        return priority->MemoryPriority && pageable->PageableDeviceLocalMemory;
    }
    internal enum Role { Other, Buffer, BoundBuffer, Slab, EmptySlab, Upload, Readback, Texture, Color, Depth, WrittenImage, Flip, Scratch, IdleBuffer, IdleImage, IdleScratch }
    internal enum Band { Low, Default, High }
    private readonly Lock _gate = new();
    private readonly Dictionary<DeviceMemory, Entry> _entries = new();
    private long _frame;

    internal sealed class Entry(GpuResidency owner, DeviceMemory memory, Role role, Band applied)
    {
        internal readonly DeviceMemory Memory = memory;
        internal Role Kind = role;
        internal Band Applied = applied;
        internal long LastUse = -1;
        internal bool Live = true;

        internal void Use(Role kind)
        {
            var frame = Volatile.Read(ref owner._frame);
            if (Volatile.Read(ref LastUse) == frame && Kind == kind) return;
            lock (owner._gate)
            {
                if (!Live) return;
                Kind = kind;
                Volatile.Write(ref LastUse, owner._frame);
                owner.Apply(this);
            }
        }
    }

    internal static Role PresenterBufferRole(BufferUsageFlags usage) => usage switch
    {
        BufferUsageFlags.TransferDstBit => Role.Readback,
        BufferUsageFlags.TransferSrcBit or (BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit) => Role.Upload,
        _ => Role.Buffer,
    };

    private static Band Desired(Role role, bool cold) => role switch
    {
        Role.IdleImage or Role.IdleScratch or Role.IdleBuffer or Role.EmptySlab or Role.Upload or Role.Readback => Band.Low,
        Role.Texture or Role.Color or Role.Depth or Role.WrittenImage when cold => Band.Low,
        Role.Slab or Role.BoundBuffer when cold => Band.Default,
        Role.Color or Role.Depth or Role.WrittenImage or Role.BoundBuffer or Role.Slab or Role.Flip or Role.Scratch => Band.High,
        _ => Band.Default,
    };

    internal static float Value(Band band) => band switch { Band.High => 1f, Band.Low => 0f, _ => 0.5f };
    internal Band Initial(Role role) => Desired(role, false);

    internal Entry Add(DeviceMemory memory, Role role, Band initial)
    {
        lock (_gate)
        {
            var entry = new Entry(this, memory, role, initial) { LastUse = _frame };
            _entries.Add(memory, entry);
            Apply(entry);
            return entry;
        }
    }

    internal Entry? Find(DeviceMemory memory)
    {
        lock (_gate) return _entries.GetValueOrDefault(memory);
    }

    internal void Remove(DeviceMemory memory)
    {
        lock (_gate)
            if (_entries.Remove(memory, out var entry)) entry.Live = false;
    }

    internal void Advance(long frame)
    {
        if (frame == _frame) return;
        lock (_gate)
        {
            bool rescan = frame / 8 != _frame / 8;
            Volatile.Write(ref _frame, frame);
            if (rescan)
                foreach (var entry in _entries.Values) Apply(entry);
        }
    }

    private void Apply(Entry entry)
    {
        var band = Desired(entry.Kind, _frame - entry.LastUse >= ColdFrames);
        if (entry.Applied == band) return;
        setPriority(entry.Memory, Value(band));
        entry.Applied = band;
    }
}
