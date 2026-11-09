// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.GpuCommands;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe partial class VulkanOcclusionQueries(Vk vulkan, Device device) : IDisposable
{
    private const uint Capacity = 256;
    internal sealed class Group
    {
        public ulong Sum;
        public long Recorded;
        public long Completed;
        public ulong SubmittedTick;
        public readonly Queue<(long Needed, Action<ulong> Publish)> Dumps = new();
    }

    internal sealed class Block(QueryPool pool)
    {
        public readonly QueryPool Pool = pool;
        public readonly int[] Queues = new int[Capacity];
        public readonly Group?[] Groups = new Group?[Capacity];
        public readonly long[] TraceIds = VisibilityResultTrace.Enabled ? new long[Capacity] : [];
        public uint Count;
        public uint ReducedCount;
    }

    private readonly List<Block> _blocks = new();
    private readonly Stack<Block> _available = new();
    private readonly Lock _blockGate = new();
    private readonly List<Block> _recorded = new();
    private readonly Dictionary<int, ulong> _samples = new();
    private readonly Dictionary<int, Group> _groups = new();
    private Block? _current;
    private bool _active;
    private long _traceId;
    private int _traceDraws;
    private int _traceSelectedDraws;

    public ulong Read(int queueId) => _samples.GetValueOrDefault(queueId);

    public bool HasUnsubmitted => _recorded.Count != 0;

    internal ulong SubmittedTick(int queueId) => GroupOf(queueId).SubmittedTick;

    private Group GroupOf(int queueId)
    {
        if (!_groups.TryGetValue(queueId, out var group))
            _groups[queueId] = group = new Group();
        return group;
    }

    public void Reset(int queueId)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query must end before the counter resets.");
        ResetGpuCounter(queueId);
        _groups[queueId] = new Group();
    }

    public bool Dump(int queueId, Action<ulong> publish)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query must end before the counter is dumped.");
        var group = GroupOf(queueId);
        if (group.Completed == group.Recorded && group.Dumps.Count == 0)
        {
            publish(group.Sum);
            return true;
        }

        group.Dumps.Enqueue((group.Recorded, publish));
        return false;
    }

    public void Begin(CommandBuffer command, int queueId, uint depthCountControl = 0)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query is already active.");

        if (_current is null || _current.Count == Capacity)
        {
            _current = RentBlock(command);
            _recorded.Add(_current);
        }

        _current.Queues[_current.Count] = queueId;
        var group = GroupOf(queueId);
        group.Recorded++;
        _current.Groups[_current.Count] = group;
        vulkan.CmdBeginQuery(command, _current.Pool, _current.Count, QueryControlFlags.PreciseBit);
        _active = true;
        if (VisibilityResultTrace.Enabled)
        {
            _current.TraceIds[_current.Count] = ++_traceId;
            _traceDraws = 0;
            _traceSelectedDraws = 0;
            VisibilityResultTrace.Occlusion($"begin query={_traceId} queue={queueId} control=0x{depthCountControl:X8}");
        }
    }

    // A render worker records its queries in blocks of its own and the render thread adopts them in stream order.
    internal Block RentBlock(CommandBuffer command)
    {
        Block? block;
        lock (_blockGate)
        {
            _available.TryPop(out block);
        }

        if (block is null)
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
            block = new Block(pool);
            lock (_blockGate)
            {
                _blocks.Add(block);
            }
        }

        block.Count = 0;
        block.ReducedCount = 0;
        vulkan.CmdResetQueryPool(command, block.Pool, 0, Capacity);
        return block;
    }

    internal static bool IsFull(Block block) => block.Count == Capacity;

    internal void BeginInBlock(CommandBuffer command, Block block) =>
        vulkan.CmdBeginQuery(command, block.Pool, block.Count, QueryControlFlags.PreciseBit);

    internal void EndInBlock(CommandBuffer command, Block block) => vulkan.CmdEndQuery(command, block.Pool, block.Count++);

    internal void Adopt(Block block, int queueId)
    {
        if (block.Count == 0)
        {
            ReturnBlock(block);
            return;
        }

        var group = GroupOf(queueId);
        for (var index = 0; index < block.Count; index++)
        {
            block.Queues[index] = queueId;
            block.Groups[index] = group;
            group.Recorded++;
            if (VisibilityResultTrace.Enabled)
                block.TraceIds[index] = ++_traceId;
        }

        _recorded.Add(block);
    }

    internal void ReturnBlock(Block block)
    {
        lock (_blockGate)
        {
            _available.Push(block);
        }
    }

    public void TraceDraw(uint? indexCount = null)
    {
        if (!VisibilityResultTrace.Enabled || !_active) return;
        _traceDraws++;
        if (IndexedDrawTrace.Enabled && indexCount == IndexedDrawTrace.SelectedCount) _traceSelectedDraws++;
    }

    public void End(CommandBuffer command)
    {
        if (!_active) return;
        if (VisibilityResultTrace.Enabled)
            VisibilityResultTrace.Occlusion($"end query={_traceId} draws={_traceDraws} selectedIndexCount={IndexedDrawTrace.SelectedCount} selectedDraws={_traceSelectedDraws}");
        vulkan.CmdEndQuery(command, _current!.Pool, _current.Count++);
        _active = false;
    }

    public void Submit(SubmissionScheduler scheduler)
    {
        if (_active)
            throw SubmissionScheduler.Fatal("An occlusion query must end before submission.");

        ReduceRecordedQueries();
        foreach (var block in _recorded)
        {
            for (var index = 0; index < block.Count; index++)
                if (block.Groups[index] is { } group) group.SubmittedTick = scheduler.CurrentTick;
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
            if (block.Groups[index] is { } group)
            {
                block.Groups[index] = null;
                group.Sum = unchecked(group.Sum + results[index]);
                group.Completed++;
                while (group.Dumps.TryPeek(out var dump) && dump.Needed <= group.Completed)
                {
                    group.Dumps.Dequeue();
                    dump.Publish(group.Sum);
                }
            }
            if (VisibilityResultTrace.Enabled)
                VisibilityResultTrace.Occlusion($"complete query={block.TraceIds[index]} queue={queueId} samples={results[index]} cumulative={_samples[queueId]}");
        }
        // The timeline callback runs after all uses of this pool have completed.
        ReturnBlock(block);
    }

    public void Dispose()
    {
        DisposeGpuResults();
        foreach (var block in _blocks)
            vulkan.DestroyQueryPool(device, block.Pool, null);
        _blocks.Clear();
        _available.Clear();
        _recorded.Clear();
    }
}
