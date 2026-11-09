// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using LibAtrac9;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;

namespace SharpEmu.GnmMode.Libraries.Ajm;

/// <summary>
/// Orbis AJM's raw-buffer ABI. The shared AJM backend handles batch-info jobs;
/// PS4 titles build the serialized command stream using pointer-returning helpers.
/// </summary>
public static partial class GnmAjmExports
{
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int MalformedBatch = unchecked((int)0x80930011);
    private const int MaxBuffers = 64;
    private const int MaxBatchBytes = 1024 * 1024;
    private const ulong SyntheticBase = 0x7000_0000_0000_0000;

    private const uint JobChunk = 0;
    private const uint RunInput = 1;
    private const uint ControlInput = 2;
    private const uint ControlFlags = 3;
    private const uint RunFlags = 4;
    private const uint ReturnAddress = 6;
    private const uint InlineChunk = 7;
    private const uint RunOutput = 17;
    private const uint ControlOutput = 18;

    private const ulong InstanceGaplessLoop = 1UL << 10;
    private const ulong ControlReset = 1UL << 13;
    private const ulong ControlInitialize = 1UL << 14;
    private const ulong ControlResample = 1UL << 15;
    private const ulong ResampleBytes = 8;
    private const int At9CodecInfoBytes = 16;
    private const int ScratchResult = 0xE00;
    private const int ScratchQuery = 0xE40;

    private readonly record struct Buffer(ulong Address, ulong Size);

    private sealed class InstanceState(uint codec, bool gaplessLoop, bool parseRiff)
    {
        public uint Codec { get; } = codec;
        public bool GaplessLoop { get; } = gaplessLoop;
        public bool ParseRiff { get; } = parseRiff;
        public Atrac9Config? Config { get; set; }
        public bool GaplessLimited { get; set; }
        public ulong DecodedBase { get; set; }
    }

    private static readonly ConcurrentDictionary<(uint Context, uint Instance), InstanceState> Instances = new();

    public static int InstanceCreate(CpuContext ctx)
    {
        var context = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codec = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var output = ctx[CpuRegister.Rcx];
        var result = AjmExports.AjmInstanceCreate(ctx);
        if (result == 0 && ctx.TryReadUInt32(output, out var instance))
        {
            Instances[(context, instance)] = new InstanceState(codec, (flags & InstanceGaplessLoop) != 0, (flags & (1UL << 32)) != 0);
            Trace($"instance_create context={context} codec={codec} flags=0x{flags:X} instance=0x{instance:X8} gapless_loop={(flags & InstanceGaplessLoop) != 0}");
        }
        return result;
    }

    public static int InstanceDestroy(CpuContext ctx)
    {
        var key = (unchecked((uint)ctx[CpuRegister.Rdi]), unchecked((uint)ctx[CpuRegister.Rsi]));
        var result = AjmExports.AjmInstanceDestroy(ctx);
        if (result == 0)
            Instances.TryRemove(key, out _);
        return result;
    }

    public static int BatchJobControlBufferRa(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var instance = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var input = new Buffer(ctx[CpuRegister.Rcx], ctx[CpuRegister.R8]);
        var output = new Buffer(ctx[CpuRegister.R9], StackArg(ctx, 0));
        var returnAddress = StackArg(ctx, 1);

        Span<byte> job = stackalloc byte[64];
        job.Clear();
        var size = 8;
        if (returnAddress != 0)
            WriteBuffer(job, ref size, ReturnAddress, new Buffer(returnAddress, 0));
        WriteBuffer(job, ref size, ControlInput, input);
        WriteFlags(job, ref size, ControlFlags, flags &
            (instance == 0xC0018007 ? 0x00000000C0018007UL : 0x000060000000E7FFUL));
        WriteBuffer(job, ref size, ControlOutput, output);
        WriteJobHeader(job, instance, size);
        return WriteBuiltJob(ctx, address, job[..size], "control", instance, flags);
    }

    public static int BatchJobRunBufferRa(CpuContext ctx) => BuildRunJob(ctx, split: false);

