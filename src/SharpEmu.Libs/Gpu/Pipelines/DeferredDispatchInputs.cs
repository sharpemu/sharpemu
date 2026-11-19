// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// What a worker needs to rebind a deferred dispatch member, taken on the command thread at issue: the kept program's entry
// and base source, and the member's own compute registers and dimensions.
internal sealed class DeferredDispatchInputs
{
    public readonly ComputeStageRegisters Compute = new();
    public ProgramSourceEntry? Entry;
    public ShaderSource? BaseSource;
    public Gen5ComputeSystemRegisters SystemRegisters;
    public bool GpuThreadCounts;
    public uint GroupsX;
    public uint GroupsY;
    public uint GroupsZ;
    public uint Initiator;

    public void Reset()
    {
        Entry = null;
        BaseSource = null;
    }
}
