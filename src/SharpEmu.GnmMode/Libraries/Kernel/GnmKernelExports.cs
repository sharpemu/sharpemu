// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Libraries.Kernel;

public static class GnmKernelExports
{
    private static int _cpuMode;

    internal static void SetCpuMode(int mode) => Volatile.Write(ref _cpuMode, mode);

    public static int GetCpuMode(CpuContext context) => context.SetReturn(Volatile.Read(ref _cpuMode));
}
