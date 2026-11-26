// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Psml;

public static class PsmlExports
{
    private const ulong PhysicalBlockSize = 0x10000;
    private const ulong PhysicalBlockAlignment = 0x10000;
    private const ulong PhysicalBlockCount = 1;
    private const ulong HandleBytes = 0x40;
    private const uint HandleMagic = 0x4C4D5350;
    private const int ContextOutputSizeOffset = 0x18;
    private const int ContextNameOffset = 0x30;
    private const uint MaxSurfaceSide = 16384;
    private const int InvalidCaptureContext = unchecked((int)0x8A810005);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, uint> SharedResources = new();
    private static readonly Dictionary<ulong, ContextState> Contexts = new();
    private static uint _nextContextId;
    private static int _dispatchesTraced;
    private static CaptureRequest? _capture;

    private sealed record ContextState(uint Id, uint OutputWidth, uint OutputHeight, string Name);

    private sealed class CaptureRequest(uint? contextId, int frames, string directory)
    {
        private int _sequence;

        public uint? ContextId { get; } = contextId;
        public int Remaining { get; set; } = frames;
        public int Pending { get; set; }

        public string BeginFrame()
        {
            Pending++;
            Remaining--;
            _sequence++;
            return Path.Combine(directory, $"frame-{_sequence:D2}");
        }

        public void CompleteFrame()
        {
            lock (Gate)
            {
                Pending--;
                FinishCaptureIfIdle(this);
            }
        }
    }

