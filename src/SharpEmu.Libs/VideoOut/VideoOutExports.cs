// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SharpEmu.Libs.VideoOut;

public static class VideoOutExports
{
    private const int OrbisVideoOutErrorInvalidValue = unchecked((int)0x80290001);
    private const int OrbisVideoOutErrorInvalidAddress = unchecked((int)0x80290002);
    private const int OrbisVideoOutErrorResourceBusy = unchecked((int)0x80290009);
    private const int OrbisVideoOutErrorInvalidIndex = unchecked((int)0x8029000A);
    private const int OrbisVideoOutErrorInvalidHandle = unchecked((int)0x8029000B);
    private const int OrbisVideoOutErrorInvalidEventQueue = unchecked((int)0x8029000C);
    private const int OrbisVideoOutErrorInvalidEvent = unchecked((int)0x8029000D);
    private const int OrbisVideoOutErrorInvalidOption = unchecked((int)0x8029001A);
    private const int SceVideoOutBusTypeMain = 0;
    private const int SceVideoOutBufferAttributeOptionNone = 0;
    private const int SceVideoOutTilingModeLinear = 1;
    private const int MaxOpenPorts = 4;
    private const int MaxDisplayBuffers = 16;
    private const int MaxDisplayBufferGroups = 4;
    private const int MaxFrameDumps = 8;
    private const int VideoOutBufferAttributeSize = 0x28;
    private const int VideoOutBufferAttribute2Size = 0x50;
    private const int VideoOutBuffersEntrySize = 0x20;
    private const int VideoOutOutputStatusSize = 0x30;
    private const int VideoOutVblankStatusSize = 0x28;
    private const ulong SceVideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong SceVideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong SceVideoOutPixelFormatA2R10G10B10 = 0x88060000;
    private const ulong SceVideoOutPixelFormatA2R10G10B10Srgb = 0x88000000;
    private const ulong SceVideoOutPixelFormatA2R10G10B10Bt2020Pq = 0x88740000;
    // Prospero/PS5 format2 values are 64-bit encodings. The 0x22000000 field
    // selects R-first component order; notably, the 0x81000000... family is
    // packed 10:10:10:2 and must not be mistaken for an 8-bit RGBA format.
    private const ulong SceVideoOutPixelFormat2R8G8B8A8Srgb = 0x8000000022000000;
    private const ulong SceVideoOutPixelFormat2B8G8R8A8Srgb = 0x8000000000000000;
    private const ulong SceVideoOutPixelFormat2R10G10B10A2 = 0x8100000622000000;
    private const ulong SceVideoOutPixelFormat2B10G10R10A2 = 0x8100000600000000;
    private const ulong SceVideoOutPixelFormat2R10G10B10A2Srgb = 0x8100000022000000;
    private const ulong SceVideoOutPixelFormat2B10G10R10A2Srgb = 0x8100000000000000;
    private const ulong SceVideoOutPixelFormat2R10G10B10A2Bt2100Pq = 0x8100070422000000;
    private const ulong SceVideoOutPixelFormat2B10G10R10A2Bt2100Pq = 0x8100070400000000;
    private const ulong SceVideoOutInternalEventFlip = 0x6;
    // Distinct internal ident for vblank events. Games interpret events through
    // sceVideoOutGetEventId (mapped below), so the exact value is internal; only
    // its distinctness from the flip ident matters for GetEventId/GetEventData.
    private const ulong SceVideoOutInternalEventVblank = 0x40;
    private const short OrbisKernelEventFilterVideoOut = -13;

