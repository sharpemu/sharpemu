// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Turns the GPU fault bitmap into buffers: a compute pass lists faulted pages per area.
public sealed unsafe class BdaFaultProcessor : IDisposable
{
    private const int MaxPendingFaults = 8;
    private const uint MaxPageFaults = 1024;
    private const ulong PageFaultAreaSize = MaxPageFaults * sizeof(ulong);

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly GuestBufferCache _cache;
    private readonly SpanSet _faultRanges = new();
    private readonly ulong _pageSize;
    private readonly ulong _pageCount;
    private readonly ulong _faultBufferSize;
    private readonly GpuBuffer _faultBuffer;
    private readonly GpuBuffer _downloadBuffer;
    private readonly GpuBuffer? _traceDownloadBuffer;
    private bool _traceInitialized;
    private readonly ulong[] _faultAreas = new ulong[MaxPendingFaults];
    private readonly DescriptorSet[] _sets = new DescriptorSet[MaxPendingFaults];
    private readonly DescriptorSetLayout _layout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorPool _pool;
    private uint _currentArea;

    public BdaFaultProcessor(GpuDeviceInfo device, SubmissionScheduler scheduler, GuestBufferCache cache, int pageBits, ulong pageCount)
    {
        _device = device;
        _scheduler = scheduler;
        _cache = cache;
        _pageSize = 1UL << pageBits;
        _pageCount = pageCount;
        _faultBufferSize = pageCount / 8;
        _faultBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags,
            _faultBufferSize + (GuestGpuMemoryHook.TraceEnabled ? 32UL : 0UL));
        if (GuestGpuMemoryHook.TraceEnabled)
            _traceDownloadBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, MaxPendingFaults * 256);
        _downloadBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, MaxPendingFaults * PageFaultAreaSize);

        var vk = device.Vk;
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint index = 0; index < 2; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        RequireSuccess(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _layout), "Fault-buffer descriptor layout creation");

        var spirv = SpirvFixedShaders.CreateFaultBufferProcess();
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            RequireSuccess(vk.CreateShaderModule(device.Device, &moduleInfo, null, out module), "Fault-buffer shader module creation");
        }

        var setLayout = _layout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        RequireSuccess(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out _pipelineLayout), "Fault-buffer pipeline layout creation");

        ReadOnlySpan<byte> entryPoint = "main\0"u8;
        Result created;
        fixed (byte* entry = entryPoint)
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = _pipelineLayout,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
            };
            created = vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out _pipeline);
        }

        vk.DestroyShaderModule(device.Device, module, null);
        RequireSuccess(created, "Fault-buffer pipeline creation");

        // One pre-written set per area replaces push descriptors; the bindings never change.
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 * MaxPendingFaults };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MaxPendingFaults,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        RequireSuccess(vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool), "Fault-buffer descriptor pool creation");
        var layouts = stackalloc DescriptorSetLayout[MaxPendingFaults];
        for (var index = 0; index < MaxPendingFaults; index++)
        {
            layouts[index] = _layout;
        }

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = MaxPendingFaults,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _sets)
        {
            RequireSuccess(vk.AllocateDescriptorSets(device.Device, &allocateInfo, sets), "Fault-buffer descriptor set allocation");
        }

        var infos = stackalloc DescriptorBufferInfo[2 * MaxPendingFaults];
        var writes = stackalloc WriteDescriptorSet[2 * MaxPendingFaults];
        for (var area = 0; area < MaxPendingFaults; area++)
        {
            infos[2 * area] = new DescriptorBufferInfo(_faultBuffer.Handle, 0, _faultBufferSize);
            infos[2 * area + 1] = new DescriptorBufferInfo(_downloadBuffer.Handle, (ulong)area * PageFaultAreaSize, PageFaultAreaSize);
            for (uint binding = 0; binding < 2; binding++)
            {
                writes[2 * area + binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[area],
                    DstBinding = binding,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = infos + 2 * area + binding,
                };
            }
        }

        vk.UpdateDescriptorSets(device.Device, 2 * MaxPendingFaults, writes, 0, null);
    }

    public GpuBuffer FaultBuffer
    {
        get
        {
            if (_traceDownloadBuffer is not null && !_traceInitialized)
            {
                _faultBuffer.Fill(_faultBufferSize, 32, 0);
                _traceInitialized = true;
            }
            return _faultBuffer;
        }
    }

    public void ProcessFaultBuffer()
    {
        var waitTick = _faultAreas[_currentArea];
        if (waitTick != 0)
        {
            _scheduler.Wait(waitTick);
            _scheduler.RunCompletedOperations();
        }

        var offset = _currentArea * PageFaultAreaSize;
        _downloadBuffer.Mapped.Slice((int)offset, (int)PageFaultAreaSize).Clear();
        _downloadBuffer.Flush(offset, PageFaultAreaSize);

        var preBarrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _faultBuffer.Handle,
            Offset = 0,
            Size = _faultBufferSize,
        };
        var postBarrier = preBarrier;
        postBarrier.DstAccessMask = AccessFlags.ShaderWriteBit;

        _scheduler.EndRendering();
        var vk = _device.Vk;
        var command = new CommandBuffer(_scheduler.Current.Handle);
        vk.CmdPipelineBarrier(
            command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit, DependencyFlags.ByRegionBit,
            0, null, 1, &preBarrier, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
        var set = _sets[_currentArea];
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        var threads = _pageCount / 32;
        vk.CmdDispatch(command, (uint)((threads + 63) / 64), 1, 1);
        vk.CmdPipelineBarrier(
            command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
            0, null, 1, &postBarrier, 0, null);

        var area = _currentArea;
        var scanTick = _scheduler.CurrentTick;
        if (_traceDownloadBuffer is not null)
        {
            _traceDownloadBuffer.CopyFrom(_scheduler.Current, _faultBuffer, _faultBufferSize, area * 256UL, 32,
                destinationAfter: AccessFlags.HostReadBit);
            _faultBuffer.Fill(_faultBufferSize, 32, 0);
        }
        _scheduler.QueueCompletionAction(() =>
        {
            if (_traceDownloadBuffer is not null)
            {
                _traceDownloadBuffer.Invalidate(area * 256UL, 32);
                var record = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256, 32));
                if (record[0] != 0)
                    Console.Error.WriteLine($"[GPU][DEVICE_ADDRESS_FAULT] scan_tick={scanTick} hash=0x{((ulong)record[2] << 32 | record[1]):X16} pc=0x{record[3]:X} address=0x{((ulong)record[5] << 32 | record[4]):X16} stage={record[6]}");
            }
            _downloadBuffer.Invalidate(offset, PageFaultAreaSize);
            _faultRanges.Clear();
            var faults = MemoryMarshal.Cast<byte, ulong>(_downloadBuffer.Mapped.Slice((int)offset, (int)PageFaultAreaSize));
            var count = Math.Min((uint)faults[0], MaxPageFaults - 1);
            for (var index = 1; index <= count; index++)
            {
                _faultRanges.Add(faults[index], _pageSize);
                GuestGpuMemoryHook.SelectDeviceFaultTracePage(faults[index]);
                if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Traces(faults[index], _pageSize))
                    SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(faults[index], _pageSize,
                        $"device-address-fault scan_tick={scanTick} callback_tick={_scheduler.CurrentTick} registered={_cache.IsRegionRegistered(faults[index], _pageSize)} reported_count={(uint)faults[0]} retained_count={count}");
                Console.Error.WriteLine($"[GPU][INFO] Accessed non-GPU cached memory at 0x{faults[index]:X16}");
            }

            _faultRanges.ForEach((start, size) =>
            {
                if (size > uint.MaxValue)
                {
                    throw SubmissionScheduler.Fatal("The fault range exceeds the buffer.");
                }

                _ = _cache.FindBuffer(start, size);
                if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Traces(start, size))
                    SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(start, size,
                        $"device-address-fault-prepared scan_tick={scanTick} submission_tick={_scheduler.CurrentTick} registered={_cache.IsRegionRegistered(start, size)}");
            });
            _faultAreas[area] = 0;
        });

        _faultAreas[_currentArea++] = _scheduler.CurrentTick;
        _currentArea %= MaxPendingFaults;
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        vk.DestroyPipeline(_device.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_device.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_device.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Device, _layout, null);
        _downloadBuffer.Dispose();
        _traceDownloadBuffer?.Dispose();
        _faultBuffer.Dispose();
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}
