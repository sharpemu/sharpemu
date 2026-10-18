// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly PendingGuestPublications _guestPublications = new();

        public bool HasPendingPublications(int queueId) => _guestPublications.HasQueue(queueId);

        private static readonly bool OcclusionGpuDumps = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_OCCLUSION_GPU_DUMPS"), "0", StringComparison.Ordinal);

        private VulkanOcclusionQueries OcclusionQueries()
        {
            if (_occlusionQueries is { } existing) return existing;
            var queries = new VulkanOcclusionQueries(_vk, _device);
            try { if (OcclusionGpuDumps && _deviceInfo is not null) queries.EnableGpuResults(_deviceInfo, _scheduler); }
            catch { queries.Dispose(); throw; }
            return _occlusionQueries = queries;
        }

        private void CompleteOverlappingPublications(ulong address, ulong size, bool includeLabels = true, bool skipGpuSlots = false)
        {
            if (OnCommandThread)
            {
                if (!_guestPublications.HasOverlap(address, size, includeLabels, skipGpuSlots)) return;
                _commandThread!.Run(() => CompleteOverlappingPublications(address, size, includeLabels, skipGpuSlots));
                return;
            }
            var tick = _guestPublications.OverlapTick(address, size, includeLabels, skipGpuSlots);
            if (tick == 0) return;
            _scheduler.Wait(tick);
            _scheduler.RunCompletedOperations();
        }
        public bool TryReadOcclusionCounter(int queueId, out ulong value)
        {
            if (OnCommandThread)
            {
                var result = _commandThread!.Run(() =>
                {
                    var supported = TryReadOcclusionCounter(queueId, out var count);
                    return (Supported: supported, Count: count);
                });
                value = result.Count;
                return result.Supported;
            }

            value = 0;
            if (!_supportsPreciseOcclusion) return false;

            // Complete earlier GPU writes before the interpreter publishes the ready bit.
            FlushAndWait();
            value = OcclusionQueries().Read(queueId);
            return true;
        }

        public bool ResetOcclusionCounter(int queueId)
        {
            if (!_supportsPreciseOcclusion) return false;
            if (OnCommandThread)
            {
                _commandThread!.Post(() => ResetOcclusionCounter(queueId));
                return true;
            }

            DrainDraws();
            EndRendering();
            var queries = OcclusionQueries();
            if (queries.HasGpuResults) _ = BeginBatchedGuestCommands();
            queries.Reset(queueId);
            return true;
        }

        public bool DumpOcclusionCounter(int queueId, ulong address, uint instanceMask, uint strideBytes)
        {
            if (!_supportsPreciseOcclusion) return false;
            if (instanceMask == 0) return true;
            if (_guestPublications.Count >= 1024)
            {
                if (OnCommandThread) _commandThread!.Run(() => _scheduler.Finish());
                else _scheduler.Finish();
            }
            var size = (ulong)System.Numerics.BitOperations.Log2(instanceMask) * strideBytes + sizeof(ulong);
            var publication = _guestPublications.Add(queueId, address, size);
            try
            {
                if (OnCommandThread)
                    _commandThread!.Post(() => RecordOcclusionDump(publication, address, instanceMask, strideBytes));
                else
                    RecordOcclusionDump(publication, address, instanceMask, strideBytes);
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
            return true;
        }

        private void RecordOcclusionDump(PendingGuestPublications.Entry publication, ulong address,
            uint instanceMask, uint strideBytes)
        {
            try
            {
                DrainDraws();
                EndRendering();
                var queries = OcclusionQueries();
                _ = CurrentRecording();
                if (queries.HasGpuResults)
                {
                    _ = BeginBatchedGuestCommands();
                    publication.DumpMask = instanceMask;
                    publication.DumpStride = strideBytes;
                    publication.GpuSlot = (long)queries.RecordGpuDump(publication.Queue);
                }
                ulong result = 0;
                var ready = false;
                queries.Dump(publication.Queue, count => { result = count; ready = true; });
                var needsSubmit = !ready && queries.HasUnsubmitted;
                if (needsSubmit)
                {
                    _ = BeginBatchedGuestCommands();
                    queries.Submit(_scheduler);
                }
                // Ready results also enter the FIFO so a reset cannot overtake an older dump.
                var tick = _scheduler.QueueMemoryPublication(() => PublishGuestEntry(publication, () =>
                {
                    if (!ready) throw SubmissionScheduler.Fatal("The occlusion result is not complete.");
                    PublishOcclusionDump(address, instanceMask, strideBytes, result);
                }), ready ? 0 : queries.SubmittedTick(publication.Queue));
                _guestPublications.Register(publication, tick);
                // The slice and render-loop flushes submit the open batch, and a CPU wait submits its tick.
                // A flush here would split the slice into two submissions.
                if (needsSubmit || tick == _scheduler.CurrentTick) _ = BeginBatchedGuestCommands();
                if (_scheduler.IsTickKnownComplete(tick)) _scheduler.RunCompletedOperations();
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
        }

        private void PublishOcclusionDump(ulong address, uint instanceMask, uint strideBytes, ulong count)
        {
            const ulong readyBit = 1UL << 63;
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            for (var block = 0; block < 24; block++)
            {
                if ((instanceMask & (1u << block)) == 0) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, block == 0 ? readyBit | (count & (readyBit - 1)) : readyBit);
                var destination = address + (ulong)block * strideBytes;
                if (!_guestMemory.TryWrite(destination, bytes))
                    throw SubmissionScheduler.Fatal($"Cannot publish an occlusion result: address=0x{destination:X16}.");
            }
        }

        // A GPU read after a dump gets the GPU-computed value in stream order; guest memory gets it later.
        private void OverlayPendingDumps(GpuBuffer target, ulong targetOffset, ulong address, ulong size)
        {
            var dumps = _guestPublications.PendingDumps;
            if (dumps.Length == 0 || _occlusionQueries is not { HasGpuResults: true } queries) return;
            Span<BufferCopy> regions = stackalloc BufferCopy[24];
            foreach (var dump in dumps)
            {
                if (dump.GpuSlot < 0 || address >= dump.Address + dump.Size || dump.Address >= address + size) continue;
                var count = 0;
                for (var block = 0; block < 24; block++)
                {
                    if ((dump.DumpMask & (1u << block)) == 0) continue;
                    var blockAddress = dump.Address + (ulong)block * dump.DumpStride;
                    var first = Math.Max(blockAddress, address);
                    var end = Math.Min(blockAddress + sizeof(ulong), address + size);
                    if (first >= end) continue;
                    var source = (block == 0 ? (ulong)dump.GpuSlot : VulkanOcclusionQueries.ReadySlotOffset) + (first - blockAddress);
                    regions[count++] = new BufferCopy(source, targetOffset + (first - address), end - first);
                }
                if (count != 0) RecordDumpOverlay(queries.DumpValues, target, regions[..count]);
            }
        }

        private void RecordDumpOverlay(GpuBuffer source, GpuBuffer target, ReadOnlySpan<BufferCopy> regions)
        {
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk, native, PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
            fixed (BufferCopy* copies = regions)
                _vk.CmdCopyBuffer(native, source.Handle, target.Handle, (uint)regions.Length, copies);
            foreach (var region in regions) target.NoteGpuWrite(region.DstOffset, region.Size);
            VulkanSynchronization.PipelineBarrier(_vk, native, PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
        }

        private void SetOcclusionCounting(uint control)
        {
            const uint incrementDisabled = 1u;
            const uint counterZeroEnabled = 1u << 8;
            var enabled = (control & (incrementDisabled | counterZeroEnabled)) == counterZeroEnabled;
            if (enabled != _occlusionCounting)
                EndRendering();
            if (enabled && _supportsPreciseOcclusion)
                _ = OcclusionQueries();
            _occlusionCounting = enabled;
        }
    }
}
