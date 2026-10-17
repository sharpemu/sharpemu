// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVideodec / libSceAudiodec compatibility exports.
/// </summary>
public static class CodecExports
{
    private const int Ok = 0;
    private const int VideodecErrorInvalidArg = unchecked((int)0x80620801);
    private const int AudiodecErrorInvalidType = unchecked((int)0x807F0001);
    private const int AudiodecErrorInvalidArg = unchecked((int)0x807F0002);
    private const int AudiodecErrorInvalidParamSize = unchecked((int)0x807F0004);
    private const int AudiodecErrorInvalidBsiInfoSize = unchecked((int)0x807F0005);
    private const int AudiodecErrorInvalidAuInfoSize = unchecked((int)0x807F0006);
    private const int AudiodecErrorInvalidPcmItemSize = unchecked((int)0x807F0007);
    private const int AudiodecErrorInvalidCtrlPointer = unchecked((int)0x807F0008);
    private const int AudiodecErrorInvalidParamPointer = unchecked((int)0x807F0009);
    private const int AudiodecErrorInvalidBsiInfoPointer = unchecked((int)0x807F000A);
    private const int AudiodecErrorInvalidAuInfoPointer = unchecked((int)0x807F000B);
    private const int AudiodecErrorInvalidPcmItemPointer = unchecked((int)0x807F000C);
    private const int AudiodecErrorInvalidAuPointer = unchecked((int)0x807F000D);
    private const int AudiodecErrorInvalidPcmPointer = unchecked((int)0x807F000E);
    private const int AudiodecErrorInvalidHandle = unchecked((int)0x807F000F);
    private const int AudiodecErrorInvalidWordLength = unchecked((int)0x807F0010);
    private const int AudiodecErrorInvalidAuSize = unchecked((int)0x807F0011);
    private const int AudiodecErrorInvalidPcmSize = unchecked((int)0x807F0012);
    private const uint AudiodecTypeAt9 = 1;
    private const uint AudiodecTypeMp3 = 2;
    private const uint AudiodecTypeAac = 3;
    private const int MaxAudioDecoders = 64;
    private const int MaxDecodeBufferBytes = 64 * 1024 * 1024;

    private static readonly ConcurrentDictionary<ulong, byte> VideoDecoders = new();
    private static readonly ConcurrentDictionary<int, AudioDecoderState> AudioDecoders = new();
    private static readonly ConcurrentDictionary<uint, int> AudioCodecInitCounts = new();
    private static readonly object AudioDecoderGate = new();
    private static long _nextHandle = 1;
    private static int _nextAudioHandle;

    private sealed class AudioDecoderState
    {
        public required uint CodecType { get; init; }

        public required int WordSize { get; init; }

        public required int Channels { get; init; }

        public required int SampleRate { get; init; }

        public required int FrameBytes { get; init; }

        public required int FramesPerSuperframe { get; init; }

        public required int FrameSamples { get; init; }

        public Atrac9DecodeState? Atrac9 { get; init; }
    }

    private readonly record struct AudioControl(
        ulong ParamAddress,
        ulong BsiInfoAddress,
        ulong AuInfoAddress,
        ulong PcmItemAddress,
        ulong AuAddress,
        uint AuSize,
        ulong PcmAddress,
        uint PcmSize,
        int WordSize,
        byte[]? Atrac9Config,
        uint AacMaxChannels,
        uint AacSampleRateIndex);

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