    public static int BatchJobRunSplitBufferRa(CpuContext ctx) => BuildRunJob(ctx, split: true);

    private static int BuildRunJob(CpuContext ctx, bool split)
    {
        var address = ctx[CpuRegister.Rdi];
        var instance = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var input = ctx[CpuRegister.Rcx];
        var inputSize = ctx[CpuRegister.R8];
        var output = ctx[CpuRegister.R9];
        var outputSize = StackArg(ctx, 0);
        var sideband = new Buffer(StackArg(ctx, 1), StackArg(ctx, 2));
        var returnAddress = StackArg(ctx, 3);

        var inputCount = split ? inputSize : 1;
        var outputCount = split ? outputSize : 1;
        if (inputCount > MaxBuffers || outputCount > MaxBuffers)
            return InvalidPointer(ctx);

        var job = new byte[8 + (returnAddress != 0 ? 16 : 0) +
            (int)(inputCount + outputCount) * 16 + 8 + 16];
        var size = 8;
        if (returnAddress != 0)
            WriteBuffer(job, ref size, ReturnAddress, new Buffer(returnAddress, 0));
        for (var index = 0UL; index < inputCount; index++)
        {
            if (!TryGetBuffer(ctx, input, inputSize, index, split, out var buffer))
                return InvalidPointer(ctx);
            WriteBuffer(job, ref size, RunInput, buffer);
        }
        WriteFlags(job, ref size, RunFlags, flags & 0x0000E00000001FFFUL);
        for (var index = 0UL; index < outputCount; index++)
        {
            if (!TryGetBuffer(ctx, output, outputSize, index, split, out var buffer))
                return InvalidPointer(ctx);
            WriteBuffer(job, ref size, RunOutput, buffer);
        }
        WriteBuffer(job, ref size, ControlOutput, sideband);
        WriteJobHeader(job, instance, size);
        return WriteBuiltJob(ctx, address, job, split ? "run_split" : "run", instance, flags);
    }

    public static int BatchJobInlineBuffer(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var outputPointer = ctx[CpuRegister.Rcx];
        if (address == 0 || outputPointer == 0 || size > MaxBatchBytes ||
            (size != 0 && source == 0))
            return InvalidPointer(ctx);

        var aligned = (size + 7UL) & ~7UL;
        var data = new byte[checked((int)aligned + 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, InlineChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), checked((uint)aligned));
        if (size != 0 && !ctx.Memory.TryRead(source, data.AsSpan(8, (int)size)))
            return InvalidPointer(ctx);
        if (!ctx.Memory.TryWrite(address, data) ||
            !WriteU64(ctx, outputPointer, address + 8))
            return InvalidPointer(ctx);
        ctx[CpuRegister.Rax] = address + (ulong)data.Length;
        return 0;
    }

    public static int BatchStartBuffer(CpuContext ctx)
    {
        var contextId = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var length = ctx[CpuRegister.Rdx];
        var priority = ctx[CpuRegister.Rcx];
        var error = ctx[CpuRegister.R8];
        var batchId = ctx[CpuRegister.R9];
        if (address == 0 || batchId == 0 || length > MaxBatchBytes || (length & 7) != 0)
            return ctx.SetReturn(MalformedBatch);

        var data = new byte[checked((int)length)];
        if (!ctx.Memory.TryRead(address, data) || !ExecuteBatch(ctx, unchecked((uint)contextId), data))
            return ctx.SetReturn(MalformedBatch);

        // Share the batch-ID/error semantics with the existing AJM backend.
        var submit = new CpuContext(ctx.Memory, Generation.Gen4);
        submit[CpuRegister.Rdi] = contextId;
        submit[CpuRegister.Rsi] = address;
        submit[CpuRegister.Rdx] = priority;
        submit[CpuRegister.Rcx] = error;
        submit[CpuRegister.R8] = batchId;
        var result = AjmExports.AjmBatchStart(submit);
        Trace($"start_buffer context={contextId} batch=0x{address:X16}+0x{length:X} priority={priority} result=0x{unchecked((uint)result):X8}");
        return ctx.SetReturn(result);
    }

