// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.AvPlayer;

namespace SharpEmu.Libs.Bink;

internal sealed class FfmpegBinkFrameSource : IBinkFrameDecoder
{
    private readonly Process _process;
    private readonly Stream _output;
    private int _errorLines;
    private int _disposed;

    private FfmpegBinkFrameSource(
        Process process,
        uint width,
        uint height,
        uint framesPerSecondNumerator,
        uint framesPerSecondDenominator)
    {
        _process = process;
        _output = process.StandardOutput.BaseStream;
        Width = width;
        Height = height;
        FramesPerSecondNumerator = framesPerSecondNumerator;
        FramesPerSecondDenominator = framesPerSecondDenominator;
    }

    public uint Width { get; }

    public uint Height { get; }

    public uint FramesPerSecondNumerator { get; }

    public uint FramesPerSecondDenominator { get; }

    internal static bool IsAvailable => AvPlayerExports.FindFfmpeg() is not null;

    internal static bool TryOpen(
        string path,
        uint width,
        uint height,
        uint framesPerSecondNumerator,
        uint framesPerSecondDenominator,
        out FfmpegBinkFrameSource? source)
    {
        source = null;
        var ffmpeg = AvPlayerExports.FindFfmpeg();
        if (ffmpeg is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            source = new FfmpegBinkFrameSource(
                process,
                width,
                height,
                framesPerSecondNumerator,
                framesPerSecondDenominator);
            process.ErrorDataReceived += source.OnErrorData;
            process.BeginErrorReadLine();
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                             InvalidOperationException or
                                             System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Bink FFmpeg decoder could not start: {exception.Message}");
            return false;
        }
    }

    public bool TryDecodeNextFrame(Span<byte> destination)
    {
        try
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = _output.Read(destination[offset..]);
                if (read == 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Bink FFmpeg stream failed: {exception.Message}");
            }
            return false;
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data) ||
            Interlocked.Increment(ref _errorLines) > 20)
        {
            return;
        }
        Console.Error.WriteLine($"[LOADER][FFMPEG-BINK] {eventArgs.Data}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _output.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}
