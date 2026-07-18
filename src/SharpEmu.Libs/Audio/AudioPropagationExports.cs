// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

// PS5 acoustic-propagation (3D-audio ray/portal/room) module. We do not model
// acoustic propagation; the geometry-driven reverb/occlusion it produces is a
// quality feature, not a correctness gate. Games (e.g. Astro Bot) call it
// during audio init and hard-assert if any entry point is missing:
//   ASSERT ... sceAudioPropagationSystemQueryMemory failed : 0x80020002
// The API is placement-style: QueryMemory reports a buffer size, the game
// allocates it, and the "system"/objects live inside that caller-owned buffer,
// so success-returning stubs let init proceed without us owning any state.
public static class AudioPropagationExports
{
    private const int Ok = 0;

    // QueryMemory reports the working-set size the caller must allocate before
    // SystemCreate. rsi points at the out size/alignment; write a modest,
    // aligned block so the caller's allocation succeeds.
    [SysAbiExport(
        Nid = "7xyAxrusLko",
        ExportName = "sceAudioPropagationSystemQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemQueryMemory(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        if (outAddress != 0)
        {
            // {size, alignment} — 1 MiB / 256 B covers the caller's allocation.
            ctx.TryWriteUInt64(outAddress, 0x10_0000);
            ctx.TryWriteUInt64(outAddress + sizeof(ulong), 0x100);
        }

        return ctx.SetReturn(Ok);
    }

    [SysAbiExport(Nid = "GrA9ke1QT+E", ExportName = "sceAudioPropagationSystemQueryInfo", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemQueryInfo(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "aNEqtSHdUSo", ExportName = "sceAudioPropagationSystemCreate", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemCreate(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "x5VPqg5iyAk", ExportName = "sceAudioPropagationSystemDestroy", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemDestroy(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "ile38Gl-p5M", ExportName = "sceAudioPropagationSystem", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int System(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "cMl3u+7QBBM", ExportName = "sceAudioPropagationSystemMemoryInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemMemoryInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "3B9IabLByyM", ExportName = "sceAudioPropagationSystemOptionInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemOptionInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "B2KI2AachWE", ExportName = "sceAudioPropagationSystemLock", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemLock(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "kIdb+iQUzCs", ExportName = "sceAudioPropagationSystemSetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemSetAttributes(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "VlBT16890mA", ExportName = "sceAudioPropagationSystemSetRays", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemSetRays(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "ht-QXT3zGxo", ExportName = "sceAudioPropagationSystemGetRays", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemGetRays(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "CPLV6G-eXmk", ExportName = "sceAudioPropagationSystemRegisterMaterial", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemRegisterMaterial(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "XKCN4gpeYsM", ExportName = "sceAudioPropagationSystemUnregisterMaterial", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SystemUnregisterMaterial(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "8bI5h8req30", ExportName = "sceAudioPropagationRoomCreate", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int RoomCreate(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "S0JwP2AFTTE", ExportName = "sceAudioPropagationRoomDestroy", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int RoomDestroy(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "b-dYXrjSNZU", ExportName = "sceAudioPropagationPortalCreate", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int PortalCreate(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "ZQXE-xS6MTE", ExportName = "sceAudioPropagationPortalDestroy", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int PortalDestroy(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "WXMhENV2NcA", ExportName = "sceAudioPropagationPortalSetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int PortalSetAttributes(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "i687TNRF+hw", ExportName = "sceAudioPropagationPortalSettingsInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int PortalSettingsInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "d84otraxt2s", ExportName = "sceAudioPropagationSourceCreate", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceCreate(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "wkseM3LWPuc", ExportName = "sceAudioPropagationSourceDestroy", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceDestroy(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "-wsUTr31yeg", ExportName = "sceAudioPropagationSourceSetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAttributes(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "PBcrVpEqUVY", ExportName = "sceAudioPropagationSourceCalculateAudioPaths", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceCalculateAudioPaths(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "eEeKqFeNI3o", ExportName = "sceAudioPropagationSourceGetAudioPath", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceGetAudioPath(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "G+QLTfyLMYk", ExportName = "sceAudioPropagationSourceGetAudioPathCount", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceGetAudioPathCount(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "aKJZx7wCma8", ExportName = "sceAudioPropagationSourceGetRays", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceGetRays(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "3aEY9tPXGKc", ExportName = "sceAudioPropagationSourceQueryInfo", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceQueryInfo(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "hhz9pITnC8k", ExportName = "sceAudioPropagationSourceRender", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceRender(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "SoKPzY1-3SU", ExportName = "sceAudioPropagationSourceRenderInfoInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceRenderInfoInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "tKSmk2JsMAA", ExportName = "sceAudioPropagationSourceSetAudioPath", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAudioPath(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "5vzOS2pHMFc", ExportName = "sceAudioPropagationSourceSetAudioPaths", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAudioPaths(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "MNmGapXrYRs", ExportName = "sceAudioPropagationSourceSetAudioPathsParamInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAudioPathsParamInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "i-0aUex3zCE", ExportName = "sceAudioPropagationAudioPathInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int AudioPathInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "JZIkSbmt2BE", ExportName = "sceAudioPropagationAudioPathPointInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int AudioPathPointInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "tL2AEPejVQE", ExportName = "sceAudioPropagationPathGetNumPoints", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int PathGetNumPoints(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "2BSFmuKtRss", ExportName = "sceAudioPropagationMaterialInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int MaterialInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "0r2+9UTg1BA", ExportName = "sceAudioPropagationRayInit", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int RayInit(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "BbOT4vBwAjs", ExportName = "sceAudioPropagationResetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int ResetAttributes(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "gCmQm6dvMxw", ExportName = "sceAudioPropagationReportApi", Target = Generation.Gen5, LibraryName = "libSceAudioPropagation")]
    public static int ReportApi(CpuContext ctx) => ctx.SetReturn(Ok);
}
