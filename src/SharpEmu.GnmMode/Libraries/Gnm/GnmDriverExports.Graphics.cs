// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.GnmMode.Gpu;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    public static int SetVsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Vs);
    public static int SetPsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Ps);
    public static int SetPsShader350(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Ps, pixel350: true);
    public static int UpdateVsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Vs, update: true);
    public static int UpdatePsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Ps, update: true);
    public static int UpdatePsShader350(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Ps, pixel350: true, update: true);
    public static int SetLsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Ls);
    public static int SetHsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Hs);
    public static int SetEsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Es);
    public static int SetGsShader(CpuContext context) => SetGraphicsShader(context, GraphicsStage.Gs);

    private enum GraphicsStage { Vs, Ps, Ls, Hs, Es, Gs }

    private static int SetGraphicsShader(CpuContext context, GraphicsStage stage, bool pixel350 = false, bool update = false)
    {
        var (length, count) = stage switch
        {
            GraphicsStage.Vs => (29, 7),
            GraphicsStage.Ps => (40, 12),
            GraphicsStage.Ls => (23, 4),
            GraphicsStage.Hs => (30, 7),
            GraphicsStage.Es => (20, 4),
            _ => (29, 7),
        };
        var pointer = context[CpuRegister.Rdx];
        var argument = (uint)context[CpuRegister.Rcx];
        var modified = stage is GraphicsStage.Vs or GraphicsStage.Ls or GraphicsStage.Es;
        if (context[CpuRegister.Rdi] == 0 || context[CpuRegister.Rsi] < (uint)length ||
            stage != GraphicsStage.Ps && pointer == 0 || modified && (argument & 0xFCFFFC3F) != 0) return ShaderBindFailure(context, stage, "arguments");
        Span<uint> registers = stackalloc uint[12];
        registers.Clear();
        if (pointer != 0 && !KernelMemoryCompatExports.TryReadGuestStructure(context, pointer, MemoryMarshal.AsBytes(registers[..count])))
            return ShaderBindFailure(context, stage, "register-read");
        if (stage == GraphicsStage.Ps && pixel350 && pointer == 0)
            registers[11] = 0xF;
        if (registers[1] != 0 && !AcceptsHighShaderAddress(context, registers, stage)) return ShaderBindFailure(context, stage, "address");
        return WriteGraphicsShader(context, stage, registers, argument, length, pixel350, update, pointer != 0);
    }

    private static int WriteGraphicsShader(CpuContext context, GraphicsStage stage, Span<uint> registers, uint argument,
        int length, bool pixel350 = false, bool update = false, bool bindPixel = false)
    {
        var modified = stage is GraphicsStage.Vs or GraphicsStage.Ls or GraphicsStage.Es;
        if (modified && argument != 0)
        {
            var kept = stage == GraphicsStage.Ls && (argument & 0xFFFFFC3F) == 0 ? 0xFFFFFC3Fu : 0xFCFFFC3Fu;
            registers[2] = (registers[2] & kept) | argument;
        }
        Span<uint> commands = stackalloc uint[length];
        commands.Clear();
        var cursor = 0;
        var sh = PacketOpcode.SetShaderRegister;
        var cx = update ? PacketOpcode.Nop : PacketOpcode.SetContextRegister;
        var contextTag = update ? 0xC01E0000u : 0u;
        switch (stage)
        {
            case GraphicsStage.Vs:
                AppendRegister(commands, ref cursor, sh, 0x48, registers[..2]);
                AppendRegister(commands, ref cursor, sh, 0x4A, registers.Slice(2, 2));
                AppendRegister(commands, ref cursor, cx, contextTag | 0x207, registers.Slice(6, 1));
                AppendRegister(commands, ref cursor, cx, contextTag | 0x1B1, registers.Slice(4, 1));
                AppendRegister(commands, ref cursor, cx, contextTag | 0x1C3, registers.Slice(5, 1));
                break;
            case GraphicsStage.Ps:
                AppendRegister(commands, ref cursor, sh, 8, registers[..2]);
                if (bindPixel)
                {
                    AppendRegister(commands, ref cursor, sh, 10, registers.Slice(2, 2));
                    AppendRegister(commands, ref cursor, cx, contextTag | 0x1C4, registers.Slice(4, 2));
                    AppendRegister(commands, ref cursor, cx, contextTag | 0x1B3, registers.Slice(6, 2));
                    AppendRegister(commands, ref cursor, cx, contextTag | 0x1B6, registers.Slice(8, 1));
                    AppendRegister(commands, ref cursor, cx, contextTag | 0x1B8, registers.Slice(9, 1));
                }
                AppendRegister(commands, ref cursor, cx, contextTag | 0x203, registers.Slice(10, 1));
                if (bindPixel || pixel350)
                    AppendRegister(commands, ref cursor, bindPixel ? cx : PacketOpcode.SetContextRegister,
                        (bindPixel ? contextTag : 0) | 0x8F, registers.Slice(11, 1));
                break;
            case GraphicsStage.Ls:
                AppendRegister(commands, ref cursor, sh, 0x148, registers[..2]);
                AppendRegister(commands, ref cursor, sh, 0x14A, registers.Slice(2, 2));
                break;
            case GraphicsStage.Hs:
                registers[7] = argument;
                AppendRegister(commands, ref cursor, sh, 0x108, registers[..2]);
                AppendRegister(commands, ref cursor, sh, 0x10A, registers.Slice(2, 2));
                AppendRegister(commands, ref cursor, cx, 0x286, registers.Slice(5, 2));
                AppendRegister(commands, ref cursor, cx, 0x2DB, registers.Slice(4, 1));
                AppendRegister(commands, ref cursor, cx, 0x2D6, registers.Slice(7, 1));
                break;
            case GraphicsStage.Es:
                AppendRegister(commands, ref cursor, sh, 0xC8, registers[..2]);
                AppendRegister(commands, ref cursor, sh, 0xCA, registers.Slice(2, 2));
                break;
            case GraphicsStage.Gs:
                AppendRegister(commands, ref cursor, sh, 0x88, registers[..2]);
                AppendRegister(commands, ref cursor, sh, 0x8A, registers.Slice(2, 2));
                AppendRegister(commands, ref cursor, cx, 0x2E5, registers.Slice(4, 1));
                AppendRegister(commands, ref cursor, cx, 0x29B, registers.Slice(5, 1));
                AppendRegister(commands, ref cursor, cx, 0x2E4, registers.Slice(6, 1));
                break;
        }
        commands[cursor] = PacketHeader.Make((uint)(length - cursor), PacketOpcode.Nop);
        return context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? context.SetReturn(0) : ShaderBindFailure(context, stage, "command-write");
    }

    private static int _shaderBindFailures;
    private static int ShaderBindFailure(CpuContext context, GraphicsStage stage, string reason)
    {
        if (Interlocked.Increment(ref _shaderBindFailures) <= 8)
        {
            Console.Error.WriteLine($"[GNMMODE][WARN] Shader bind failed: stage={stage} reason={reason}.");
            if (GnmDiagnostics.Log)
                Console.Error.WriteLine($"[GNMMODE][BINDPROBE] stage={stage} reason={reason} pointer=0x{context[CpuRegister.Rdx]:X} destination=0x{context[CpuRegister.Rdi]:X} memory={context.Memory.GetType().FullName} " +
                    context.Memory.DescribeReadRange(context[CpuRegister.Rdx], 48));
        }
        return context.SetReturn(-1);
    }

    private static bool AcceptsHighShaderAddress(CpuContext context, ReadOnlySpan<uint> registers, GraphicsStage stage)
    {
        var address = ((ulong)registers[0] << 8) | ((ulong)(registers[1] & 0xFF) << 40);
        var accepted = registers[1] <= 0xFF && context.Memory.CanRead(address, sizeof(uint));
        if (Interlocked.Increment(ref _highShaderAddressReports) <= 8)
            Console.Error.WriteLine($"[GNMMODE][WARN] {stage.ToString().ToUpperInvariant()} shader registers carry PGM_HI=0x{registers[1]:X}: " +
                $"PGM_LO=0x{registers[0]:X8} address=0x{address:X16} {(accepted ? "accepted" : "rejected")}.");
        return accepted;
    }

    private static int _highShaderAddressReports;

    private static void AppendRegister(Span<uint> commands, ref int cursor, uint opcode, uint offset, ReadOnlySpan<uint> values)
    {
        commands[cursor++] = PacketHeader.Make((uint)values.Length + 2, opcode);
        commands[cursor++] = offset;
        values.CopyTo(commands[cursor..]);
        cursor += values.Length;
    }

    public static int InsertMarker(CpuContext context)
    {
        var words = (uint)context[CpuRegister.Rsi];
        if (words < 2 || words > 0x100000 || !context.Memory.CanRead(context[CpuRegister.Rdi], (ulong)words * 4))
            return context.SetReturn(-1);
        Span<byte> header = stackalloc byte[8];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, PacketHeader.Make(words, PacketOpcode.Nop));
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], header) ? 0 : -1);
    }

    public static int SubmitCommandBuffers(CpuContext context) => SubmitGraphics(context, flip: false);
    public static int SubmitAndFlipCommandBuffers(CpuContext context) => SubmitGraphics(context, flip: true);
    private static readonly Lock GraphicsSubmissionGate = new();

    public static int SubmitDone(CpuContext context)
    {
        lock (GraphicsSubmissionGate)
        {
            var result = AgcExports.SuspendPoint(context);
            if (result == 0)
                AgcExports.WaitForAcceptedCommandStreams();
            GcnSampledImageDetiler.AdvanceEpoch();
            return result;
        }
    }

    private static int SubmitGraphics(CpuContext context, bool flip)
    {
        lock (GraphicsSubmissionGate)
            return SubmitGraphicsCore(context, flip);
    }

    private static int SubmitGraphicsCore(CpuContext context, bool flip)
    {
        var count = (uint)context[CpuRegister.Rdi];
        var addresses = context[CpuRegister.Rsi];
        var sizes = context[CpuRegister.Rdx];
        if (count == 0 || count > 512 || addresses == 0 || sizes == 0) return context.SetReturn(-1);
        var buffers = new (ulong Address, uint Words, ulong ConstantAddress, uint ConstantWords)[count];
        for (var index = 0u; index < count; index++)
        {
            if (!context.TryReadUInt64(addresses + index * 8UL, out var address) ||
                !context.TryReadUInt32(sizes + index * 4UL, out var bytes) || bytes == 0 || (bytes & 3) != 0 ||
                !context.Memory.CanRead(address, bytes)) return context.SetReturn(-1);
            ulong constantAddress = 0;
            uint constantBytes = 0;
            if (context[CpuRegister.Rcx] != 0 && context[CpuRegister.R8] != 0 &&
                (!context.TryReadUInt32(context[CpuRegister.R8] + index * 4UL, out constantBytes) ||
                 constantBytes != 0 &&
                 (!context.TryReadUInt64(context[CpuRegister.Rcx] + index * 8UL, out constantAddress) ||
                  (constantBytes & 3) != 0 || !context.Memory.CanRead(constantAddress, constantBytes))))
                return context.SetReturn(-1);
            buffers[index] = (address, bytes / 4, constantAddress, constantBytes / 4);
        }
        var handle = (int)context[CpuRegister.R9];
        uint bufferIndex = 0, mode = 0;
        ulong argument = 0;
        if (flip && (!context.TryReadUInt32(context[CpuRegister.Rsp] + 8, out bufferIndex) ||
            !context.TryReadUInt32(context[CpuRegister.Rsp] + 16, out mode) ||
            !context.TryReadUInt64(context[CpuRegister.Rsp] + 24, out argument))) return context.SetReturn(-1);
        if (flip)
        {
            if ((int)bufferIndex < -1 || (int)bufferIndex >= 16)
                return context.SetReturn(unchecked((int)0x8029000A));
            var videoOut = new CpuContext(context.Memory, context.TargetGeneration);
            videoOut[CpuRegister.Rdi] = unchecked((ulong)handle);
            var pending = VideoOutExports.VideoOutIsFlipPending(videoOut);
            if (pending < 0)
                return context.SetReturn(pending);
            if ((int)bufferIndex >= 0 && !VideoOutExports.TryGetDisplayBufferInfo(handle, (int)bufferIndex, out _))
                return context.SetReturn(unchecked((int)0x8029000A));
        }
        if (flip && GnmDiagnostics.Log)
        {
            var flipNumber = Interlocked.Increment(ref _gnmDiagnosticFlipCount);
            if (flipNumber <= 8 || flipNumber % 120 == 0)
                Console.Error.WriteLine($"[GNMMODE][TIMING] gnm.submit_and_flip #{flipNumber} " +
                    $"handle={handle} index={bufferIndex} mode={mode} arg=0x{argument:X}.");
        }
        List<(ulong Address, uint Value, bool Interrupt)>? flipLabels = flip ? [] : null;
        var handlers = flipLabels is null ? GnmCommandPackets.Handlers
            : GnmCommandPackets.WithFlipLabels((address, value, interrupt) => flipLabels.Add((address, value, interrupt)));
        foreach (var buffer in buffers)
        {
            uint[] begin = [], end = [];
            if (buffer.ConstantWords != 0)
            {
                if (!TryCopyCommands(context, buffer.ConstantAddress, buffer.ConstantWords, out var constantCopy))
                    return context.SetReturn(-1);
                begin = GnmConstantEngine.Begin(constantCopy, buffer.ConstantWords);
                end = GnmConstantEngine.End();
            }
            var data = ArrayPool<uint>.Shared.Rent((int)(begin.Length + buffer.Words + end.Length));
            try
            {
                var commands = data.AsSpan(begin.Length, (int)buffer.Words);
                if (!context.Memory.TryRead(buffer.Address, MemoryMarshal.AsBytes(commands)))
                    return context.SetReturn(-1);
                var words = GnmCommandPackets.WordsBeforeOverrunningNop(commands, buffer.Address);
                if (words == 0) continue;
                begin.CopyTo(data.AsSpan());
                end.CopyTo(data.AsSpan(begin.Length + (int)words));
                var total = (uint)(begin.Length + words + end.Length);
                var copy = GnmCommandBufferCopies.Copy(data.AsSpan(0, (int)total));
                var result = copy != 0
                    ? AgcExports.SubmitGraphicsCommands(context, copy, total, handlers)
                    : AgcExports.SubmitGraphicsCommands(context, buffer.Address, words, handlers);
                if (result != 0) return result;
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(data);
            }
        }
        if (!flip)
            return context.SetReturn(0);
        AgcExports.WaitForAcceptedCommandStreams();
        var labels = new uint[flipLabels!.Count * 5];
        var cursor = 0;
        foreach (var label in flipLabels)
        {
            labels[cursor++] = PacketHeader.Make(5, PacketOpcode.EventWriteEndOfShader) | 2;
            labels[cursor++] = 0x62F | (label.Interrupt ? 0x38000u : 0u);
            labels[cursor++] = (uint)label.Address;
            labels[cursor++] = (uint)(label.Address >> 32) | 0x40000000u | (label.Interrupt ? 0x02000000u : 0u);
            labels[cursor++] = label.Value;
        }
        var labelAddress = labels.Length == 0 ? 0 : GnmCommandBufferCopies.Copy(labels);
        if (labels.Length != 0 && labelAddress == 0)
            return context.SetReturn(-1);
        var flipResult = VideoOutExports.SubmitFlip(context, handle, (int)bufferIndex, (int)mode, (long)argument);
        return flipResult != 0 || labels.Length == 0 ? flipResult
            : AgcExports.SubmitGraphicsCommands(context, labelAddress, (uint)labels.Length, GnmCommandPackets.Handlers);
    }

    private static bool TryCopyCommands(CpuContext context, ulong address, uint words, out ulong copy)
    {
        copy = address;
        var data = ArrayPool<uint>.Shared.Rent((int)words);
        try
        {
            if (!context.Memory.TryRead(address, MemoryMarshal.AsBytes(data.AsSpan(0, (int)words))))
                return false;
            var copied = GnmCommandBufferCopies.Copy(data.AsSpan(0, (int)words));
            if (copied != 0)
                copy = copied;
            return true;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(data);
        }
    }

    private static long _gnmDiagnosticFlipCount;
}
