// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// One update-after-bind image set shared by all translated guest pipelines. The
// shader still has separate Vulkan image types for dimensions and numeric classes,
// but the arrays themselves are runtime arrays owned by this set rather than by a
// shader permutation.
public sealed unsafe class BindlessImageHeap : IDisposable
{
    // Sampled images, storage images, and the samplers runtime-descriptor accesses index
    // (BindingLayout.BindlessSamplerBinding).
    private const uint BindingCount = 3;
    private const uint SamplerBinding = BindingLayout.BindlessSamplerBinding;
    private const uint SamplerCapacity = 4096;
    // NVIDIA reports maxUpdateAfterBindDescriptorsInAllPools as UINT_MAX. Keep
    // the persistent layout bounded even when that device-wide limit is not
    // useful, while retaining the full capacity on devices with lower limits.
    private const uint StableCapacityPerBinding = 128u * 1024u;

    private readonly struct SlotKey : IEquatable<SlotKey>
    {
        private const int MaxWords = 8;

        public readonly DescriptorBindingKind Kind;
        public readonly ulong View;
        public readonly ImageLayout Layout;
        private readonly uint _wordCount;
        private readonly uint _word0;
        private readonly uint _word1;
        private readonly uint _word2;
        private readonly uint _word3;
        private readonly uint _word4;
        private readonly uint _word5;
        private readonly uint _word6;
        private readonly uint _word7;

        public SlotKey(DescriptorBindingKind kind, ulong view, ImageLayout layout, ReadOnlySpan<uint> words)
        {
            if (words.Length > MaxWords)
            {
                throw SubmissionScheduler.Fatal($"A bindless image descriptor has too many key words: {words.Length} > {MaxWords}.");
            }

            Kind = kind;
            View = view;
            Layout = layout;
            _wordCount = (uint)words.Length;
            _word0 = words.Length > 0 ? words[0] : 0;
            _word1 = words.Length > 1 ? words[1] : 0;
            _word2 = words.Length > 2 ? words[2] : 0;
            _word3 = words.Length > 3 ? words[3] : 0;
            _word4 = words.Length > 4 ? words[4] : 0;
            _word5 = words.Length > 5 ? words[5] : 0;
            _word6 = words.Length > 6 ? words[6] : 0;
            _word7 = words.Length > 7 ? words[7] : 0;
        }

        public bool Equals(SlotKey other) => Kind == other.Kind && View == other.View &&
            Layout == other.Layout &&
            _wordCount == other._wordCount &&
            _word0 == other._word0 && _word1 == other._word1 &&
            _word2 == other._word2 && _word3 == other._word3 &&
            _word4 == other._word4 && _word5 == other._word5 &&
            _word6 == other._word6 && _word7 == other._word7;

        public override bool Equals(object? obj) => obj is SlotKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(View);
            hash.Add(Layout);
            hash.Add(_wordCount);
            hash.Add(_word0);
            hash.Add(_word1);
            hash.Add(_word2);
            hash.Add(_word3);
            hash.Add(_word4);
            hash.Add(_word5);
            hash.Add(_word6);
            hash.Add(_word7);
            return hash.ToHashCode();
        }
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly DescriptorSet _set;
    private readonly Dictionary<SlotKey, uint> _slots = new();
    private readonly Dictionary<ulong, HashSet<SlotKey>> _slotsByView = new();
    private readonly List<uint>[] _free = [[], [], []];
    private readonly Dictionary<ulong, uint> _samplerSlots = new();
    private readonly uint[] _next = new uint[BindingCount];
    private readonly uint[] _capacity = new uint[BindingCount];
    private readonly DescriptorSetLayout _layout;