    [SysAbiExport(
        Nid = "3WVD91e12ZQ",
        ExportName = "scePsmlMfsrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrInit(CpuContext ctx)
    {
        Trace("init");
        return ctx.SetReturn(0);
    }

    // Mfsr2 runs on a Trinity console only; any other console reports it as unsupported.
    private const int Mfsr2ErrorUnsupportedConsole = unchecked((int)0x8A8A0012);

    [SysAbiExport(
        Nid = "o+NM86gEwFE",
        ExportName = "scePsmlMfsr2Init",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2Init(CpuContext ctx) => ctx.SetReturn(Mfsr2ErrorUnsupportedConsole);

    [SysAbiExport(
        Nid = "+2KpvixvL6E",
        ExportName = "scePsmlMfsrGetSharedResourcesInitRequirement",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetSharedResourcesInitRequirement(CpuContext ctx) =>
        WriteRequirement(ctx, ctx[CpuRegister.Rdi], "shared_resources_requirement");

    [SysAbiExport(
        Nid = "eWoKNeB6V-k",
        ExportName = "scePsmlMfsrCreateSharedResources",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateSharedResources(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        if (output == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryCreateHandle(ctx, 0, out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (Gate)
        {
            SharedResources[handle] = 0;
        }

        if (!ctx.TryWriteUInt64(output, handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Trace($"create_shared_resources handle=0x{handle:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "jEevBXmagOQ",
        ExportName = "scePsmlMfsrReleaseSharedResources",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrReleaseSharedResources(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (Gate)
        {
            SharedResources.Remove(handle);
        }

        Trace($"release_shared_resources handle=0x{handle:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ArakEpzsZo0",
        ExportName = "scePsmlMfsrGetContextBufferRequirement800M3_2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetContextBufferRequirement800M3_2(CpuContext ctx) =>
        WriteRequirement(ctx, ctx[CpuRegister.Rdi], "context_requirement_800m3_2");

    [SysAbiExport(
        Nid = "VGjrQa-WqdU",
        ExportName = "scePsmlMfsrGetContextBufferRequirement1100",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetContextBufferRequirement1100(CpuContext ctx) =>
        WriteRequirement(ctx, ctx[CpuRegister.Rdi], "context_requirement_1100");

    [SysAbiExport(
        Nid = "gxv3i+MTEzU",
        ExportName = "scePsmlMfsrCreateContext800M3_2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateContext800M3_2(CpuContext ctx) => CreateContext(ctx, "800m3_2");

    [SysAbiExport(
        Nid = "fccGInHrj8A",
        ExportName = "scePsmlMfsrCreateContext1100",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateContext1100(CpuContext ctx) => CreateContext(ctx, "1100");

    [SysAbiExport(
        Nid = "JaLBe0P3jSU",
        ExportName = "scePsmlMfsrReleaseContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrReleaseContext(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (Gate)
        {
            if (Contexts.Remove(handle, out var state) && _capture is { } capture && capture.ContextId == state.Id)
            {
                capture.Remaining = 0;
                FinishCaptureIfIdle(capture);
            }
        }

        Trace($"release_context handle=0x{handle:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "AHalTX9wFZY",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacketSizeInDwords",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetDispatchMfsrPacketSizeInDwords(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        if (output == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(ctx.TryWriteUInt32(output, SuperResolutionPacket.Dwords)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "RUNLFro+qok",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket900",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetDispatchMfsrPacket900(CpuContext ctx) => WriteDispatchPacket(ctx, "900");

    [SysAbiExport(
        Nid = "s2psNHUIdjk",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket1000",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetDispatchMfsrPacket1000(CpuContext ctx) => WriteDispatchPacket(ctx, "1000");

    [SysAbiExport(
        Nid = "94iBp3KvIuI",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket1100",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetDispatchMfsrPacket1100(CpuContext ctx) => WriteDispatchPacket(ctx, "1100");

    [SysAbiExport(
        Nid = "GHna9-DvnUk",
        ExportName = "scePsmlMfsrGetMipmapBias",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetMipmapBias(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var output = ctx[CpuRegister.Rsi];
        var renderWidth = (uint)ctx[CpuRegister.Rdx];
        var renderHeight = (uint)ctx[CpuRegister.Rcx];
        if (output == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ContextState? state;
        lock (Gate)
        {
            Contexts.TryGetValue(handle, out state);
        }

        var bias = 0f;
        if (state is { OutputWidth: > 0 } && renderWidth > 0 && renderWidth < state.OutputWidth)
        {
            bias = MathF.Log2(renderWidth / (float)state.OutputWidth);
        }

        Trace($"mipmap_bias handle=0x{handle:X16} render={renderWidth}x{renderHeight} bias={bias:0.###}");
        return ctx.SetReturn(ctx.TryWriteUInt32(output, BitConverter.SingleToUInt32Bits(bias))
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "GJY0MvuTcs8",
        ExportName = "scePsmlMfsrRequestCapture",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrRequestCapture(CpuContext ctx)
    {
        if (ctx.TargetGeneration != Generation.Gen5 || !CaptureEnabled)
        {
            return ctx.SetReturn(0);
        }

        var frames = unchecked((byte)ctx[CpuRegister.Rsi]);
        lock (Gate)
        {
            if (!Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var state))
            {
                return ctx.SetReturn(InvalidCaptureContext);
            }

            return ctx.SetReturn(frames == 0
                ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : RequestCapture(state.Id, frames));
        }
    }

    [SysAbiExport(
        Nid = "LXq+6mIxpCw",
        ExportName = "scePsmlMfsrIsCaptureInProgress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrIsCaptureInProgress(CpuContext ctx)
    {
        if (ctx.TargetGeneration != Generation.Gen5 || !CaptureEnabled)
        {
            return ctx.SetReturn(0);
        }

        lock (Gate)
        {
            return ctx.SetReturn(Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var state) &&
                _capture?.ContextId == state.Id ? 1 : 0);
        }
    }

    internal static bool CaptureEnabled => ConsoleHardware.IsPro &&
        Environment.GetEnvironmentVariable("SHARPEMU_PSSR_CAPTURE") == "1";

    internal static void RequestCaptureFromWindow()
    {
        if (!CaptureEnabled)
        {
            return;
        }

        lock (Gate)
        {
            _ = RequestCapture(contextId: null, frames: 4);
        }
    }

    private static int RequestCapture(uint? contextId, int frames)
    {
        if (_capture is not null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }

        var directory = Path.Combine(SharpEmuPaths.DataRoot, "user", "logs", "pssr-captures",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Volatile.Write(ref _capture, new CaptureRequest(contextId, frames, directory));
        Console.Error.WriteLine($"[PSSR][INFO] Capture requested for the next {frames} dispatches. path={directory}");
        return 0;
    }

    internal static (string Path, Action Complete)? BeginCaptureFrame(uint contextId)
    {
        if (Volatile.Read(ref _capture) is null)
        {
            return null;
        }

        lock (Gate)
        {
            if (_capture is not { Remaining: > 0 } capture ||
                (capture.ContextId is uint requested && requested != contextId))
            {
                return null;
            }

            return (capture.BeginFrame(), capture.CompleteFrame);
        }
    }

    private static void FinishCaptureIfIdle(CaptureRequest capture)
    {
        if (capture is { Remaining: 0, Pending: 0 } && ReferenceEquals(_capture, capture))
        {
            Volatile.Write(ref _capture, null);
        }
    }

    [SysAbiExport(
        Nid = "FSGaTQze0UY",
        ExportName = "scePsmlMfsrSelectConfig",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrSelectConfig(CpuContext ctx)
    {
        Trace($"select_config a0=0x{ctx[CpuRegister.Rdi]:X} a1=0x{ctx[CpuRegister.Rsi]:X}");
        return ctx.SetReturn(0);
    }

    private static int WriteRequirement(CpuContext ctx, ulong requirement, string name)
    {
        if (requirement == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(requirement, PhysicalBlockSize) ||
            !ctx.TryWriteUInt64(requirement + 0x08, PhysicalBlockAlignment) ||
            !ctx.TryWriteUInt64(requirement + 0x10, PhysicalBlockCount))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Trace($"{name} block=0x{PhysicalBlockSize:X} align=0x{PhysicalBlockAlignment:X} count={PhysicalBlockCount}");
        return ctx.SetReturn(0);
    }

    private static int CreateContext(CpuContext ctx, string variant)
    {
        var output = ctx[CpuRegister.Rdi];
        var parameters = ctx[CpuRegister.Rsi];
        if (output == 0 || parameters == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint outputWidth = 0;
        uint outputHeight = 0;
        if (ctx.TryReadUInt16(parameters + ContextOutputSizeOffset, out var width) &&
            ctx.TryReadUInt16(parameters + ContextOutputSizeOffset + 2, out var height) &&
            width is > 0 and <= (ushort)MaxSurfaceSide && height is > 0 and <= (ushort)MaxSurfaceSide)
        {
            outputWidth = width;
            outputHeight = height;
        }

        var name = string.Empty;
        if (ctx.TryReadUInt64(parameters + ContextNameOffset, out var namePointer) && namePointer != 0 &&
            ctx.TryReadNullTerminatedUtf8(namePointer, 64, out var text))
        {
            name = text;
        }

        uint id;
        lock (Gate)
        {
            id = ++_nextContextId;
        }

        if (!TryCreateHandle(ctx, id, out var handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (Gate)
        {
            Contexts[handle] = new ContextState(id, outputWidth, outputHeight, name);
        }

        if (!ctx.TryWriteUInt64(output, handle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[PSSR][INFO] Context {id} created ({variant}): output={outputWidth}x{outputHeight}" +
            (name.Length > 0 ? $" name={name}" : string.Empty) +
            (ConsoleHardware.IsPro ? string.Empty : " (console model is PS5)"));
        return ctx.SetReturn(0);
    }

    private static void ConvertDispatchParameters900(Span<byte> block)
    {
        const int renderOffset = 0xF8;
        const int renderOffsetBytes = 8;
        block[renderOffset..^renderOffsetBytes].CopyTo(block[(renderOffset + renderOffsetBytes)..]);
        block.Slice(renderOffset, renderOffsetBytes).Clear();
    }

    private static int WriteDispatchPacket(CpuContext ctx, string variant)
    {
        var handle = ctx[CpuRegister.Rdi];
        var span = ctx[CpuRegister.Rsi];
        var parameters = ctx[CpuRegister.Rdx];
        if (span == 0 || parameters == 0 ||
            !ctx.TryReadUInt64(span, out var packetAddress) || packetAddress == 0 ||
            !ctx.TryReadUInt32(span + 0x08, out var packetDwords) || packetDwords < SuperResolutionPacket.Dwords)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ContextState? state;
        lock (Gate)
        {
            Contexts.TryGetValue(handle, out state);
        }

        Span<byte> block = stackalloc byte[SuperResolutionPacket.DispatchParameterBytes];
        if (!ctx.Memory.TryRead(parameters, block))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (variant == "900")
        {
            ConvertDispatchParameters900(block);
        }

        Span<uint> textures = stackalloc uint[SuperResolutionPacket.TextureCount * SuperResolutionPacket.TextureDwords];
        textures.Clear();
        var mask = 0u;
        var pointers = MemoryMarshal.Cast<byte, ulong>(block[..(SuperResolutionPacket.TextureCount * sizeof(ulong))]);
        for (var index = 0; index < SuperResolutionPacket.TextureCount; index++)
        {
            var pointer = pointers[index];
            if (pointer == 0)
            {
                continue;
            }

            var words = textures.Slice(index * SuperResolutionPacket.TextureDwords, SuperResolutionPacket.TextureDwords);
            if (ctx.Memory.TryRead(pointer, MemoryMarshal.AsBytes(words)))
            {
                mask |= 1u << index;
            }
            else
            {
                words.Clear();
            }
        }

        Span<uint> packet = stackalloc uint[SuperResolutionPacket.Dwords];
        SuperResolutionPacket.Write(
            packet,
            state?.Id ?? 0,
            state?.OutputWidth ?? 0,
            state?.OutputHeight ?? 0,
            mask,
            textures,
            block[SuperResolutionPacket.ParameterOffset..]);
        if (!ctx.Memory.TryWrite(packetAddress, MemoryMarshal.AsBytes(packet)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (Interlocked.Increment(ref _dispatchesTraced) <= 4 || TraceEnabled)
        {
            var render = SuperResolutionPacket.TryRead(packet[1..], out var dispatch)
                ? $"{dispatch.RenderWidth}x{dispatch.RenderHeight}+{dispatch.RenderOffsetX},{dispatch.RenderOffsetY} jitter=({dispatch.JitterX:0.###},{dispatch.JitterY:0.###}) " +
                  $"mv_scale=({dispatch.MotionVectorScaleX:0.###},{dispatch.MotionVectorScaleY:0.###}) flags=0x{(uint)dispatch.Flags:X} reset={dispatch.Reset}"
                : "unreadable";
            Console.Error.WriteLine(
                $"[PSSR][INFO] Dispatch ({variant}) context={state?.Id ?? 0} inputs=0x{mask:X2} render={render}");
        }

        return ctx.SetReturn(0);
    }

    private static bool TryCreateHandle(CpuContext ctx, uint id, out ulong handle)
    {
        handle = 0;
        if (!(ctx.Memory is IGuestMemoryAllocator allocator && allocator.TryAllocateGuestMemory(HandleBytes, 16, out handle)) &&
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleBytes, 16, out handle))
        {
            return false;
        }

        Span<uint> words = stackalloc uint[(int)(HandleBytes / sizeof(uint))];
        words.Clear();
        words[0] = HandleMagic;
        words[1] = id;
        return ctx.Memory.TryWrite(handle, MemoryMarshal.AsBytes(words));
    }

    private static bool TraceEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PSML"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[PSSR][TRACE] {message}");
        }
    }
}
