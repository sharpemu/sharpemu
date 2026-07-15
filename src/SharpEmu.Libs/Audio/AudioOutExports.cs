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
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
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

    public static void ShutdownAllPorts()
    {
        foreach (var handle in Ports.Keys)
        {
            if (Ports.TryRemove(handle, out var port))
            {
                port.Dispose();
            }
        }
    }

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
