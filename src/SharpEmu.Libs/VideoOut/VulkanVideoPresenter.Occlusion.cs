// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // Guest occlusion queries are a pair of PIXEL_PIPE_STAT_DUMP events into one 256-byte block:
        // the begin dump writes each depth block's sample counter to block + 16 * db, the end dump to
        // block + 8 + 16 * db, and the guest sums end - begin over the depth blocks once every slot
        // carries the ready bit. The host counts the draws in between with Vulkan occlusion queries.
        // A query is split at every rendering scope boundary (Vulkan queries cannot span scopes), and
        // segment k is published as depth block k's count, so the guest's own sum adds the segments.
        // Publishing is recorded after the scope that ended the query, so it never splits a scope.
        private const int OcclusionBlockBytes = OcclusionResultBlock.Bytes;
        private const int OcclusionDepthBlocks = OcclusionResultBlock.DepthBlocks;
        private const uint OcclusionPoolSize = 4096;

        private bool _supportsPreciseOcclusion;
        private bool _supportsHostQueryReset;
        private QueryPool _occlusionPool;
        private readonly Stack<uint> _freeOcclusionQueries = new();
        private OcclusionQuery? _openOcclusion;
        private uint? _activeOcclusionSegment;
        private readonly List<OcclusionQuery> _pendingOcclusionResults = new();

        private sealed class OcclusionQuery(ulong block)
        {
            public ulong Block { get; } = block;

            public List<uint> Segments { get; } = new();

            // More scopes than depth blocks, or no free query: publish a visible result.
            public bool Conservative { get; set; }
        }

        // Opt-in while an intermittent false "occluded" result is open: in Silent Hill: The Short
        // Message the title-menu character is sometimes culled for seconds at a time. Without it the
        // interpreter keeps publishing a conservative visible result.
        private static readonly bool HostOcclusionQueries =
            Environment.GetEnvironmentVariable("SHARPEMU_HOST_OCCLUSION_QUERIES") == "1";

        // False when the device cannot count samples; the interpreter then publishes a visible result.
        public bool TryRecordOcclusionCounterDump(ulong address)
        {
            if (!HostOcclusionQueries || !_supportsPreciseOcclusion || !_supportsHostQueryReset || (address & 7) != 0)
            {
                return false;
            }

            switch (address & 0xF)
            {
                case 0:
                    BeginOcclusionQuery(address);
                    return true;
                case 8:
                    EndOcclusionQuery(address - 8);
                    return true;
                default:
                    return false;
            }
        }

        private void EnsureOcclusionPool()
        {
            if (_occlusionPool.Handle != 0)
            {
                return;
            }

            var create = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Occlusion,
                QueryCount = OcclusionPoolSize,
            };
            Check(_vk.CreateQueryPool(_device, &create, null, out _occlusionPool), "vkCreateQueryPool(occlusion)");
            _vk.ResetQueryPool(_device, _occlusionPool, 0, OcclusionPoolSize);
            for (var index = OcclusionPoolSize; index > 0; index--)
            {
                _freeOcclusionQueries.Push(index - 1);
            }
        }

        private void BeginOcclusionQuery(ulong block)
        {
            EnsureOcclusionPool();
            if (_openOcclusion is { } previous)
            {
                // A begin without an end: close the previous query so its slots still become ready.
                EndOcclusionQuery(previous.Block);
            }

            _openOcclusion = new OcclusionQuery(block);
            if (_renderingActive)
            {
                BeginOcclusionSegment(new CommandBuffer(_scheduler.Current.Handle));
            }
        }

        private void EndOcclusionQuery(ulong block)
        {
            EnsureOcclusionPool();
            var query = _openOcclusion;
            if (query is null || query.Block != block)
            {
                // An end without its begin: there is nothing to count, so report the block visible.
                query = new OcclusionQuery(block) { Conservative = true };
            }
            else
            {
                EndOcclusionSegment(new CommandBuffer(_scheduler.Current.Handle));
                _openOcclusion = null;
            }

            _pendingOcclusionResults.Add(query);
            if (!_renderingActive)
            {
                PublishOcclusionResults(BeginBatchedGuestCommands());
            }
        }

        // Called right after a rendering scope begins.
        private void BeginOcclusionSegment(CommandBuffer command)
        {
            if (_openOcclusion is not { } query || _activeOcclusionSegment is not null)
            {
                return;
            }

            if (query.Segments.Count >= OcclusionDepthBlocks || !_freeOcclusionQueries.TryPop(out var index))
            {
                query.Conservative = true;
                return;
            }

            query.Segments.Add(index);
            _vk.CmdBeginQuery(command, _occlusionPool, index, QueryControlFlags.PreciseBit);
            _activeOcclusionSegment = index;
        }

        // Called right before a rendering scope ends.
        private void EndOcclusionSegment(CommandBuffer command)
        {
            if (_activeOcclusionSegment is not { } index)
            {
                return;
            }

            _vk.CmdEndQuery(command, _occlusionPool, index);
            _activeOcclusionSegment = null;
        }

        // Records the results of every ended query; must run outside a scope. The counts land in the
        // download ring, and once the GPU retires them the 256-byte blocks are written to guest
        // memory on this worker, the way a command-processor WRITE_DATA would, so the buffer cache
        // refreshes any GPU copy itself and the blocks never become GPU-owned memory.
        private void PublishOcclusionResults(CommandBuffer command)
        {
            if (_pendingOcclusionResults.Count == 0)
            {
                return;
            }

            var queries = _pendingOcclusionResults.ToArray();
            _pendingOcclusionResults.Clear();
            var bytes = (ulong)queries.Length * OcclusionDepthBlocks * sizeof(ulong);
            var ring = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Download);
            GpuBuffer download = ring;
            // This can run inside submission's EndRendering hook. A ring wrap must not
            // recursively submit that command buffer, nor leave us recording into a
            // submitted handle. Overflow storage survives until its completion callback.
            var overflow = !ring.TryMap(bytes, out var offset, sizeof(ulong), allowWait: false);
            if (overflow)
            {
                download = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download,
                    0, BufferUsageFlags.TransferDstBit, bytes);
                offset = 0;
            }
            else
            {
                ring.Commit();
            }
            for (var index = 0; index < queries.Length; index++)
            {
                var segments = queries[index].Segments;
                for (var segment = 0; segment < segments.Count; segment++)
                {
                    _vk.CmdCopyQueryPoolResults(command, _occlusionPool, segments[segment], 1, download.Handle,
                        offset + ((ulong)index * OcclusionDepthBlocks + (ulong)segment) * sizeof(ulong), sizeof(ulong),
                        QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
                }
            }

            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
            foreach (var query in queries)
            {
                foreach (var segment in query.Segments)
                {
                    // Query commands execute in order, so the reset follows the copy above.
                    _vk.CmdResetQueryPool(command, _occlusionPool, segment, 1);
                    _freeOcclusionQueries.Push(segment);
                }
            }

            _scheduler.QueuePriorityCompletionAction(() =>
            {
                try
                {
                    download.Invalidate(offset, bytes);
                    var counts = MemoryMarshal.Cast<byte, ulong>(download.Mapped.Slice((int)offset, (int)bytes));
                    for (var index = 0; index < queries.Length; index++)
                    {
                        _occlusionWrites.Enqueue((queries[index].Block, BuildOcclusionBlock(queries[index],
                            counts.Slice(index * OcclusionDepthBlocks, OcclusionDepthBlocks))));
                    }
                }
                finally
                {
                    if (overflow) download.Dispose();
                }
            });
        }

        private static byte[] BuildOcclusionBlock(OcclusionQuery query, ReadOnlySpan<ulong> counts) =>
            OcclusionResultBlock.Build(query.Segments.Count, query.Conservative, counts);

        private readonly System.Collections.Concurrent.ConcurrentQueue<(ulong Block, byte[] Data)> _occlusionWrites = new();

        // Runs on the command worker before every packet.
        private void DrainOcclusionWrites()
        {
            while (_occlusionWrites.TryDequeue(out var write))
            {
                if (!_guestMemory.TryWrite(write.Block, write.Data))
                {
                    throw SubmissionScheduler.Fatal($"The occlusion result cannot be written: block=0x{write.Block:X16}.");
                }
            }
        }

        private void DestroyOcclusionPool()
        {
            if (_occlusionPool.Handle != 0)
            {
                _vk.DestroyQueryPool(_device, _occlusionPool, null);
                _occlusionPool = default;
            }

            _freeOcclusionQueries.Clear();
            _pendingOcclusionResults.Clear();
            _occlusionWrites.Clear();
            _openOcclusion = null;
            _activeOcclusionSegment = null;
        }
    }
}
