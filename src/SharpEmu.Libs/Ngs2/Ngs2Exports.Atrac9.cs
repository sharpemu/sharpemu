// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using LibAtrac9;
using SharpEmu.Libs.Audio;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static bool DecodeAtrac9Samples(VoiceState voice, WaveBlock block, Atrac9Config config, uint sample)
    {
        if (sample < block.DecodedOffset) return false;
        if (sample >= block.DecodedOffset && sample < (ulong)block.DecodedOffset + (uint)block.DecodedFrames) return true;
        var decoder = voice.Atrac9!;
        if (block.EncodedData is not { } input) return false;
        var frameSamples = config.SuperframeSamples;
        var frameValues = frameSamples * voice.Channels;
        // Keep two frames so interpolation can read both sides of a superframe boundary.
        block.Decoded ??= new short[2 * frameValues];
        if (block.EncodedCursor == 0 && (!block.Continuous || block.Repeated != 0)) decoder.Reset();
        while (sample >= (ulong)block.DecodedOffset + (uint)block.DecodedFrames)
        {
            if (block.EncodedCursor + (uint)config.SuperframeBytes > block.ByteLength) return false;
            if (block.DecodedFrames == 2 * frameSamples)
            {
                block.Decoded.AsSpan(frameValues, frameValues).CopyTo(block.Decoded);
                block.DecodedOffset += (uint)frameSamples;
                block.DecodedFrames = frameSamples;
            }
            var output = MemoryMarshal.AsBytes(block.Decoded.AsSpan(block.DecodedFrames * voice.Channels, frameValues));
            var result = decoder.Decode(input.AsSpan((int)block.EncodedCursor, config.SuperframeBytes), output,
                Atrac9PcmEncoding.Signed16, voice.Channels, multipleFrames: false);
            if (result.Status != 0 || result.InputConsumed != config.SuperframeBytes || result.OutputWritten != output.Length)
            {
                Trace($"atrac9 decode failed: status=0x{result.Status:X} address=0x{block.Address + block.EncodedCursor:X}");
                return false;
            }
            block.EncodedCursor += (uint)result.InputConsumed;
            voice.DecodedBytes += (uint)result.InputConsumed;
            block.DecodedFrames += frameSamples;
        }
        if (block.Repeats != 0 && block.LoopFirstSample is null)
            block.LoopFirstSample = block.Decoded.AsSpan((int)(block.Skip - block.DecodedOffset) * voice.Channels, voice.Channels).ToArray();
        return true;
    }
}
