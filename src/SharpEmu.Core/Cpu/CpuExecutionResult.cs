// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class CpuExecutionResult
{
    public OrbisGen2Result Result { get; }
    public CpuExitReason ExitReason { get; }
    public int? ExitCode { get; }
    public ulong LastGuestRip { get; }
    public ulong LastStubRip { get; }
    public int TotalInstructions { get; }
    public int ImportsHit { get; }
    public int UniqueNidsHit { get; }
    public CpuTrapInfo? TrapInfo { get; }
    public CpuMemoryFaultInfo? MemoryFaultInfo { get; }
    public CpuControlTransferInfo? ControlTransferInfo { get; }
    public CpuNotImplementedInfo? NotImplementedInfo { get; }
    public string? ImportResolutionTrace { get; }
    public string? BasicBlockTrace { get; }
    public string? MilestoneLog { get; }
    public string? RecentInstructionWindow { get; }
    public string? RecentControlTransferTrace { get; }

    public CpuExecutionResult(
        OrbisGen2Result result,
        CpuExitReason exitReason,
        int? exitCode,
        ulong lastGuestRip,
        ulong lastStubRip,
        int totalInstructions,
        int importsHit,
        int uniqueNidsHit,
        CpuTrapInfo? trapInfo = null,
        CpuMemoryFaultInfo? memoryFaultInfo = null,
        CpuControlTransferInfo? controlTransferInfo = null,
        CpuNotImplementedInfo? notImplementedInfo = null,
        string? importResolutionTrace = null,
        string? basicBlockTrace = null,
        string? milestoneLog = null,
        string? recentInstructionWindow = null,
        string? recentControlTransferTrace = null)
    {
        Result = result;
        ExitReason = exitReason;
        ExitCode = exitCode;
        LastGuestRip = lastGuestRip;
        LastStubRip = lastStubRip;
        TotalInstructions = totalInstructions;
        ImportsHit = importsHit;
        UniqueNidsHit = uniqueNidsHit;
        TrapInfo = trapInfo;
        MemoryFaultInfo = memoryFaultInfo;
        ControlTransferInfo = controlTransferInfo;
        NotImplementedInfo = notImplementedInfo;
        ImportResolutionTrace = importResolutionTrace;
        BasicBlockTrace = basicBlockTrace;
        MilestoneLog = milestoneLog;
        RecentInstructionWindow = recentInstructionWindow;
        RecentControlTransferTrace = recentControlTransferTrace;
    }

    public static CpuExecutionResult FromError(
        OrbisGen2Result result,
        CpuExitReason exitReason,
        ulong entryPoint,
        string? detail = null,
        CpuNotImplementedSource source = CpuNotImplementedSource.Unknown)
    {
        var notImpl = new CpuNotImplementedInfo(source, entryPoint, null, null, null, detail);
        return new CpuExecutionResult(
            result,
            exitReason,
            null,
            entryPoint,
            0,
            0,
            0,
            0,
            notImplementedInfo: notImpl);
    }
}