    private static readonly object _stateGate = new();
    private static readonly object _frameDumpGate = new();
    private static readonly Dictionary<int, VideoOutPortState> _ports = new();
    private static int _presentationWindowCloseNotified;
    private static int _vblankStopRequested;
    private static readonly Dictionary<(int Handle, int BufferIndex, ulong Address), ulong> _lastFrameFingerprints = new();
    private static int _nextHandle = 1;
    private static int _frameDumpCount;
    private static long _nextFrameDumpIndex;
    private static string _windowTitle = "SharpEmu VideoOut";
    private static readonly bool _logFrameRate = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEOOUT_FPS"),
        "1",
        StringComparison.Ordinal);
    private static long _frameRateWindowStart = Stopwatch.GetTimestamp();
    private static long _submittedFrameCount;
    private static int _diagnosticFlipCount;
    private static readonly int _holdFirstFlipMilliseconds =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_HOLD_FIRST_FLIP_MS"), out var holdMs)
            ? Math.Clamp(holdMs, 0, 60_000)
            : 0;
    private static readonly int _holdFlipNumber =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_HOLD_FLIP_NUMBER"), out var holdFlip)
            ? Math.Max(1, holdFlip)
            : 1;
    private static long _presentedFrameCount;

    static VideoOutExports()
    {
        RunPixelFormatSelfChecks();
    }

    public static void ConfigureApplicationInfo(string? title, string? titleId, string? version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(titleId))
        {
            parts.Add($"[{titleId.Trim()}]");
        }

        var application = parts.Count == 0 ? "VideoOut" : string.Join(' ', parts);
        var versionSuffix = string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version.Trim()}";
        lock (_stateGate)
        {
            _windowTitle = $"SharpEmu - {application}{versionSuffix}";
        }
    }

    internal static string GetWindowTitle()
    {
        lock (_stateGate)
        {
            return _windowTitle;
        }
    }

    internal static void SetSelectedGpuName(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
        {
            return;
        }

        lock (_stateGate)
        {
            _windowTitle = $"{_windowTitle} · {gpuName.Trim()}";
        }
    }

    public static void NotifyPresentationWindowClosed()
    {
        if (Interlocked.Exchange(ref _presentationWindowCloseNotified, 1) != 0)
        {
            return;
        }

        RequestHostShutdown("videoout-window-closed");
    }

    public static void NotifyHostInterrupt()
    {
        if (Interlocked.Exchange(ref _presentationWindowCloseNotified, 1) != 0)
        {
            return;
        }

        RequestHostShutdown("host-interrupt");
    }

    private static void RequestHostShutdown(string reason)
    {
        Console.Error.WriteLine($"[LOADER][INFO] Host shutdown requested: {reason}");
        VulkanVideoPresenter.RequestClose();
        AudioOutExports.ShutdownAllPorts();
        Interlocked.Exchange(ref _vblankStopRequested, 1);
        HostSessionControl.RequestShutdown(reason);
        ThreadPool.QueueUserWorkItem(static _ =>
        {
            Thread.Sleep(2000);
            Environment.Exit(0);
        });
    }

    private sealed class VideoOutPortState
    {
        public required int Handle { get; init; }
        public int FlipRate { get; set; }
        public ulong VblankCount { get; set; }
        public ulong FlipCount { get; set; }
        public int CurrentBuffer { get; set; } = -1;
        public uint OutputWidth { get; set; } = 1920;
        public uint OutputHeight { get; set; } = 1080;
        public uint RefreshRate { get; set; } = 60;
        public float Gamma { get; set; } = 1.0f;
        public VideoOutBufferGroup?[] Groups { get; } = new VideoOutBufferGroup?[MaxDisplayBufferGroups];
        public VideoOutBufferSlot[] BufferSlots { get; } = CreateBufferSlots();
        public List<FlipEventRegistration> FlipEvents { get; } = new();
        public List<FlipEventRegistration> VblankEvents { get; } = new();
        public long OpenTimestamp;
        public long LastVblankTimestamp;
    }

    private sealed class VideoOutBufferGroup
    {
        public required int Index { get; init; }
        public required BufferAttribute Attribute { get; init; }
    }

    private sealed class VideoOutBufferSlot
    {
        public int GroupIndex { get; set; } = -1;
        public ulong AddressLeft { get; set; }
        public ulong AddressRight { get; set; }
    }

    private readonly record struct FlipEventRegistration(ulong Equeue, ulong UserData);

    private readonly record struct BufferAttribute(
        ulong PixelFormat,
        uint TilingMode,
        uint AspectRatio,
        uint Width,
        uint Height,
        uint PitchInPixel,
        ulong Option);

    internal readonly record struct DisplayBufferInfo(
        ulong Address,
        ulong PixelFormat,
        uint TilingMode,
        uint Width,
        uint Height,
        uint PitchInPixel);

    [SysAbiExport(
        Nid = "Up36PTk687E",
        ExportName = "sceVideoOutOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var busType = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        _ = ctx[CpuRegister.Rcx];

        if (busType != SceVideoOutBusTypeMain || index != 0)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        if (userId != 0 && userId != 255)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        lock (_stateGate)
        {
            if (_ports.Count >= MaxOpenPorts)
            {
                return OrbisVideoOutErrorResourceBusy;
            }

            var handle = _nextHandle++;
            var openedAt = Stopwatch.GetTimestamp();
            _ports[handle] = new VideoOutPortState
            {
                Handle = handle,
                OpenTimestamp = openedAt,
                LastVblankTimestamp = openedAt,
            };
            return handle;
        }
    }

    [SysAbiExport(
        Nid = "Nv8c-Kb+DUM",
        ExportName = "sceVideoOutIsOutputSupported",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutIsOutputSupported(CpuContext ctx)
    {
        var busType = unchecked((int)ctx[CpuRegister.Rdi]);
        _ = ctx[CpuRegister.Rsi]; // pixelFormat
        _ = ctx[CpuRegister.Rdx]; // aspectRatio

        // The emulator supports any output configuration on the main bus.
        // Return 1 (supported) for SceVideoOutBusTypeMain, 0 otherwise.
        return busType == SceVideoOutBusTypeMain ? 1 : 0;
    }

    [SysAbiExport(
        Nid = "uquVH4-Du78",
        ExportName = "sceVideoOutClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_stateGate)
        {
            _ports.Remove(handle);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CBiu4mCE1DA",
        ExportName = "sceVideoOutSetFlipRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutSetFlipRate(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var rate = unchecked((int)ctx[CpuRegister.Rsi]);
        if (rate is < 0 or > 2)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        port.FlipRate = rate;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "w0hLuNarQxY",
        ExportName = "sceVideoOutConfigureOutput",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutConfigureOutput(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return TryGetPort(handle, out _)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : OrbisVideoOutErrorInvalidHandle;
    }

    [SysAbiExport(
        Nid = "+I4K03i3EL0",
        ExportName = "sceVideoOutInitializeOutputOptions",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutInitializeOutputOptions(CpuContext ctx)
    {
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "utPrVdxio-8",
        ExportName = "sceVideoOutGetOutputStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutGetOutputStatus(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        Span<byte> status = stackalloc byte[VideoOutOutputStatusSize];
        status.Clear();
        var resolutionClass = port.OutputWidth >= 3840 || port.OutputHeight >= 2160 ? 2 : 1;
        BinaryPrimitives.WriteInt32LittleEndian(status[0x00..0x04], resolutionClass);
        BinaryPrimitives.WriteInt32LittleEndian(status[0x04..0x08], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(status[0x08..0x10], port.RefreshRate);
        return ctx.Memory.TryWrite(statusAddress, status)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "DYhhWbJSeRg",
        ExportName = "sceVideoOutColorSettingsSetGamma_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutColorSettingsSetGamma(CpuContext ctx)
    {
        var settingsAddress = ctx[CpuRegister.Rdi];
        if (settingsAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        ctx.GetXmmRegister(0, out var xmm0Low, out _);
        var gamma = BitConverter.Int32BitsToSingle(unchecked((int)xmm0Low));
        if (!float.IsFinite(gamma) || gamma is < 0.1f or > 2.0f)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        Span<byte> gammaBytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(gammaBytes, BitConverter.SingleToInt32Bits(gamma));
        return ctx.Memory.TryWrite(settingsAddress, gammaBytes)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "pv9CI5VC+R0",
        ExportName = "sceVideoOutAdjustColor_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutAdjustColor(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var settingsAddress = ctx[CpuRegister.Rsi];
        if (settingsAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        Span<byte> gammaBytes = stackalloc byte[sizeof(float)];
        if (!ctx.Memory.TryRead(settingsAddress, gammaBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        port.Gamma = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(gammaBytes));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j6RaAUlaLv0",
        ExportName = "sceVideoOutWaitVblank",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutWaitVblank(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        // Wait to the next boundary of the emulated display refresh rather
        // than a raw Thread.Sleep(1): coarse sleeps overshoot to the
        // scheduler quantum, which mis-paces games that spin on vblank. A
        // caller that arrives past the boundary already missed the vblank:
        // report it immediately instead of charging a full extra interval.
        var intervalTicks = Stopwatch.Frequency / Math.Max(1, (long)port.RefreshRate);
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref port.LastVblankTimestamp);
        var target = last + intervalTicks;
        if (target <= now || target > now + intervalTicks)
        {
            Interlocked.CompareExchange(ref port.LastVblankTimestamp, now, last);
        }
        else
        {
            HostTiming.SleepUntil(target);
            Interlocked.CompareExchange(ref port.LastVblankTimestamp, target, last);
        }
        lock (_stateGate)
        {
            port.VblankCount++;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1FZBKy8HeNU",
        ExportName = "sceVideoOutGetVblankStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutGetVblankStatus(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        var now = Stopwatch.GetTimestamp();
        ulong count;
        long openedAt;
        lock (_stateGate)
        {
            openedAt = port.OpenTimestamp;
            var elapsedTicks = Math.Max(now - openedAt, 0);
            var elapsedCount = unchecked((ulong)(elapsedTicks *
                Math.Max(1L, (long)port.RefreshRate) / Stopwatch.Frequency));
            port.VblankCount = Math.Max(port.VblankCount, elapsedCount);
            count = port.VblankCount;
        }

        var elapsedMicroseconds = unchecked((ulong)(Math.Max(now - openedAt, 0) *
            1_000_000L / Stopwatch.Frequency));
        Span<byte> status = stackalloc byte[VideoOutVblankStatusSize];
        status.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(status, count);
        BinaryPrimitives.WriteUInt64LittleEndian(status[0x08..], elapsedMicroseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(status[0x10..], unchecked((ulong)now));
        status[0x20] = 0;
        return ctx.Memory.TryWrite(statusAddress, status)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "HXzjK9yI30k",
        ExportName = "sceVideoOutAddFlipEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutAddFlipEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var handle = unchecked((int)ctx[CpuRegister.Rsi]);
        var userData = ctx[CpuRegister.Rdx];
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (!KernelEventQueueCompatExports.IsValidEqueue(equeue))
        {
            return OrbisVideoOutErrorInvalidEventQueue;
        }

        lock (_stateGate)
        {
            var existingIndex = port.FlipEvents.FindIndex(registration => registration.Equeue == equeue);
            if (existingIndex >= 0)
            {
                port.FlipEvents[existingIndex] = new FlipEventRegistration(equeue, userData);
            }
            else
            {
                port.FlipEvents.Add(new FlipEventRegistration(equeue, userData));
            }
        }

        if (_traceVideoOut)
        {
            TraceVideoOut($"videoout.add_flip_event eq=0x{equeue:X16} handle={handle} udata=0x{userData:X16}");
        }
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Xru92wHJRmg",
        ExportName = "sceVideoOutAddVblankEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutAddVblankEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var handle = unchecked((int)ctx[CpuRegister.Rsi]);
        var userData = ctx[CpuRegister.Rdx];
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (!KernelEventQueueCompatExports.IsValidEqueue(equeue))
        {
            return OrbisVideoOutErrorInvalidEventQueue;
        }

        lock (_stateGate)
        {
            var existingIndex = port.VblankEvents.FindIndex(registration => registration.Equeue == equeue);
            if (existingIndex >= 0)
            {
                port.VblankEvents[existingIndex] = new FlipEventRegistration(equeue, userData);
            }
            else
            {
                port.VblankEvents.Add(new FlipEventRegistration(equeue, userData));
            }
        }

        // A guest that parks its main/render loop on a vblank event needs a
        // steady tick to advance; start the emulated vblank cadence on demand.
        StartVblankThreadOnce();
        TraceVideoOut($"videoout.add_vblank_event eq=0x{equeue:X16} handle={handle} udata=0x{userData:X16}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "oNOQn3knW6s",
        ExportName = "sceVideoOutDeleteVblankEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutDeleteVblankEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var handle = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        lock (_stateGate)
        {
            port.VblankEvents.RemoveAll(registration => registration.Equeue == equeue);
        }

        TraceVideoOut($"videoout.delete_vblank_event eq=0x{equeue:X16} handle={handle}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "U46NwOiJpys",
        ExportName = "sceVideoOutSubmitFlip",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutSubmitFlip(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferIndex = unchecked((int)ctx[CpuRegister.Rsi]);
        var flipMode = unchecked((int)ctx[CpuRegister.Rdx]);
        var flipArg = unchecked((long)ctx[CpuRegister.Rcx]);
        return SubmitFlip(ctx, handle, bufferIndex, flipMode, flipArg, submitGpuImage: true);
    }

    [SysAbiExport(
        Nid = "SbU3dwp80lQ",
        ExportName = "sceVideoOutGetFlipStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutGetFlipStatus(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        ulong count;
        uint currentBuffer;
        lock (_stateGate)
        {
            count = port.FlipCount;
            currentBuffer = unchecked((uint)port.CurrentBuffer);
        }

        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x00, count);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x08, 0);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x10, 0);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x18, 0);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x20, currentBuffer);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "zgXifHT9ErY",
        ExportName = "sceVideoOutIsFlipPending",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutIsFlipPending(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TryGetPort(handle, out _))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "U2JJtSqNKZI",
        ExportName = "sceVideoOutGetEventId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutGetEventId(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!ctx.TryReadUInt64(eventAddress, out var ident) ||
            !TryReadInt16(ctx, eventAddress + 0x08, out var filter))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (filter != OrbisKernelEventFilterVideoOut)
        {
            return OrbisVideoOutErrorInvalidEvent;
        }

        // sceVideoOutGetEventId reports the event kind: 0 = flip, 1 = vblank.
        if (ident == SceVideoOutInternalEventFlip)
        {
            return 0;
        }

        if (ident == SceVideoOutInternalEventVblank)
        {
            return 1;
        }

        return OrbisVideoOutErrorInvalidEvent;
    }

    [SysAbiExport(
        Nid = "rWUTcKdkUzQ",
        ExportName = "sceVideoOutGetEventData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutGetEventData(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        if (eventAddress == 0 || dataAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (!ctx.TryReadUInt64(eventAddress, out var ident) ||
            !TryReadInt16(ctx, eventAddress + 0x08, out var filter) ||
            !ctx.TryReadUInt64(eventAddress + 0x10, out var data))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (filter != OrbisKernelEventFilterVideoOut ||
            (ident != SceVideoOutInternalEventFlip && ident != SceVideoOutInternalEventVblank))
        {
            return OrbisVideoOutErrorInvalidEvent;
        }

        var decodedData = unchecked((ulong)(unchecked((long)data) >> 16));
        return ctx.TryWriteUInt64(dataAddress, decodedData)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    public static int SubmitFlipFromAgc(CpuContext ctx, int handle, int bufferIndex, int flipMode, long flipArg) =>
        SubmitFlip(ctx, handle, bufferIndex, flipMode, flipArg, submitGpuImage: false);

    internal static void SubmitHostRgbaFrame(ReadOnlySpan<byte> rgbaFrame, uint width, uint height)
    {
        if (rgbaFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        var bgraFrame = new byte[rgbaFrame.Length];
        for (var offset = 0; offset < rgbaFrame.Length; offset += 4)
        {
            bgraFrame[offset + 0] = rgbaFrame[offset + 2];
            bgraFrame[offset + 1] = rgbaFrame[offset + 1];
            bgraFrame[offset + 2] = rgbaFrame[offset + 0];
            bgraFrame[offset + 3] = rgbaFrame[offset + 3];
        }

        GuestGpu.Current.Submit(bgraFrame, width, height);
    }

    internal static bool TryGetDisplayBufferInfo(int handle, int bufferIndex, out DisplayBufferInfo info)
    {
        info = default;
        if (bufferIndex < 0 || bufferIndex >= MaxDisplayBuffers)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (!_ports.TryGetValue(handle, out var port))
            {
                return false;
            }

            var slot = port.BufferSlots[bufferIndex];
            if (slot.AddressLeft == 0 ||
                slot.GroupIndex < 0 ||
                slot.GroupIndex >= port.Groups.Length ||
                port.Groups[slot.GroupIndex] is not { } group)
            {
                return false;
            }

            var attribute = group.Attribute;
            info = new DisplayBufferInfo(
                slot.AddressLeft,
                attribute.PixelFormat,
                attribute.TilingMode,
                attribute.Width,
                attribute.Height,
                attribute.PitchInPixel);
            return true;
        }
    }

    [SysAbiExport(
        Nid = "MTxxrOCeSig",
        ExportName = "sceVideoOutSetWindowModeMargins",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutSetWindowModeMargins(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        _ = unchecked((int)ctx[CpuRegister.Rsi]);
        _ = unchecked((int)ctx[CpuRegister.Rdx]);

        return TryGetPort(handle, out _)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : OrbisVideoOutErrorInvalidHandle;
    }

    [SysAbiExport(
        Nid = "N5KDtkIjjJ4",
        ExportName = "sceVideoOutUnregisterBuffers",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutUnregisterBuffers(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var attributeIndex = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (attributeIndex < 0)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        lock (_stateGate)
        {
            if (attributeIndex >= port.Groups.Length || port.Groups[attributeIndex] is null)
            {
                return OrbisVideoOutErrorInvalidValue;
            }

            port.Groups[attributeIndex] = null;
            foreach (var slot in port.BufferSlots)
            {
                if (slot.GroupIndex == attributeIndex)
                {
                    slot.GroupIndex = -1;
                    slot.AddressLeft = 0;
                    slot.AddressRight = 0;
                }
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    [SysAbiExport(
        Nid = "i6-sR91Wt-4",
        ExportName = "sceVideoOutSetBufferAttribute",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutSetBufferAttribute(CpuContext ctx)
    {
        var attributeAddress = ctx[CpuRegister.Rdi];
        var pixelFormat = unchecked((uint)ctx[CpuRegister.Rsi]);
        var tilingMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var aspectRatio = unchecked((uint)ctx[CpuRegister.Rcx]);
        var width = unchecked((uint)ctx[CpuRegister.R8]);
        var height = unchecked((uint)ctx[CpuRegister.R9]);
        if (!TryReadStackUInt32(ctx, 0, out var pitchInPixel))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (attributeAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        Span<byte> attribute = stackalloc byte[VideoOutBufferAttributeSize];
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x00..0x04], pixelFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x04..0x08], tilingMode);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x08..0x0C], aspectRatio);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x0C..0x10], width);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..0x14], height);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x14..0x18], pitchInPixel);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x18..0x1C], SceVideoOutBufferAttributeOptionNone);
        if (!ctx.Memory.TryWrite(attributeAddress, attribute))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "PjS5uASwcV8",
        ExportName = "sceVideoOutSetBufferAttribute2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutSetBufferAttribute2(CpuContext ctx)
    {
        var attributeAddress = ctx[CpuRegister.Rdi];
        var pixelFormat = ctx[CpuRegister.Rsi];
        var tilingMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var width = unchecked((uint)ctx[CpuRegister.Rcx]);
        var height = unchecked((uint)ctx[CpuRegister.R8]);
        var option = ctx[CpuRegister.R9];
        if (!TryReadStackUInt32(ctx, 0, out var dccControl) ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 0x10, out var dccClearColor))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (attributeAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        Span<byte> attribute = stackalloc byte[VideoOutBufferAttribute2Size];
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x04..0x08], tilingMode);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x0C..0x10], width);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..0x14], height);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x18..0x20], option);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x20..0x28], pixelFormat);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute[0x28..0x30], dccClearColor);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x30..0x34], dccControl);
        if (!ctx.Memory.TryWrite(attributeAddress, attribute))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "w3BY+tAEiQY",
        ExportName = "sceVideoOutRegisterBuffers",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutRegisterBuffers(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var startIndex = unchecked((int)ctx[CpuRegister.Rsi]);
        var addressesAddress = ctx[CpuRegister.Rdx];
        var bufferNum = unchecked((int)ctx[CpuRegister.Rcx]);
        var attributeAddress = ctx[CpuRegister.R8];
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (addressesAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (attributeAddress == 0)
        {
            return OrbisVideoOutErrorInvalidOption;
        }

        if (!IsValidBufferRange(startIndex, bufferNum))
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        if (!TryReadBufferAttribute(ctx, attributeAddress, false, out var attribute))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<ulong> addresses = stackalloc ulong[Math.Min(bufferNum, MaxDisplayBuffers)];
        for (var i = 0; i < bufferNum; i++)
        {
            if (!ctx.TryReadUInt64(addressesAddress + ((ulong)i * 8), out addresses[i]))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        return RegisterBufferRange(port, startIndex, addresses[..bufferNum], attribute);
    }

    [SysAbiExport(
        Nid = "rKBUtgRrtbk",
        ExportName = "sceVideoOutRegisterBuffers2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVideoOut")]
    public static int VideoOutRegisterBuffers2(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var setIndex = unchecked((int)ctx[CpuRegister.Rsi]);
        var bufferIndexStart = unchecked((int)ctx[CpuRegister.Rdx]);
        var buffersAddress = ctx[CpuRegister.Rcx];
        var bufferNum = unchecked((int)ctx[CpuRegister.R8]);
        var attributeAddress = ctx[CpuRegister.R9];
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 0x08, out var categoryRaw) ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 0x10, out var option))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (buffersAddress == 0)
        {
            return OrbisVideoOutErrorInvalidAddress;
        }

        if (attributeAddress == 0)
        {
            return OrbisVideoOutErrorInvalidOption;
        }

        if (!IsValidBufferRange(bufferIndexStart, bufferNum))
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        if (categoryRaw != 0 || option != 0)
        {
            return OrbisVideoOutErrorInvalidValue;
        }

        if (!TryReadBufferAttribute(ctx, attributeAddress, true, out var attribute))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<ulong> addresses = stackalloc ulong[Math.Min(bufferNum, MaxDisplayBuffers)];
        for (var i = 0; i < bufferNum; i++)
        {
            var entryAddress = buffersAddress + ((ulong)i * VideoOutBuffersEntrySize);
            if (!ctx.TryReadUInt64(entryAddress + 0x00, out addresses[i]) ||
                !ctx.TryReadUInt64(entryAddress + 0x08, out _))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        var groupIndex = RegisterBufferRange(port, bufferIndexStart, addresses[..bufferNum], attribute, setIndex);
        return groupIndex < 0 ? groupIndex : setIndex;
    }

    private static int SubmitFlip(
        CpuContext ctx,
        int handle,
        int bufferIndex,
        int flipMode,
        long flipArg,
        bool submitGpuImage)
    {
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (bufferIndex < -1 || bufferIndex >= MaxDisplayBuffers)
        {
            return OrbisVideoOutErrorInvalidIndex;
        }

        // Pooled snapshot for the same reason as SignalVblank: triggers run outside
        // _stateGate, and SubmitFlip is per-frame so a fresh List copy is steady churn.
        ulong eventHint;
        FlipEventRegistration[]? flipEvents = null;
        int flipEventCount;
        lock (_stateGate)
        {
            if (bufferIndex != -1 && port.BufferSlots[bufferIndex].GroupIndex < 0)
            {
                return OrbisVideoOutErrorInvalidIndex;
            }

            port.CurrentBuffer = bufferIndex;
            port.FlipCount++;
            eventHint = SceVideoOutInternalEventFlip |
                ((unchecked((ulong)flipArg) & 0x0000_FFFF_FFFF_FFFFUL) << 16);
            flipEventCount = port.FlipEvents.Count;
            if (flipEventCount != 0)
            {
                flipEvents = ArrayPool<FlipEventRegistration>.Shared.Rent(flipEventCount);
                port.FlipEvents.CopyTo(flipEvents);
            }
        }

        PaceFlip(port.FlipRate);
        PerfOverlay.RecordSubmit();

        var guestImageSubmitted = false;
        ulong guestImageAddress = 0;
        if (submitGpuImage &&
            bufferIndex >= 0 &&
            TryGetDisplayBufferInfo(handle, bufferIndex, out var displayBuffer))
        {
            guestImageAddress = displayBuffer.Address;
            guestImageSubmitted = GuestGpu.Current.TrySubmitGuestImage(
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        if (_dumpVideoOut)
        {
            _ = TryDumpFrame(ctx, port, bufferIndex, flipMode, flipArg);
        }

        void TriggerFlipEvents()
        {
            if (flipEvents is null)
            {
                return;
            }

            try
            {
                for (var i = 0; i < flipEventCount; i++)
                {
                    _ = KernelEventQueueCompatExports.TriggerDisplayEvent(
                        flipEvents[i].Equeue,
                        SceVideoOutInternalEventFlip,
                        OrbisKernelEventFilterVideoOut,
                        eventHint,
                        flipEvents[i].UserData);
                }
            }
            finally
            {
                ArrayPool<FlipEventRegistration>.Shared.Return(flipEvents);
                flipEvents = null;
            }
        }

        if (submitGpuImage)
        {
            TriggerFlipEvents();
        }
        else if (VulkanVideoPresenter.SubmitOrderedGuestAction(
                     TriggerFlipEvents,
                     $"videoout flip complete handle={handle} index={bufferIndex}") == 0)
        {
            // Headless startup has no render queue to order against.
            TriggerFlipEvents();
        }

        TraceVideoOut(
            $"videoout.submit_flip handle={handle} index={bufferIndex} mode={flipMode} " +
            $"arg={flipArg} addr=0x{guestImageAddress:X16} submitted={guestImageSubmitted} " +
            $"events={flipEventCount} ordered_completion={!submitGpuImage}");
        ReportFrameRate(presented: false);
        var diagnosticFlipNumber = Interlocked.Increment(ref _diagnosticFlipCount);
        if (_holdFirstFlipMilliseconds > 0 && diagnosticFlipNumber == _holdFlipNumber)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Holding guest flip #{diagnosticFlipNumber} for {_holdFirstFlipMilliseconds} ms for visual verification.");
            Thread.Sleep(_holdFirstFlipMilliseconds);
        }
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static void ReportPresentedFrame() =>
        ReportFrameRate(presented: true);

    private static void ReportFrameRate(bool presented)
    {
        if (!_logFrameRate)
        {
            return;
        }

        if (presented)
        {
            Interlocked.Increment(ref _presentedFrameCount);
        }
        else
        {
            Interlocked.Increment(ref _submittedFrameCount);
        }

        var started = Volatile.Read(ref _frameRateWindowStart);
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - started;
        if (elapsedTicks < Stopwatch.Frequency ||
            Interlocked.CompareExchange(ref _frameRateWindowStart, now, started) != started)
        {
            return;
        }

        var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
        var submitted = Interlocked.Exchange(ref _submittedFrameCount, 0);
        var presentedCount = Interlocked.Exchange(ref _presentedFrameCount, 0);
        var (draws, drawMs, pipelines, spirvCompiles) = VulkanVideoPresenter.ReadAndResetPerfCounters();
        Console.Error.WriteLine(
            $"[LOADER][PERF] videoout submitted_fps={submitted / elapsedSeconds:F1} " +
            $"presented_fps={presentedCount / elapsedSeconds:F1} " +
            $"draws={draws} draw_ms={drawMs:F0} pipelines={pipelines} spirv={spirvCompiles}");
    }

    private static readonly bool _flipPacingDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_FLIP_PACING"),
        "1",
        StringComparison.Ordinal);
    private static long _lastFlipPacingTimestamp;

    private static Thread? _vblankThread;
    private static readonly object _vblankThreadGate = new();

    /// <summary>
    /// Starts the emulated vblank tick once a guest registers interest in vblank
    /// events. The tick fires the registered vblank events on their event queues
    /// at the display refresh cadence so guests that park their main/render loop
    /// on a vblank equeue keep advancing.
    /// </summary>
    private static void StartVblankThreadOnce()
    {
        if (Volatile.Read(ref _vblankThread) is not null)
        {
            return;
        }

        lock (_vblankThreadGate)
        {
            if (_vblankThread is not null)
            {
                return;
            }

            var thread = new Thread(VblankTickLoop)
            {
                IsBackground = true,
                Name = "SharpEmu-Vblank",
            };
            _vblankThread = thread;
            thread.Start();
        }
    }

    private static void VblankTickLoop()
    {
        var pending = new List<(ulong Equeue, ulong DataHint, ulong UserData)>();
        var next = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _vblankStopRequested) == 0)
        {
            uint refresh = 60;
            pending.Clear();
            lock (_stateGate)
            {
                foreach (var port in _ports.Values)
                {
                    if (port.VblankEvents.Count == 0)
                    {
                        continue;
                    }

                    refresh = port.RefreshRate == 0 ? 60 : port.RefreshRate;
                    port.VblankCount++;
                    var dataHint = (port.VblankCount & 0x0000_FFFF_FFFF_FFFFUL) << 16;
                    foreach (var registration in port.VblankEvents)
                    {
                        pending.Add((registration.Equeue, dataHint, registration.UserData));
                    }
                }
            }

            foreach (var (equeue, dataHint, userData) in pending)
            {
                _ = KernelEventQueueCompatExports.TriggerDisplayEvent(
                    equeue,
                    SceVideoOutInternalEventVblank,
                    OrbisKernelEventFilterVideoOut,
                    dataHint,
                    userData);
            }

            var interval = Stopwatch.Frequency / Math.Max(1, (long)refresh);
            next += interval;
            var now = Stopwatch.GetTimestamp();
            if (next < now)
            {
                next = now;
            }

            HostTiming.SleepUntil(next);
        }
    }

    /// <summary>
    /// Emulates the display vblank cadence: hardware completes flips at the
    /// requested rate, which is what paces the game's main loop. Without this
    /// the guest runs as fast as the GPU pipeline drains, so frame delivery
    /// is bursty and animation judders. When the emulator runs slower than
    /// the target rate the sleep never engages.
    /// </summary>
    private static void PaceFlip(int flipRate)
    {
        if (_flipPacingDisabled)
        {
            return;
        }

        var refreshRate = flipRate switch
        {
            1 => 30,
            2 => 20,
            _ => 60,
        };
        var intervalTicks = Stopwatch.Frequency / refreshRate;
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastFlipPacingTimestamp);
        var target = last + intervalTicks;
        if (target <= now)
        {
            Interlocked.CompareExchange(ref _lastFlipPacingTimestamp, now, last);
            return;
        }

        var waitMilliseconds = (target - now) * 1000 / Stopwatch.Frequency;
        if (waitMilliseconds is >= 0 and < 100)
        {
            // Precise wait: Thread.Sleep alone overshoots by a scheduler
            // quantum, which caps the flip rate below the target cadence.
            HostTiming.SleepUntil(target);
        }

        Interlocked.CompareExchange(ref _lastFlipPacingTimestamp, target, last);
    }

    private static int RegisterBufferRange(VideoOutPortState port, int startIndex, ReadOnlySpan<ulong> addresses, BufferAttribute attribute, int requestedGroupIndex = -1)
    {
        lock (_stateGate)
        {
            var groupIndex = requestedGroupIndex >= 0 ? requestedGroupIndex : FindFreeGroupIndex(port);
            if (groupIndex < 0 || groupIndex >= MaxDisplayBufferGroups)
            {
                return OrbisVideoOutErrorInvalidValue;
            }

            if (port.Groups[groupIndex] is not null)
            {
                return OrbisVideoOutErrorResourceBusy;
            }

            for (var i = 0; i < addresses.Length; i++)
            {
                if (port.BufferSlots[startIndex + i].GroupIndex >= 0)
                {
                    return OrbisVideoOutErrorResourceBusy;
                }
            }

            port.Groups[groupIndex] = new VideoOutBufferGroup
            {
                Index = groupIndex,
                Attribute = attribute,
            };
            port.OutputWidth = attribute.Width;
            port.OutputHeight = attribute.Height;

            for (var i = 0; i < addresses.Length; i++)
            {
                var slot = port.BufferSlots[startIndex + i];
                slot.GroupIndex = groupIndex;
                slot.AddressLeft = addresses[i];
                slot.AddressRight = 0;
            }

            TraceVideoOut(
                $"videoout.register_buffers handle={port.Handle} group={groupIndex} start={startIndex} count={addresses.Length} " +
                $"addresses=[{string.Join(',', addresses.ToArray().Select(static address => $"0x{address:X16}"))}] " +
                $"fmt=0x{attribute.PixelFormat:X} tile={attribute.TilingMode} {attribute.Width}x{attribute.Height} pitch={attribute.PitchInPixel}");
            GuestGpu.Current.EnsureStarted(attribute.Width, attribute.Height);

            var guestFormat = MapPixelFormatToGuestTextureFormat(attribute.PixelFormat);
            if (guestFormat != 0)
            {
                foreach (var address in addresses)
                {
                    GuestGpu.Current.RegisterKnownDisplayBuffer(address, guestFormat);
                }
            }

            return groupIndex;
        }
    }

    private static int FindFreeGroupIndex(VideoOutPortState port)
    {
        for (var i = 0; i < port.Groups.Length; i++)
        {
            if (port.Groups[i] is null)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadBufferAttribute(CpuContext ctx, ulong attributeAddress, bool attribute2, out BufferAttribute attribute)
    {
        attribute = default;
        if (!TryReadUInt32(ctx, attributeAddress + 0x04, out var tilingMode) ||
            !TryReadUInt32(ctx, attributeAddress + 0x0C, out var width) ||
            !TryReadUInt32(ctx, attributeAddress + 0x10, out var height))
        {
            return false;
        }

        if (attribute2)
        {
            if (!ctx.TryReadUInt64(attributeAddress + 0x18, out var option) ||
                !ctx.TryReadUInt64(attributeAddress + 0x20, out var pixelFormat))
            {
                return false;
            }

            attribute = new BufferAttribute(NormalizePixelFormat(pixelFormat), tilingMode, 0, width, height, width, option);
            return true;
        }

        if (!TryReadUInt32(ctx, attributeAddress + 0x00, out var pixelFormat32) ||
            !TryReadUInt32(ctx, attributeAddress + 0x08, out var aspectRatio) ||
            !TryReadUInt32(ctx, attributeAddress + 0x14, out var pitchInPixel) ||
            !TryReadUInt32(ctx, attributeAddress + 0x18, out var option32))
        {
            return false;
        }

        attribute = new BufferAttribute(NormalizePixelFormat(pixelFormat32), tilingMode, aspectRatio, width, height, pitchInPixel, option32);
        return true;
    }

    private static bool TryDumpFrame(CpuContext ctx, VideoOutPortState port, int bufferIndex, int flipMode, long flipArg)
    {
        if (bufferIndex < 0)
        {
            return false;
        }

        VideoOutBufferSlot slot;
        VideoOutBufferGroup? group;
        lock (_stateGate)
        {
            slot = port.BufferSlots[bufferIndex];
            group = slot.GroupIndex >= 0 && slot.GroupIndex < port.Groups.Length
                ? port.Groups[slot.GroupIndex]
                : null;
        }

        if (group is null || slot.AddressLeft == 0)
        {
            return false;
        }

        var attribute = group.Attribute;
        if (attribute.Width == 0 || attribute.Height == 0 || attribute.Width > 8192 || attribute.Height > 8192)
        {
            return false;
        }

        var bytesPerPixel = GetBytesPerPixel(attribute.PixelFormat);
        if (bytesPerPixel == 0)
        {
            return DumpRawFrame(ctx, port.Handle, slot.AddressLeft, attribute, bufferIndex, flipMode, flipArg, "unsupported-format");
        }

        var pitch = attribute.PitchInPixel == 0 ? attribute.Width : attribute.PitchInPixel;
        var rowBytes = checked((int)(pitch * bytesPerPixel));
        var visibleRowBytes = checked((int)(attribute.Width * bytesPerPixel));
        var frameBytes = checked((ulong)rowBytes * attribute.Height);
        if (frameBytes > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        lock (_frameDumpGate)
        {
            if (_frameDumpCount >= MaxFrameDumps)
            {
                return false;
            }
        }

        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        var row = new byte[rowBytes];
        for (uint y = 0; y < attribute.Height; y++)
        {
            if (!ctx.Memory.TryRead(slot.AddressLeft + ((ulong)y * (ulong)rowBytes), row))
            {
                return false;
            }

            foreach (var value in row.AsSpan(0, visibleRowBytes))
            {
                fingerprint = (fingerprint ^ value) * fnvPrime;
            }
        }

        var fingerprintKey = (port.Handle, bufferIndex, slot.AddressLeft);
        lock (_frameDumpGate)
        {
            if (_lastFrameFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return false;
            }

            if (_frameDumpCount >= MaxFrameDumps)
            {
                return false;
            }

            _lastFrameFingerprints[fingerprintKey] = fingerprint;
            _frameDumpCount++;
        }

        var rgb = new byte[checked((int)(attribute.Width * attribute.Height * 3))];
        var rgbOffset = 0;
        for (uint y = 0; y < attribute.Height; y++)
        {
            if (!ctx.Memory.TryRead(slot.AddressLeft + ((ulong)y * (ulong)rowBytes), row))
            {
                return false;
            }

            ConvertRowToRgb(row.AsSpan(0, visibleRowBytes), rgb.AsSpan(rgbOffset, (int)attribute.Width * 3), attribute.PixelFormat);
            rgbOffset += (int)attribute.Width * 3;
        }

        var frameIndex = Interlocked.Increment(ref _nextFrameDumpIndex);
        var basePath = GetFrameDumpBasePath(frameIndex, port.Handle, bufferIndex);
        WriteBmp(basePath + ".bmp", attribute.Width, attribute.Height, rgb);
        WriteFrameMetadata(basePath + ".txt", slot.AddressLeft, attribute, bufferIndex, flipMode, flipArg, "bmp-linear-read", fingerprint);
        if (_traceVideoOut)
        {
            TraceVideoOut($"videoout.dump_frame path={basePath}.bmp addr=0x{slot.AddressLeft:X16} {attribute.Width}x{attribute.Height} fmt=0x{attribute.PixelFormat:X} fingerprint=0x{fingerprint:X16}");
        }
        return true;
    }

    private static bool DumpRawFrame(CpuContext ctx, int handle, ulong address, BufferAttribute attribute, int bufferIndex, int flipMode, long flipArg, string reason)
    {
        var bytesPerPixel = Math.Max(GetBytesPerPixel(attribute.PixelFormat), 4u);
        var pitch = attribute.PitchInPixel == 0 ? attribute.Width : attribute.PitchInPixel;
        var byteCount = checked((ulong)pitch * attribute.Height * bytesPerPixel);
        if (byteCount == 0 || byteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var bytes = new byte[(int)byteCount];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(bytes);
        var fingerprintKey = (handle, bufferIndex, address);
        lock (_frameDumpGate)
        {
            if ((_lastFrameFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                 previousFingerprint == fingerprint) ||
                _frameDumpCount >= MaxFrameDumps)
            {
                return false;
            }

            _lastFrameFingerprints[fingerprintKey] = fingerprint;
            _frameDumpCount++;
        }

        var frameIndex = Interlocked.Increment(ref _nextFrameDumpIndex);
        var basePath = GetFrameDumpBasePath(frameIndex, handle, bufferIndex);
        File.WriteAllBytes(basePath + ".raw", bytes);
        WriteFrameMetadata(basePath + ".txt", address, attribute, bufferIndex, flipMode, flipArg, reason, fingerprint);
        if (_traceVideoOut)
        {
            TraceVideoOut($"videoout.dump_frame path={basePath}.raw addr=0x{address:X16} bytes={byteCount} reason={reason} fingerprint=0x{fingerprint:X16}");
        }
        return true;
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static uint GetBytesPerPixel(ulong pixelFormat) =>
        pixelFormat is SceVideoOutPixelFormatA8R8G8B8Srgb or
            SceVideoOutPixelFormatA8B8G8R8Srgb or
            SceVideoOutPixelFormatA2R10G10B10 or
            SceVideoOutPixelFormatA2R10G10B10Srgb or
            SceVideoOutPixelFormatA2R10G10B10Bt2020Pq or
            SceVideoOutPixelFormat2R8G8B8A8Srgb or
            SceVideoOutPixelFormat2B8G8R8A8Srgb or
            SceVideoOutPixelFormat2R10G10B10A2 or
            SceVideoOutPixelFormat2B10G10R10A2 or
            SceVideoOutPixelFormat2R10G10B10A2Srgb or
            SceVideoOutPixelFormat2B10G10R10A2Srgb or
            SceVideoOutPixelFormat2R10G10B10A2Bt2100Pq or
            SceVideoOutPixelFormat2B10G10R10A2Bt2100Pq
            ? 4u
            : 0u;

    internal static bool IsPacked10BitPixelFormat(ulong pixelFormat) =>
        IsPacked10BitPixelFormatNormalized(NormalizePixelFormat(pixelFormat));

    private static bool IsPacked10BitPixelFormatNormalized(ulong pixelFormat) =>
        pixelFormat is
            SceVideoOutPixelFormatA2R10G10B10 or
            SceVideoOutPixelFormatA2R10G10B10Srgb or
            SceVideoOutPixelFormatA2R10G10B10Bt2020Pq or
            SceVideoOutPixelFormat2R10G10B10A2 or
            SceVideoOutPixelFormat2B10G10R10A2 or
            SceVideoOutPixelFormat2R10G10B10A2Srgb or
            SceVideoOutPixelFormat2B10G10R10A2Srgb or
            SceVideoOutPixelFormat2R10G10B10A2Bt2100Pq or
            SceVideoOutPixelFormat2B10G10R10A2Bt2100Pq;

    // Maps the PS5 VideoOut pixel format space to the AGC "guest texture format" tags
    // the backend keys its guest-image registry on (see VulkanVideoPresenter.
    // GetGuestTextureFormat: format=10 => 56 for 8-bit RGBA variants, format=9 => 9 for 10-bit).
    private static uint MapPixelFormatToGuestTextureFormat(ulong pixelFormat) =>
        NormalizePixelFormat(pixelFormat) switch
        {
            SceVideoOutPixelFormatA8R8G8B8Srgb or
            SceVideoOutPixelFormatA8B8G8R8Srgb or
            SceVideoOutPixelFormat2R8G8B8A8Srgb or
            SceVideoOutPixelFormat2B8G8R8A8Srgb => 56u,
            SceVideoOutPixelFormatA2R10G10B10 or
            SceVideoOutPixelFormatA2R10G10B10Srgb or
            SceVideoOutPixelFormatA2R10G10B10Bt2020Pq or
            SceVideoOutPixelFormat2R10G10B10A2 or
            SceVideoOutPixelFormat2B10G10R10A2 or
            SceVideoOutPixelFormat2R10G10B10A2Srgb or
            SceVideoOutPixelFormat2B10G10R10A2Srgb or
            SceVideoOutPixelFormat2R10G10B10A2Bt2100Pq or
            SceVideoOutPixelFormat2B10G10R10A2Bt2100Pq => 9u,
            _ => 0u,
        };

    internal static bool TryPackRgba8Pixel(
        ulong pixelFormat,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        out uint packed)
    {
        pixelFormat = NormalizePixelFormat(pixelFormat);
        if (!IsPacked10BitPixelFormatNormalized(pixelFormat))
        {
            packed = 0;
            return false;
        }

        packed = PackRgba8PixelNormalized(pixelFormat, red, green, blue, alpha);
        return true;
    }

    private static uint PackRgba8PixelNormalized(
        ulong pixelFormat,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var red10 = ExpandUnorm8To10(red);
        var green10 = ExpandUnorm8To10(green);
        var blue10 = ExpandUnorm8To10(blue);
        var alpha2 = ((uint)alpha * 3u + 127u) / 255u;
        return HasRedInLeastSignificantBits(pixelFormat)
            ? red10 | (green10 << 10) | (blue10 << 20) | (alpha2 << 30)
            : blue10 | (green10 << 10) | (red10 << 20) | (alpha2 << 30);
    }

    internal static bool TryConvertPacked10ToRgba8(
        uint packed,
        ulong pixelFormat,
        Span<byte> rgba)
    {
        pixelFormat = NormalizePixelFormat(pixelFormat);
        if (rgba.Length < 4 || !IsPacked10BitPixelFormatNormalized(pixelFormat))
        {
            return false;
        }

        ConvertPacked10ToRgba8Normalized(packed, pixelFormat, rgba);
        return true;
    }

    private static void ConvertPacked10ToRgba8Normalized(
        uint packed,
        ulong pixelFormat,
        Span<byte> rgba)
    {
        var least = packed & 0x3FFu;
        var green = (packed >> 10) & 0x3FFu;
        var most = (packed >> 20) & 0x3FFu;
        var redIsLeast = HasRedInLeastSignificantBits(pixelFormat);
        var red = redIsLeast ? least : most;
        var blue = redIsLeast ? most : least;
        rgba[0] = ReduceUnorm10To8(red);
        rgba[1] = ReduceUnorm10To8(green);
        rgba[2] = ReduceUnorm10To8(blue);
        rgba[3] = (byte)((((packed >> 30) & 0x3u) * 255u + 1u) / 3u);
    }

    private static bool HasRedInLeastSignificantBits(ulong pixelFormat) =>
        pixelFormat is
            SceVideoOutPixelFormat2R10G10B10A2 or
            SceVideoOutPixelFormat2R10G10B10A2Srgb or
            SceVideoOutPixelFormat2R10G10B10A2Bt2100Pq;

    private static uint ExpandUnorm8To10(byte value) =>
        ((uint)value * 1023u + 127u) / 255u;

    // Preserve both UNORM endpoints and round to nearest. A plain >> 2 is a
    // biased truncation because the 10-bit maximum is 1023, not 1020.
    private static byte ReduceUnorm10To8(uint value) =>
        (byte)((value * 255u + 511u) / 1023u);

    private static ulong NormalizePixelFormat(ulong pixelFormat)
    {
        if (GetBytesPerPixel(pixelFormat) != 0)
        {
            return pixelFormat;
        }

        var low = (uint)(pixelFormat & 0xFFFF_FFFFUL);
        if (GetBytesPerPixel(low) != 0)
        {
            return low;
        }

        var high = (uint)(pixelFormat >> 32);
        if (GetBytesPerPixel(high) != 0)
        {
            return high;
        }

        var packed = high | (low >> 16);
        return GetBytesPerPixel(packed) != 0 ? packed : pixelFormat;
    }

    private static void ConvertRowToRgb(ReadOnlySpan<byte> source, Span<byte> destination, ulong pixelFormat)
    {
        pixelFormat = NormalizePixelFormat(pixelFormat);
        var dst = 0;
        Span<byte> rgba = stackalloc byte[4];
        var packed10 = IsPacked10BitPixelFormatNormalized(pixelFormat);
        for (var src = 0; src + 3 < source.Length; src += 4)
        {
            if (packed10)
            {
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(source[src..(src + 4)]);
                ConvertPacked10ToRgba8Normalized(packed, pixelFormat, rgba);
                destination[dst++] = rgba[0];
                destination[dst++] = rgba[1];
                destination[dst++] = rgba[2];
            }
            else if (pixelFormat is
                     SceVideoOutPixelFormatA8B8G8R8Srgb or
                     SceVideoOutPixelFormat2R8G8B8A8Srgb)
            {
                destination[dst++] = source[src + 0];
                destination[dst++] = source[src + 1];
                destination[dst++] = source[src + 2];
            }
            else
            {
                destination[dst++] = source[src + 2];
                destination[dst++] = source[src + 1];
                destination[dst++] = source[src + 0];
            }
        }
    }

    [Conditional("DEBUG")]
    private static void RunPixelFormatSelfChecks()
    {
        Span<byte> rgba = stackalloc byte[4];
        Debug.Assert(TryPackRgba8Pixel(
            SceVideoOutPixelFormat2R10G10B10A2Srgb,
            255, 0, 0, 255,
            out var rFirst));
        Debug.Assert(rFirst == 0xC00003FFu);
        Debug.Assert(TryConvertPacked10ToRgba8(
            rFirst,
            SceVideoOutPixelFormat2R10G10B10A2Srgb,
            rgba));
        Debug.Assert(rgba.SequenceEqual(new byte[] { 255, 0, 0, 255 }));

        Debug.Assert(TryPackRgba8Pixel(
            SceVideoOutPixelFormat2B10G10R10A2Srgb,
            255, 0, 0, 255,
            out var bFirst));
        Debug.Assert(bFirst == 0xFFF00000u);
        Debug.Assert(TryConvertPacked10ToRgba8(
            bFirst,
            SceVideoOutPixelFormat2B10G10R10A2Srgb,
            rgba));
        Debug.Assert(rgba.SequenceEqual(new byte[] { 255, 0, 0, 255 }));
        Debug.Assert(ReduceUnorm10To8(0) == 0);
        Debug.Assert(ReduceUnorm10To8(512) == 128);
        Debug.Assert(ReduceUnorm10To8(1023) == 255);
    }

    private static string GetFrameDumpBasePath(long frameIndex, int handle, int bufferIndex)
    {
        var directory = GetLogsDirectory();
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"videoout_frame_{frameIndex:D4}_h{handle}_b{bufferIndex}");
    }

    private static string GetLogsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SharpEmu.slnx")))
            {
                return Path.Combine(current.FullName, "logs");
            }

            current = current.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "logs");
    }

    private static void WriteBmp(string path, uint width, uint height, byte[] rgb)
    {
        var rowStride = checked((int)(((width * 3u) + 3u) & ~3u));
        var pixelBytes = checked(rowStride * (int)height);
        var fileSize = 54 + pixelBytes;
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x02..], (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0A..], 54);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0E..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[0x12..], (int)width);
        BinaryPrimitives.WriteInt32LittleEndian(header[0x16..], -(int)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x22..], (uint)pixelBytes);
        stream.Write(header);

        var row = new byte[rowStride];
        var sourceStride = (int)width * 3;
        var heightInt = (int)height;
        var widthInt = (int)width;
        for (var y = 0; y < heightInt; y++)
        {
            row.AsSpan().Clear();
            var src = rgb.AsSpan(y * sourceStride, sourceStride);
            for (var x = 0; x < widthInt; x++)
            {
                row[(x * 3) + 0] = src[(x * 3) + 2];
                row[(x * 3) + 1] = src[(x * 3) + 1];
                row[(x * 3) + 2] = src[(x * 3) + 0];
            }

            stream.Write(row);
        }
    }

    private static void WriteFrameMetadata(
        string path,
        ulong address,
        BufferAttribute attribute,
        int bufferIndex,
        int flipMode,
        long flipArg,
        string kind,
        ulong fingerprint)
    {
        File.WriteAllText(
            path,
            $"kind={kind}\naddress=0x{address:X16}\nbuffer_index={bufferIndex}\nflip_mode={flipMode}\nflip_arg={flipArg}\nfingerprint=0x{fingerprint:X16}\npixel_format=0x{attribute.PixelFormat:X}\ntiling_mode={attribute.TilingMode}\nwidth={attribute.Width}\nheight={attribute.Height}\npitch_in_pixel={attribute.PitchInPixel}\noption=0x{attribute.Option:X}\n");
    }

    private static bool IsValidBufferRange(int startIndex, int bufferNum)
    {
        return startIndex >= 0 &&
               startIndex < MaxDisplayBuffers &&
               bufferNum >= 1 &&
               bufferNum <= MaxDisplayBuffers &&
               startIndex + bufferNum <= MaxDisplayBuffers;
    }

    private static bool TryGetPort(int handle, [NotNullWhen(true)] out VideoOutPortState? port)
    {
        lock (_stateGate)
        {
            return _ports.TryGetValue(handle, out port);
        }
    }

    private static VideoOutBufferSlot[] CreateBufferSlots()
    {
        var slots = new VideoOutBufferSlot[MaxDisplayBuffers];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new VideoOutBufferSlot();
        }

        return slots;
    }

    private static bool TryReadStackUInt32(CpuContext ctx, int stackIndex, out uint value)
    {
        var address = ctx[CpuRegister.Rsp] + 0x08 + ((ulong)stackIndex * 0x08);
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadInt16(CpuContext ctx, ulong address, out short value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(short)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(buffer);
        return true;
    }

    private static readonly bool _traceVideoOut = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEOOUT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _dumpVideoOut = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_VIDEOOUT"),
        "1",
        StringComparison.Ordinal);

    private static void TraceVideoOut(string message)
    {
        if (!_traceVideoOut)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }
}
