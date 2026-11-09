// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static bool TryDecodeVagBlock(CpuContext ctx, VoiceState voice, ulong address, ulong length, out short[]? samples)
    {
        samples = null;
        if (length is 0 or > 8 * 1024 * 1024 || address > ulong.MaxValue - length) return false;
        var raw = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            var data = raw.AsSpan(0, (int)length);
            if (!ctx.Memory.TryRead(address, data)) return false;
            if (Ngs2VagDecoder.IsVag(data))
            {
                if (voice.Channels != 1 || !Ngs2VagDecoder.TryDecode(data, out var waveform)) return false;
                samples = waveform.Samples;
                return true;
            }
            var units = data.Length / (16 * voice.Channels);
            if (units == 0) return false;
            var channelData = ArrayPool<byte>.Shared.Rent(units * 16);
            try
            {
                var decoded = new short[voice.Channels][];
                var frames = int.MaxValue;
                for (var channel = 0; channel < voice.Channels; channel++)
                {
                    for (var unit = 0; unit < units; unit++)
                        data.Slice((unit * voice.Channels + channel) * 16, 16).CopyTo(channelData.AsSpan(unit * 16));
                    decoded[channel] = Ngs2VagDecoder.Decode(channelData.AsSpan(0, units * 16), voice.SourceRate).Samples;
                    frames = Math.Min(frames, decoded[channel].Length);
                }
                samples = new short[frames * voice.Channels];
                for (var frame = 0; frame < frames; frame++)
                    for (var channel = 0; channel < voice.Channels; channel++) samples[frame * voice.Channels + channel] = decoded[channel][frame];
                return frames > 0;
            }
            finally { ArrayPool<byte>.Shared.Return(channelData); }
        }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }
}
