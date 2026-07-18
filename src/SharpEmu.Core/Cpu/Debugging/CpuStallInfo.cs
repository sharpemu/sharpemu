// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>The kind of execution stall the backend detected.</summary>
public enum CpuStallKind
{
    /// <summary>
    /// The guest is repeatedly re-dispatching the same import with no forward
    /// progress — most commonly a spin on a mutex lock/unlock pair.
    /// </summary>
    ImportLoop,
}

/// <summary>
/// Details of a detected stall handed to <see cref="ICpuDebugHook.OnStall"/>.
/// Reported from the emulation thread at the point the backend recognises the
/// livelock, before it forces the guest out of the loop.
/// </summary>
public readonly struct CpuStallInfo
{
    public CpuStallInfo(
        CpuStallKind kind,
        string? nid,
        ulong instructionPointer,
        long dispatchIndex,
        ulong argument0,
        ulong argument1,
        string detail,
        string? libraryName = null,
        string? functionName = null)
    {
        Kind = kind;
        Nid = nid;
        InstructionPointer = instructionPointer;
        DispatchIndex = dispatchIndex;
        Argument0 = argument0;
        Argument1 = argument1;
        Detail = detail ?? string.Empty;
        LibraryName = libraryName;
        FunctionName = functionName;
    }

    public CpuStallKind Kind { get; }

    /// <summary>The NID of the import being spun on, when known.</summary>
    public string? Nid { get; }

    /// <summary>The guest return address of the looping import dispatch.</summary>
    public ulong InstructionPointer { get; }

    /// <summary>The import dispatch counter at detection time.</summary>
    public long DispatchIndex { get; }

    /// <summary>The first two guest ABI arguments at stall detection.</summary>
    public ulong Argument0 { get; }

    public ulong Argument1 { get; }

    /// <summary>The resolved HLE export, when the NID is registered.</summary>
    public string? LibraryName { get; }

    public string? FunctionName { get; }

    public bool IsResolved => !string.IsNullOrWhiteSpace(FunctionName);

    /// <summary>A human-readable one-line summary of the stall.</summary>
    public string Detail { get; }
}
