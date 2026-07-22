// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Audio;

public static class AudioOutExports
{
    private static readonly ConcurrentDictionary<int, PortState> Ports = new();
    private static int _nextPortHandle;

    // Diagnostic: confirm sceAudioOutOutput is actually called and whether the
    // guest submits real samples or silence. Gated so it costs nothing when off.
    private static readonly bool _traceOutput = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_OUT"), "1", StringComparison.Ordinal);
    private static long _outputCount;

    private sealed class PortState : IDisposable
    {
        private readonly object _paceGate = new();
        private long _nextSilentOutput;

        public PortState(
            int userId,
            int type,
            uint bufferLength,
            uint frequency,
            int format,
            int channels,
            int bytesPerSample,
            bool isFloat,
            IHostAudioStream? backend)
        {
            UserId = userId;
            Type = type;
            BufferLength = bufferLength;
            Frequency = frequency;
            Format = format;
            Channels = channels;
            BytesPerSample = bytesPerSample;
            IsFloat = isFloat;
            Backend = backend;
        }

        public int UserId { get; }
        public int Type { get; }
        public uint BufferLength { get; }
        public uint Frequency { get; }
        public int Format { get; }
        public int Channels { get; }
        public int BytesPerSample { get; }
        public bool IsFloat { get; }
        public IHostAudioStream? Backend { get; }
        public volatile float Volume = 1.0f;
        public int BufferByteLength =>
            checked((int)BufferLength * Channels * BytesPerSample);

        public void PaceSilence()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextSilentOutput < now)
                {
                    _nextSilentOutput = now;
                }

                delay = _nextSilentOutput - now;
                _nextSilentOutput += checked(
                    (long)Math.Ceiling(
                        Stopwatch.Frequency * (double)BufferLength / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }

        public void Dispose() => Backend?.Dispose();
    }

