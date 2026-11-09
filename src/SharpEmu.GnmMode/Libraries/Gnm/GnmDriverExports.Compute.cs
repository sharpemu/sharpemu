// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.GnmMode.Gpu;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    private const uint ComputePipeCount = 7;
    private const uint QueuesPerPipe = 8;
    private static readonly ConditionalWeakTable<ICpuMemory, ComputeMappings> _computeMappings = new();

    private sealed class ComputeMappings
    {
        public object Gate { get; } = new();
        public ComputeQueue?[] Queues { get; } = new ComputeQueue[ComputePipeCount * QueuesPerPipe];
    }

    private sealed class ComputeQueue(uint pipe, uint queue, ulong ring, uint dwords, ulong readPointer, uint owner)
    {
        public object Gate { get; } = new();
        public uint Pipe { get; } = pipe;
        public uint Queue { get; } = queue;
        public ulong Ring { get; } = ring;
        public uint Dwords { get; } = dwords;
        public ulong ReadPointer { get; } = readPointer;
        public uint Owner { get; } = owner;
        public uint WriteOffset { get; set; }
    }

    public static int MapComputeQueue(CpuContext context)
    {
        var pipe = (uint)context[CpuRegister.Rdi];
        var queue = (uint)context[CpuRegister.Rsi];
        var ring = context[CpuRegister.Rdx];
        var dwords = (uint)context[CpuRegister.Rcx];
        var readPointer = context[CpuRegister.R8];
        if (pipe >= ComputePipeCount)
            return context.SetReturn(unchecked((int)0x80D17000));
        if (queue >= QueuesPerPipe)
            return context.SetReturn(unchecked((int)0x80D17001));
        if (ring == 0 || (ring & 255) != 0)
            return context.SetReturn(unchecked((int)0x80D17003));
        if (!BitOperations.IsPow2(dwords))
            return context.SetReturn(unchecked((int)0x80D17002));
        if (readPointer == 0 || (readPointer & 3) != 0)
            return context.SetReturn(unchecked((int)0x80D17004));
        var root = UnwrapMemory(context.Memory);
        if (!root.CanRead(ring, (ulong)dwords * sizeof(uint)))
            return context.SetReturn(unchecked((int)0x80D17003));
        var mappings = _computeMappings.GetValue(root, static _ => new ComputeMappings());
        lock (mappings.Gate)
        {
            var slot = Array.FindIndex(mappings.Queues, static value => value is null);
            if (slot < 0 || mappings.Queues.Any(value => value is not null && value.Pipe == pipe && value.Queue == queue))
                return context.SetReturn(unchecked((int)0x80D170FF));
            if (!context.TryWriteUInt32(readPointer, 0))
                return context.SetReturn(unchecked((int)0x80D17004));
            mappings.Queues[slot] = new ComputeQueue(pipe, queue, ring, dwords, readPointer,
                (uint)GpuCommandInterpreter.ComputeQueueBase + pipe * QueuesPerPipe + queue);
            return context.SetReturn(slot + 1);
        }
    }

    public static void ResetComputeMappings(ICpuMemory memory) => _computeMappings.Remove(UnwrapMemory(memory));

    public static int DingDong(CpuContext context)
    {
        var id = (uint)context[CpuRegister.Rdi];
        var offset = (uint)context[CpuRegister.Rsi];
        var memory = UnwrapMemory(context.Memory);
        if (!_computeMappings.TryGetValue(memory, out var mappings) || id == 0 || id > mappings.Queues.Length)
            return context.SetReturn(unchecked((int)0x80D17001));
        ComputeQueue? queue;
        lock (mappings.Gate)
            queue = mappings.Queues[id - 1];
        if (queue is null)
            return context.SetReturn(unchecked((int)0x80D17001));
        lock (queue.Gate)
        {
            if (offset >= queue.Dwords)
                return context.SetReturn(unchecked((int)0x80D17002));
            if (offset == queue.WriteOffset)
                return context.SetReturn(0);
            int Submit(uint end)
            {
                var start = queue.WriteOffset;
                var complete = GnmCommandPackets.CompleteRingWords(memory, queue.Ring + (ulong)start * sizeof(uint), end - start);
                if (complete == 0)
                    return context.SetReturn(0);
                var stop = start + complete;
                var next = stop == queue.Dwords ? 0 : stop;
                var result = AgcExports.SubmitComputeCommands(context, queue.Owner,
                    queue.Ring + (ulong)start * sizeof(uint), complete, () =>
                    {
                        Span<byte> value = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(value, next);
                        if (!memory.TryWrite(queue.ReadPointer, value))
                            throw new InvalidOperationException($"GNM compute read pointer is unmapped: 0x{queue.ReadPointer:X}.");
                    }, GnmCommandPackets.Handlers);
                if (result == 0)
                    queue.WriteOffset = next;
                return result;
            }
            if (offset < queue.WriteOffset)
            {
                var result = Submit(queue.Dwords);
                if (result != 0 || offset == 0 || queue.WriteOffset != 0)
                    return result;
            }
            return Submit(offset);
        }
    }

    private static ICpuMemory UnwrapMemory(ICpuMemory memory)
    {
        for (var depth = 0; depth < 4 && memory is ICpuMemoryWrapper wrapper && !ReferenceEquals(memory, wrapper.Inner); depth++)
            memory = wrapper.Inner;
        return memory;
    }
}
