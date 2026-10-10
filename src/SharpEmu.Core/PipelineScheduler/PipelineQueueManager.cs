// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Thread-safe multi-pipeline task queue scheduler
/// Balances task distribution between all active CPU pipelines to reduce congestion & block stalls
/// </summary>
public sealed class PipelineQueueManager : IDisposable
{
    private readonly ConcurrentQueue<PipelineGlobalTypes.PipelineTaskInfo> _globalTaskQueue;
    private readonly List<uint> _activePipelineIds;
    private readonly uint _maxConcurrentPipelineCount;
    private bool _disposed;

    /// <summary>
    /// Initialize pipeline queue balancer
    /// </summary>
    /// <param name="maxConcurrentPipelineCount">Upper limit of parallel running pipelines</param>
    public PipelineQueueManager(uint maxConcurrentPipelineCount)
    {
        _maxConcurrentPipelineCount = maxConcurrentPipelineCount;
        _globalTaskQueue = new ConcurrentQueue<PipelineGlobalTypes.PipelineTaskInfo>();
        _activePipelineIds = new List<uint>();
    }

    /// <summary>
    /// Enqueue new emulation task to global scheduling queue
    /// </summary>
    /// <param name="task">Task metadata to schedule</param>
    public void EnqueueTask(PipelineGlobalTypes.PipelineTaskInfo task)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipelineQueueManager));
        _globalTaskQueue.Enqueue(task);
    }

    /// <summary>
    /// Fetch next available task for idle pipeline
    /// </summary>
    /// <param name="outTask">Output scheduled task info</param>
    /// <returns>True if valid task fetched, false when queue empty</returns>
    public bool TryFetchNextTask(out PipelineGlobalTypes.PipelineTaskInfo outTask)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipelineQueueManager));
        return _globalTaskQueue.TryDequeue(out outTask);
    }

    /// <summary>
    /// Register new running pipeline for load balance calculation
    /// </summary>
    /// <param name="pipelineId">Unique pipeline identifier</param>
    public void RegisterActivePipeline(uint pipelineId)
    {
        if (!_activePipelineIds.Contains(pipelineId) && _activePipelineIds.Count < _maxConcurrentPipelineCount)
        {
            _activePipelineIds.Add(pipelineId);
        }
    }

    /// <summary>
    /// Unregister pipeline after task complete / suspend
    /// </summary>
    /// <param name="pipelineId">Unique pipeline identifier</param>
    public void UnregisterPipeline(uint pipelineId)
    {
        _activePipelineIds.Remove(pipelineId);
    }

    public void Dispose()
    {
        Dispose(true);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _globalTaskQueue.Clear();
            _activePipelineIds.Clear();
        }
        _disposed = true;
    }
}