    public BindlessImageHeap(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        uint maxPerStageSampledImages,
        uint maxPerStageStorageImages,
        uint maxPerStageUpdateAfterBindSampledImages,
        uint maxPerStageUpdateAfterBindStorageImages,
        uint maxUpdateAfterBindSampledImages,
        uint maxUpdateAfterBindStorageImages,
        uint maxUpdateAfterBindDescriptors)
    {
        _scheduler = scheduler;
        var sampledLimit = Math.Min(
            Math.Min(Math.Min(maxPerStageSampledImages, maxPerStageUpdateAfterBindSampledImages), maxUpdateAfterBindSampledImages),
            StableCapacityPerBinding);
        var storageLimit = Math.Min(
            Math.Min(Math.Min(maxPerStageStorageImages, maxPerStageUpdateAfterBindStorageImages), maxUpdateAfterBindStorageImages),
            StableCapacityPerBinding);
        var totalLimit = Math.Min((ulong)maxUpdateAfterBindDescriptors, (ulong)sampledLimit + storageLimit);
        var sampledCapacity = Math.Min((ulong)sampledLimit, totalLimit / 2);
        var storageCapacity = Math.Min((ulong)storageLimit, totalLimit - sampledCapacity);
        if (sampledCapacity == 0 || storageCapacity == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The device has no usable persistent image heap capacity: sampled={sampledLimit} storage={storageLimit} total={maxUpdateAfterBindDescriptors}.");
        }

        // If one type has a smaller limit, give the unused half to the other type.
        var unassigned = totalLimit - sampledCapacity - storageCapacity;
        if (unassigned != 0)
        {
            var sampledRoom = (ulong)sampledLimit - sampledCapacity;
            var sampledExtra = Math.Min(unassigned, sampledRoom);
            sampledCapacity += sampledExtra;
            storageCapacity += Math.Min(unassigned - sampledExtra, (ulong)storageLimit - storageCapacity);
        }

        _capacity[0] = (uint)sampledCapacity;
        _capacity[1] = (uint)storageCapacity;
        _capacity[SamplerBinding] = SamplerCapacity;
        _device = device;
        Console.Error.WriteLine(
            $"[LOADER][INFO] Vulkan bindless heap sampled={sampledCapacity}/{sampledLimit} " +
            $"storage={storageCapacity}/{storageLimit} total={sampledCapacity + storageCapacity}/{totalLimit}");

        var bindings = new DescriptorSetLayoutBinding[BindingCount];
        var flags = new DescriptorBindingFlags[bindings.Length];
        for (var index = 0u; index < bindings.Length; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = index switch
                {
                    0 => DescriptorType.SampledImage,
                    1 => DescriptorType.StorageImage,
                    _ => DescriptorType.Sampler,
                },
                DescriptorCount = _capacity[index],
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
            };
            flags[index] = DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.UpdateAfterBindBit |
                DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        }

