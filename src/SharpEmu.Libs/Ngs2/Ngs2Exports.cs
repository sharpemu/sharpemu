// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Audio;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int OrbisNgs2ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    // Waveform-parse error codes. The PS5 error base is 0x804A80xx while the PS4
    // library (and OrbisNgs2ErrorInvalidOutAddress above) used 0x804A00xx; the parse
    // APIs only exist with the newer numbering.
    private const int OrbisNgs2ErrorInvalidParseOutAddress = unchecked((int)0x804A8010);
    private const int OrbisNgs2ErrorInvalidWaveformAddress = unchecked((int)0x804A8055);
    private const int OrbisNgs2ErrorInvalidWaveformData = unchecked((int)0x804A8430);
    private const int OrbisNgs2ErrorInvalidWaveformFormat = unchecked((int)0x804A8431);
    // Container headers are small; RIFF/VAG descriptors always fit well inside this.
    private const ulong MaximumWaveformHeaderBytes = 0x1000;
    private const int MaximumWaveformPathBytes = 1024;
    private const ulong HandleStorageSize = 0x20;
    private const int RenderBufferInfoSize = 0x18;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static long _nextUid;
    private static long _renderCount;

    // A system render advances one grain, regardless of the number of output buffers.
    private const int DefaultGrainSamples = 256;
    private const int DefaultSampleRate = 48000;

    private sealed class SystemState
    {
        public SystemState(uint uid) => Uid = uid;

        public uint Uid { get; }
        public int GrainSamples { get; set; } = DefaultGrainSamples;
        public int SampleRate { get; set; } = DefaultSampleRate;
        public ulong CallbackBuffer { get; set; }
        public Queue<VoiceCallback> Callbacks { get; } = new();
    }

    private sealed record RackState(ulong SystemHandle, uint RackId);

    private sealed class VoiceState
    {
        public VoiceState(ulong rackHandle, uint voiceIndex)
        {
            RackHandle = rackHandle;
            VoiceIndex = voiceIndex;
        }

        public ulong RackHandle { get; }
        public uint VoiceIndex { get; }

        public uint WaveformType { get; set; }
        public uint ConfigData { get; set; }
        public Atrac9DecodeState? Atrac9 { get; set; }
        public int Channels { get; set; } = 2;
        public int SourceRate { get; set; } = DefaultSampleRate;
        public ulong SourceAddr { get; set; }
        public Queue<WaveBlock> Blocks { get; } = new();
        public byte[] PcmCache { get; set; } = [];
        public WaveBlock? PcmCacheBlock { get; set; }
        public long PcmCacheFirst { get; set; }
        public int PcmCacheFrames { get; set; }
        public double Position { get; set; }
        public bool Playing { get; set; }
        public bool Paused { get; set; }
        public bool InUse { get; set; }
        public bool Stopped { get; set; }
        public int ErrorCode { get; set; }
        public float Pitch { get; set; } = 1f;
        public float Gain { get; set; } = 1f;
        public uint OutputId { get; set; }
        public ulong DecodedSamples { get; set; }
        public ulong DecodedBytes { get; set; }
        public ulong Callback { get; set; }
        public ulong CallbackData { get; set; }
        public uint CallbackFlags { get; set; }
        public Dictionary<uint, VoicePort> Ports { get; } = new();
        public Dictionary<int, float[]> Matrices { get; } = new();
    }

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 1, ownerHandle: 0, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            var system = new SystemState(unchecked((uint)Interlocked.Increment(ref _nextUid)));
            var options = ctx[CpuRegister.Rdi];
            var grainOffset = options != 0 && ctx.TryReadUInt64(options, out var optionSize) && optionSize >= 144 ? 112UL : 32UL;
            if (options != 0 && ctx.TryReadUInt32(options + grainOffset, out var grain) && grain is > 0 and <= 8192)
                system.GrainSamples = (int)grain;
            if (options != 0 && ctx.TryReadUInt32(options + grainOffset + 4, out var rate) && rate is >= 8000 and <= 192000)
                system.SampleRate = (int)rate;
            Systems[handle] = system;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "AQkj7C0f3PY",
        ExportName = "sceNgs2SystemResetOption",
        Target = Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemResetOption(CpuContext ctx)
    {
        // PS5: size, name[64], four scheduler pointers, then the system limits and reserved words.
        Span<byte> option = stackalloc byte[144];
        option.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(option, (ulong)option.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(option[108..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(option[112..], DefaultGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option[116..], DefaultSampleRate);
        return SetReturn(ctx, ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], option)
            ? 0 : unchecked((int)0x804A8012));
    }

    // Non-allocator create: identical to the WithAllocator form for our purposes.
    // The only signature difference is the caller-supplied buffer info in rsi
    // (vs an allocator callback); the system option (rdi) and out-handle (rdx)
    // sit at the same argument positions, so we reuse the same implementation.
    // Dead Cells uses these variants — leaving sceNgs2SystemCreate unresolved
    // gave the game a garbage system handle, so every later rack/voice call
    // failed and it polled sceNgs2VoiceGetState forever, freezing at FLIP 0.
    [SysAbiExport(
        Nid = "koBbCMvOKWw",
        ExportName = "sceNgs2SystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreate(CpuContext ctx) => Ngs2SystemCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            var rackHandles = Racks
                .Where(pair => pair.Value.SystemHandle == handle)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var rackHandle in rackHandles)
            {
                RemoveRackLocked(rackHandle);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.R8];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 2, systemHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Racks[handle] = new RackState(systemHandle, rackId);
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator rack create: system handle (rdi), rack id (rsi) and the
    // out-handle (r8) share the WithAllocator argument layout, so reuse it.
    [SysAbiExport(
        Nid = "cLV4aiT9JpA",
        ExportName = "sceNgs2RackCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreate(CpuContext ctx) => Ngs2RackCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            RemoveRackLocked(handle);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            var existing = Voices.FirstOrDefault(
                pair => pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? SetReturn(ctx, 0)
                    : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 4, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Voices[handle] = new VoiceState(rackHandle, voiceIndex);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var paramList = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        if (ShouldTrace())
        {
            TraceVoiceParamList(ctx, voiceHandle, paramList);
        }

        return SetReturn(ctx, HandleVoiceParams(ctx, voiceHandle, paramList));
    }


    // Optional inspection of the signed-relative control list, separate from RunCommands records.
    private static void TraceVoiceParamList(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        Span<byte> peek = stackalloc byte[32];
        var offset = paramList;
        for (int guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var next) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} @0x{offset:X}: unreadable header");
                return;
            }

            peek.Clear();
            var readable = Math.Min((int)Math.Max((ushort)8, size), peek.Length);
            ctx.Memory.TryRead(offset, peek[..readable]);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} id=0x{id:X} size={size} next={unchecked((short)next)} bytes={Convert.ToHexString(peek[..readable])}");

            // For the waveform-blocks param, follow the embedded pointers and
            // dump the pointed-to bytes so we can tell PCM16 from ATRAC9.
            if (id == 0x10000001 && Interlocked.Increment(ref _waveformDumps) <= 8)
            {
                for (int po = 8; po + 8 <= readable; po += 8)
                {
                    if (ctx.TryReadUInt64(offset + (ulong)po, out var ptr) && ptr > 0x10000 &&
                        ctx.Memory.TryRead(ptr, peek))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] ngs2.waveform @+{po} ptr=0x{ptr:X} head={Convert.ToHexString(peek)}");
                    }
                }
            }

            var advance = unchecked((short)next);
            if (advance <= 0)
            {
                return;
            }

            offset += (ulong)advance;
        }
    }

    private static long _waveformDumps;

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx) =>
        SetReturn(ctx, RunVoiceCommands(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var result = RenderSystem(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], unchecked((uint)ctx[CpuRegister.Rdx]));
        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 200 == 0))
            Trace($"render#{count} system=0x{ctx[CpuRegister.Rdi]:X} result=0x{result:X}");
        return SetReturn(ctx, result);
    }


    [SysAbiExport(
        Nid = "pgFAiLR5qT4",
        ExportName = "sceNgs2SystemQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "0eFLVCfWVds",
        ExportName = "sceNgs2RackQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rdx]);

    // Report a fixed working-memory footprint for the requested object. The
    // out struct (SceNgs2BufferAllocator-style) begins with the size field.
    private static int WriteBufferSize(CpuContext ctx, ulong outAddress)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        Span<byte> info = stackalloc byte[RenderBufferInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..8], 0x10000);
        BinaryPrimitives.WriteUInt64LittleEndian(info[8..16], 0x100);
        return ctx.Memory.TryWrite(outAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "l4Q2dWEH6UM",
        ExportName = "sceNgs2SystemSetGrainSamples",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetGrainSamples(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var grain = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (grain > 0 && grain <= 8192)
            {
                system.GrainSamples = grain;
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-tbc2SxQD60",
        ExportName = "sceNgs2SystemSetSampleRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetSampleRate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var sampleRate = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (sampleRate is < 8000 or > 192000)
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            system.SampleRate = sampleRate;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "gThZqM5PYlQ",
        ExportName = "sceNgs2SystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemLock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "JXRC5n0RQls",
        ExportName = "sceNgs2SystemUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemUnlock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "-TOuuAQ-buE",
        ExportName = "sceNgs2VoiceGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetState(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rsi];
        var size = (int)Math.Min(ctx[CpuRegister.Rdx], 0x400);
        if (address == 0 || size < 4) return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        Span<byte> state = stackalloc byte[size];
        state.Clear();
        lock (StateGate)
        {
            if (!Voices.TryGetValue(ctx[CpuRegister.Rdi], out var voice))
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            BinaryPrimitives.WriteUInt32LittleEndian(state, StateFlags(voice));
            if (size >= 8) BinaryPrimitives.WriteInt32LittleEndian(state[4..], voice.ErrorCode);
            // SceNgs2VoiceState includes errorCode; the following uint64 fields are 8-byte aligned.
            if (size >= 56)
            {
                BinaryPrimitives.WriteSingleLittleEndian(state[8..], voice.Playing ? 1 : 0);
                BinaryPrimitives.WriteUInt64LittleEndian(state[24..], voice.DecodedSamples);
                BinaryPrimitives.WriteUInt64LittleEndian(state[32..], voice.DecodedBytes);
                if (voice.Blocks.TryPeek(out var block))
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(state[40..], block.UserData);
                    BinaryPrimitives.WriteUInt64LittleEndian(state[48..], block.Address);
                }
            }
        }
        return SetReturn(ctx, ctx.Memory.TryWrite(address, state) ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }

    [SysAbiExport(
        Nid = "rEh728kXk3w",
        ExportName = "sceNgs2VoiceGetStateFlags",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetStateFlags(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Voices.TryGetValue(ctx[CpuRegister.Rdi], out var voice))
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            return SetReturn(ctx, ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], StateFlags(voice))
                ? 0 : OrbisNgs2ErrorInvalidOutAddress);
        }
    }

    private static int ValidateSystem(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                Systems.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : OrbisNgs2ErrorInvalidSystemHandle);
        }
    }

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        handle = 0;
        if (!(ctx.Memory is IGuestMemoryAllocator allocator && allocator.TryAllocateGuestMemory(HandleStorageSize, 16, out handle)) &&
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }

        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[0..8], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..16], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], type);
        return ctx.Memory.TryWrite(handle, data);
    }


    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
        }
    }

    // ABI: int32_t sceNgs2ParseWaveformData(const void *data, size_t dataSize,
    // SceNgs2WaveformInfo *outInfo).
    [SysAbiExport(
        Nid = "hyVLT2VlOYk",
        ExportName = "sceNgs2ParseWaveformData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformData(CpuContext ctx)
    {
        var dataAddress = ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rsi];
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (outInfoAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidParseOutAddress);
        }

        if (dataAddress == 0 || dataSize == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformAddress);
        }

        // Only the container header is needed; cap the copy so a title that
        // declares a multi-megabyte streaming buffer does not drag it all in.
        var length = (int)Math.Min(dataSize, MaximumWaveformHeaderBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var span = buffer.AsSpan(0, length);
            if (!ctx.Memory.TryRead(dataAddress, span))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformAddress);
            }

            return WriteParsedWaveform(ctx, span, dataSize, outInfoAddress);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ABI: int32_t sceNgs2ParseWaveformFile(const char *path, uint64_t offset,
    // SceNgs2WaveformInfo *outInfo).
    [SysAbiExport(
        Nid = "iprCTXPVWMI",
        ExportName = "sceNgs2ParseWaveformFile",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformFile(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var offset = ctx[CpuRegister.Rsi];
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (outInfoAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidParseOutAddress);
        }

        if (pathAddress == 0 || !TryReadGuestPath(ctx, pathAddress, out var guestPath))
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformAddress);
        }

        var hostPath = KernelMemoryCompatExports.ResolveGuestPath(guestPath);
        byte[] header;
        long fileLength;
        try
        {
            using var stream = App0Archive.OpenReadOrFile(hostPath);
            fileLength = stream.Length;
            if (offset >= (ulong)fileLength)
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
            }

            stream.Position = (long)offset;
            var length = (int)Math.Min((ulong)(fileLength - (long)offset), MaximumWaveformHeaderBytes);
            header = new byte[length];
            stream.ReadExactly(header, 0, length);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace($"parse-waveform-file open-failed path={guestPath} error={exception.Message}");
            return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
        }

        return WriteParsedWaveform(ctx, header, (ulong)fileLength - offset, outInfoAddress);
    }

    // ABI: int32_t sceNgs2ParseWaveformUser(SceNgs2ParseReadHandler handler,
    // uintptr_t userData, SceNgs2WaveformInfo *outInfo). The handler is a guest
    // callback, which this HLE cannot drive, so report the data as unparsable
    // rather than publishing a fabricated description.
    [SysAbiExport(
        Nid = "t9T0QM17Kvo",
        ExportName = "sceNgs2ParseWaveformUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformUser(CpuContext ctx) =>
        SetReturn(
            ctx,
            ctx[CpuRegister.Rdx] == 0
                ? OrbisNgs2ErrorInvalidParseOutAddress
                : OrbisNgs2ErrorInvalidWaveformData);

    private static int WriteParsedWaveform(
        CpuContext ctx,
        ReadOnlySpan<byte> header,
        ulong totalDataSize,
        ulong outInfoAddress)
    {
        if (!Ngs2WaveformParser.TryParse(header, out var parsed))
        {
            Trace(
                Ngs2WaveformParser.IsSupportedContainer(header)
                    ? "parse-waveform unsupported-format"
                    : "parse-waveform unknown-container");
            return SetReturn(
                ctx,
                Ngs2WaveformParser.IsSupportedContainer(header)
                    ? OrbisNgs2ErrorInvalidWaveformFormat
                    : OrbisNgs2ErrorInvalidWaveformData);
        }

        // Only the container header was copied in. When the real image is longer,
        // the data chunk continues past it, so extend it from the caller's total
        // length instead of reporting the truncated size.
        if ((ulong)header.Length < totalDataSize && totalDataSize > parsed.DataOffset)
        {
            var available = totalDataSize - parsed.DataOffset;
            parsed.DataSize = (uint)Math.Min(available, uint.MaxValue);
            if (parsed.AudioUnitSize != 0 &&
                parsed.WaveformType != Ngs2WaveformParser.WaveformTypeAtrac9)
            {
                parsed.NumSamples =
                    parsed.DataSize / parsed.AudioUnitSize * parsed.NumAudioUnitSamples;
                parsed.LoopEndPosition = parsed.LoopEndPosition == 0 && parsed.NumSamples != 0
                    ? parsed.NumSamples - 1
                    : parsed.LoopEndPosition;
            }
        }

        Span<byte> info = stackalloc byte[Ngs2WaveformParser.WaveformInfoSize];
        Ngs2WaveformParser.Write(parsed, info);
        if (!ctx.Memory.TryWrite(outInfoAddress, info))
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidParseOutAddress);
        }

        Trace(
            $"parse-waveform type=0x{parsed.WaveformType:X} channels={parsed.NumChannels} " +
            $"rate={parsed.SampleRate} dataOffset=0x{parsed.DataOffset:X} " +
            $"dataSize=0x{parsed.DataSize:X} samples={parsed.NumSamples}");
        return SetReturn(ctx, 0);
    }

    private static bool TryReadGuestPath(CpuContext ctx, ulong address, out string path)
    {
        Span<byte> buffer = stackalloc byte[MaximumWaveformPathBytes];
        var length = 0;
        Span<byte> one = stackalloc byte[1];
        while (length < buffer.Length)
        {
            if (!ctx.Memory.TryRead(address + (ulong)length, one))
            {
                break;
            }

            if (one[0] == 0)
            {
                path = System.Text.Encoding.UTF8.GetString(buffer[..length]);
                return length != 0;
            }

            buffer[length++] = one[0];
        }

        path = string.Empty;
        return false;
    }

    private static void Trace(string message)
    {
        if (ShouldTrace())
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ngs2.{message}");
        }
    }

    private static bool ShouldTrace() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"),
            "1",
            StringComparison.Ordinal);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
    [SysAbiExport(
        Nid = "xa8oL9dmXkM",
        ExportName = "sceNgs2PanInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2PanInit(CpuContext ctx) => SetReturn(ctx, InitializePanner(ctx));

    [SysAbiExport(
        Nid = "1WsleK-MTkE",
        ExportName = "sceNgs2GeomCalcListener",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomCalcListener(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "0lbbayqDNoE",
        ExportName = "sceNgs2GeomResetSourceParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetSourceParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "7Lcfo8SmpsU",
        ExportName = "sceNgs2GeomResetListenerParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetListenerParam(CpuContext ctx) => ctx.SetReturn(0);
}
