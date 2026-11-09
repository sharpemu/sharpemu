// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using LibAtrac9;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

// Builds SceNgs2WaveformInfo out of a raw waveform image, the way
// sceNgs2ParseWaveformData/File/User do. Layout and every derived quantity come
// from the libNgs2 ABI (SceNgs2WaveformFormat, SceNgs2WaveformBlock,
// SceNgs2WaveformInfo and the audio-unit table documented on the info fields).
internal static class Ngs2WaveformParser
{
    // libNgs2 waveform types.
    internal const uint WaveformTypePcmI8 = 0x10;
    internal const uint WaveformTypePcmU8 = 0x11;
    internal const uint WaveformTypePcmI16L = 0x12;
    internal const uint WaveformTypePcmI24L = 0x14;
    internal const uint WaveformTypePcmI32L = 0x16;
    internal const uint WaveformTypePcmF32L = 0x18;
    internal const uint WaveformTypePcmF64L = 0x1A;
    internal const uint WaveformTypeVag = 0x1C;
    internal const uint WaveformTypeAtrac9 = 0x40;

    // sizeof(SceNgs2WaveformFormat): 6 x uint32.
    internal const int FormatSize = 0x18;
    // sizeof(SceNgs2WaveformBlock): uintptr dataOffset, size_t dataSize,
    // 4 x uint32, uintptr userData.
    internal const int BlockSize = 0x28;
    // SCE_NGS2_WAVEFORM_INFO_MAX_BLOCKS.
    internal const int MaxBlocks = 4;
    internal const int BlockArrayOffset = 0x48;
    // sizeof(SceNgs2WaveformInfo): format (0x18) + 12 x uint32 (0x30, the last
    // one padded to 8) + aBlock[4].
    internal const int WaveformInfoSize = BlockArrayOffset + (MaxBlocks * BlockSize);

    // HE-VAG: one 16-byte ADPCM unit carries 28 samples per channel.
    private const int VagUnitBytesPerChannel = 16;
    private const int VagUnitSamples = 28;

    // WAVE_FORMAT_EXTENSIBLE SubFormat GUID slot inside the "fmt " chunk.
    private const int SubFormatOffset = 0x18;
    // {47E142D2-36BA-4D8D-88FC-61654F8C836C} — Sony ATRAC9.
    private static ReadOnlySpan<byte> Atrac9SubFormatGuid =>
    [
        0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D,
        0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C,
    ];

    // KSDATAFORMAT_SUBTYPE_PCM {00000001-0000-0010-8000-00AA00389B71}.
    private static ReadOnlySpan<byte> PcmSubFormatGuid =>
    [
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00,
        0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    ];

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {00000003-0000-0010-8000-00AA00389B71}.
    private static ReadOnlySpan<byte> FloatSubFormatGuid =>
    [
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00,
        0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
    ];

    internal struct WaveformInfo
    {
        public uint WaveformType;
        public uint NumChannels;
        public uint SampleRate;
        public uint ConfigData;
        public uint FrameMargin;
        public uint FrameOffset;

        public uint DataOffset;
        public uint DataSize;
        public uint LoopBeginPosition;
        public uint LoopEndPosition;
        public uint NumSamples;

        public uint AudioUnitSize;
        public uint NumAudioUnitSamples;
        public uint NumAudioUnitPerFrame;
        public uint AudioFrameSize;
        public uint NumAudioFrameSamples;
        public uint NumDelaySamples;
        public uint NumBlocks;
    }

