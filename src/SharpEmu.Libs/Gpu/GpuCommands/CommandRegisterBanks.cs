// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum ContextStateOperation : uint
{
    Clear = 0,
    Push = 1,
    Pop = 2,
    PushClear = 3,
}

// Shader compilation reads raw words until its typed register connection is complete.
public sealed class CommandRegisterBanks
{
    public Dictionary<uint, uint> Shader { get; } = new();

    public void Reset() => Shader.Clear();
}
