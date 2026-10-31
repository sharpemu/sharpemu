// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Cpu;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// CPU & memory resource quota limiter for each emulation pipeline
/// Prevent single pipeline from exhausting system resources and causing global emulation stall
/// </summary>
public sealed class PipelineResourceLimiter
{
    private readonly ulong _maxMemoryAllocationBytes;
    private readonly ulong _maxSingleTaskCycleQuota;

    /// <summary>
    /// Initialize resource quota limits
    /// </summary>
    /// <param name="maxMemoryAllocationBytes">Max virtual memory a single pipeline can occupy</param>
    /// <param name="maxSingleTaskCycleQuota">Max instruction cycles per pipeline task</param>
    public PipelineResourceLimiter(ulong maxMemoryAllocationBytes, ulong maxSingleTaskCycleQuota)
    {
        _maxMemoryAllocationBytes = maxMemoryAllocationBytes;
        _maxSingleTaskCycleQuota = maxSingleTaskCycleQuota;
    }

    /// <summary>
    /// Check whether target memory allocation exceeds pipeline quota
    /// </summary>
    /// <param name="currentAllocated">Current memory occupied by pipeline</param>
    /// <param name="requestAllocSize">New memory block size to allocate</param>
    /// <returns>True if allocation request exceeds limit, need block</returns>
    public bool IsMemoryQuotaExceeded(ulong currentAllocated, ulong requestAllocSize)
    {
        return currentAllocated + requestAllocSize > _maxMemoryAllocationBytes;
    }

    /// <summary>
    /// Check if current task consumed too many CPU cycles
    /// </summary>
    /// <param name="consumedCycles">Cycles already consumed by running task</param>
    /// <returns>True if cycle quota overflowed</returns>
    public bool IsCpuCycleQuotaExceeded(ulong consumedCycles)
    {
        return consumedCycles > _maxSingleTaskCycleQuota;
    }

    /// <summary>
    /// Get standardized resource limit warning log text
    /// </summary>
    /// <param name="pipelineId">Target pipeline ID</param>
    /// <param name="isMemoryLimit">True for memory overflow, false for CPU cycle overflow</param>
    /// <returns>Unified runtime warning string</returns>
    public string GetResourceLimitWarning(uint pipelineId, bool isMemoryLimit)
    {
        if (isMemoryLimit)
        {
            return $"[RUNTIME][PIPELINE-{pipelineId}] Memory allocation hit pipeline quota limit, suspend task temporarily";
        }
        return $"[RUNTIME][PIPELINE-{pipelineId}] Task consumed over maximum CPU cycle quota, forced pause";
    }
}