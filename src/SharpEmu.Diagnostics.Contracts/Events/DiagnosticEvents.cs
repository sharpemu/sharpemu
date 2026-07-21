// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Contracts.Events;

/// <summary>Boot stage reached.</summary>
public record BootEvent(long Timestamp, string StageName, bool Success, string? Detail = null) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "boot";
    public string Type => "stage";
}

/// <summary>Import call completed.</summary>
public record ImportEvent(long Timestamp, string Nid, string? ExportName, string? Library, int Result, long DurationMicros) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "import";
    public string Type => "call";
}

/// <summary>CPU instruction checkpoint.</summary>
public record CpuEvent(long Timestamp, ulong Rip, byte[] Opcode, ulong[]? Registers) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "cpu";
    public string Type => "instruction";
}

/// <summary>Memory operation.</summary>
public record MemoryEvent(long Timestamp, string Operation, ulong Address, ulong Size, string? Detail) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "memory";
    public string Type => Operation;
}

/// <summary>Thread lifecycle event.</summary>
public record ThreadEvent(long Timestamp, ulong ThreadId, string Operation, string? Detail) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "thread";
    public string Type => Operation;
}

/// <summary>Crash or exception.</summary>
public record CrashEvent(long Timestamp, ulong Rip, ulong FaultAddress, int Signal, string CrashType, Dictionary<string, ulong>? Registers) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "crash";
    public string Type => "fault";
}

/// <summary>GPU operation.</summary>
public record GpuEvent(long Timestamp, string Operation, ulong? Address, string? Detail) : IDiagnosticEvent
{
    public int Version => 1;
    public string Category => "gpu";
    public string Type => Operation;
}
