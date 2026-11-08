// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GnmMode;

internal static class GnmDiagnostics
{
    internal static readonly bool Log = Environment.GetEnvironmentVariable("SHARPEMU_LOG_GNM") == "1";
    internal static readonly bool LogImages = Environment.GetEnvironmentVariable("SHARPEMU_LOG_GNM_IMAGES") == "1";
}
