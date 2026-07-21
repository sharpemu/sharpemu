// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// ============================================================================
// SHARPEMU DIAGNOSTICS CONTRACTS
//
// This file contains ALL interfaces that define the contract between
// the Emulator Core (SharpEmu.Core, SharpEmu.Libs, SharpEmu.HLE) and
// the Diagnostics subsystem (SharpEmu.Diagnostics).
//
// RULES:
// - This file has ZERO dependencies on SharpEmu.Core/Libs/HLE.
// - It only uses primitive types (ulong, int, string, bool, ReadOnlySpan<byte>).
// - Core/Libs/HLE IMPLEMENT these interfaces.
// - Diagnostics CONSUMES these interfaces.
//
// This breaks the circular dependency that previously caused 100+ compile
// errors every time Core changed.
// ============================================================================

using System;

namespace SharpEmu.Diagnostics.Contracts;

#region CPU Diagnostic Source

/// <summary>
/// Provides CPU execution data to the Diagnostics subsystem.
/// Implemented by SharpEmu.Core.Cpu.Native.DirectExecutionBackend.
/// </summary>
public interface ICpuDiagnosticSource
{
    /// <summary>True if diagnostics are currently active.</summary>
    bool IsActive { get; }
    
    /// <summary>Records a CPU instruction checkpoint at an import dispatch point.</summary>
    /// <param name="rip">Return instruction pointer (where guest will resume)</param>
    /// <param name="opcode">Raw opcode bytes at RIP (up to 16 bytes)</param>
    /// <param name="registers">Register snapshot (16 GP registers, 128 bytes)</param>
    /// <param name="memoryAddress">Memory operand address (0 if none)</param>
    /// <param name="memoryAccess">0=none, 1=read, 2=write, 4=execute</param>
    /// <param name="memoryValue">Value read/written (if applicable)</param>
    void RecordInstruction(
        ulong rip,
        ReadOnlySpan<byte> opcode,
        ReadOnlySpan<byte> registers,
        ulong memoryAddress,
        int memoryAccess,
        ulong memoryValue);
    
    /// <summary>Lightweight RIP-only checkpoint (no opcode/register capture).</summary>
    void RecordInstructionLightweight(ulong rip);
}

/// <summary>
/// Register snapshot data structure (value type, no allocations).
/// </summary>
public readonly struct RegisterSnapshot
{
    public readonly ulong Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp, Rsp;
    public readonly ulong R8, R9, R10, R11, R12, R13, R14, R15;
    public readonly ulong RFlags, Rip;
    
    public RegisterSnapshot(
        ulong rax, ulong rbx, ulong rcx, ulong rdx,
        ulong rsi, ulong rdi, ulong rbp, ulong rsp,
        ulong r8, ulong r9, ulong r10, ulong r11,
        ulong r12, ulong r13, ulong r14, ulong r15,
        ulong rflags, ulong rip)
    {
        Rax = rax; Rbx = rbx; Rcx = rcx; Rdx = rdx;
        Rsi = rsi; Rdi = rdi; Rbp = rbp; Rsp = rsp;
        R8 = r8; R9 = r9; R10 = r10; R11 = r11;
        R12 = r12; R13 = r13; R14 = r14; R15 = r15;
        RFlags = rflags; Rip = rip;
    }
}

#endregion

#region GPU Diagnostic Source

/// <summary>
/// Provides GPU/AGC execution data to the Diagnostics subsystem.
/// Implemented by SharpEmu.Libs.Agc.AgcExports.
/// </summary>
public interface IGpuDiagnosticSource
{
    /// <summary>Records a GPU command buffer submission (sceAgcDriverSubmitDcb/Acb).</summary>
    void RecordSubmit(ulong commandBufferAddress, uint commandCount);
    
    /// <summary>Records a draw call.</summary>
    void RecordDraw(uint vertexCount, uint instanceCount, ulong shaderId);
    
    /// <summary>Records a compute dispatch.</summary>
    void RecordDispatch(uint threadGroupsX, uint threadGroupsY, uint threadGroupsZ);
    
    /// <summary>Records a shader compilation event.</summary>
    void RecordShaderCompiled(ulong shaderId, byte[] sourceHash, string shaderType, bool success, string? error);
    
    /// <summary>Records a GPU resource creation (texture/buffer/render target).</summary>
    void RecordResourceCreated(ulong address, string type, ulong size, string format);
    
    /// <summary>Records a GPU resource destruction.</summary>
    void RecordResourceDestroyed(ulong address);
}

