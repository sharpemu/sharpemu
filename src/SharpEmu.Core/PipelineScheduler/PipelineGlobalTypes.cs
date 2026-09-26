// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using System;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Global unified type definitions for multi-pipeline scheduling system
/// Standard shared enums & structures to avoid inconsistent state types across pipeline modules
/// </summary>
public static class PipelineGlobalTypes
{
    /// <summary>
    /// Status mark of single execution pipeline
    /// </summary>
    public enum PipelineState : uint
    {
        /// <summary>Pipeline idle, waiting for new task</summary>
        Idle = 0,
        /// <summary>Processing emulation task normally</summary>
        Running = 1,
        /// <summary>Blocked by resource lock / fiber wait / memory fault</summary>
        Blocked = 2,
        /// <summary>Deadlock detected, forced suspend</summary>
        Deadlock = 3,
        /// <summary>Task execution finished</summary>
        Completed = 4
    }

    /// <summary>
    /// Unified task metadata structure for all pipeline queue items
    /// </summary>
    public readonly struct PipelineTaskInfo
    {
        /// <summary>Unique task identity number</summary>
        public ulong TaskId { get; init; }
        /// <summary>Target CPU execution context for this task</summary>
        public CpuContext TargetContext { get; init; }
        /// <summary>PS5 kernel NID of target HLE function</summary>
        public string TargetNid { get; init; }
        /// <summary>Timestamp when task enqueued</summary>
        public ulong EnqueueTimestamp { get; init; }
        /// <summary>Max allowed execution cycle count before force pause</summary>
        public ulong MaxCycleQuota { get; init; }
    }
}