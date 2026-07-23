// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Stubs;

/// <summary>
/// Success stubs for trophy, character-encoding and telemetry ABI calls that
/// Void Terrarium (and other titles) invoke during startup. They were
/// previously unresolved and returned NOT_FOUND, and a title that gates its
/// UI/text initialization on these succeeding then skips ahead and never draws
/// its content (a black screen with only a clear pass). These return success
/// (and a non-zero handle where an out pointer is expected) so init proceeds.
/// </summary>
public static class GameServiceStubs
{
    private static int Ok(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Writes a small non-zero handle to the pointer in the given register so
    // the caller treats the object as created; returns success.
    private static int OkWithHandle(CpuContext ctx, CpuRegister outPointerRegister)
    {
        var outAddress = ctx[outPointerRegister];
        if (outAddress != 0)
        {
            Span<byte> handle = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(handle, 1);
            _ = ctx.Memory.TryWrite(outAddress, handle);
        }

        return Ok(ctx);
    }

    // ---- NpTrophy2: trophy context/handle registration at boot ----
    public static int NpTrophy2CreateContext(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2CreateHandle(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2RegisterContext(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "4IzqhhUQ3nk", ExportName = "sceNpTrophy2GetGameInfo",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameInfo(CpuContext ctx) => Ok(ctx);

    // ---- CES: Shift-JIS <-> Unicode conversion setup (Japanese text) ----

    [SysAbiExport(Nid = "ZiDCxUUGbec", ExportName = "sceCesUcsProfileInitSJis1997Cp932",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesUcsProfileInitSJis1997Cp932(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "538bRGc6Zo8", ExportName = "sceCesMbcsUcsContextInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesMbcsUcsContextInit(CpuContext ctx) => Ok(ctx);

    // ---- NpUniversalDataSystem: gameplay telemetry events ----
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx) => Ok(ctx);
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "47UAEuQl+iI", ExportName = "sceNpUniversalDataSystemTerminate",
        Target = Generation.Gen5, LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "0HBYxYAjmf0", ExportName = "sceNpGameIntentTerminate",
        Target = Generation.Gen5, LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "jqb7HntFQFc", ExportName = "sceWebBrowserDialogInitialize",
        Target = Generation.Gen5, LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "ocHtyBwHfys", ExportName = "sceWebBrowserDialogTerminate",
        Target = Generation.Gen5, LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "kvYEw2lBndk", ExportName = "sceGameLiveStreamingInitialize",
        Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GameLiveStreamingInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "isruqthpYcw", ExportName = "sceSharePlayInitialize",
        Target = Generation.Gen5, LibraryName = "libSceSharePlay")]
    public static int SharePlayInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "0IL1keINExQ", ExportName = "sceShareTerminate",
        Target = Generation.Gen5, LibraryName = "libSceShareUtility")]
    public static int ShareTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "YBiIdcDPrxs", ExportName = "sceShareFeaturePermit",
        Target = Generation.Gen5, LibraryName = "libSceShareUtility")]
    public static int ShareFeaturePermit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "9TrhuGzberQ", ExportName = "sceVoiceInit",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "clyKUyi3RYU", ExportName = "sceVoiceSetThreadsParams",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceSetThreadsParams(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "dPj4ZtRcIWk", ExportName = "sceContentSearchInit",
        Target = Generation.Gen5, LibraryName = "libSceContentSearch")]
    public static int ContentSearchInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "zoxb0wEChEM", ExportName = "sceContentDeleteInitialize",
        Target = Generation.Gen5, LibraryName = "libSceContentDelete")]
    public static int ContentDeleteInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "Fc8qxlKINYQ", ExportName = "sceVideoRecordingSetInfo",
        Target = Generation.Gen5, LibraryName = "libSceVideoRecording")]
    public static int VideoRecordingSetInfo(CpuContext ctx) => Ok(ctx);

    // Captured from GTA V Enhanced (PPSA04264); not in the public NID catalog.
    // Side-effect-free success — same as unresolved stub behavior that kept boot
    // moving; reverse the ABI before writing guest memory.
    #pragma warning disable SHEM006
    [SysAbiExport(Nid = "Ikfdt-rIqCE", ExportName = "sceUnknownIkfdt",
        Target = Generation.Gen5, LibraryName = "libKernel")]
    public static int UnknownIkfdt(CpuContext ctx) => Ok(ctx);
    #pragma warning restore SHEM006
}
