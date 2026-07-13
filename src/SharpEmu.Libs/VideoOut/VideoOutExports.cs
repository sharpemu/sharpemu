// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
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
    private const ulong SceVideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong SceVideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong SceVideoOutPixelFormatB8G8R8A8Unorm = 0x8100000000000000;
    private const ulong SceVideoOutPixelFormatR8G8B8A8Unorm = 0x8100000022000000;
    private const ulong SceVideoOutPixelFormatA2R10G10B10 = 0x88060000;
    private const ulong SceVideoOutPixelFormatA2R10G10B10Srgb = 0x88000000;
    private const ulong SceVideoOutPixelFormatA2R10G10B10Bt2020Pq = 0x88740000;
    private const ulong SceVideoOutInternalEventVblank = 0x5;
    private const ulong SceVideoOutInternalEventFlip = 0x6;
    private const short OrbisKernelEventFilterVideoOut = -13;

    private static readonly object _stateGate = new();
    private static readonly object _frameDumpGate = new();
    private static readonly Dictionary<int, VideoOutPortState> _ports = new();

    // Hardware raises vblank autonomously; UE blocks its frame loop on it.
    private static Timer? _vblankPumpTimer;
    private static int _vblankPumpActive;
    private const int VblankPumpIntervalMs = 16;

    private static void EnsureVblankPumpStarted()
    {
        if (_vblankPumpTimer is not null)
        {
            return;
        }

        _vblankPumpTimer = new Timer(
            static _ => PumpVblanks(),
            null,
            VblankPumpIntervalMs,
            VblankPumpIntervalMs);
    }

    private static void PumpVblanks()
    {
        if (Interlocked.Exchange(ref _vblankPumpActive, 1) != 0)
        {
            return;
        }

        try
        {
            VideoOutPortState[] ports;
            lock (_stateGate)
            {
                if (_ports.Count == 0)
                {
                    return;
                }

                // Signalling reaches WakeBlockedThreads -> Pump(), which serialises on one global
                // flag. Waking an unwatched queue would hold it 60x/sec and starve guest threads.
                ports = _ports.Values.Where(static port => port.VblankEvents.Count != 0).ToArray();
            }

            foreach (var port in ports)
            {
                SignalVblank(port);
            }
        }
        finally
        {
            Volatile.Write(ref _vblankPumpActive, 0);
        }
    }

    private static readonly Dictionary<(int Handle, int BufferIndex, ulong Address), ulong> _lastFrameFingerprints = new();
    private static int _nextHandle = 1;
    private static int _frameDumpCount;
    private static long _nextFrameDumpIndex;
    private static string _windowTitle = "SharpEmu VideoOut";
    private static readonly bool _logFrameRate = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEOOUT_FPS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _logVideoOutSync = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEOOUT_SYNC"),
        "1",
        StringComparison.Ordinal);
    private static long _frameRateWindowStart = Stopwatch.GetTimestamp();
    private static long _submittedFrameCount;
    private static long _presentedFrameCount;
    private static long _vblankSignalCount;
    private static long _flipSubmitCount;

    public static void ConfigureApplicationInfo(string? title, string? titleId, string? version, string? emulatorCommitSha)
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
        var commitSuffix = string.IsNullOrWhiteSpace(emulatorCommitSha) ? string.Empty : $" \u00b7 {emulatorCommitSha.Trim()}";
        lock (_stateGate)
        {
            _windowTitle = $"SharpEmu{commitSuffix} - {application}{versionSuffix}";
        }
    }

    internal static string GetWindowTitle()
    {
        lock (_stateGate)
        {
            return _windowTitle;
        }
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
        public List<FlipEventRegistration> VblankEvents { get; } = new();
        public List<FlipEventRegistration> FlipEvents { get; } = new();
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
            _ports[handle] = new VideoOutPortState
            {
                Handle = handle,
            };
            EnsureVblankPumpStarted();
            return handle;
        }
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

        Thread.Sleep(1);
        SignalVblank(port);

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
        if (!TryGetPort(handle, out var port))
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        if (!KernelEventQueueCompatExports.IsValidEqueue(equeue))
        {
            return OrbisVideoOutErrorInvalidEventQueue;
        }

        var userData = ctx[CpuRegister.Rdx];
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

        // Some engines wait on this queue before issuing their first flip. Provide a first
        // edge now; later calls to WaitVblank advance the same notification sequence.
        SignalVblank(port);
        TraceVideoOut($"videoout.add_vblank_event eq=0x{equeue:X16} handle={handle} udata=0x{userData:X16}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
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

        TraceVideoOut($"videoout.add_flip_event eq=0x{equeue:X16} handle={handle} udata=0x{userData:X16}");
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

    // Struct layout matches the classic SceVideoOutFlipStatus (40 bytes):
    // count, processTime, tsc, flipArg, currentBuffer, flipPendingNum.
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

        VideoOutPortState? port;
        lock (_stateGate)
        {
            _ports.TryGetValue(handle, out port);
        }

        if (port is null)
        {
            return OrbisVideoOutErrorInvalidHandle;
        }

        ulong count;
        long flipArg;
        uint currentBuffer;
        lock (_stateGate)
        {
            count = port.FlipCount;
            flipArg = 0;
            currentBuffer = unchecked((uint)port.CurrentBuffer);
        }

        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x00, count);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x08, 0);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x10, 0);
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x18, unchecked((ulong)flipArg));
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, statusAddress + 0x20, currentBuffer);

        TraceVideoOut($"videoout.get_flip_status handle={handle} count={count} currentBuffer={currentBuffer}");
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

        if (filter != OrbisKernelEventFilterVideoOut ||
            ident is not (SceVideoOutInternalEventVblank or SceVideoOutInternalEventFlip))
        {
            return OrbisVideoOutErrorInvalidEvent;
        }

        return 0;
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
            ident is not (SceVideoOutInternalEventVblank or SceVideoOutInternalEventFlip))
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

        VulkanVideoPresenter.Submit(bgraFrame, width, height);
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

    private static void SignalVblank(VideoOutPortState port)
    {
        List<FlipEventRegistration> vblankEvents;
        ulong eventHint;
        lock (_stateGate)
        {
            port.VblankCount++;
            eventHint = SceVideoOutInternalEventVblank |
                ((port.VblankCount & 0x0000_FFFF_FFFF_FFFFUL) << 16);
            vblankEvents = new List<FlipEventRegistration>(port.VblankEvents);
        }

        var signalCount = Interlocked.Increment(ref _vblankSignalCount);

        foreach (var vblankEvent in vblankEvents)
        {
            _ = KernelEventQueueCompatExports.TriggerDisplayEvent(
                vblankEvent.Equeue,
                SceVideoOutInternalEventVblank,
                OrbisKernelEventFilterVideoOut,
                eventHint,
                vblankEvent.UserData);
        }

        if (_logVideoOutSync && (signalCount <= 8 || signalCount % 60 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][SYNC] vblank#{signalCount} handle={port.Handle} count={port.VblankCount} " +
                $"queues={vblankEvents.Count} hint=0x{eventHint:X16}");
        }
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

        ulong eventHint;
        List<FlipEventRegistration> flipEvents;
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
            flipEvents = new List<FlipEventRegistration>(port.FlipEvents);
        }

        var guestImageSubmitted = false;
        ulong guestImageAddress = 0;
        if (submitGpuImage &&
            bufferIndex >= 0 &&
            TryGetDisplayBufferInfo(handle, bufferIndex, out var displayBuffer))
        {
            guestImageAddress = displayBuffer.Address;
            guestImageSubmitted = VulkanVideoPresenter.TrySubmitGuestImage(
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_VIDEOOUT"),
                "1",
                StringComparison.Ordinal))
        {
            _ = TryDumpFrame(ctx, port, bufferIndex, flipMode, flipArg);
        }

        foreach (var flipEvent in flipEvents)
        {
            _ = KernelEventQueueCompatExports.TriggerDisplayEvent(
                flipEvent.Equeue,
                SceVideoOutInternalEventFlip,
                OrbisKernelEventFilterVideoOut,
                eventHint,
                flipEvent.UserData);
        }

        var flipCount = Interlocked.Increment(ref _flipSubmitCount);
        if (_logVideoOutSync && (flipCount <= 8 || flipCount % 60 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][SYNC] flip#{flipCount} handle={handle} buffer={bufferIndex} " +
                $"addr=0x{guestImageAddress:X16} submitted={guestImageSubmitted} " +
                $"flipQueues={flipEvents.Count}");
        }

        TraceVideoOut($"videoout.submit_flip handle={handle} index={bufferIndex} mode={flipMode} arg={flipArg} events={flipEvents.Count}");
        ReportFrameRate(presented: false);
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
        Console.Error.WriteLine(
            $"[LOADER][PERF] videoout submitted_fps={submitted / elapsedSeconds:F1} " +
            $"presented_fps={presentedCount / elapsedSeconds:F1}");
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
                $"videoout.register_buffers handle={port.Handle} group={groupIndex} start={startIndex} count={addresses.Length} fmt=0x{attribute.PixelFormat:X} tile={attribute.TilingMode} {attribute.Width}x{attribute.Height} pitch={attribute.PitchInPixel}");
            VulkanVideoPresenter.EnsureStarted(attribute.Width, attribute.Height);

            var guestFormat = MapPixelFormatToGuestTextureFormat(attribute.PixelFormat);
            if (guestFormat != 0)
            {
                foreach (var address in addresses)
                {
                    VulkanVideoPresenter.RegisterKnownDisplayBuffer(address, guestFormat);
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
        if (!ctx.TryReadUInt32(attributeAddress + 0x04, out var tilingMode) ||
            !ctx.TryReadUInt32(attributeAddress + 0x0C, out var width) ||
            !ctx.TryReadUInt32(attributeAddress + 0x10, out var height))
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

        if (!ctx.TryReadUInt32(attributeAddress + 0x00, out var pixelFormat32) ||
            !ctx.TryReadUInt32(attributeAddress + 0x08, out var aspectRatio) ||
            !ctx.TryReadUInt32(attributeAddress + 0x14, out var pitchInPixel) ||
            !ctx.TryReadUInt32(attributeAddress + 0x18, out var option32))
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
        TraceVideoOut($"videoout.dump_frame path={basePath}.bmp addr=0x{slot.AddressLeft:X16} {attribute.Width}x{attribute.Height} fmt=0x{attribute.PixelFormat:X} fingerprint=0x{fingerprint:X16}");
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
        TraceVideoOut($"videoout.dump_frame path={basePath}.raw addr=0x{address:X16} bytes={byteCount} reason={reason} fingerprint=0x{fingerprint:X16}");
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
            SceVideoOutPixelFormatB8G8R8A8Unorm or
            SceVideoOutPixelFormatR8G8B8A8Unorm or
            SceVideoOutPixelFormatA2R10G10B10 or
            SceVideoOutPixelFormatA2R10G10B10Srgb or
            SceVideoOutPixelFormatA2R10G10B10Bt2020Pq
            ? 4u
            : 0u;

    // Maps the PS5 VideoOut pixel format space to the AGC "guest texture format" tags
    // VulkanVideoPresenter._availableGuestImages keys on (see VulkanVideoPresenter.
    // GetGuestTextureFormat: format=10 => 56 for 8-bit RGBA variants, format=9 => 9 for 10-bit).
    private static uint MapPixelFormatToGuestTextureFormat(ulong pixelFormat) =>
        NormalizePixelFormat(pixelFormat) switch
        {
            SceVideoOutPixelFormatA8R8G8B8Srgb or
            SceVideoOutPixelFormatA8B8G8R8Srgb or
            SceVideoOutPixelFormatB8G8R8A8Unorm or
            SceVideoOutPixelFormatR8G8B8A8Unorm => 56u,
            SceVideoOutPixelFormatA2R10G10B10 or
            SceVideoOutPixelFormatA2R10G10B10Srgb or
            SceVideoOutPixelFormatA2R10G10B10Bt2020Pq => 9u,
            _ => 0u,
        };

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
        var dst = 0;
        for (var src = 0; src + 3 < source.Length; src += 4)
        {
            if (pixelFormat is SceVideoOutPixelFormatA8B8G8R8Srgb or SceVideoOutPixelFormatR8G8B8A8Unorm)
            {
                destination[dst++] = source[src + 0];
                destination[dst++] = source[src + 1];
                destination[dst++] = source[src + 2];
            }
            else if (pixelFormat is SceVideoOutPixelFormatA2R10G10B10 or SceVideoOutPixelFormatA2R10G10B10Srgb or SceVideoOutPixelFormatA2R10G10B10Bt2020Pq)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(source[src..(src + 4)]);
                destination[dst++] = (byte)(((value >> 20) & 0x3FF) >> 2);
                destination[dst++] = (byte)(((value >> 10) & 0x3FF) >> 2);
                destination[dst++] = (byte)((value & 0x3FF) >> 2);
            }
            else
            {
                destination[dst++] = source[src + 2];
                destination[dst++] = source[src + 1];
                destination[dst++] = source[src + 0];
            }
        }
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

    private static void TraceVideoOut(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEOOUT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }
}
