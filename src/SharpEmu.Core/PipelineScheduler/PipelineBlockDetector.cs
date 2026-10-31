// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using System;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Runtime pipeline stall & deadlock detection module
/// Detects long-time blocking, infinite spin loops, cross-thread resource deadlock during emulation
/// </summary>
public sealed class PipelineBlockDetector
{
    private readonly ulong _stallCycleThreshold;

    /// <summary>
    /// Create block detector with custom stall cycle limit
    /// </summary>
    /// <param name="stallCycleThreshold">Max continuous cycles without state change to mark as blocked</param>
    public PipelineBlockDetector(ulong stallCycleThreshold = 0x800000)
    {
        _stallCycleThreshold = stallCycleThreshold;
    }

    /// <summary>
    /// Check if current CPU execution enters stall/block state
    /// </summary>
    /// <param name="currentCycleCount">Continuous unchanged execution cycles</param>
    /// <returns>True if pipeline stalls beyond threshold</returns>
    public bool IsPipelineStalled(ulong currentCycleCount)
    {
        return currentCycleCount >= _stallCycleThreshold;
    }

    /// <summary>
    /// Analyze CPU context to judge deadlock risk
    /// Triggered when multiple pipelines hold exclusive resource locks simultaneously
    /// </summary>
    /// <param name="context">Target CPU runtime context</param>
    /// <param name="heldResourceLockCount">Count of exclusive locks occupied by this pipeline</param>
    /// <returns>Detected deadlock risk level</returns>
    public PipelineGlobalTypes.PipelineState DetectDeadlockRisk(CpuContext context, uint heldResourceLockCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (heldResourceLockCount >= 2)
        {
            return PipelineGlobalTypes.PipelineState.Deadlock;
        }
        return PipelineGlobalTypes.PipelineState.Running;
    }

    /// <summary>
    /// Generate diagnostic log string for blocked pipeline
    /// Matches runtime unified log output format
    /// </summary>
    /// <param name="pipelineId">Target pipeline unique ID</param>
    /// <param name="stallCycles">Total stall cycles counted</param>
    /// <returns>Formatted diagnostic message</returns>
    public string GetBlockDiagnosticLog(uint pipelineId, ulong stallCycles)
    {
        return $"[RUNTIME][PIPELINE-{pipelineId}] Pipeline stalled for {stallCycles:X16} cycles, possible resource contention";
    }
}