    private static bool ExecuteBatch(CpuContext ctx, uint context, ReadOnlySpan<byte> batch)
    {
        var offset = 0;
        while (offset < batch.Length)
        {
            if (batch.Length - offset < 8)
                return false;
            var header = BinaryPrimitives.ReadUInt32LittleEndian(batch[offset..]);
            var jobSize = BinaryPrimitives.ReadUInt32LittleEndian(batch[(offset + 4)..]);
            var ident = header & 63;
            if (jobSize > batch.Length - offset - 8 || (jobSize & 7) != 0)
                return false;
            if (ident == InlineChunk)
            {
                offset += 8 + (int)jobSize;
                continue;
            }
            if (ident != JobChunk || !ExecuteJob(ctx, context, (header >> 6) & 0xFFFFF,
                batch.Slice(offset + 8, (int)jobSize)))
                return false;
            offset += 8 + (int)jobSize;
        }
        return true;
    }

    private static bool ExecuteJob(CpuContext ctx, uint context, uint instance, ReadOnlySpan<byte> job)
    {
        var inputs = new List<Buffer>();
        var outputs = new List<Buffer>();
        Buffer controlInput = default;
        Buffer controlOutput = default;
        ulong flags = 0;
        var run = false;
        for (var offset = 0; offset < job.Length;)
        {
            if (job.Length - offset < 8)
                return false;
            var word = BinaryPrimitives.ReadUInt32LittleEndian(job[offset..]);
            var kind = word & 63;
            if (kind == ControlFlags || kind == RunFlags)
            {
                flags = ((ulong)((word >> 6) & 0xFFFFF) << 32) |
                    BinaryPrimitives.ReadUInt32LittleEndian(job[(offset + 4)..]);
                run = kind == RunFlags;
                offset += 8;
                continue;
            }
            if (job.Length - offset < 16)
                return false;
            var chunk = new Buffer(BinaryPrimitives.ReadUInt64LittleEndian(job[(offset + 8)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(job[(offset + 4)..]));
            switch (kind)
            {
                case ReturnAddress: break;
                case ControlInput: controlInput = chunk; break;
                case ControlOutput: controlOutput = chunk; break;
                case RunInput: inputs.Add(chunk); break;
                case RunOutput: outputs.Add(chunk); break;
                default: return false;
            }
            offset += 16;
        }

        using var scratch = new BatchScratch(ctx.Memory);
        var call = scratch.Context;
        call[CpuRegister.Rsi] = instance;
        Instances.TryGetValue((context, instance), out var state);
        if (!run)
        {
            var status = ExecuteControl(ctx, scratch, state, flags, controlInput, controlOutput);
            Trace($"control instance=0x{instance:X} flags=0x{flags:X} in=0x{controlInput.Address:X}+0x{controlInput.Size:X} out=0x{controlOutput.Address:X}+0x{controlOutput.Size:X} status=0x{status:X}");
            return true;
        }

        if (inputs.Count > MaxBuffers || outputs.Count > MaxBuffers)
            return false;
        if (state is { ParseRiff: true, Codec: AjmExports.Atrac9CodecType })
        {
            var initialized = InitializeRiff(ctx, scratch, state, inputs);
            if (initialized > 0)
            {
                var layout = AjmExports.GetRunSidebandLayout(flags, controlOutput.Size);
                if (controlOutput.Address != 0 && controlOutput.Size >= AjmExports.AjmSidebandResultBytes)
                {
                    Span<byte> result = stackalloc byte[layout.End];
                    result.Clear();
                    BinaryPrimitives.WriteInt32LittleEndian(result, initialized);
                    if (!ctx.Memory.TryWrite(controlOutput.Address, result))
                        return false;
                }
                return true;
            }
        }
        call[CpuRegister.Rdx] = flags;
        var split = inputs.Count != 1 || outputs.Count != 1;
        if (split)
        {
            scratch.WriteBuffers(0x400, inputs);
            scratch.WriteBuffers(0x900, outputs);
            call[CpuRegister.Rcx] = SyntheticBase + 0x400;
            call[CpuRegister.R8] = (ulong)inputs.Count;
            call[CpuRegister.R9] = SyntheticBase + 0x900;
            scratch.WriteStackArgs((ulong)outputs.Count, controlOutput.Address, controlOutput.Size);
            AjmExports.AjmBatchJobRunSplitBufferRa(call);
        }
        else
        {
            call[CpuRegister.Rcx] = inputs[0].Address;
            call[CpuRegister.R8] = inputs[0].Size;
            call[CpuRegister.R9] = outputs[0].Address;
            scratch.WriteStackArgs(outputs[0].Size, controlOutput.Address, controlOutput.Size);
            AjmExports.AjmBatchJobRunBufferRa(call);
        }
        Trace($"run instance=0x{instance:X} flags=0x{flags:X} buffers={inputs.Count}/{outputs.Count} result=0x{call[CpuRegister.Rax]:X}");
        if (state is not null)
            FinishRun(ctx, scratch, state, instance, flags, inputs, controlOutput);
        return true;
    }

    private static int ExecuteControl(CpuContext ctx, BatchScratch scratch, InstanceState? state,
        ulong flags, Buffer input, Buffer output)
    {
        var call = scratch.Context;
        var cursor = (flags & AjmExports.AjmJobSidebandFlagFormat) != 0 ? (ulong)AjmExports.AjmSidebandFormatBytes : 0;
        var gapless = 0UL;
        if ((flags & AjmExports.AjmJobSidebandFlagGaplessDecode) != 0)
        {
            if (input.Address != 0 && input.Size >= cursor + AjmExports.AjmSidebandGaplessDecodeBytes)
                gapless = input.Address + cursor;
            cursor += AjmExports.AjmSidebandGaplessDecodeBytes;
        }
        if ((flags & ControlResample) != 0)
            cursor += ResampleBytes;

        var reset = (flags & ControlReset) != 0;
        var status = 0;
        if (reset)
        {
            call[CpuRegister.Rdx] = scratch.Address(ScratchResult);
            status |= scratch.Call(AjmExports.AjmBatchJobClearContext, ScratchResult);
            if (state is not null)
                state.DecodedBase = 0;
        }
        if ((flags & ControlInitialize) != 0)
        {
            var parameters = input.Address != 0 && input.Size > cursor
                ? new Buffer(input.Address + cursor, input.Size - cursor)
                : default;
            call[CpuRegister.Rdx] = parameters.Address;
            call[CpuRegister.Rcx] = parameters.Size;
            call[CpuRegister.R8] = scratch.Address(ScratchResult);
            var initialized = scratch.Call(AjmExports.AjmBatchJobInitialize, ScratchResult);
            status |= initialized;
            if (state is not null)
            {
                state.Config = initialized == 0 ? ReadAtrac9Config(ctx, state, parameters) : null;
                state.DecodedBase = 0;
            }
        }
        if (gapless != 0)
        {
            call[CpuRegister.Rdx] = gapless;
            call[CpuRegister.Rcx] = reset ? 1UL : 0UL;
            call[CpuRegister.R8] = scratch.Address(ScratchResult);
            var applied = scratch.Call(AjmExports.AjmBatchJobSetGaplessDecode, ScratchResult);
            status |= applied;
            if (state is not null && applied == 0 && ctx.TryReadUInt32(gapless, out var total))
                state.GaplessLimited = total is not 0 and not uint.MaxValue;
        }

        if (output.Address != 0 && output.Size >= AjmExports.AjmSidebandResultBytes)
        {
            Span<byte> result = stackalloc byte[AjmExports.AjmSidebandResultBytes];
            result.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(result, status);
            ctx.Memory.TryWrite(output.Address, result);
        }
        return status;
    }

    private static void FinishRun(CpuContext ctx, BatchScratch scratch, InstanceState state, uint instance,
        ulong flags, List<Buffer> inputs, Buffer sideband)
    {
        var hasSideband = sideband.Address != 0 && sideband.Size >= AjmExports.AjmSidebandResultBytes;
        var layout = AjmExports.GetRunSidebandLayout(flags, sideband.Size);
        var stream = hasSideband && layout.Stream >= 0 ? sideband.Address + (ulong)layout.Stream : 0;
        if (stream != 0 && state.DecodedBase != 0 &&
            ctx.TryReadUInt64(stream + 8, out var reported))
            ctx.TryWriteUInt64(stream + 8, reported + state.DecodedBase);

        if (hasSideband && (flags & AjmExports.AjmJobRunFlagGetCodecInfo) != 0 && state.Config is { } config &&
            (ulong)layout.End + At9CodecInfoBytes <= sideband.Size)
        {
            Span<byte> info = stackalloc byte[At9CodecInfoBytes];
            BinaryPrimitives.WriteInt32LittleEndian(info, config.SuperframeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(info[4..], config.FramesPerSuperframe);
            BinaryPrimitives.WriteInt32LittleEndian(info[8..], config.FrameBytes);
            BinaryPrimitives.WriteInt32LittleEndian(info[12..], config.FrameSamples);
            ctx.Memory.TryWrite(sideband.Address + (ulong)layout.End, info);
            Trace($"codec_info instance=0x{instance:X} superframe_bytes={config.SuperframeBytes} frames={config.FramesPerSuperframe} frame_bytes={config.FrameBytes} frame_samples={config.FrameSamples}");
        }

        if (!state.GaplessLoop || !state.GaplessLimited || state.Config is null)
            return;

        const ulong queryFlags = AjmExports.AjmJobSidebandFlagStream | AjmExports.AjmJobSidebandFlagGaplessDecode;
        var query = AjmExports.GetRunSidebandLayout(queryFlags, ulong.MaxValue);
        var call = scratch.Context;
        call[CpuRegister.Rdx] = queryFlags;
        call[CpuRegister.Rcx] = 0;
        call[CpuRegister.R8] = 0;
        call[CpuRegister.R9] = 0;
        scratch.WriteStackArgs(0, scratch.Address(ScratchQuery), (ulong)query.End);
        if (scratch.Call(AjmExports.AjmBatchJobRunBufferRa, ScratchQuery) != 0 ||
            scratch.ReadUInt32(ScratchQuery + query.Gapless) != 0)
            return;

        var decoded = scratch.ReadUInt64(ScratchQuery + query.Stream + 8);
        call[CpuRegister.Rdx] = scratch.Address(ScratchResult);
        if (scratch.Call(AjmExports.AjmBatchJobClearContext, ScratchResult) != 0)
            return;

        state.DecodedBase += decoded;
        var consumed = 0UL;
        foreach (var input in inputs)
            consumed += input.Size;
        if (stream != 0)
            ctx.TryWriteUInt32(stream, unchecked((uint)Math.Min(consumed, int.MaxValue)));
        Trace($"gapless_loop instance=0x{instance:X} decoded={decoded} base={state.DecodedBase} consumed=0x{consumed:X}");
    }

    private static Atrac9Config? ReadAtrac9Config(CpuContext ctx, InstanceState state, Buffer parameters)
    {
        Span<byte> data = stackalloc byte[4];
        if (state.Codec != AjmExports.Atrac9CodecType || parameters.Size < 4 || !ctx.Memory.TryRead(parameters.Address, data))
            return null;
        try
        {
            return new Atrac9Config(data.ToArray());
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryGetBuffer(CpuContext ctx, ulong address, ulong countOrSize,
        ulong index, bool split, out Buffer buffer)
    {
        if (!split)
        {
            buffer = new Buffer(address, countOrSize);
            return true;
        }
        Span<byte> descriptor = stackalloc byte[16];
        if (address != 0 && ctx.Memory.TryRead(address + index * 16, descriptor))
        {
            buffer = new Buffer(BinaryPrimitives.ReadUInt64LittleEndian(descriptor),
                BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]));
            return buffer.Size <= uint.MaxValue;
        }
        buffer = default;
        return false;
    }

    private static void WriteJobHeader(Span<byte> data, uint instance, int size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data, (instance & 0xFFFFF) << 6);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], (uint)(size - 8));
    }