        var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = (uint)flags.Length,
        };
        fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
        fixed (DescriptorBindingFlags* flagPointer = flags)
        {
            bindingFlags.PBindingFlags = flagPointer;
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingPointer,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &create, null, out _layout), "vkCreateDescriptorSetLayout(bindless)");
        }

        var poolSizes = new List<DescriptorPoolSize>();
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.SampledImage, _capacity[0]));
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.StorageImage, _capacity[1]));
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.Sampler, _capacity[SamplerBinding]));
        fixed (DescriptorPoolSize* poolPointer = poolSizes.ToArray())
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = (uint)poolSizes.Count,
                PPoolSizes = poolPointer,
            };
            Check(device.Vk.CreateDescriptorPool(device.Device, &poolInfo, null, out var pool), "vkCreateDescriptorPool(bindless)");
            var setLayout = _layout;
            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(device.Vk.AllocateDescriptorSets(device.Device, &allocate, out _set), "vkAllocateDescriptorSets(bindless)");
            _pool = pool;
        }

        // Slot zero is the null descriptor. A draw maps an image to it only when the descriptor
        // was rejected before residency, or a runtime lookup has not found it yet.
        WriteNullDescriptor(0, 0);
        WriteNullDescriptor(1, 0);
    }

    // Samplers have no null descriptor: slot zero, which a missed runtime lookup reads, holds
    // the host's default sampler.
    public void SetDefaultSampler(Sampler sampler) => WriteSampler(0, sampler);

    public uint GetOrCreateSamplerSlot(Sampler sampler)
    {
        if (sampler.Handle == 0)
        {
            throw SubmissionScheduler.Fatal("A bindless sampler slot needs a sampler.");
        }

        if (_samplerSlots.TryGetValue(sampler.Handle, out var existing))
        {
            return existing;
        }

        var slot = ++_next[SamplerBinding];
        if (slot >= _capacity[SamplerBinding])
        {
            throw SubmissionScheduler.Fatal($"The bindless sampler heap is full: capacity={_capacity[SamplerBinding]}.");
        }

        WriteSampler(slot, sampler);
        _samplerSlots.Add(sampler.Handle, slot);
        return slot;
    }

    private void WriteSampler(uint slot, Sampler sampler)
    {
        var info = new DescriptorImageInfo { Sampler = sampler };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = SamplerBinding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = &info,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
    }

    private DescriptorPool _pool;

    public DescriptorSetLayout Layout => _layout;
    public DescriptorSet Set => _set;

    public uint GetOrCreateSlot(DescriptorBindingKind kind, ReadOnlySpan<uint> words, ImageView view, ImageLayout layout)
    {
        var index = DescriptorWriter.DescriptorType(kind) == DescriptorType.SampledImage ? 0u : 1u;
        if (index >= _capacity.Length || view.Handle == 0)
        {
            throw SubmissionScheduler.Fatal($"The bindless image slot is invalid: kind={kind} view=0x{view.Handle:X}.");
        }

        layout = index == 1 ? ImageLayout.General : layout;
        var key = new SlotKey(kind, view.Handle, layout, words);
        if (_slots.TryGetValue(key, out var existing))
        {
            return existing;
        }

        uint slot;
        if (_free[index].Count != 0)
        {
            slot = _free[index][^1];
            _free[index].RemoveAt(_free[index].Count - 1);
        }
        else
        {
            slot = ++_next[index];
            if (slot >= _capacity[index])
            {
                throw SubmissionScheduler.Fatal($"The bindless image heap is full: kind={kind} capacity={_capacity[index]}.");
            }
        }

        var info = new DescriptorImageInfo { ImageView = view, ImageLayout = layout };
        DescriptorImageInfo* infoPointer = &info;
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = index,
                DstArrayElement = slot,
                DescriptorCount = 1,
                DescriptorType = DescriptorWriter.DescriptorType(kind),
                PImageInfo = infoPointer,
            };
            _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
        }
        _slots.Add(key, slot);
        if (!_slotsByView.TryGetValue(view.Handle, out var viewSlots))
        {
            viewSlots = [];
            _slotsByView.Add(view.Handle, viewSlots);
        }
        viewSlots.Add(key);
        return slot;
    }

    public void InvalidateViews(IReadOnlyList<ImageView> views)
    {
        if (views.Count == 0)
        {
            return;
        }

        var retired = new List<(uint Binding, uint Slot)>();
        foreach (var view in views)
        {
            if (!_slotsByView.Remove(view.Handle, out var viewSlots))
            {
                continue;
            }

            foreach (var key in viewSlots)
            {
                var binding = DescriptorWriter.DescriptorType(key.Kind) == DescriptorType.SampledImage ? 0u : 1u;
                if (_slots.Remove(key, out var slot))
                {
                    retired.Add((binding, slot));
                }
            }
        }

        if (retired.Count != 0)
        {
            _scheduler.QueueCompletionAction(() =>
            {
                foreach (var (binding, slot) in retired)
                {
                    WriteNullDescriptor(binding, slot);
                    _free[binding].Add(slot);
                }
            });
        }
    }

    private void WriteNullDescriptor(uint binding, uint slot)
    {
        var info = new DescriptorImageInfo
        {
            ImageLayout = binding == 1 ? ImageLayout.General : ImageLayout.Undefined,
        };
        DescriptorImageInfo* infoPointer = &info;
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = binding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = binding == 0 ? DescriptorType.SampledImage : DescriptorType.StorageImage,
            PImageInfo = infoPointer,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        if (_pool.Handle != 0) _device.Vk.DestroyDescriptorPool(_device.Device, _pool, null);
        if (_layout.Handle != 0) _device.Vk.DestroyDescriptorSetLayout(_device.Device, _layout, null);
        _slots.Clear();
        _slotsByView.Clear();
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"{operation} failed: result={result}.");
    }
}
