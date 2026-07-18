// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// Identifies the kind of guest entry frame a debugger is observing. The
/// dispatcher enters a fresh frame for the process entry point and for every
/// module initializer, so the debug layer can label stops accordingly.
/// </summary>
public enum CpuDebugFrameKind
{
    /// <summary>The guest process entry point (<c>eboot.bin</c> start).</summary>
    ProcessEntry,

    /// <summary>A module DT_INIT / initializer routine.</summary>
    ModuleInitializer,
}
