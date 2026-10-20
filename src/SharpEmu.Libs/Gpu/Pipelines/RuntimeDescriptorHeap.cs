// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The persistent descriptor set a shader indexes when it reads its image and sampler descriptors at run time
// (RuntimeDescriptorTable): one partially bound, update-after-bind array of sampled images and one of samplers.
// A pipeline binds it once as set 1, whatever the number of images the shaders reach through it.
//
// Slot 0 of both arrays is what a missed lookup reads: the null image, and the host's default sampler. A
// slot is written when the host first needs it and again, with the null descriptor, once the view it named
// is gone and every command that could read it has completed; a slot is never rewritten while a view lives.
public sealed unsafe class RuntimeDescriptorHeap : IDisposable
{
    private const uint SamplerCeiling = 2048;
    // Descriptors are small, but the driver reserves the whole array. The scenes this serves need a few
    // thousand distinct views; keep the set bounded even when the device would allow millions.
    private const uint ImageCeiling = 16 * 1024;

    // The device's descriptor limits that bound the heap.
    public readonly record struct Limits(
        uint MaxPerStageSampledImages,
        uint MaxPerStageSamplers,
        uint MaxPerStageUpdateAfterBindSampledImages,
        uint MaxPerStageUpdateAfterBindSamplers,
        uint MaxUpdateAfterBindSampledImages,
        uint MaxUpdateAfterBindSamplers,
        uint MaxUpdateAfterBindDescriptorsInAllPools);

