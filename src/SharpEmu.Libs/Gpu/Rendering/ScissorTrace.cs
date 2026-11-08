// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Gpu.Rendering;

// Keep startup traffic in a bounded history until a small window clips a full-target viewport.
internal sealed class ScissorTrace
{
    internal static readonly ScissorTrace? Instance = CreateConfigured();

    private readonly object _gate = new();
    private readonly Queue<string> _history = new();
    private readonly TextWriter _output;
    private readonly int _historyLimit;
    private readonly int _followupLimit;
    private long _sequence;
    private int _followup;
    private bool _triggered;
    private bool _complete;

    internal ScissorTrace(TextWriter output, int historyLimit = 8192, int followupLimit = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(followupLimit);
        _output = output;
        _historyLimit = historyLimit;
        _followupLimit = followupLimit;
    }

    internal bool Active => !Volatile.Read(ref _complete);

    private static ScissorTrace? CreateConfigured()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SCISSOR") != "1") return null;
        var trace = new ScissorTrace(Console.Error);
        Console.Error.WriteLine("[GPU][SCISSOR_TRACE] armed history_limit=8192 followup_limit=1024 " +
            "trigger=small-window-on-full-viewport; this condition is diagnostic, not necessarily an error.");
        return trace;
    }

    internal static bool IsScissorRegister(uint offset) =>
        offset is PaScScreenScissorTl or PaScScreenScissorBr or PaClClipCntl or PaClVteCntl or PaScModeCntl0 ||
        offset is >= PaScWindowOffset and <= PaScWindowOffset + 11 ||
        offset is >= PaScGenericScissorTl and <= PaScViewportScissor15Br ||
        offset is >= PaClViewportXScale and <= PaClViewportZOffset15;

    internal void RegisterWrite(RegisterBanks banks, ulong submitId, int queueId, in PacketContext packet,
        uint offset, uint value, ulong tableAddress = 0, uint entry = 0, uint count = 0)
    {
        if (!Active || !IsScissorRegister(offset)) return;
        Record($"event=register submit={submitId} queue={queueId} bank={Id(banks)} context={Id(banks.Context)} " +
            $"packet=0x{packet.PacketAddress:X16} header=0x{packet.Header:X8} source={(tableAddress == 0 ? "direct" : "table")} " +
            $"table=0x{tableAddress:X16} entry={entry} count={count} register=0x{offset:X4} value=0x{value:X8}");
    }

    internal void RegisterTable(RegisterBanks banks, ulong submitId, int queueId, in PacketContext packet,
        ulong tableAddress, uint count)
    {
        if (!Active) return;
        Record($"event=table submit={submitId} queue={queueId} bank={Id(banks)} context={Id(banks.Context)} " +
            $"packet=0x{packet.PacketAddress:X16} header=0x{packet.Header:X8} table=0x{tableAddress:X16} count={count}");
    }

    internal void ContextOperation(ulong submitId, int queueId, ulong packetAddress, string operation,
        RegisterBanks banks, ContextRegisters before)
    {
        if (!Active) return;
        Record($"event=context submit={submitId} queue={queueId} packet=0x{packetAddress:X16} operation={operation} " +
            $"bank={Id(banks)} pushed={banks.ContextPushed} before_context={Id(before)} after_context={Id(banks.Context)} " +
            $"before=[{Describe(before)}] after=[{Describe(banks.Context)}]");
    }

    internal void DrawIssued(RegisterBanks banks, ulong submitId, int queueId, ulong packetAddress)
    {
        if (!Active) return;
        Record($"event=draw-issued submit={submitId} queue={queueId} packet=0x{packetAddress:X16} " +
            $"bank={Id(banks)} context={Id(banks.Context)} {Describe(banks.Context)}");
    }

    internal void Draw(RegisterBanks banks, ulong submitId, ulong packetAddress, uint width, uint height,
        ulong colorAddress, ulong depthAddress, in DynamicDrawState state)
    {
        if (!Active) return;
        var viewport = FormattableString.Invariant($"({state.ViewportX},{state.ViewportY},{state.ViewportWidth},{state.ViewportHeight})");
        var window = banks.Context.ScreenViewport;
        var windowIgnored = window.WindowScissorRight > window.WindowScissorLeft && window.WindowScissorBottom > window.WindowScissorTop &&
            (state.Scissor.Left < window.WindowScissorLeft || state.Scissor.Top < window.WindowScissorTop ||
             state.Scissor.Right > window.WindowScissorRight || state.Scissor.Bottom > window.WindowScissorBottom);
        Record($"event=draw-execute submit={submitId} packet=0x{packetAddress:X16} bank={Id(banks)} context={Id(banks.Context)} " +
            $"target={width}x{height} color=0x{colorAddress:X16} depth=0x{depthAddress:X16} " +
            $"vs=0x{banks.Shader.Vertex.ExportAddress:X16} ps=0x{banks.Shader.Pixel.Address:X16} host_viewport={viewport} " +
            $"final={state.Scissor.Left},{state.Scissor.Top},{state.Scissor.Right},{state.Scissor.Bottom} window_ignored={windowIgnored} {Describe(banks.Context)}",
            ShouldTrigger(banks.Context, width, height, in state));
    }

    internal static bool ShouldTrigger(ContextRegisters context, uint width, uint height, in DynamicDrawState state)
    {
        var window = context.ScreenViewport;
        var windowWidth = (long)window.WindowScissorRight - window.WindowScissorLeft;
        var windowHeight = (long)window.WindowScissorBottom - window.WindowScissorTop;
        return width > 0 && height > 0 && windowWidth > 0 && windowHeight > 0 &&
            (windowWidth * 2 < width || windowHeight * 2 < height) &&
            MathF.Abs(state.ViewportWidth) >= width && MathF.Abs(state.ViewportHeight) >= height;
    }

    private static int Id(object value) => RuntimeHelpers.GetHashCode(value);

    private static string Describe(ContextRegisters context)
    {
        var s = context.ScreenViewport;
        var v = s.Primary;
        var transform = FormattableString.Invariant($"({v.XScale},{v.YScale},{v.XOffset},{v.YOffset})");
        var clips = string.Join(";", Enumerable.Range(0, ScreenViewportRegisters.ClipRectangleCount).Select(i =>
            $"{s.ClipRectangleLeft[i]},{s.ClipRectangleTop[i]},{s.ClipRectangleRight[i]},{s.ClipRectangleBottom[i]},{s.ClipRectangleWindowOffsetEnable[i]}"));
        return $"screen={s.ScreenScissorLeft},{s.ScreenScissorTop},{s.ScreenScissorRight},{s.ScreenScissorBottom} " +
            $"window={s.WindowScissorLeft},{s.WindowScissorTop},{s.WindowScissorRight},{s.WindowScissorBottom} " +
            $"generic={s.GenericScissorLeft},{s.GenericScissorTop},{s.GenericScissorRight},{s.GenericScissorBottom} " +
            $"viewport_scissor={v.ScissorLeft},{v.ScissorTop},{v.ScissorRight},{v.ScissorBottom} viewport_transform={transform} " +
            $"offset={s.WindowOffsetX},{s.WindowOffsetY} offset_enable={s.WindowScissorWindowOffsetEnable},{s.GenericScissorWindowOffsetEnable},{v.ScissorWindowOffsetEnable} " +
            $"viewport_enable={context.ScanMode.ViewportScissorEnable} clip_disable={context.Clip.ClipDisable} " +
            $"transform=0x{s.TransformControl:X8} clip_rule=0x{s.ClipRectangleRule:X4} clip_rectangles=[{clips}]";
    }

    internal void Record(string details, bool trigger = false)
    {
        lock (_gate)
        {
            if (_complete) return;
            var line = $"[GPU][SCISSOR_TRACE] seq={++_sequence} tick={Stopwatch.GetTimestamp()} managed_tid={Environment.CurrentManagedThreadId} {details}";
            if (!_triggered)
            {
                if (_history.Count == _historyLimit) _history.Dequeue();
                _history.Enqueue(line);
                if (!trigger) return;
                FlushHistory("small-window-on-full-viewport");
                _triggered = true;
            }
            else
            {
                _output.WriteLine(line);
                _followup++;
            }

            if (_followup >= _followupLimit) Complete("followup-limit");
        }
    }

    internal void Finish(string reason)
    {
        lock (_gate)
        {
            if (_complete) return;
            if (!_triggered) FlushHistory(reason);
            Complete(reason);
        }
    }

    private void FlushHistory(string reason)
    {
        _output.WriteLine($"[GPU][SCISSOR_TRACE] begin reason={reason} history_entries={_history.Count}");
        foreach (var line in _history) _output.WriteLine(line);
        _history.Clear();
    }

    private void Complete(string reason)
    {
        _output.WriteLine($"[GPU][SCISSOR_TRACE] end reason={reason} last_seq={_sequence}");
        _output.Flush();
        Volatile.Write(ref _complete, true);
    }
}
