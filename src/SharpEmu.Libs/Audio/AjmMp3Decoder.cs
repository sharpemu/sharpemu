// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using NLayer;
using System.Buffers.Binary;
using System.Reflection;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Stateful AJM MP3 (codec 0) decoder. GTA menu music arrives as ~960-byte
/// packets that NLayer must decode with a persistent bit-reservoir.
/// </summary>
internal sealed class AjmMp3Decoder
{
    private static readonly Type? MpegStreamReaderType =
        typeof(MpegFrameDecoder).Assembly.GetType("NLayer.Decoder.MpegStreamReader");

    private static readonly MethodInfo? NextFrameMethod =
        MpegStreamReaderType?.GetMethod(
            "NextFrame",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo? ClearBufferMethod =
        typeof(MpegFrameDecoder).Assembly.GetType("NLayer.Decoder.FrameBase")
            ?.GetMethod("ClearBuffer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly MpegFrameDecoder _decoder = new();
    private readonly object _gate = new();
    private byte[] _pending = Array.Empty<byte>();
    private readonly float[] _floatScratch = new float[1152 * 2];

    public ulong TotalDecodedSamples { get; private set; }

    public void Reset()
    {
        lock (_gate)
        {
            _decoder.Reset();
            _pending = Array.Empty<byte>();
            TotalDecodedSamples = 0;
        }
    }

    public DecodeResult Decode(ReadOnlySpan<byte> input, Span<byte> output, bool pcm16)
    {
        lock (_gate)
        {
            if (MpegStreamReaderType is null || NextFrameMethod is null)
            {
                return DecodeResult.Failed;
            }

            var merged = new byte[_pending.Length + input.Length];
            if (_pending.Length != 0)
            {
                _pending.CopyTo(merged, 0);
            }

            input.CopyTo(merged.AsSpan(_pending.Length));

            using var stream = new MemoryStream(merged, writable: false);
            object? reader;
            try
            {
                reader = Activator.CreateInstance(
                    MpegStreamReaderType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [stream],
                    culture: null);
            }
            catch
            {
                return DecodeResult.Failed;
            }

            if (reader is null)
            {
                return DecodeResult.Failed;
            }

            var outputOffset = 0;
            var inputConsumed = 0;
            var frames = 0u;
            var samplesThisCall = 0u;

            while (outputOffset < output.Length)
            {
                object? frameObj;
                try
                {
                    frameObj = NextFrameMethod.Invoke(reader, null);
                }
                catch
                {
                    break;
                }

                if (frameObj is not IMpegFrame frame)
                {
                    break;
                }

                try
                {
                    var frameOffset = GetFrameOffset(frameObj);
                    var frameLength = frame.FrameLength;
                    if (frameLength <= 0 || frameOffset + frameLength > merged.Length)
                    {
                        break;
                    }

                    int sampleCount;
                    try
                    {
                        sampleCount = _decoder.DecodeFrame(frame, _floatScratch, 0);
                    }
                    catch
                    {
                        _decoder.Reset();
                        inputConsumed = frameOffset + frameLength;
                        continue;
                    }

                    if (sampleCount <= 0)
                    {
                        inputConsumed = frameOffset + frameLength;
                        continue;
                    }

                    var channels = frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
                    var bytesPerSample = pcm16 ? 2 : 4;
                    var byteCount = sampleCount * bytesPerSample;
                    if (outputOffset + byteCount > output.Length)
                    {
                        // Not enough room for this frame — leave it for next job.
                        break;
                    }

                    if (pcm16)
                    {
                        WritePcm16(_floatScratch.AsSpan(0, sampleCount), output[outputOffset..]);
                    }
                    else
                    {
                        WriteFloat(_floatScratch.AsSpan(0, sampleCount), output[outputOffset..]);
                    }

                    outputOffset += byteCount;
                    inputConsumed = frameOffset + frameLength;
                    frames++;
                    samplesThisCall += (uint)(sampleCount / Math.Max(channels, 1));
                    TotalDecodedSamples += (ulong)(sampleCount / Math.Max(channels, 1));
                }
                finally
                {
                    try
                    {
                        ClearBufferMethod?.Invoke(frameObj, null);
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }

            _pending = inputConsumed < merged.Length
                ? merged[inputConsumed..]
                : Array.Empty<byte>();

            // Consume the portion of *this* input that left the pending window.
            var pendingBefore = merged.Length - input.Length;
            var consumedFromInput = Math.Clamp(inputConsumed - pendingBefore, 0, input.Length);

            return new DecodeResult(
                Success: frames > 0 || consumedFromInput > 0,
                InputConsumed: consumedFromInput,
                OutputWritten: outputOffset,
                Frames: frames,
                SamplesThisCall: samplesThisCall);
        }
    }

    private static int GetFrameOffset(object frameObj)
    {
        for (var type = frameObj.GetType(); type is not null; type = type.BaseType)
        {
            var prop = type.GetProperty(
                "Offset",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop?.GetValue(frameObj) is long offset)
            {
                return checked((int)offset);
            }
        }

        return 0;
    }

    private static void WritePcm16(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var scaled = sample < 0f ? sample * 32768f : sample * 32767f;
            var value = (short)Math.Clamp(MathF.Round(scaled), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], value);
        }
    }

    private static void WriteFloat(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var bits = BitConverter.SingleToInt32Bits(samples[i]);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], bits);
        }
    }

    internal readonly record struct DecodeResult(
        bool Success,
        int InputConsumed,
        int OutputWritten,
        uint Frames,
        uint SamplesThisCall)
    {
        public static DecodeResult Failed { get; } = new(false, 0, 0, 0, 0);
    }
}