#endregion

#region Memory Diagnostic Source

/// <summary>
/// Provides memory allocation data to the Diagnostics subsystem.
/// Implemented by SharpEmu.Core.Memory.PhysicalVirtualMemory.
/// </summary>
public interface IMemoryDiagnosticSource
{
    /// <summary>Records a guest memory allocation.</summary>
    void RecordAllocation(ulong address, ulong size, string allocator, ulong callerAddress);
    
    /// <summary>Records a guest memory free.</summary>
    void RecordFree(ulong address);
    
    /// <summary>Records a guest memory access (sampled, for watchpoints).</summary>
    void RecordAccess(ulong address, ulong size, int accessType, ulong rip);
}

#endregion

#region Thread Diagnostic Source

/// <summary>
/// Provides thread state data to the Diagnostics subsystem.
/// Implemented by SharpEmu.Libs.Kernel.KernelPthreadCompatExports.
/// </summary>
public interface IThreadDiagnosticSource
{
    /// <summary>Records a thread state change.</summary>
    void RecordStateChange(int threadId, string newState, string? reason);
    
    /// <summary>Records a mutex acquisition.</summary>
    void RecordMutexAcquire(int threadId, ulong mutexAddress);
    
    /// <summary>Records a mutex release.</summary>
    void RecordMutexRelease(int threadId, ulong mutexAddress);
}

#endregion

#region Crash Diagnostic Source

/// <summary>
/// Provides crash capture functionality.
/// Implemented by SharpEmu.Core.Cpu.Native.DirectExecutionBackend (signal handler).
/// </summary>
public interface ICrashDiagnosticSource
{
    /// <summary>
    /// [SIGNAL-SAFE] Queues crash data for async writing.
    /// MUST NOT do any heap allocations, file I/O, or take locks.
    /// </summary>
    void QueueCrash(
        string signalType,
        ulong faultAddress,
        ulong rip,
        in RegisterSnapshot registers);
}

#endregion

#region Syscall Diagnostic Source

/// <summary>
/// Provides syscall/HLE call tracking.
/// Implemented by SharpEmu.Core.Cpu.Native.DirectExecutionBackend.Imports.
/// </summary>
public interface ISyscallDiagnosticSource
{
    /// <summary>Records a syscall/HLE export call.</summary>
    void RecordCall(
        string library,
        string name,
        string nid,
        long returnValue,
        long durationMicros,
        int threadId,
        ulong[]? args);
}

#endregion

#region File I/O Diagnostic Source

/// <summary>
/// Provides file I/O tracking.
/// Implemented by SharpEmu.Libs.Kernel.KernelMemoryCompatExports.
/// </summary>
public interface IFileIoDiagnosticSource
{
    void RecordOpen(string path, string mode, bool success);
    void RecordRead(string path, ulong offset, ulong size, double durationMs);
    void RecordWrite(string path, ulong offset, ulong size, double durationMs);
    void RecordStat(string path, bool success);
}

#endregion

#region Boot Stage Diagnostic Source

/// <summary>
/// Provides boot stage milestone tracking.
/// Implemented by SharpEmu.Core.Runtime.SharpEmuRuntime.
/// </summary>
public interface IBootStageDiagnosticSource
{
    /// <summary>Records a boot stage milestone (e.g., "SelfImage", "EntryPoint").</summary>
    void RecordBootStage(string stageName, string details);
}

#endregion

#region Diagnostic Profile

/// <summary>
/// Diagnostic profiling levels - controls how much data is collected.
/// </summary>
public enum DiagnosticProfile
{
    /// <summary>Minimal tracking - only boot stages and crashes.</summary>
    Normal = 0,
    /// <summary>Standard compatibility testing.</summary>
    Compatibility = 1,
    /// <summary>Full debugging - event log, detailed heap, all thread transitions.</summary>
    DeepDebug = 2,
    /// <summary>Developer mode - everything including full event trace.</summary>
    Developer = 3,
    /// <summary>Forensic mode - maximum data capture for crash reproduction.</summary>
    Forensic = 4
}

#endregion

#region Diagnostic Event Bus

/// <summary>
/// Central event bus for diagnostic events.
/// In v5, replaced by SharpEmu.Diagnostics.Core.EventBus class.
/// </summary>

#endregion

/// <summary>
/// Stopwatch helper for diagnostics timing.
/// </summary>
public static class DiagStopwatch
{
    private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public static double GetElapsedTimeMs() => _sw.Elapsed.TotalMilliseconds;
}
