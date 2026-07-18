// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// Adapts a live <see cref="CpuContext"/> to <see cref="ICpuDebugFrame"/>. The
/// dispatcher creates one of these around the guest context it is about to run
/// and passes it to the attached <see cref="ICpuDebugHook"/>; every accessor
/// forwards directly to the underlying context.
/// </summary>
internal sealed class CpuContextDebugFrame : ICpuDebugFrame
{
    private readonly CpuContext _context;

    internal CpuContextDebugFrame(
        CpuDebugFrameKind kind,
        ulong entryPoint,
        string label,
        CpuContext context,
        IReadOnlyDictionary<ulong, string> importStubs)
    {
        Kind = kind;
        EntryPoint = entryPoint;
        Label = label ?? string.Empty;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ImportStubs = importStubs ?? new Dictionary<ulong, string>();
    }

    public CpuDebugFrameKind Kind { get; }

    public Generation Generation => _context.TargetGeneration;

    public ulong EntryPoint { get; }

    public string Label { get; }

    public ICpuMemory Memory => _context.Memory;

    public ulong GetRegister(CpuRegister register) => _context[register];

    public void SetRegister(CpuRegister register, ulong value) => _context[register] = value;

    public ulong Rip
    {
        get => _context.Rip;
        set => _context.Rip = value;
    }

    public ulong Rflags
    {
        get => _context.Rflags;
        set => _context.Rflags = value;
    }

    public ulong FsBase => _context.FsBase;

    public ulong GsBase => _context.GsBase;

    public void GetXmm(int registerIndex, out ulong low, out ulong high)
        => _context.GetXmmRegister(registerIndex, out low, out high);

    public IReadOnlyDictionary<ulong, string> ImportStubs { get; }
}