    // Sampled images and samplers that fit the limits; zero when the device leaves no room for the heap.
    public static (uint Images, uint Samplers) Capacities(in Limits limits)
    {
        var samplers = Math.Min(Math.Min(Math.Min(limits.MaxPerStageSamplers, limits.MaxPerStageUpdateAfterBindSamplers),
            limits.MaxUpdateAfterBindSamplers), SamplerCeiling);
        var images = Math.Min(Math.Min(Math.Min(limits.MaxPerStageSampledImages, limits.MaxPerStageUpdateAfterBindSampledImages),
            limits.MaxUpdateAfterBindSampledImages), ImageCeiling);
        // The pool-wide total is shared with every other update-after-bind pool; the samplers take their share first.
        if (limits.MaxUpdateAfterBindDescriptorsInAllPools <= samplers)
        {
            return (0, 0);
        }

        images = Math.Min(images, limits.MaxUpdateAfterBindDescriptorsInAllPools - samplers);
        return images < 2 || samplers < 2 ? (0, 0) : (images, samplers);
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly object _gate = new();
    private readonly DescriptorSetLayout _layout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly uint _imageCapacity;
    private readonly uint _samplerCapacity;
    private readonly Dictionary<(ulong View, ImageLayout Layout), uint> _imageSlots = new();
    private readonly Dictionary<ulong, List<(ulong View, ImageLayout Layout)>> _slotsByView = new();
    private readonly Stack<uint> _freeImageSlots = new();
    private readonly Dictionary<ulong, uint> _samplerSlots = new();
    private uint _nextImageSlot;
    private uint _nextSamplerSlot;
    private bool _imagesFullReported;
    private bool _samplersFullReported;

    public RuntimeDescriptorHeap(GpuDeviceInfo device, SubmissionScheduler scheduler, in Limits limits)
    {
        _device = device;
        _scheduler = scheduler;
        (_imageCapacity, _samplerCapacity) = Capacities(limits);
        if (_imageCapacity == 0)
        {
            throw SubmissionScheduler.Fatal($"The device has no room for the runtime image descriptor heap: limits={limits}.");
        }

        Console.Error.WriteLine($"[LOADER][INFO] Vulkan runtime descriptor heap images={_imageCapacity} samplers={_samplerCapacity}");
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        var flags = stackalloc DescriptorBindingFlags[2];
        const ShaderStageFlags Stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        bindings[0] = new DescriptorSetLayoutBinding(RuntimeDescriptorTable.HeapSampledImageBinding, DescriptorType.SampledImage, _imageCapacity, Stages);
        bindings[1] = new DescriptorSetLayoutBinding(RuntimeDescriptorTable.HeapSamplerBinding, DescriptorType.Sampler, _samplerCapacity, Stages);
        // Only slots a shader reads need to hold a valid descriptor, and the host writes new slots while
        // earlier draws that cannot reach them are still executing.
        flags[0] = flags[1] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit |
            DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 2,
            PBindingFlags = flags,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &bindingFlags,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 2,
            PBindings = bindings,
        };
        Check(device.Vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _layout), "vkCreateDescriptorSetLayout(runtime heap)");

        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.SampledImage, _imageCapacity);
        sizes[1] = new DescriptorPoolSize(DescriptorType.Sampler, _samplerCapacity);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
        };
        Check(device.Vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool), "vkCreateDescriptorPool(runtime heap)");
        var setLayout = _layout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Check(device.Vk.AllocateDescriptorSets(device.Device, &allocate, out _set), "vkAllocateDescriptorSets(runtime heap)");
        WriteImage(0, default, ImageLayout.Undefined);
    }

    public DescriptorSetLayout Layout => _layout;

    public DescriptorSet Set => _set;

    public uint ImageCapacity => _imageCapacity;

    public uint SamplerCapacity => _samplerCapacity;

    // Samplers have no null descriptor: slot 0, which a missed lookup reads, holds the host's default sampler.
    public void SetDefaultSampler(Sampler sampler) => WriteSampler(0, sampler);

    // The slot of a view in the layout the draw reads it in; a new view takes a free or fresh slot.
    // When the heap is full the slot is 0, the null image, and the lookup keeps missing.
    public uint GetOrCreateImageSlot(ImageView view, ImageLayout layout)
    {
        if (view.Handle == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var key = (view.Handle, layout);
            if (_imageSlots.TryGetValue(key, out var existing))
            {
                return existing;
            }

            uint slot;
            if (_freeImageSlots.TryPop(out var free))
            {
                slot = free;
            }
            else if (_nextImageSlot + 1 < _imageCapacity)
            {
                slot = ++_nextImageSlot;
            }
            else
            {
                if (!_imagesFullReported)
                {
                    _imagesFullReported = true;
                    Console.Error.WriteLine($"[GPU][WARN] The runtime image heap is full: capacity={_imageCapacity}. Further descriptors read the null image.");
                }

                return 0;
            }

            WriteImage(slot, view, layout);
            _imageSlots.Add(key, slot);
            if (!_slotsByView.TryGetValue(view.Handle, out var keys))
            {
                _slotsByView.Add(view.Handle, keys = []);
            }

            keys.Add(key);
            return slot;
        }
    }

    public uint GetOrCreateSamplerSlot(Sampler sampler)
    {
        if (sampler.Handle == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_samplerSlots.TryGetValue(sampler.Handle, out var existing))
            {
                return existing;
            }

            if (_nextSamplerSlot + 1 >= _samplerCapacity)
            {
                if (!_samplersFullReported)
                {
                    _samplersFullReported = true;
                    Console.Error.WriteLine($"[GPU][WARN] The runtime sampler heap is full: capacity={_samplerCapacity}. Further descriptors use the default sampler.");
                }

                return 0;
            }

            var slot = ++_nextSamplerSlot;
            WriteSampler(slot, sampler);
            _samplerSlots.Add(sampler.Handle, slot);
            return slot;
        }
    }

    // The views leave the cache. Their slots stop naming them at once; each is overwritten with the null
    // descriptor and reused once the commands recorded so far have completed, since one of them may still read it.
    public void InvalidateViews(IReadOnlyList<ImageView> views)
    {
        List<uint>? retired = null;
        lock (_gate)
        {
            foreach (var view in views)
            {
                if (!_slotsByView.Remove(view.Handle, out var keys))
                {
                    continue;
                }

                foreach (var key in keys)
                {
                    if (_imageSlots.Remove(key, out var slot))
                    {
                        (retired ??= []).Add(slot);
                    }
                }
            }
        }

        if (retired is null)
        {
            return;
        }

        void Release()
        {
            lock (_gate)
            {
                foreach (var slot in retired)
                {
                    WriteImage(slot, default, ImageLayout.Undefined);
                    _freeImageSlots.Push(slot);
                }
            }
        }

        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(Release);
        }
        else
        {
            Release();
        }
    }

    private void WriteImage(uint slot, ImageView view, ImageLayout layout)
    {
        var info = new DescriptorImageInfo { ImageView = view, ImageLayout = layout };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = RuntimeDescriptorTable.HeapSampledImageBinding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = &info,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
    }

    private void WriteSampler(uint slot, Sampler sampler)
    {
        var info = new DescriptorImageInfo { Sampler = sampler };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = RuntimeDescriptorTable.HeapSamplerBinding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = &info,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        if (_pool.Handle != 0)
        {
            _device.Vk.DestroyDescriptorPool(_device.Device, _pool, null);
        }

        if (_layout.Handle != 0)
        {
            _device.Vk.DestroyDescriptorSetLayout(_device.Device, _layout, null);
        }

        lock (_gate)
        {
            _imageSlots.Clear();
            _slotsByView.Clear();
        }
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed: result={result}.");
        }
    }
}
