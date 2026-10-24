// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Text;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Pipeline performance profiling & unified diagnostic log collector
/// Collect stall, cycle, resource usage metrics, output data compatible with runtime debug trace storage
/// </summary>
public sealed class PipelineDebugProfiler
{
    private readonly StringBuilder _profileLogBuffer;

    public PipelineDebugProfiler()
    {
        _profileLogBuffer = new StringBuilder(4096);
    }

    /// <summary>
    /// Record pipeline runtime performance metric entry
    /// </summary>
    /// <param name="pipelineId">Target pipeline unique identifier</param>
    /// <param name="totalRunCycles">Total executed instruction cycles</param>
    /// <param name="totalStallCycles">Total stalled waiting cycles</param>
    /// <param name="blockCount">Total detected block events during task</param>
    public void RecordPerformanceMetric(uint pipelineId, ulong totalRunCycles, ulong totalStallCycles, uint blockCount)
    {
        double stallRate = totalRunCycles == 0 ? 0d : (double)totalStallCycles / totalRunCycles;
        _profileLogBuffer.AppendLine(
            $"[PIPELINE-PROFILE-{pipelineId}] TotalCycles:{totalRunCycles:X16} StallCycles:{totalStallCycles:X16} BlockEvents:{blockCount} StallRate:{stallRate:P2}"
        );
    }

    /// <summary>
    /// Export all collected profiling logs as single string
    /// Can be assigned to ISharpEmuRuntime.LastMilestoneLog
    /// </summary>
    /// <returns>Full formatted profiling log text</returns>
    public string ExportFullProfileLog()
    {
        string output = _profileLogBuffer.ToString();
        _profileLogBuffer.Clear();
        return output;
    }

    /// <summary>
    /// Clear all cached profiling log data
    /// </summary>
    public void ClearProfileBuffer()
    {
        _profileLogBuffer.Clear();
    }
}