// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class VulkanOcclusionQueries(Vk vulkan, Device device) : IDisposable
{
    private const uint Capacity = 256;
    private sealed class Block(QueryPool pool)
    {
        public readonly QueryPool Pool = pool;
        public readonly int[] Queues = new int[Capacity];
        public uint Count;
    }

    private readonly List<Block> _blocks = new();
    private readonly Stack<Block> _available = new();
    private readonly List<Block> _recorded = new();
    private readonly Dictionary<int, ulong> _samples = new();
    private Block? _current;
    private bool _active;

    public ulong Read(int queueId) => _samples.GetValueOrDefault(queueId);

    public void Begin(CommandBuffer command, int queueId)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query is already active.");

        if (_current is null || _current.Count == Capacity)
        {
            if (!_available.TryPop(out _current))
            {
                var info = new QueryPoolCreateInfo
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Occlusion,
                    QueryCount = Capacity,
                };
                var result = vulkan.CreateQueryPool(device, &info, null, out var pool);
                if (result != Result.Success)
                    throw SubmissionScheduler.Fatal($"Cannot create an occlusion query pool: {result}.");
                _current = new Block(pool);
                _blocks.Add(_current);
            }

            _current.Count = 0;
            _recorded.Add(_current);
            vulkan.CmdResetQueryPool(command, _current.Pool, 0, Capacity);
        }

        _current.Queues[_current.Count] = queueId;
        vulkan.CmdBeginQuery(command, _current.Pool, _current.Count, QueryControlFlags.PreciseBit);
        _active = true;
    }

    public void End(CommandBuffer command)
    {
        if (!_active) return;
        vulkan.CmdEndQuery(command, _current!.Pool, _current.Count++);
        _active = false;
    }

    public void Submit(SubmissionScheduler scheduler)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query must end before submission.");

        foreach (var block in _recorded)
        {
            var submitted = block;
            scheduler.QueueCompletionAction(() => Complete(submitted));
        }
        _recorded.Clear();
        _current = null;
    }

    private void Complete(Block block)
    {
        Span<ulong> results = stackalloc ulong[(int)block.Count];
        fixed (ulong* data = results)
        {
            var status = vulkan.GetQueryPoolResults(device, block.Pool, 0, block.Count,
                (nuint)(results.Length * sizeof(ulong)), data, sizeof(ulong), QueryResultFlags.Result64Bit);
            if (status != Result.Success)
                throw SubmissionScheduler.Fatal($"Cannot read a completed occlusion query: {status}.");
        }

        for (var index = 0; index < results.Length; index++)
        {
            var queueId = block.Queues[index];
            _samples[queueId] = unchecked(_samples.GetValueOrDefault(queueId) + results[index]);
        }
        // The timeline callback runs after all uses of this pool have completed.
        _available.Push(block);
    }

    public void Dispose()
    {
        foreach (var block in _blocks)
            vulkan.DestroyQueryPool(device, block.Pool, null);
        _blocks.Clear();
        _available.Clear();
        _recorded.Clear();
    }
}