    // Serializes the parsed description into the guest SceNgs2WaveformInfo the
    // caller supplied. Exactly WaveformInfoSize bytes are produced; the single
    // block mirrors the whole data chunk, which is what the library reports for a
    // file that carries no explicit block table.
    internal static void Write(in WaveformInfo info, Span<byte> destination)
    {
        destination[..WaveformInfoSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x00..], info.WaveformType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x04..], info.NumChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x08..], info.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x0C..], info.ConfigData);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x10..], info.FrameMargin);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x14..], info.FrameOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x18..], info.DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x1C..], info.DataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x20..], info.LoopBeginPosition);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x24..], info.LoopEndPosition);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x28..], info.NumSamples);

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x2C..], info.AudioUnitSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x30..], info.NumAudioUnitSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x34..], info.NumAudioUnitPerFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x38..], info.AudioFrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x3C..], info.NumAudioFrameSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x40..], info.NumDelaySamples);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x44..], info.NumBlocks);

        if (info.NumBlocks == 0)
        {
            return;
        }

        // aBlock[0]: dataOffset/dataSize plus the sample span, so a title can feed
        // the block straight back into sceNgs2VoiceControl.
        var block = destination[BlockArrayOffset..];
        BinaryPrimitives.WriteUInt64LittleEndian(block[0x00..], info.DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(block[0x08..], info.DataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x14..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x18..], info.NumSamples);
    }

    // True when the image starts with a container this parser understands.
    internal static bool IsSupportedContainer(ReadOnlySpan<byte> data) =>
        Ngs2VagDecoder.IsVag(data) ||
        (data.Length >= 12 &&
            data[..4].SequenceEqual("RIFF"u8) &&
            data.Slice(8, 4).SequenceEqual("WAVE"u8));

    internal static bool TryParse(ReadOnlySpan<byte> data, out WaveformInfo info)
    {
        info = default;
        if (Ngs2VagDecoder.IsVag(data))
        {
            return TryParseVag(data, ref info);
        }

        return TryParseRiff(data, ref info);
    }

    // Classic Sony "VAGp" container: 0x30-byte big-endian header, then 16-byte
    // PS-ADPCM units. libNgs2 maps this onto SCE_NGS2_WAVEFORM_TYPE_VAG with
    // 28 samples per unit.
    private static bool TryParseVag(ReadOnlySpan<byte> data, ref WaveformInfo info)
    {
        if (data.Length < Ngs2VagDecoder.VagHeaderSize)
        {
            return false;
        }

        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
        var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        var channels = data[0x1E] == 0 ? 1u : data[0x1E];
        var available = (uint)(data.Length - Ngs2VagDecoder.VagHeaderSize);
        var dataSize = declaredSize == 0 || declaredSize > available ? available : declaredSize;

        info.WaveformType = WaveformTypeVag;
        info.NumChannels = channels;
        info.SampleRate = sampleRate == 0 ? 48000 : sampleRate;
        info.DataOffset = Ngs2VagDecoder.VagHeaderSize;
        info.DataSize = dataSize;
        info.AudioUnitSize = (uint)VagUnitBytesPerChannel * channels;
        info.NumAudioUnitSamples = VagUnitSamples;
        info.NumAudioUnitPerFrame = 1;
        info.NumSamples = info.AudioUnitSize == 0
            ? 0
            : dataSize / info.AudioUnitSize * VagUnitSamples;
        FinishDerivedFields(ref info);
        return true;
    }

    private static bool TryParseRiff(ReadOnlySpan<byte> data, ref WaveformInfo info)
    {
        if (data.Length < 12 ||
            !data[..4].SequenceEqual("RIFF"u8) ||
            !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var haveFormat = false;
        uint dataOffset = 0;
        uint dataSize = 0;
        uint loopBegin = 0;
        uint loopEnd = 0;
        uint declaredSamples = 0;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkId = data.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var body = offset + 8;
            var bodySize = (int)Math.Min(chunkSize, (uint)(data.Length - body));
            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (!TryParseFormatChunk(data.Slice(body, bodySize), ref info))
                {
                    return false;
                }

                haveFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataOffset = (uint)body;
                dataSize = (uint)bodySize;
            }
            else if (chunkId.SequenceEqual("fact"u8) && bodySize >= 4)
            {
                // "fact" carries the decoded sample count for compressed formats.
                declaredSamples = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body, 4));
            }
            else if (chunkId.SequenceEqual("smpl"u8) && bodySize >= 0x3C)
            {
                // Sampler chunk: numSampleLoops at +0x1C, first loop start/end at
                // +0x2C/+0x30 of the chunk body.
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body + 0x1C, 4)) != 0)
                {
                    loopBegin = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body + 0x2C, 4));
                    loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body + 0x30, 4));
                }
            }

            // RIFF chunks are word aligned.
            var advance = (ulong)chunkSize + (chunkSize & 1);
            if (advance > (ulong)(int.MaxValue - body))
            {
                break;
            }

            offset = body + (int)advance;
        }

        if (!haveFormat || dataSize == 0)
        {
            return false;
        }

        info.DataOffset = dataOffset;
        info.DataSize = dataSize;
        info.LoopBeginPosition = loopBegin;
        info.LoopEndPosition = loopEnd;

        if (info.WaveformType == WaveformTypeAtrac9)
        {
            // The ATRAC9 superframe is the NGS2 "audio frame"; a single frame is
            // the audio unit. NumDelaySamples is the encoder delay, one frame.
            info.NumSamples = declaredSamples != 0
                ? declaredSamples
                : info.AudioUnitSize == 0 || info.NumAudioUnitPerFrame == 0
                    ? 0
                    : dataSize / (info.AudioUnitSize * info.NumAudioUnitPerFrame) *
                        (info.NumAudioUnitSamples * info.NumAudioUnitPerFrame);
            info.NumDelaySamples = info.NumAudioUnitSamples;
        }
        else if (info.AudioUnitSize != 0)
        {
            info.NumSamples = dataSize / info.AudioUnitSize;
        }

        FinishDerivedFields(ref info);
        return true;
    }

    // WAVE "fmt " chunk: wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec,
    // nBlockAlign, wBitsPerSample, [cbSize, extension...].
    private static bool TryParseFormatChunk(ReadOnlySpan<byte> chunk, ref WaveformInfo info)
    {
        if (chunk.Length < 16)
        {
            return false;
        }

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);

        info.NumChannels = channels == 0 ? 1u : channels;
        info.SampleRate = sampleRate == 0 ? 48000u : sampleRate;
        info.NumAudioUnitPerFrame = 1;
        info.NumAudioUnitSamples = 1;

        // WAVE_FORMAT_EXTENSIBLE resolves through the SubFormat GUID at +0x18.
        // Sony's ATRAC9 GUID is followed by { uint32 versionInfo; uint8 config[4] }.
        var extensionSize = chunk.Length >= 18
            ? BinaryPrimitives.ReadUInt16LittleEndian(chunk[16..])
            : 0;
        if (formatTag == 0xFFFE)
        {
            if (extensionSize < 22 || chunk.Length < SubFormatOffset + 16)
            {
                return false;
            }

            var subFormat = chunk.Slice(SubFormatOffset, 16);
            if (subFormat.SequenceEqual(Atrac9SubFormatGuid))
            {
                return TryParseAtrac9Extension(chunk, ref info);
            }

            if (subFormat.SequenceEqual(PcmSubFormatGuid))
            {
                formatTag = 0x0001;
            }
            else if (subFormat.SequenceEqual(FloatSubFormatGuid))
            {
                formatTag = 0x0003;
            }
            else
            {
                return false;
            }
        }

        switch (formatTag)
        {
            case 0x0001: // WAVE_FORMAT_PCM
                info.WaveformType = bitsPerSample switch
                {
                    8 => WaveformTypePcmU8,
                    16 => WaveformTypePcmI16L,
                    24 => WaveformTypePcmI24L,
                    32 => WaveformTypePcmI32L,
                    _ => 0,
                };
                break;
            case 0x0003: // WAVE_FORMAT_IEEE_FLOAT
                info.WaveformType = bitsPerSample switch
                {
                    32 => WaveformTypePcmF32L,
                    64 => WaveformTypePcmF64L,
                    _ => 0,
                };
                break;
            default:
                info.WaveformType = 0;
                break;
        }

        if (info.WaveformType == 0)
        {
            return false;
        }

        // PCM: the audio unit is one interleaved frame and carries one sample.
        var bytesPerSample = bitsPerSample / 8u;
        info.AudioUnitSize = blockAlign != 0 ? blockAlign : bytesPerSample * info.NumChannels;
        return info.AudioUnitSize != 0;
    }

    private static bool TryParseAtrac9Extension(ReadOnlySpan<byte> chunk, ref WaveformInfo info)
    {
        // Sony's at9 fmt extension: SubFormat GUID (+0x18), uint32 versionInfo
        // (+0x28), uint8 configData[4] (+0x2C).
        const int configOffset = SubFormatOffset + 16 + 4;
        if (chunk.Length < configOffset + 4)
        {
            return false;
        }

        var configData = BinaryPrimitives.ReadUInt32LittleEndian(chunk[configOffset..]);
        Atrac9Config config;
        try
        {
            config = new Atrac9Config(chunk.Slice(configOffset, 4).ToArray());
        }
        catch (Exception)
        {
            return false;
        }

        info.WaveformType = WaveformTypeAtrac9;
        info.ConfigData = configData;
        info.NumChannels = (uint)config.ChannelCount;
        info.SampleRate = (uint)config.SampleRate;
        info.AudioUnitSize = (uint)config.FrameBytes;
        info.NumAudioUnitSamples = (uint)config.FrameSamples;
        info.NumAudioUnitPerFrame = (uint)config.FramesPerSuperframe;
        return true;
    }

    // ABI: audioFrameSize = audioUnitSize x numAudioUnitPerFrame and
    // numAudioFrameSamples = numAudioUnitSamples x numAudioUnitPerFrame.
    private static void FinishDerivedFields(ref WaveformInfo info)
    {
        if (info.NumAudioUnitPerFrame == 0)
        {
            info.NumAudioUnitPerFrame = 1;
        }

        info.AudioFrameSize = info.AudioUnitSize * info.NumAudioUnitPerFrame;
        info.NumAudioFrameSamples = info.NumAudioUnitSamples * info.NumAudioUnitPerFrame;
        if (info.LoopEndPosition == 0 && info.LoopBeginPosition == 0 && info.NumSamples != 0)
        {
            info.LoopEndPosition = info.NumSamples - 1;
        }

        info.NumBlocks = 1;
    }
}