    private static void WriteBuffer(Span<byte> data, ref int offset, uint kind, Buffer buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], kind);
        BinaryPrimitives.WriteUInt32LittleEndian(data[(offset + 4)..], checked((uint)buffer.Size));
        BinaryPrimitives.WriteUInt64LittleEndian(data[(offset + 8)..], buffer.Address);
        offset += 16;
    }

    private static void WriteFlags(Span<byte> data, ref int offset, uint kind, ulong flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], kind | ((uint)(flags >> 32) << 6));
        BinaryPrimitives.WriteUInt32LittleEndian(data[(offset + 4)..], unchecked((uint)flags));
        offset += 8;
    }

    private static int WriteBuiltJob(CpuContext ctx, ulong address, ReadOnlySpan<byte> job,
        string type, uint instance, ulong flags)
    {
        if (address == 0 || !ctx.Memory.TryWrite(address, job))
            return InvalidPointer(ctx);
        ctx[CpuRegister.Rax] = address + (ulong)job.Length;
        Trace($"build_{type} instance=0x{instance:X} flags=0x{flags:X} at=0x{address:X16} size=0x{job.Length:X} next=0x{ctx[CpuRegister.Rax]:X16}");
        return 0;
    }

    private static int InvalidPointer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return InvalidParameter;
    }

    private static ulong StackArg(CpuContext ctx, int index)
    {
        if (ctx.TryGetImportStackArgument(index, out var value))
            return value;
        return ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 8 + (ulong)index * 8, out value) ? value : 0;
    }

    private static bool WriteU64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM") == "1")
            Console.Error.WriteLine($"[GNMMODE][AJM] {message}");
    }

    /// <summary>Transient guest-memory view for reusing the shared AJM decoder without touching its ABI.</summary>
    private sealed class BatchScratch : IDisposable
    {
        private readonly byte[] _data = new byte[0x1400];
        public CpuContext Context { get; }

        public BatchScratch(ICpuMemory memory)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_data, SyntheticBase + 0x80);
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(16), 0x100);
            Context = new CpuContext(new ScratchMemory(memory, _data), Generation.Gen4);
            Context[CpuRegister.Rdi] = SyntheticBase;
            Context[CpuRegister.Rsp] = SyntheticBase + 0x2F8;
        }

        public void WriteBuffers(int offset, List<Buffer> buffers)
        {
            foreach (var buffer in buffers)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(offset), buffer.Address);
                BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(offset + 8), buffer.Size);
                offset += 16;
            }
        }

        public void WriteStackArgs(ulong first, ulong second, ulong third)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(0x300), first);
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(0x308), second);
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(0x310), third);
        }

        public ulong Address(int offset) => SyntheticBase + (ulong)offset;

        public void WriteBytes(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_data.AsSpan(offset));

        public int Call(Func<CpuContext, int> function, int resultOffset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(resultOffset), 0);
            var returned = function(Context);
            return returned != 0 ? returned : BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(resultOffset));
        }

        public uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

        public ulong ReadUInt64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset));

        public void Dispose() { }

        private sealed class ScratchMemory(ICpuMemory inner, byte[] data) : ICpuMemory, ICpuMemoryWrapper
        {
            public ICpuMemory Inner => inner;

            public bool TryRead(ulong address, Span<byte> destination)
            {
                if (address >= SyntheticBase && address - SyntheticBase < (ulong)data.Length)
                {
                    var index = checked((int)(address - SyntheticBase));
                    if (destination.Length > data.Length - index)
                        return false;
                    data.AsSpan(index, destination.Length).CopyTo(destination);
                    return true;
                }
                return inner.TryRead(address, destination);
            }

            public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
            {
                if (address >= SyntheticBase && address - SyntheticBase < (ulong)data.Length)
                {
                    var index = checked((int)(address - SyntheticBase));
                    if (source.Length > data.Length - index)
                        return false;
                    source.CopyTo(data.AsSpan(index));
                    return true;
                }
                return inner.TryWrite(address, source);
            }
        }
    }
}
