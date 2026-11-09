// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AudioInExports
{
    [SysAbiExport(
        Nid = "BohEAQ7DlUE",
        ExportName = "sceAudioInGetSilentState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioIn")]
    public static int GetSilentState(CpuContext ctx) =>
        // No AudioIn ports can be opened until an input backend is implemented.
        ctx.SetReturn(unchecked((int)0x80260101));
}