    [SysAbiExport(
        Nid = "JfEPXVxhFqA",
        ExportName = "sceAudioOutInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "ekNvsT22rsY",
        ExportName = "sceAudioOutOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var bufferLength = unchecked((uint)ctx[CpuRegister.Rcx]);
        var frequency = unchecked((uint)ctx[CpuRegister.R8]);
        var format = unchecked((int)ctx[CpuRegister.R9]);
        if (bufferLength == 0 || frequency == 0 ||
            !TryGetFormat(format, out var channels, out var bytesPerSample, out var isFloat))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        IHostAudioStream? backend = null;
        string backendName;
        try
        {
            var audio = HostPlatform.Current.Audio;
            backend = audio.OpenStereoPcm16Stream(frequency);
            backendName = audio.BackendName;
        }
        catch (Exception exception)
        {
            backendName = "silent";
            Console.Error.WriteLine(
                $"[LOADER][WARN] AudioOut host backend unavailable: {exception.Message}");
        }

        var handle = Interlocked.Increment(ref _nextPortHandle);
        Ports[handle] = new PortState(
            userId,
            type,
            bufferLength,
            frequency,
            format,
            channels,
            bytesPerSample,
            isFloat,
            backend);
        Console.Error.WriteLine(
            $"[LOADER][INFO] AudioOut port {handle}: {frequency} Hz, " +
            $"{channels} ch, {(isFloat ? "float32" : "s16")}, " +
            $"{bufferLength} frames, backend={backendName}");
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "s1--uE9mBFw",
        ExportName = "sceAudioOutClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Ports.TryRemove(handle, out var port))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        port.Dispose();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "GrQ9s4IrNaQ",
        ExportName = "sceAudioOutGetPortState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutGetPortState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !Ports.TryGetValue(handle, out var port))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Same rule as AudioOut2 PortGetState: never bulk-write onto the caller
        // stack. Some titles place small locals next to the canary; a full
        // SceAudioOutPortState write smashes it.
        if (IsGuestStackAddress(stateAddress))
        {
            return ctx.SetReturn(0);
        }

        // SceAudioOutPortState: report a connected primary output at full volume
        // so pacing/mixing code sees a live port. We do no host rerouting, so
        // rerouteCounter and flag stay zero.
        Span<byte> state = stackalloc byte[16];
        state.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(state, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            state[2..], (ushort)port.Channels);
        state[7] = 127;
        if (!ctx.Memory.TryWrite(stateAddress, state))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    private static bool IsGuestStackAddress(ulong value) =>
        value >= 0x0000_7FF0_0000_0000UL && value <= 0x0000_7FFF_FFFF_FFFFUL;

    [SysAbiExport(
        Nid = "QOQtbeDqsT4",
        ExportName = "sceAudioOutOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOutput(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (!Ports.TryGetValue(handle, out var port))
        {
            // Host shutdown disposes the ports while guest audio threads are
            // still draining their last buffers; report success so the guest
            // winds down without a per-buffer error (and its WARN log flood).
            return ctx.SetReturn(_shutdown
                ? 0
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (sourceAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(port.BufferByteLength);
        try
        {
            var source = buffer.AsSpan(0, port.BufferByteLength);
            if (!ctx.Memory.TryRead(sourceAddress, source))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (_traceOutput)
            {
                var n = Interlocked.Increment(ref _outputCount);
                if (n <= 8 || n % 200 == 0)
                {
                    var peak = PeakAmplitude(source, port.IsFloat, port.BytesPerSample);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] audioout.output#{n} handle={handle} bytes={source.Length} ch={port.Channels} float={port.IsFloat} vol={port.Volume:F2} peak={peak:F4} backend={(port.Backend is null ? "none" : "coreaudio")}");
                }
            }

            if (port.Backend is null)
            {
                port.PaceSilence();
                return ctx.SetReturn(0);
            }

            var outputLength = checked((int)port.BufferLength * AudioPcmConversion.OutputFrameSize);
            var output = ArrayPool<byte>.Shared.Rent(outputLength);
            try
            {
                AudioPcmConversion.ConvertToStereoPcm16(
                    source,
                    output.AsSpan(0, outputLength),
                    checked((int)port.BufferLength),
                    port.Channels,
                    port.BytesPerSample,
                    port.IsFloat,
                    port.Volume);
                if (!port.Backend.Submit(output.AsSpan(0, outputLength)))
                {
                    port.PaceSilence();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }

            return ctx.SetReturn(0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [SysAbiExport(
        Nid = "b+uAV89IlxE",
        ExportName = "sceAudioOutSetVolume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutSetVolume(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var channelFlags = unchecked((uint)ctx[CpuRegister.Rsi]);
        var volumeArrayAddress = ctx[CpuRegister.Rdx];
        if (!Ports.TryGetValue(handle, out var port))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        const int unityVolume = 32768;
        var maxVolume = 0;
        var found = false;
        if (volumeArrayAddress != 0)
        {
            Span<byte> raw = stackalloc byte[sizeof(int)];
            for (var channel = 0; channel < 8; channel++)
            {
                if ((channelFlags & (1u << channel)) == 0)
                {
                    continue;
                }

                if (!ctx.Memory.TryRead(volumeArrayAddress + (ulong)(channel * sizeof(int)), raw))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw);
                maxVolume = Math.Max(maxVolume, value);
                found = true;
            }
        }

        if (found)
        {
            port.Volume = Math.Clamp(maxVolume / (float)unityVolume, 0f, 1f);
        }

        return ctx.SetReturn(0);
    }

    // Peak normalized amplitude [0,1] of an interleaved PCM buffer, used only by
    // the SHARPEMU_LOG_AUDIO_OUT diagnostic to distinguish real audio from silence.
    private static float PeakAmplitude(ReadOnlySpan<byte> source, bool isFloat, int bytesPerSample)
    {
        var peak = 0f;
        if (isFloat && bytesPerSample == 4)
        {
            for (var i = 0; i + 4 <= source.Length; i += 4)
            {
                var v = Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i, 4)));
                if (v > peak)
                {
                    peak = v;
                }
            }
        }
        else if (bytesPerSample == 2)
        {
            for (var i = 0; i + 2 <= source.Length; i += 2)
            {
                var v = Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i, 2)) / 32768f);
                if (v > peak)
                {
                    peak = v;
                }
            }
        }

        return peak;
    }

    public static void ShutdownAllPorts()
    {
        Volatile.Write(ref _shutdown, true);
        foreach (var handle in Ports.Keys)
        {
            if (Ports.TryRemove(handle, out var port))
            {
                port.Dispose();
            }
        }
    }

    private static bool _shutdown;

    private static bool TryGetFormat(
        int rawFormat,
        out int channels,
        out int bytesPerSample,
        out bool isFloat)
    {
        var format = rawFormat & 0xFF;
        channels = format switch
        {
            0 or 3 => 1,
            1 or 4 => 2,
            2 or 5 or 6 or 7 => 8,
            _ => 0,
        };
        bytesPerSample = format is >= 3 and <= 5 or 7 ? 4 : 2;
        isFloat = bytesPerSample == 4;
        return channels != 0;
    }
}