    [SysAbiExport(Nid = "VjhsmxpcezI", ExportName = "sceAudiodecInitLibrary",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecInitLibrary(CpuContext ctx)
    {
        var codecType = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!IsValidAudioCodecType(codecType))
        {
            return SetReturn(ctx, AudiodecErrorInvalidType);
        }

        AudioCodecInitCounts.AddOrUpdate(codecType, 1, static (_, count) => count + 1);
        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(Nid = "h5jSB2QIDV0", ExportName = "sceAudiodecTermLibrary",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecTermLibrary(CpuContext ctx)
    {
        var codecType = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!IsValidAudioCodecType(codecType))
        {
            return SetReturn(ctx, AudiodecErrorInvalidType);
        }

        AudioCodecInitCounts.AddOrUpdate(codecType, 0, static (_, count) => Math.Max(0, count - 1));
        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(Nid = "O3f1sLMWRvs", ExportName = "sceAudiodecCreateDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecCreateDecoder(CpuContext ctx)
    {
        var controlAddress = ctx[CpuRegister.Rdi];
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!IsValidAudioCodecType(codecType))
        {
            return SetReturn(ctx, AudiodecErrorInvalidType);
        }

        var validation = TryReadAudioControl(ctx, controlAddress, codecType, decode: false, out var control);
        if (validation != Ok)
        {
            return SetReturn(ctx, validation);
        }

        if (!AudioCodecInitCounts.TryGetValue(codecType, out var initCount) || initCount == 0)
        {
            return SetReturn(ctx, AudiodecErrorInvalidArg);
        }

        var decoder = CreateAudioDecoder(codecType, control);
        if (decoder is null)
        {
            return SetReturn(ctx, AudiodecErrorInvalidArg);
        }

        int handle;
        lock (AudioDecoderGate)
        {
            if (AudioDecoders.Count >= MaxAudioDecoders)
            {
                return SetReturn(ctx, AudiodecErrorInvalidArg);
            }

            do
            {
                _nextAudioHandle = _nextAudioHandle % MaxAudioDecoders + 1;
                handle = _nextAudioHandle;
            }
            while (AudioDecoders.ContainsKey(handle));

            AudioDecoders[handle] = decoder;
        }

        WriteAudioDecoderInfo(ctx, control.BsiInfoAddress, decoder, Ok);
        return SetReturn(ctx, handle);
    }

    [SysAbiExport(Nid = "KHXHMDLkILw", ExportName = "sceAudiodecDecode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecDecode(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!AudioDecoders.TryGetValue(handle, out var decoder))
        {
            return SetReturn(ctx, AudiodecErrorInvalidHandle);
        }

        var validation = TryReadAudioControl(
            ctx,
            ctx[CpuRegister.Rsi],
            decoder.CodecType,
            decode: true,
            out var control);
        if (validation != Ok)
        {
            return SetReturn(ctx, validation);
        }

        if (control.AuSize > MaxDecodeBufferBytes)
        {
            return SetReturn(ctx, AudiodecErrorInvalidAuSize);
        }

        if (control.PcmSize > MaxDecodeBufferBytes)
        {
            return SetReturn(ctx, AudiodecErrorInvalidPcmSize);
        }

        if (decoder.CodecType == AudiodecTypeAt9 && decoder.Atrac9 is not null)
        {
            DecodeAtrac9(ctx, decoder, control);
        }
        else
        {
            DecodeAudioSilence(ctx, decoder, control);
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(Nid = "Tp+ZEy69mLk", ExportName = "sceAudiodecDeleteDecoder",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecDeleteDecoder(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return SetReturn(
            ctx,
            AudioDecoders.TryRemove(handle, out _)
                ? Ok
                : AudiodecErrorInvalidHandle);
    }

    [SysAbiExport(Nid = "6Vf9WTLDoss", ExportName = "sceAudiodecClearContext",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceAudiodec")]
    public static int AudiodecClearContext(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!AudioDecoders.TryGetValue(handle, out var decoder))
        {
            return SetReturn(ctx, AudiodecErrorInvalidHandle);
        }

        decoder.Atrac9?.Reset();
        return SetReturn(ctx, Ok);
    }

    private static bool IsValidAudioCodecType(uint codecType) =>
        codecType is AudiodecTypeAt9 or AudiodecTypeMp3 or AudiodecTypeAac;

    private static int TryReadAudioControl(
        CpuContext ctx,
        ulong controlAddress,
        uint codecType,
        bool decode,
        out AudioControl control)
    {
        control = default;
        if (controlAddress == 0)
        {
            return AudiodecErrorInvalidCtrlPointer;
        }

        Span<byte> controlData = stackalloc byte[32];
        if (!ctx.Memory.TryRead(controlAddress, controlData))
        {
            return AudiodecErrorInvalidCtrlPointer;
        }

        var paramAddress = BinaryPrimitives.ReadUInt64LittleEndian(controlData);
        var bsiInfoAddress = BinaryPrimitives.ReadUInt64LittleEndian(controlData[8..]);
        var auInfoAddress = BinaryPrimitives.ReadUInt64LittleEndian(controlData[16..]);
        var pcmItemAddress = BinaryPrimitives.ReadUInt64LittleEndian(controlData[24..]);
        if (paramAddress == 0)
        {
            return AudiodecErrorInvalidParamPointer;
        }

        if (bsiInfoAddress == 0)
        {
            return AudiodecErrorInvalidBsiInfoPointer;
        }

        if (auInfoAddress == 0)
        {
            return AudiodecErrorInvalidAuInfoPointer;
        }

        if (pcmItemAddress == 0)
        {
            return AudiodecErrorInvalidPcmItemPointer;
        }

        Span<byte> auInfo = stackalloc byte[24];
        if (!ctx.Memory.TryRead(auInfoAddress, auInfo))
        {
            return AudiodecErrorInvalidAuInfoPointer;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(auInfo) != auInfo.Length)
        {
            return AudiodecErrorInvalidAuInfoSize;
        }

        Span<byte> pcmItem = stackalloc byte[24];
        if (!ctx.Memory.TryRead(pcmItemAddress, pcmItem))
        {
            return AudiodecErrorInvalidPcmItemPointer;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(pcmItem) != pcmItem.Length)
        {
            return AudiodecErrorInvalidPcmItemSize;
        }

        var auAddress = BinaryPrimitives.ReadUInt64LittleEndian(auInfo[8..]);
        var auSize = BinaryPrimitives.ReadUInt32LittleEndian(auInfo[16..]);
        var pcmAddress = BinaryPrimitives.ReadUInt64LittleEndian(pcmItem[8..]);
        var pcmSize = BinaryPrimitives.ReadUInt32LittleEndian(pcmItem[16..]);
        if (decode && auAddress == 0)
        {
            return AudiodecErrorInvalidAuPointer;
        }

        if (decode && pcmAddress == 0)
        {
            return AudiodecErrorInvalidPcmPointer;
        }

        var parameterResult = TryReadAudioParameters(
            ctx,
            codecType,
            paramAddress,
            bsiInfoAddress,
            out var wordSize,
            out var atrac9Config,
            out var aacMaxChannels,
            out var aacSampleRateIndex);
        if (parameterResult != Ok)
        {
            return parameterResult;
        }

        if (decode && auSize == 0)
        {
            return AudiodecErrorInvalidAuSize;
        }

        if (decode && pcmSize == 0)
        {
            return AudiodecErrorInvalidPcmSize;
        }

        control = new AudioControl(
            paramAddress,
            bsiInfoAddress,
            auInfoAddress,
            pcmItemAddress,
            auAddress,
            auSize,
            pcmAddress,
            pcmSize,
            wordSize,
            atrac9Config,
            aacMaxChannels,
            aacSampleRateIndex);
        return Ok;
    }

    private static int TryReadAudioParameters(
        CpuContext ctx,
        uint codecType,
        ulong paramAddress,
        ulong bsiInfoAddress,
        out int wordSize,
        out byte[]? atrac9Config,
        out uint aacMaxChannels,
        out uint aacSampleRateIndex)
    {
        wordSize = 0;
        atrac9Config = null;
        aacMaxChannels = 0;
        aacSampleRateIndex = 0;

        var paramSize = codecType switch
        {
            AudiodecTypeAt9 => 12,
            AudiodecTypeMp3 => 8,
            AudiodecTypeAac => 24,
            _ => 0,
        };
        var bsiSize = codecType switch
        {
            AudiodecTypeAt9 => 36,
            AudiodecTypeMp3 => 24,
            AudiodecTypeAac => 20,
            _ => 0,
        };
        if (paramSize == 0 || bsiSize == 0)
        {
            return AudiodecErrorInvalidType;
        }

        Span<byte> param = stackalloc byte[paramSize];
        if (!ctx.Memory.TryRead(paramAddress, param))
        {
            return AudiodecErrorInvalidParamPointer;
        }

        var suppliedParamSize = BinaryPrimitives.ReadUInt32LittleEndian(param);
        if (codecType == AudiodecTypeAac
                ? suppliedParamSize < paramSize
                : suppliedParamSize != paramSize)
        {
            return AudiodecErrorInvalidParamSize;
        }

        Span<byte> bsi = stackalloc byte[bsiSize];
        if (!ctx.Memory.TryRead(bsiInfoAddress, bsi))
        {
            return AudiodecErrorInvalidBsiInfoPointer;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bsi) != bsiSize)
        {
            return AudiodecErrorInvalidBsiInfoSize;
        }

        wordSize = BinaryPrimitives.ReadInt32LittleEndian(param[4..]);
        if (wordSize is < 0 or > 2)
        {
            return AudiodecErrorInvalidWordLength;
        }

        if (codecType == AudiodecTypeAt9)
        {
            atrac9Config = param[8..12].ToArray();
        }
        else if (codecType == AudiodecTypeAac)
        {
            aacSampleRateIndex = BinaryPrimitives.ReadUInt32LittleEndian(param[12..]);
            aacMaxChannels = BinaryPrimitives.ReadUInt32LittleEndian(param[16..]);
        }

        return Ok;
    }

    private static AudioDecoderState? CreateAudioDecoder(uint codecType, AudioControl control)
    {
        if (codecType == AudiodecTypeAt9)
        {
            var atrac9 = new Atrac9DecodeState();
            if (control.Atrac9Config is null || !atrac9.TryInitialize(control.Atrac9Config))
            {
                return null;
            }

            var config = atrac9.Config!;
            return new AudioDecoderState
            {
                CodecType = codecType,
                WordSize = control.WordSize,
                Channels = config.ChannelCount,
                SampleRate = config.SampleRate,
                FrameBytes = config.SuperframeBytes,
                FramesPerSuperframe = config.FramesPerSuperframe,
                FrameSamples = config.FrameSamples,
                Atrac9 = atrac9,
            };
        }

        if (codecType == AudiodecTypeMp3)
        {
            return new AudioDecoderState
            {
                CodecType = codecType,
                WordSize = control.WordSize,
                Channels = 2,
                SampleRate = 48_000,
                FrameBytes = 1_441,
                FramesPerSuperframe = 1,
                FrameSamples = 1_152,
            };
        }

        var channels = control.AacMaxChannels == 0
            ? 2
            : unchecked((int)Math.Min(control.AacMaxChannels, 8));
        return new AudioDecoderState
        {
            CodecType = codecType,
            WordSize = control.WordSize,
            Channels = channels,
            SampleRate = GetAacSampleRate(control.AacSampleRateIndex),
            FrameBytes = 4_608,
            FramesPerSuperframe = 1,
            FrameSamples = 2_048,
        };
    }

    private static void DecodeAtrac9(CpuContext ctx, AudioDecoderState decoder, AudioControl control)
    {
        var input = ArrayPool<byte>.Shared.Rent(checked((int)control.AuSize));
        var output = ArrayPool<byte>.Shared.Rent(checked((int)control.PcmSize));
        try
        {
            if (!ctx.Memory.TryRead(control.AuAddress, input.AsSpan(0, checked((int)control.AuSize))))
            {
                WriteAudioDecoderInfo(ctx, control.BsiInfoAddress, decoder, AudiodecErrorInvalidAuPointer);
                WriteAudioBufferSizes(ctx, control, 0, 0);
                return;
            }

            var result = decoder.Atrac9!.Decode(
                input.AsSpan(0, checked((int)control.AuSize)),
                output.AsSpan(0, checked((int)control.PcmSize)),
                GetAtrac9Encoding(decoder.WordSize),
                decoder.Channels,
                multipleFrames: false);

            var outputWritten = result.OutputWritten;
            if (outputWritten != 0 &&
                !ctx.Memory.TryWrite(control.PcmAddress, output.AsSpan(0, outputWritten)))
            {
                outputWritten = 0;
            }

            WriteAudioBufferSizes(
                ctx,
                control,
                unchecked((uint)result.InputConsumed),
                unchecked((uint)outputWritten));
            WriteAudioDecoderInfo(ctx, control.BsiInfoAddress, decoder, result.Status);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private static void DecodeAudioSilence(CpuContext ctx, AudioDecoderState decoder, AudioControl control)
    {
        var wantedPcm = checked(
            decoder.FrameSamples *
            decoder.Channels *
            GetAudioBytesPerSample(decoder.WordSize));
        var outputSize = Math.Min(control.PcmSize, unchecked((uint)wantedPcm));
        ClearGuestMemory(ctx, control.PcmAddress, outputSize);
        WriteAudioBufferSizes(
            ctx,
            control,
            Math.Min(control.AuSize, unchecked((uint)decoder.FrameBytes)),
            outputSize);
        WriteAudioDecoderInfo(ctx, control.BsiInfoAddress, decoder, Ok);
    }

    private static void WriteAudioBufferSizes(
        CpuContext ctx,
        AudioControl control,
        uint inputConsumed,
        uint outputWritten)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, inputConsumed);
        _ = ctx.Memory.TryWrite(control.AuInfoAddress + 16, value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, outputWritten);
        _ = ctx.Memory.TryWrite(control.PcmItemAddress + 16, value);
    }

    private static void WriteAudioDecoderInfo(
        CpuContext ctx,
        ulong infoAddress,
        AudioDecoderState decoder,
        int result)
    {
        if (decoder.CodecType == AudiodecTypeAt9)
        {
            Span<byte> info = stackalloc byte[36];
            info.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(info, unchecked((uint)info.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], unchecked((uint)decoder.Channels));
            var superframeSamples = checked(decoder.FrameSamples * decoder.FramesPerSuperframe);
            var bitrate = superframeSamples == 0
                ? 0u
                : unchecked((uint)((ulong)decoder.FrameBytes * 8 * (uint)decoder.SampleRate /
                                   (uint)superframeSamples));
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], bitrate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[12..], unchecked((uint)decoder.SampleRate));
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], unchecked((uint)decoder.FrameBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(info[20..], unchecked((uint)decoder.FramesPerSuperframe));
            BinaryPrimitives.WriteUInt32LittleEndian(
                info[24..],
                unchecked((uint)(decoder.FrameBytes / Math.Max(decoder.FramesPerSuperframe, 1))));
            BinaryPrimitives.WriteUInt32LittleEndian(info[28..], unchecked((uint)decoder.FrameSamples));
            BinaryPrimitives.WriteInt32LittleEndian(info[32..], result);
            _ = ctx.Memory.TryWrite(infoAddress, info);
            return;
        }

        if (decoder.CodecType == AudiodecTypeMp3)
        {
            Span<byte> info = stackalloc byte[24];
            info.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(info, unchecked((uint)info.Length));
            BinaryPrimitives.WriteInt32LittleEndian(info[20..], result);
            _ = ctx.Memory.TryWrite(infoAddress, info);
            return;
        }

        Span<byte> aacInfo = stackalloc byte[20];
        aacInfo.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(aacInfo, unchecked((uint)aacInfo.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(aacInfo[4..], unchecked((uint)decoder.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(aacInfo[8..], unchecked((uint)decoder.Channels));
        BinaryPrimitives.WriteInt32LittleEndian(aacInfo[16..], result);
        _ = ctx.Memory.TryWrite(infoAddress, aacInfo);
    }

    private static Atrac9PcmEncoding GetAtrac9Encoding(int wordSize) =>
        wordSize switch
        {
            0 => Atrac9PcmEncoding.Signed32,
            1 => Atrac9PcmEncoding.Signed16,
            2 => Atrac9PcmEncoding.Float,
            _ => throw new ArgumentOutOfRangeException(nameof(wordSize)),
        };

    private static int GetAudioBytesPerSample(int wordSize) =>
        wordSize == 1 ? sizeof(short) : sizeof(int);

    private static int GetAacSampleRate(uint index)
    {
        ReadOnlySpan<int> rates =
        [
            96_000, 88_200, 64_000, 48_000, 44_100, 32_000,
            24_000, 22_050, 16_000, 12_000, 11_025, 8_000,
        ];
        return index < rates.Length ? rates[unchecked((int)index)] : 48_000;
    }

    private static void ClearGuestMemory(CpuContext ctx, ulong address, uint byteCount)
    {
        Span<byte> zero = stackalloc byte[256];
        var cursor = address;
        var remaining = byteCount;
        while (remaining != 0)
        {
            var length = unchecked((int)Math.Min(remaining, (uint)zero.Length));
            if (!ctx.Memory.TryWrite(cursor, zero[..length]))
            {
                return;
            }

            cursor += unchecked((uint)length);
            remaining -= unchecked((uint)length);
        }
    }

    internal static void ResetAudioDecodersForTests()
    {
        AudioDecoders.Clear();
        AudioCodecInitCounts.Clear();
        Interlocked.Exchange(ref _nextAudioHandle, 0);
    }

    private static bool TryWriteHandle(CpuContext ctx, ulong address, ulong handle) =>
        ctx.TryWriteUInt64(address, handle);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
