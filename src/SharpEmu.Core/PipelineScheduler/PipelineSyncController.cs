// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Fiber;
using System;
using System.Threading;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Cross-pipeline synchronization controller
/// Manage fiber mutex, shared resource lock & thread wake/suspend logic to avoid race condition blocks
/// </summary>
public sealed class PipelineSyncController : IDisposable
{
    private readonly object _globalResourceLock;
    private readonly ManualResetEventSlim _pipelineWakeEvent;
    private bool _disposed;

    public PipelineSyncController()
    {
        _globalResourceLock = new object();
        _pipelineWakeEvent = new ManualResetEventSlim(false);
    }

    /// <summary>
    /// Acquire exclusive shared resource lock for target pipeline
    /// </summary>
    /// <param name="timeoutMs">Max wait time before mark as blocked</param>
    /// <returns>True if lock acquired successfully</returns>
    public bool TryAcquireGlobalLock(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipelineSyncController));
        return Monitor.TryEnter(_globalResourceLock, timeoutMs);
    }

    /// <summary>
    /// Release occupied global shared resource lock
    /// </summary>
    public void ReleaseGlobalLock()
    {
        if (Monitor.IsEntered(_globalResourceLock))
        {
            Monitor.Exit(_globalResourceLock);
        }
    }

    /// <summary>
    /// Suspend current pipeline fiber and wait for wake signal
    /// </summary>
    /// <param name="waitTimeoutMs">Max suspend duration limit</param>
    public void SuspendPipelineWait(int waitTimeoutMs)
    {
        _pipelineWakeEvent.Wait(waitTimeoutMs);
        _pipelineWakeEvent.Reset();
    }

    /// <summary>
    /// Send wake signal to all suspended waiting pipelines
    /// </summary>
    public void WakeAllWaitingPipelines()
    {
        _pipelineWakeEvent.Set();
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
            _pipelineWakeEvent.Dispose();
        }
        _disposed = true;
    }
}