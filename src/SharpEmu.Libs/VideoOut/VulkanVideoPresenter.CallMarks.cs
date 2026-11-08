// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// The call marks of programs linked with run-time call records (CallMarks): one device bitset that every such dispatch
// marks, copied to the host and cleared in the command stream before a dispatch that marks, one copy at a time. A snapshot
// that has been read stamps each bit it holds with its number, which the program cache compares with the snapshot in which
// it named a callee (ShaderCalleeTiers).
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private GpuBuffer? _callMarks;
        private GpuBuffer? _callMarksReadback;
        private readonly long[] _callMarkedAt = new long[CallMarks.Bits];
        private long _callMarkSnapshots;
        private int _callMarksCopyPending;

        bool Gpu.Pipelines.IShaderPipelineHost.CallMarksSupported => true;

        long Gpu.Pipelines.IShaderPipelineHost.CallMarkSnapshots => Volatile.Read(ref _callMarkSnapshots);

        long Gpu.Pipelines.IShaderPipelineHost.CallMarkedAt(uint bit) => Volatile.Read(ref _callMarkedAt[bit & (CallMarks.Bits - 1)]);

        internal DescriptorBufferInfo CallMarksDescriptor()
        {
            var marks = _callMarks ?? throw SubmissionScheduler.Fatal("A program marks calls before the call marks exist.");
            return new DescriptorBufferInfo { Buffer = marks.Handle, Offset = 0, Range = marks.Size };
        }

        // Before the descriptors of a dispatch that marks are written: creates the bitset, or takes the marks of the dispatches
        // recorded since the last copy and clears them.
        internal void PollCallMarks()
        {
            if (_callMarks is null)
            {
                _callMarks = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, CallMarks.Bytes);
                _callMarksReadback = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, CallMarks.Bytes);
                _ = BeginBatchedGuestCommands();
                _callMarks.Fill(0, CallMarks.Bytes, 0);
                return;
            }

            if (Volatile.Read(ref _callMarksCopyPending) != 0)
            {
                return;
            }

            var marks = _callMarks;
            var readback = _callMarksReadback!;
            Volatile.Write(ref _callMarksCopyPending, 1);
            _ = BeginBatchedGuestCommands();
            readback.CopyFrom(
                _scheduler.Current, marks, 0, 0, CallMarks.Bytes,
                AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit, AccessFlags.None,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                AccessFlags.HostReadBit);
            marks.Fill(0, CallMarks.Bytes, 0);
            _scheduler.QueueCompletionAction(() =>
            {
                if (_callMarksReadback is null)
                {
                    return;
                }

                readback.Invalidate(0, CallMarks.Bytes);
                var words = MemoryMarshal.Cast<byte, uint>(readback.Mapped[..(int)CallMarks.Bytes]);
                var snapshot = Volatile.Read(ref _callMarkSnapshots) + 1;
                for (var word = 0; word < words.Length; word++)
                {
                    for (var bits = words[word]; bits != 0; bits &= bits - 1)
                    {
                        Volatile.Write(ref _callMarkedAt[word * 32 + BitOperations.TrailingZeroCount(bits)], snapshot);
                    }
                }

                Volatile.Write(ref _callMarkSnapshots, snapshot);
                Volatile.Write(ref _callMarksCopyPending, 0);
            });
        }

        private void DestroyCallMarks()
        {
            _callMarks?.Dispose();
            _callMarksReadback?.Dispose();
            _callMarks = null;
            _callMarksReadback = null;
            Volatile.Write(ref _callMarksCopyPending, 0);
        }
    }
}
