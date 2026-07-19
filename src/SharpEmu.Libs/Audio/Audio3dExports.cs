// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

// PS5 3D-audio / acoustic-scene module (libSceAudio3d). We do not model
// the 3D audio render graph (ports, objects, speaker arrays, acoustic
// scene). It is a quality/positioning feature, not a correctness gate:
// games (e.g. Astro Bot) call it heavily during audio init and will hit
// the unresolved-import trap (and AV) on every entry point if it is
// missing. NID strings are derived from the export names with the project's
// PS5 NID algorithm (see SharpEmu.SourceGenerators/Ps5Nid.cs) so they
// match what the guest binary actually calls. Success-returning stubs let
// init proceed without us owning any audio state.
public static class Audio3dExports
{
    private const int Ok = 0;

    [SysAbiExport(Nid = "pZlOm1aF3aA", ExportName = "sceAudio3dAudioOutClose", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int AudioOutClose(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "ucEsi62soTo", ExportName = "sceAudio3dAudioOutOpen", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int AudioOutOpen(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "7NYEzJ9SJbM", ExportName = "sceAudio3dAudioOutOutput", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int AudioOutOutput(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "HbxYY27lK6E", ExportName = "sceAudio3dAudioOutOutputs", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int AudioOutOutputs(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "9tEwE0GV0qo", ExportName = "sceAudio3dBedWrite", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int BedWrite(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "xH4Q9UILL3o", ExportName = "sceAudio3dBedWrite2", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int BedWrite2(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "lvWMW6vEqFU", ExportName = "sceAudio3dCreateSpeakerArray", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int CreateSpeakerArray(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "8hm6YdoQgwg", ExportName = "sceAudio3dDeleteSpeakerArray", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int DeleteSpeakerArray(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "Im+jOoa5WAI", ExportName = "sceAudio3dGetDefaultOpenParameters", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int GetDefaultOpenParameters(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "kEqqyDkmgdI", ExportName = "sceAudio3dGetSpeakerArrayMemorySize", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int GetSpeakerArrayMemorySize(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "-R1DukFq7Dk", ExportName = "sceAudio3dGetSpeakerArrayMixCoefficients", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int GetSpeakerArrayMixCoefficients(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "-Re+pCWvwjQ", ExportName = "sceAudio3dGetSpeakerArrayMixCoefficients2", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int GetSpeakerArrayMixCoefficients2(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "UmCvjSmuZIw", ExportName = "sceAudio3dInitialize", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int Initialize(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "jO2tec4dJ2M", ExportName = "sceAudio3dObjectReserve", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ObjectReserve(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "m+eR7y8286E", ExportName = "sceAudio3dObjectSetAttribute", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ObjectSetAttribute(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "4uyHN9q4ZeU", ExportName = "sceAudio3dObjectSetAttributes", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ObjectSetAttributes(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "1HXxo-+1qCw", ExportName = "sceAudio3dObjectUnreserve", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ObjectUnreserve(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "lw0qrdSjZt8", ExportName = "sceAudio3dPortAdvance", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortAdvance(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "OyVqOeVNtSk", ExportName = "sceAudio3dPortClose", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortClose(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "UHFOgVNz0kk", ExportName = "sceAudio3dPortCreate", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortCreate(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "Mw9mRQtWepY", ExportName = "sceAudio3dPortDestroy", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortDestroy(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "ZOGrxWLgQzE", ExportName = "sceAudio3dPortFlush", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortFlush(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "uJ0VhGcxCTQ", ExportName = "sceAudio3dPortFreeState", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortFreeState(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "9ZA23Ia46Po", ExportName = "sceAudio3dPortGetAttributesSupported", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetAttributesSupported(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "SEggctIeTcI", ExportName = "sceAudio3dPortGetList", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetList(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "flPcUaXVXcw", ExportName = "sceAudio3dPortGetParameters", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetParameters(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "YaaDbDwKpFM", ExportName = "sceAudio3dPortGetQueueLevel", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetQueueLevel(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "CKHlRW2E9dA", ExportName = "sceAudio3dPortGetState", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetState(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "iRX6GJs9tvE", ExportName = "sceAudio3dPortGetStatus", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortGetStatus(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "XeDDK0xJWQA", ExportName = "sceAudio3dPortOpen", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortOpen(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "VEVhZ9qd4ZY", ExportName = "sceAudio3dPortPush", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortPush(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "-pzYDZozm+M", ExportName = "sceAudio3dPortQueryDebug", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortQueryDebug(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "Yq9bfUQ0uJg", ExportName = "sceAudio3dPortSetAttribute", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int PortSetAttribute(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "QfNXBrKZeI0", ExportName = "sceAudio3dReportRegisterHandler", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ReportRegisterHandler(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "psv2gbihC1A", ExportName = "sceAudio3dReportUnregisterHandler", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int ReportUnregisterHandler(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "yEYXcbAGK14", ExportName = "sceAudio3dSetGpuRenderer", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int SetGpuRenderer(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "Aacl5qkRU6U", ExportName = "sceAudio3dStrError", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int StrError(CpuContext ctx) => ctx.SetReturn(Ok);

    [SysAbiExport(Nid = "WW1TS2iz5yc", ExportName = "sceAudio3dTerminate", Target = Generation.Gen5, LibraryName = "libSceAudio3d")]
    public static int Terminate(CpuContext ctx) => ctx.SetReturn(Ok);
}
