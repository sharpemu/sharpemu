// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private const int InvalidControlAddress = unchecked((int)0x804A8360);
    private const int InvalidControlSize = unchecked((int)0x804A8361);
    private const int InvalidControlId = unchecked((int)0x804A8362);
    private const int InvalidWaveformFormat = unchecked((int)0x804A8431);

    private sealed class VoicePort
    {
        public float Volume = 1f;
        public int MatrixId = -1;
        public uint PatchType;
        public ulong Destination;
        public uint BufferId;
    }

    private sealed class WaveBlock
    {
        public ulong Address;
        public ulong ByteLength;
        public uint Skip;
        public uint Frames;
        public uint Repeats;
        public uint Repeated;
        public bool JoinPrevious;
        public bool Continuous;
        public ulong UserData;
        public short[]? Decoded;
        public byte[]? EncodedData;
        public ulong EncodedCursor;
        public uint DecodedOffset;
        public int DecodedFrames;
        public short[]? LoopFirstSample;
    }

    private static VoicePort Port(VoiceState voice, uint index)
    {
        if (!voice.Ports.TryGetValue(index, out var port)) voice.Ports[index] = port = new VoicePort();
        return port;
    }

    private static int HandleVoiceParams(CpuContext ctx, ulong handle, ulong address)
    {
        if (address == 0) return InvalidControlAddress;
        Span<byte> data = stackalloc byte[128];
        lock (StateGate)
        {
            if (!Voices.TryGetValue(handle, out var voice)) return OrbisNgs2ErrorInvalidVoiceHandle;
            // next is a signed relative offset, not the structure's size. A bound also rejects cycles.
            for (var count = 0; count < 256; count++)
            {
                if (!ctx.Memory.TryRead(address, data[..8])) return InvalidControlAddress;
                var size = BinaryPrimitives.ReadUInt16LittleEndian(data);
                var next = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                if (size < 8 || size > 4096) return InvalidControlSize;
                var length = Math.Min(size, data.Length);
                if (address > ulong.MaxValue - size || !ctx.Memory.TryRead(address, data[..length])) return InvalidControlAddress;
                var result = ApplyControl(ctx, voice, id, data[..length]);
                if (result != 0) return result;
                if (next == 0) return 0;
                if (next > 0 ? address > ulong.MaxValue - (ulong)next : address < (ulong)-next) return InvalidControlAddress;
                address = next > 0 ? address + (ulong)next : address - (ulong)-next;
            }
        }
        return InvalidControlSize;
    }

    private static int ApplyControl(CpuContext ctx, VoiceState voice, uint id, ReadOnlySpan<byte> data)
    {
        // Sampler and custom-sampler share their waveform controls, but have different rack IDs.
        if ((id >> 16) is 0x1000 or 0x4001)
        {
            switch (id & 0xffff)
            {
                case 0: return data.Length >= 40 ? SetFormat(voice, data[8..]) : InvalidControlSize;
                case 1:
                    if (data.Length < 32) return InvalidControlSize;
                    voice.SourceAddr = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
                    return AddBlocks(ctx, voice, BinaryPrimitives.ReadUInt64LittleEndian(data[24..]), U32(data, 20), U32(data, 16));
                case 4:
                    foreach (var block in voice.Blocks) block.Repeats = 0;
                    return 0;
                case 5:
                    if (data.Length < 16) return InvalidControlSize;
                    var pitch = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
                    if (!float.IsFinite(pitch) || pitch < 0 || pitch > 16) return InvalidWaveformFormat;
                    voice.Pitch = pitch;
                    return 0;
                default: return 0;
            }
        }

        switch (id)
        {
            case 1:
                return data.Length >= 24 ? SetMatrix(ctx, voice, (int)U32(data, 8), U32(data, 12), BinaryPrimitives.ReadUInt64LittleEndian(data[16..])) : InvalidControlSize;
            case 2:
                if (data.Length < 16 || U32(data, 8) >= 32) return InvalidControlSize;
                var level = BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
                if (!float.IsFinite(level) || Math.Abs(level) > 4) return InvalidControlSize;
                Port(voice, U32(data, 8)).Volume = level;
                return 0;
            case 3:
                if (data.Length < 16 || U32(data, 8) >= 32) return InvalidControlSize;
                Port(voice, U32(data, 8)).MatrixId = (int)U32(data, 12);
                return 0;
            case 5:
                if (data.Length < 24 || U32(data, 8) >= 32) return InvalidControlSize;
                var port = Port(voice, U32(data, 8));
                port.Destination = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
                port.PatchType = port.Destination == 0 ? 0u : 1u;
                return 0;
            case 6:
                if (data.Length < 12) return InvalidControlSize;
                KickEvent(voice, U32(data, 8));
                return 0;
            case 7:
                if (data.Length < 32) return InvalidControlSize;
                voice.Callback = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
                voice.CallbackData = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
                voice.CallbackFlags = U32(data, 24);
                return 0;
            case 0x20000000:
            case 0x30000000:
                if (data.Length < 16 || U32(data, 8) is < 1 or > 8) return InvalidControlSize;
                voice.Channels = (int)U32(data, 8);
                return 0;
            case 0x30000004:
                if (data.Length < 16) return InvalidControlSize;
                var gain = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
                if (!float.IsFinite(gain) || Math.Abs(gain) > 32) return InvalidControlSize;
                voice.Gain = gain;
                return 0;
            case 0x30000005:
                if (data.Length < 16 || U32(data, 8) >= 32) return InvalidControlSize;
                voice.OutputId = U32(data, 8);
                return 0;
            default:
                // Existing effects controls remain accepted; they do not replace the dry PCM path.
                return 0;
        }
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static int SetFormat(VoiceState voice, ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return InvalidControlSize;
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var channels = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (type != 0 && (PcmSampleBytes(type) == 0 && type is not (0x1c or 0x40) || channels is < 1 or > 8 || rate is < 1 or > 192000))
            return InvalidWaveformFormat;
        Atrac9DecodeState? atrac9 = null;
        if (type == 0x40)
        {
            var config = ReadAtrac9Config(data.Slice(12, 4));
            if (config is null || config.ChannelCount != channels || config.SampleRate != rate || U32(data, 16) != 0 || U32(data, 20) != 0)
                return InvalidWaveformFormat;
            atrac9 = new Atrac9DecodeState();
            if (!atrac9.TryInitialize(config.ConfigData)) return InvalidWaveformFormat;
        }
        voice.WaveformType = type;
        voice.Atrac9 = atrac9;
        voice.Channels = channels == 0 ? 2 : (int)channels;
        voice.SourceRate = rate == 0 ? DefaultSampleRate : (int)rate;
        voice.ConfigData = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        voice.Blocks.Clear();
        voice.PcmCacheBlock = null;
        voice.Position = 0;
        voice.DecodedSamples = voice.DecodedBytes = 0;
        voice.ErrorCode = 0;
        voice.InUse = true;
        voice.Playing = voice.Paused = voice.Stopped = false;
        return 0;
    }

    private static int AddBlocks(CpuContext ctx, VoiceState voice, ulong address, uint count, uint flags)
    {
        if (count > 256 || count != 0 && address == 0 || (flags & 12) == 0 && voice.Blocks.Count + count > 1024) return InvalidControlSize;
        Span<byte> data = stackalloc byte[Ngs2WaveformParser.BlockSize];
        var blocks = new List<WaveBlock>((int)count);
        var frameBytes = voice.Channels * PcmSampleBytes(voice.WaveformType);
        var atrac9 = voice.Atrac9?.Config;
        if (frameBytes == 0 && voice.WaveformType != 0x1c && atrac9 is null) return InvalidWaveformFormat;
        for (uint index = 0; index < count; index++)
        {
            if (address > ulong.MaxValue - (ulong)data.Length || !ctx.Memory.TryRead(address, data)) return InvalidControlAddress;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
            var skip = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
            var samples = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
            short[]? decoded = null;
            if (voice.WaveformType == 0x1c)
            {
                if (voice.SourceAddr > ulong.MaxValue - offset ||
                    !TryDecodeVagBlock(ctx, voice, voice.SourceAddr + offset, length, out decoded)) return InvalidWaveformFormat;
            }
            ulong available;
            if (atrac9 is not null)
            {
                // shortcut: split data-only ATRAC9 packets need assembly; explicit sample blocks contain whole superframes.
                if (length > 8 * 1024 * 1024 || length % (uint)atrac9.SuperframeBytes != 0) return InvalidWaveformFormat;
                available = length / (uint)atrac9.SuperframeBytes * (uint)atrac9.SuperframeSamples;
            }
            else available = decoded is null ? length / (uint)frameBytes : (ulong)(decoded.Length / voice.Channels);
            if (voice.SourceAddr > ulong.MaxValue - offset || voice.SourceAddr + offset > ulong.MaxValue - length ||
                available < skip || atrac9 is null && samples > available - skip || available - skip > uint.MaxValue) return InvalidWaveformFormat;
            // Compressed streams can supply an open-ended duration; data exhaustion still ends the block.
            var frames = samples == 0 ? (uint)(available - skip) : (uint)Math.Min(samples, available - skip);
            var block = new WaveBlock
            {
                Address = voice.SourceAddr + offset,
                ByteLength = length,
                Skip = skip,
                Frames = frames,
                Repeats = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
                JoinPrevious = (flags & 1) != 0 && (flags & 16) == 0,
                Continuous = (flags & 1) != 0,
                UserData = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
                Decoded = decoded,
            };
            if (atrac9 is not null)
            {
                // Keep queued compressed data alive independently of guest streaming-buffer reuse.
                block.EncodedData = new byte[(int)length];
                if (length != 0 && !ctx.Memory.TryRead(block.Address, block.EncodedData)) return InvalidControlAddress;
            }
            blocks.Add(block);
            address += (ulong)data.Length;
        }
        if ((flags & 12) != 0) { voice.Blocks.Clear(); voice.Position = 0; voice.PcmCacheBlock = null; }
        foreach (var block in blocks) voice.Blocks.Enqueue(block);
        return 0;
    }

    private static int SetMatrix(CpuContext ctx, VoiceState voice, int id, uint count, ulong address)
    {
        if (id is < 0 or >= 20 || count is < 1 or > 64) return InvalidControlSize;
        Span<byte> data = stackalloc byte[(int)count * 4];
        if (!ctx.Memory.TryRead(address, data)) return InvalidControlAddress;
        var levels = new float[count];
        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
            if (!float.IsFinite(levels[i]) || Math.Abs(levels[i]) > 4) return InvalidControlSize;
        }
        voice.Matrices[id] = levels;
        return 0;
    }

    private static void KickEvent(VoiceState voice, uint events)
    {
        if ((events & 8) != 0)
        {
            voice.Blocks.Clear(); voice.Position = 0;
            voice.PcmCacheBlock = null;
            voice.ErrorCode = 0;
            voice.InUse = voice.Playing = voice.Paused = voice.Stopped = false;
        }
        if ((events & 6) != 0) { voice.Playing = voice.Paused = false; voice.Stopped = true; }
        if ((events & 1) != 0) { voice.Playing = voice.InUse = true; voice.Paused = voice.Stopped = false; }
        if ((events & 16) != 0) voice.Paused = true;
        if ((events & 32) != 0) voice.Paused = false;
    }

    private static uint StateFlags(VoiceState voice) =>
        !voice.InUse ? 0 : 1u | (voice.Playing ? 2u : 0) | (voice.Paused ? 4u : 0) |
        (voice.Stopped ? 8u : 0) | (voice.ErrorCode != 0 ? 16u : 0) | (voice.Blocks.Count == 0 ? 32u : 0);

    private static int RunVoiceCommands(CpuContext ctx, ulong handle, ulong address, ulong count)
    {
        if (count > 4096 || count != 0 && address == 0) return InvalidControlAddress;
        Span<byte> data = stackalloc byte[16];
        Span<byte> valueData = stackalloc byte[40];
        lock (StateGate)
        {
            if (!Voices.TryGetValue(handle, out var voice)) return OrbisNgs2ErrorInvalidVoiceHandle;
            for (ulong index = 0; index < count; index++)
            {
                if (address > ulong.MaxValue - 16 || !ctx.Memory.TryRead(address, data)) return InvalidControlAddress;
                var id = BinaryPrimitives.ReadUInt32LittleEndian(data);
                var value = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
                var numValues = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
                var component = (id >> 16) & 255;
                var command = id & 0x3fff;
                var slot = id >> 24;
                var result = 0;
                if (ShouldTrace()) Trace($"command voice=0x{handle:X} id=0x{id:X} type={data[5]:X} value=0x{value:X}");
                if ((id & 0xc000) != 0) return InvalidControlId;
                if (component == 0)
                {
                    switch (command)
                    {
                        case 1:
                            if (!ctx.Memory.TryRead(value, valueData[..8])) return InvalidControlAddress;
                            var channels = BinaryPrimitives.ReadUInt32LittleEndian(valueData) & 255;
                            if (channels is > 0 and <= 8) voice.Channels = (int)channels;
                            voice.InUse = true;
                            break;
                        case 2: KickEvent(voice, (uint)value); break;
                        case 5: result = SetMatrix(ctx, voice, (int)slot, numValues, value); break;
                        case 6:
                            var level = BitConverter.UInt32BitsToSingle((uint)value);
                            if (slot >= 32 || !float.IsFinite(level) || Math.Abs(level) > 4) return InvalidControlSize;
                            Port(voice, slot).Volume = level;
                            break;
                        case 7:
                            if (slot >= 32) return InvalidControlSize;
                            Port(voice, slot).MatrixId = (int)value;
                            break;
                        case 9:
                            if (slot >= 32 || !ctx.Memory.TryRead(value, valueData[..32])) return InvalidControlAddress;
                            var port = Port(voice, slot);
                            port.PatchType = BinaryPrimitives.ReadUInt32LittleEndian(valueData);
                            port.Destination = BinaryPrimitives.ReadUInt64LittleEndian(valueData[8..]);
                            port.BufferId = BinaryPrimitives.ReadUInt32LittleEndian(valueData[8..]);
                            break;
                    }
                }
                else if (component == 1)
                {
                    switch (command)
                    {
                        case 0:
                            if (!ctx.Memory.TryRead(value, valueData[..24])) return InvalidControlAddress;
                            result = SetFormat(voice, valueData[..24]);
                            break;
                        case 1: result = AddBlocks(ctx, voice, value, numValues, data[4]); break;
                        case 2: voice.SourceAddr = value; break;
                        case 3:
                            var pitch = BitConverter.UInt32BitsToSingle((uint)value);
                            if (!float.IsFinite(pitch) || pitch < 0 || pitch > 16) return InvalidWaveformFormat;
                            voice.Pitch = pitch;
                            break;
                        case 4: foreach (var block in voice.Blocks) block.Repeats = 0; break;
                    }
                }
                if (result != 0) return result;
                address += 16;
            }
        }
        return 0;
    }
}
