// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// In-window performance HUD. The panel is rasterized on the CPU into a
/// small BGRA buffer each frame (embedded 5x7 font, no assets) and the
/// presenter blits it onto the swapchain image, so it needs no pipelines,
/// descriptors or blending state. Toggled with F1; SHARPEMU_OVERLAY=0
/// starts it hidden.
/// </summary>
public static class PerfOverlay
{
    public const int PanelWidth = 376;
    public const int PanelHeight = 176;

    private const int GlyphColumns = 5;
    private const int GlyphRows = 7;
    private const int Scale = 2;
    private const int CellWidth = (GlyphColumns + 1) * Scale;
    private const int LineHeight = (GlyphRows + 2) * Scale;
    private const int FrameHistorySize = 128;

    private static volatile bool _enabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_OVERLAY"),
        "0",
        StringComparison.Ordinal);

    private static long _lastPresentTimestamp;
    private static readonly double[] _frameMilliseconds = new double[FrameHistorySize];
    private static int _frameHistoryIndex;
    private static long _presentedInWindow;
    private static long _submittedInWindow;
    private static long _drawsInWindow;

    // Refreshed once per second so per-frame fills never allocate.
    private static long _statsWindowStart = Stopwatch.GetTimestamp();
    private static double _fps;
    private static double _submittedFps;
    private static double _drawsPerSecond;
    private static double _averageFrameMs;
    private static double _allocatedMbPerSecond;
    private static long _lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
    private static int _lastGen0 = GC.CollectionCount(0);
    private static int _lastGen1 = GC.CollectionCount(1);
    private static int _lastGen2 = GC.CollectionCount(2);
    private static int _gen0PerWindow;
    private static int _gen1PerWindow;
    private static int _gen2PerWindow;
    private static double _cpuPercent;
    private static TimeSpan _lastCpuTime = GetProcessCpuTime();
    private static string _line1 = string.Empty;
    private static string _line2 = string.Empty;
    private static string _line3 = string.Empty;
    private static string _line4 = string.Empty;

    public static bool Enabled => _enabled;

    public static void Toggle() => _enabled = !_enabled;

    /// <summary>Called by the presenter after each successful present.</summary>
    public static void RecordPresent()
    {
        var now = Stopwatch.GetTimestamp();
        var last = _lastPresentTimestamp;
        _lastPresentTimestamp = now;
        Interlocked.Increment(ref _presentedInWindow);
        if (last != 0)
        {
            var milliseconds = (now - last) * 1000.0 / Stopwatch.Frequency;
            if (milliseconds < 1000.0)
            {
                _frameMilliseconds[_frameHistoryIndex] = milliseconds;
                _frameHistoryIndex = (_frameHistoryIndex + 1) % FrameHistorySize;
            }
        }
    }

    /// <summary>Called on every guest flip submission.</summary>
    public static void RecordSubmit() => Interlocked.Increment(ref _submittedInWindow);

    /// <summary>Called per translated draw/dispatch executed.</summary>
    public static void RecordDraw() => Interlocked.Increment(ref _drawsInWindow);

    /// <summary>
    /// Rasterizes the panel into a BGRA byte span of PanelWidth x PanelHeight.
    /// Runs on the render thread.
    /// </summary>
    public static void Fill(Span<byte> bgra, int pendingWork, int inFlightSubmissions)
    {
        RefreshStatsIfDue(pendingWork, inFlightSubmissions);

        // Background: opaque dark slate.
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 0x20;
            bgra[i + 1] = 0x18;
            bgra[i + 2] = 0x14;
            bgra[i + 3] = 0xFF;
        }

        var y = 6;
        DrawString(bgra, 8, y, _line1, 0x60, 0xFF, 0x60);
        y += LineHeight;
        DrawString(bgra, 8, y, _line2, 0xFF, 0xFF, 0xFF);
        y += LineHeight;
        DrawString(bgra, 8, y, _line3, 0xB0, 0xD0, 0xFF);
        y += LineHeight;
        DrawString(bgra, 8, y, _line4, 0xB0, 0xB0, 0xB0);
        y += LineHeight + 4;
        DrawFrameGraph(bgra, 8, y, PanelWidth - 16, PanelHeight - y - 6);
    }

    private static void RefreshStatsIfDue(int pendingWork, int inFlightSubmissions)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - _statsWindowStart;
        if (elapsedTicks >= Stopwatch.Frequency)
        {
            var seconds = (double)elapsedTicks / Stopwatch.Frequency;
            _statsWindowStart = now;
            _fps = Interlocked.Exchange(ref _presentedInWindow, 0) / seconds;
            _submittedFps = Interlocked.Exchange(ref _submittedInWindow, 0) / seconds;
            _drawsPerSecond = Interlocked.Exchange(ref _drawsInWindow, 0) / seconds;

            double totalMs = 0;
            var samples = 0;
            foreach (var ms in _frameMilliseconds)
            {
                if (ms > 0)
                {
                    totalMs += ms;
                    samples++;
                }
            }

            _averageFrameMs = samples > 0 ? totalMs / samples : 0;

            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            _allocatedMbPerSecond = (allocated - _lastAllocatedBytes) / seconds / (1024.0 * 1024.0);
            _lastAllocatedBytes = allocated;

            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            _gen0PerWindow = gen0 - _lastGen0;
            _gen1PerWindow = gen1 - _lastGen1;
            _gen2PerWindow = gen2 - _lastGen2;
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            var cpuTime = GetProcessCpuTime();
            _cpuPercent = (cpuTime - _lastCpuTime).TotalSeconds / seconds * 100.0;
            _lastCpuTime = cpuTime;

            var drawsPerFrame = _fps > 0.5 ? _drawsPerSecond / _fps : 0;
            _line1 = $"FPS {_fps:0.0}  FLIP {_submittedFps:0.0}  {_averageFrameMs:0.0} MS";
            _line2 = $"DRAWS {_drawsPerSecond:0}/S  {drawsPerFrame:0}/F  Q {pendingWork}+{inFlightSubmissions}";
            _line3 = $"ALLOC {_allocatedMbPerSecond:0.0} MB/S  GC {_gen0PerWindow}/{_gen1PerWindow}/{_gen2PerWindow}";
            _line4 = $"CPU {_cpuPercent:0}%  HEAP {GC.GetTotalMemory(false) / (1024 * 1024)} MB  F1 HIDE";
        }
    }

    private static TimeSpan GetProcessCpuTime()
    {
        var usage = Environment.CpuUsage;
        return usage.UserTime + usage.PrivilegedTime;
    }

    private static void DrawFrameGraph(Span<byte> bgra, int x, int y, int width, int height)
    {
        if (height < 8)
        {
            return;
        }

        // Reference lines at 16.7ms and 33.3ms of a 40ms-tall scale.
        const double maxMs = 40.0;
        DrawHorizontalLine(bgra, x, y + height - (int)(16.7 / maxMs * height), width, 0x30, 0x60, 0x30);
        DrawHorizontalLine(bgra, x, y + height - (int)(33.3 / maxMs * height), width, 0x30, 0x30, 0x60);

        var barWidth = Math.Max(1, width / FrameHistorySize);
        for (var i = 0; i < FrameHistorySize; i++)
        {
            var ms = _frameMilliseconds[(_frameHistoryIndex + i) % FrameHistorySize];
            if (ms <= 0)
            {
                continue;
            }

            var barHeight = (int)Math.Min(height, ms / maxMs * height);
            var barX = x + i * barWidth;
            if (barX + barWidth > x + width)
            {
                break;
            }

            byte r, g, b;
            if (ms <= 17.5)
            {
                (r, g, b) = ((byte)0x40, (byte)0xE0, (byte)0x40);
            }
            else if (ms <= 34.0)
            {
                (r, g, b) = ((byte)0xE0, (byte)0xC0, (byte)0x30);
            }
            else
            {
                (r, g, b) = ((byte)0xE8, (byte)0x40, (byte)0x40);
            }

            for (var py = 0; py < barHeight; py++)
            {
                var rowY = y + height - 1 - py;
                for (var px = 0; px < barWidth; px++)
                {
                    SetPixel(bgra, barX + px, rowY, r, g, b);
                }
            }
        }
    }

    private static void DrawHorizontalLine(Span<byte> bgra, int x, int y, int width, byte r, byte g, byte b)
    {
        if (y < 0 || y >= PanelHeight)
        {
            return;
        }

        for (var px = 0; px < width; px++)
        {
            SetPixel(bgra, x + px, y, r, g, b);
        }
    }

    private static void DrawString(Span<byte> bgra, int x, int y, string text, byte r, byte g, byte b)
    {
        var penX = x;
        foreach (var rawChar in text)
        {
            var c = char.ToUpperInvariant(rawChar);
            if (c < ' ' || c > 'Z')
            {
                c = '?';
            }

            var glyph = Font.Slice((c - ' ') * GlyphColumns, GlyphColumns);
            for (var column = 0; column < GlyphColumns; column++)
            {
                var bits = glyph[column];
                for (var row = 0; row < GlyphRows; row++)
                {
                    if ((bits & (1 << row)) == 0)
                    {
                        continue;
                    }

                    for (var sy = 0; sy < Scale; sy++)
                    {
                        for (var sx = 0; sx < Scale; sx++)
                        {
                            SetPixel(
                                bgra,
                                penX + column * Scale + sx,
                                y + row * Scale + sy,
                                r,
                                g,
                                b);
                        }
                    }
                }
            }

            penX += CellWidth;
            if (penX + CellWidth > PanelWidth)
            {
                break;
            }
        }
    }

    private static void SetPixel(Span<byte> bgra, int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= PanelWidth || (uint)y >= PanelHeight)
        {
            return;
        }

        var offset = (y * PanelWidth + x) * 4;
        bgra[offset] = b;
        bgra[offset + 1] = g;
        bgra[offset + 2] = r;
        bgra[offset + 3] = 0xFF;
    }

    // Classic 5x7 column-encoded font (bit 0 = top row), ASCII 32..90.
    private static ReadOnlySpan<byte> Font =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, // ' '
        0x00, 0x00, 0x5F, 0x00, 0x00, // '!'
        0x00, 0x07, 0x00, 0x07, 0x00, // '"'
        0x14, 0x7F, 0x14, 0x7F, 0x14, // '#'
        0x24, 0x2A, 0x7F, 0x2A, 0x12, // '$'
        0x23, 0x13, 0x08, 0x64, 0x62, // '%'
        0x36, 0x49, 0x55, 0x22, 0x50, // '&'
        0x00, 0x05, 0x03, 0x00, 0x00, // '''
        0x00, 0x1C, 0x22, 0x41, 0x00, // '('
        0x00, 0x41, 0x22, 0x1C, 0x00, // ')'
        0x08, 0x2A, 0x1C, 0x2A, 0x08, // '*'
        0x08, 0x08, 0x3E, 0x08, 0x08, // '+'
        0x00, 0x50, 0x30, 0x00, 0x00, // ','
        0x08, 0x08, 0x08, 0x08, 0x08, // '-'
        0x00, 0x60, 0x60, 0x00, 0x00, // '.'
        0x20, 0x10, 0x08, 0x04, 0x02, // '/'
        0x3E, 0x51, 0x49, 0x45, 0x3E, // '0'
        0x00, 0x42, 0x7F, 0x40, 0x00, // '1'
        0x42, 0x61, 0x51, 0x49, 0x46, // '2'
        0x21, 0x41, 0x45, 0x4B, 0x31, // '3'
        0x18, 0x14, 0x12, 0x7F, 0x10, // '4'
        0x27, 0x45, 0x45, 0x45, 0x39, // '5'
        0x3C, 0x4A, 0x49, 0x49, 0x30, // '6'
        0x01, 0x71, 0x09, 0x05, 0x03, // '7'
        0x36, 0x49, 0x49, 0x49, 0x36, // '8'
        0x06, 0x49, 0x49, 0x29, 0x1E, // '9'
        0x00, 0x36, 0x36, 0x00, 0x00, // ':'
        0x00, 0x56, 0x36, 0x00, 0x00, // ';'
        0x00, 0x08, 0x14, 0x22, 0x41, // '<'
        0x14, 0x14, 0x14, 0x14, 0x14, // '='
        0x41, 0x22, 0x14, 0x08, 0x00, // '>'
        0x02, 0x01, 0x51, 0x09, 0x06, // '?'
        0x32, 0x49, 0x79, 0x41, 0x3E, // '@'
        0x7E, 0x11, 0x11, 0x11, 0x7E, // 'A'
        0x7F, 0x49, 0x49, 0x49, 0x36, // 'B'
        0x3E, 0x41, 0x41, 0x41, 0x22, // 'C'
        0x7F, 0x41, 0x41, 0x22, 0x1C, // 'D'
        0x7F, 0x49, 0x49, 0x49, 0x41, // 'E'
        0x7F, 0x09, 0x09, 0x09, 0x01, // 'F'
        0x3E, 0x41, 0x49, 0x49, 0x7A, // 'G'
        0x7F, 0x08, 0x08, 0x08, 0x7F, // 'H'
        0x00, 0x41, 0x7F, 0x41, 0x00, // 'I'
        0x20, 0x40, 0x41, 0x3F, 0x01, // 'J'
        0x7F, 0x08, 0x14, 0x22, 0x41, // 'K'
        0x7F, 0x40, 0x40, 0x40, 0x40, // 'L'
        0x7F, 0x02, 0x0C, 0x02, 0x7F, // 'M'
        0x7F, 0x04, 0x08, 0x10, 0x7F, // 'N'
        0x3E, 0x41, 0x41, 0x41, 0x3E, // 'O'
        0x7F, 0x09, 0x09, 0x09, 0x06, // 'P'
        0x3E, 0x41, 0x51, 0x21, 0x5E, // 'Q'
        0x7F, 0x09, 0x19, 0x29, 0x46, // 'R'
        0x46, 0x49, 0x49, 0x49, 0x31, // 'S'
        0x01, 0x01, 0x7F, 0x01, 0x01, // 'T'
        0x3F, 0x40, 0x40, 0x40, 0x3F, // 'U'
        0x1F, 0x20, 0x40, 0x20, 0x1F, // 'V'
        0x3F, 0x40, 0x38, 0x40, 0x3F, // 'W'
        0x63, 0x14, 0x08, 0x14, 0x63, // 'X'
        0x07, 0x08, 0x70, 0x08, 0x07, // 'Y'
        0x61, 0x51, 0x49, 0x45, 0x43, // 'Z'
    ];
}
