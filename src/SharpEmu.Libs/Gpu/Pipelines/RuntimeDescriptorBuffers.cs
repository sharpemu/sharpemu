// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The two storage buffers one shader binds for its runtime descriptors: the table the host built, which the shader
// only reads, and the buffer the shader records its misses in with atomics.
//
// The miss buffer lives in device memory. A shader that does atomics on host-cached system memory is a device loss on
// NVIDIA the moment a fragment shader does it for millions of pixels, so the shader writes a device-local buffer and
// the host copies it into a readback buffer at the end of the tick (RecordMissCopies, called just before the submit
// that ends the tick), clears it on the device for its next use, and reads the copy when the tick completes.
internal sealed class RuntimeDescriptorBuffers : IDisposable
{
    private delegate void MissReader(ReadOnlySpan<uint> words);

    private sealed class MissSlot(GpuBuffer device, GpuBuffer publish)
    {
        public GpuBuffer Device { get; } = device;

        public GpuBuffer Publish { get; } = publish;
    }

    private const ulong MissBytes = RuntimeDescriptorTable.MissBufferDwords * sizeof(uint);

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly List<RuntimeDescriptorBuffers> _recordingThisTick;
    private readonly ConcurrentBag<MissSlot> _free = [];
    private GpuBuffer? _table;
    private MissSlot? _current;
    private ulong _currentTick = ulong.MaxValue;
    private MissReader? _reader;

    // recordingThisTick lists the buffers whose miss buffer the current tick has bound; the owner calls RecordMissCopies
    // on each before it submits the tick.
    public RuntimeDescriptorBuffers(GpuDeviceInfo device, SubmissionScheduler scheduler, List<RuntimeDescriptorBuffers> recordingThisTick)
    {
        _device = device;
        _scheduler = scheduler;
        _recordingThisTick = recordingThisTick;
    }

    // The table buffer of the registry's entries. A change builds a new buffer: commands already recorded keep
    // reading the one they were bound with. The old one is destroyed when the tick that is recording completes,
    // by which time every command that could have bound it has finished.
    public GpuBuffer Table<TResolved>(RuntimeDescriptorRegistry<TResolved> registry) where TResolved : class
    {
        var current = _table;
        if (current is not null && !registry.TableDirty)
        {
            return current;
        }

        var words = registry.BuildTable();
        var buffer = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.StorageBufferBit, (ulong)words.Length * sizeof(uint));
        buffer.Write(0, MemoryMarshal.AsBytes(words.AsSpan()));
        if (current is not null)
        {
            _scheduler.QueueCompletionAction(current.Dispose);
        }

        _table = buffer;
        return buffer;
    }

    // The miss buffer of the tick being recorded: every draw of the tick records into it. Cleared when handed out
    // for the first time and again after each copy, so it is zero whenever a tick starts using it.
    public GpuBuffer Misses<TResolved>(RuntimeDescriptorRegistry<TResolved> registry) where TResolved : class
    {
        var tick = _scheduler.CurrentTick;
        if (_current is { } current && _currentTick == tick)
        {
            return current.Device;
        }

        _reader ??= words => registry.ReadMisses(words);
        if (!_free.TryTake(out var slot))
        {
            slot = new MissSlot(
                new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, MissBytes),
                new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, MissBytes));
            slot.Device.Fill(0, MissBytes, 0);
        }

        _current = slot;
        _currentTick = tick;
        _recordingThisTick.Add(this);
        return slot.Device;
    }

    // Copies the miss buffer the tick bound to its readback buffer, clears it, and reads the copy once the tick completes.
    public void RecordMissCopies()
    {
        if (_current is not { } slot)
        {
            return;
        }

        _current = null;
        slot.Publish.CopyFrom(
            _scheduler.Current, slot.Device, 0, 0, MissBytes,
            AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit, AccessFlags.None,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        slot.Device.Fill(0, MissBytes, 0);
        var reader = _reader!;
        _scheduler.QueueCompletionAction(() =>
        {
            slot.Publish.Invalidate(0, MissBytes);
            reader(MemoryMarshal.Cast<byte, uint>(slot.Publish.Mapped));
            _free.Add(slot);
        });
    }

    // Only after the scheduler has finished all work.
    public void Dispose()
    {
        _table?.Dispose();
        _table = null;
        if (_current is { } current)
        {
            current.Device.Dispose();
            current.Publish.Dispose();
            _current = null;
        }

        foreach (var slot in _free)
        {
            slot.Device.Dispose();
            slot.Publish.Dispose();
        }
    }
}
