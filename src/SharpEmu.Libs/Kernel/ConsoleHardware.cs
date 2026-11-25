// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

public static class ConsoleHardware
{
    private static int _pro;

    public static bool IsPro => Volatile.Read(ref _pro) != 0;

    public static void Configure(bool pro) => Volatile.Write(ref _pro, pro ? 1 : 0);
}
