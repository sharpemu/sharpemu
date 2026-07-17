// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVideodec / libSceAudiodec handle management. Actual H.264/HEVC and
/// AAC/AT9 decoding requires an external codec, which is out of scope; these
/// exports keep the decoder lifecycle resolvable (create/decode/flush/delete)
/// and report "no output produced" so guests advance instead of failing on
/// unresolved imports.
/// </summary>
public static class CodecExports
{
    private const int Ok = 0;
    private const int VideodecErrorInvalidArg = unchecked((int)0x80620801);
    private const int AudiodecErrorInvalidArg = unchecked((int)0x807F0002);

    private static readonly ConcurrentDictionary<ulong, byte> VideoDecoders = new();
    private static readonly ConcurrentDictionary<ulong, byte> AudioDecoders = new();
    private static long _nextHandle = 1;

    // ---- Video decoder ----

    [SysAbiExport(Nid = "qkgRiwHyheU", ExportName = "sceVideodecCreateDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceVideodec")]
    public static int VideodecCreateDecoder(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextHandle);
        VideoDecoders[handle] = 1;
        return TryWriteHandle(ctx, outHandleAddress, handle) ? SetReturn(ctx, Ok) : SetReturn(ctx, VideodecErrorInvalidArg);
    }

    [SysAbiExport(Nid = "q0W5GJMovMs", ExportName = "sceVideodecDecode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceVideodec")]
    public static int VideodecDecode(CpuContext ctx)
    {
        // No decoder is present: report success with no picture produced.
        return SetReturn(ctx, VideoDecoders.ContainsKey(ctx[CpuRegister.Rdi]) ? Ok : VideodecErrorInvalidArg);
    }

    [SysAbiExport(Nid = "jeigLlKdp5I", ExportName = "sceVideodecFlush",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceVideodec")]
    public static int VideodecFlush(CpuContext ctx) =>
        SetReturn(ctx, VideoDecoders.ContainsKey(ctx[CpuRegister.Rdi]) ? Ok : VideodecErrorInvalidArg);

    [SysAbiExport(Nid = "U0kpGF1cl90", ExportName = "sceVideodecDeleteDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceVideodec")]
    public static int VideodecDeleteDecoder(CpuContext ctx)
    {
        VideoDecoders.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, Ok);
    }

    // ---- Audio decoder ----

    [SysAbiExport(Nid = "O3f1sLMWRvs", ExportName = "sceAudiodecCreateDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecCreateDecoder(CpuContext ctx)
    {
        var handle = (ulong)Interlocked.Increment(ref _nextHandle);
        AudioDecoders[handle] = 1;
        // sceAudiodec returns the handle directly (>= 0) or a negative error.
        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(Nid = "KHXHMDLkILw", ExportName = "sceAudiodecDecode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecDecode(CpuContext ctx)
    {
        // No decoder present: report success with zero output samples so the
        // caller treats the frame as silent rather than erroring.
        return SetReturn(ctx, AudioDecoders.ContainsKey(ctx[CpuRegister.Rdi]) ? Ok : AudiodecErrorInvalidArg);
    }

    [SysAbiExport(Nid = "Tp+ZEy69mLk", ExportName = "sceAudiodecDeleteDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecDeleteDecoder(CpuContext ctx)
    {
        AudioDecoders.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, Ok);
    }

    private static bool TryWriteHandle(CpuContext ctx, ulong address, ulong handle) =>
        ctx.TryWriteUInt64(address, handle);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
