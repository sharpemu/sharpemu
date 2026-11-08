// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.GnmMode.Libraries.VideoOut;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.GnmMode.Gpu;

internal static class GnmCommandPackets
{
    internal static readonly PacketHandler?[] Handlers = CreateHandlers();

    internal static PacketHandler?[] WithFlipLabels(Action<ulong, uint, bool> capture)
    {
        var handlers = (PacketHandler?[])Handlers.Clone();
        handlers[PacketOpcode.Nop] = Guard(
            (GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload) =>
            {
                if (packet.CustomCode != PacketCustomCode.Zero || payload.Length == 0 ||
                    payload[0] is not (0x68750778u or 0x68750781u))
                    return Nop(interpreter, in packet, payload);
                var interrupt = payload[0] == 0x68750781u;
                if (payload.Length < (interrupt ? 6 : 4) ||
                    interrupt && (payload[4] != 0x04 || payload[5] != 0x38))
                    throw new InvalidOperationException($"Unsupported GNM flip label at 0x{packet.PacketAddress:X16}: length={packet.Length}.");
                capture((payload[1] & ~3u) | ((ulong)payload[2] << 32), payload[3], interrupt);
                return packet.Length - 1;
            });
        return handlers;
    }

    private const uint GcnRequestedColorMaskKey = 0x400;
    private const uint ColorBase = 0x318;
    private const uint ColorStride = 15;
    private const uint ColorCmask = 7;
    private const uint ExtentHintHeader = 0xC0001000;
    private const uint NopOverrunWindow = 0x400;
    private const uint DepthSetupValues = 8;
    private const ulong DepthHintDistance = 12;
    private const int SampleLocationRegisters = 16;
    private static readonly ConcurrentDictionary<string, byte> Reported = new();
    private static readonly ConcurrentDictionary<uint, byte> ReportedConsumption = new();
    private static readonly ConditionalWeakTable<GpuCommandInterpreter, TargetExtents> Extents = new();
    private static readonly ConditionalWeakTable<GpuCommandInterpreter, StageResources> HullResources = new();
    private const uint HullResource1 = 0x10A;
    private const uint HullResource2 = 0x10B;
    private const uint HullUserData = 0x10C;
    private const uint LocalResource1 = 0x14A;
    private const uint LocalResource2 = 0x14B;
    private const uint LocalUserData = 0x14C;
    private const uint StageUserScalars = 16;
    private const uint JoinedHullUserScalars = 1u << 27;
    private const uint ShaderStages = 0x2D5;
    private const uint GcnTessellationStages = 0x45;
    private const uint MergedTessellationStages = 1u | 1u << 2 | 1u << 3 | 1u << 8 | 1u << 13 | 1u << 25;
    private static readonly bool TessellationEnabled = Environment.GetEnvironmentVariable("SHARPEMU_GNM_TESSELLATION") != "0";
    private static ICpuMemory? _memory;

    internal static void Reset(ICpuMemory memory)
    {
        _memory = memory;
        GnmDepthTargets.Reset(memory);
        GnmCommandBufferCopies.Reset(memory);
    }

    private sealed class StageResources
    {
        public uint Local;
        public uint Hull;
    }

    private sealed class TargetExtents
    {
        private const int MaxKnownHints = 4096;

        public ulong ColorHintAddress = ulong.MaxValue;
        public int ColorHintSlot = -1;
        public ulong DepthHintAddress = ulong.MaxValue;
        public ulong DepthHintTarget;
        public readonly uint[] Hints = new uint[8];
        public readonly ulong[] GuestAddresses = new ulong[8];
        public readonly Dictionary<ulong, uint> ColorHints = new();
        public readonly Dictionary<ulong, uint> DepthHints = new();

        public static void Remember(Dictionary<ulong, uint> hints, ulong address, uint hint)
        {
            if (address == 0)
                return;
            if (hints.Count >= MaxKnownHints && !hints.ContainsKey(address))
                hints.Clear();
            hints[address] = hint;
        }
    }

    private static PacketHandler?[] CreateHandlers()
    {
        var handlers = new PacketHandler?[256];
        for (var i = 0; i < handlers.Length; i++)
            if (PacketDispatchTable.Opcodes[i] is { } agcHandler)
                handlers[i] = (GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload) =>
                {
                    // Most AGC handlers encode the queue type implicitly rather than in header bit 1.
                    var normalized = packet with { Header = packet.Header & ~2u };
                    return agcHandler(interpreter, in normalized, payload);
                };
        handlers[PacketOpcode.Nop] = Nop;
        handlers[PacketOpcode.ReleaseMemory] = ReleaseMemory;
        handlers[PacketOpcode.EventWriteEndOfPipe] = EventWriteEndOfPipe;
        handlers[PacketOpcode.EventWriteEndOfShader] = EventWriteEndOfShader;
        handlers[PacketOpcode.SetContextRegister] = SetContextRegisters;
        handlers[PacketOpcode.SetShaderRegister] = SetShaderRegisters;
        handlers[PacketOpcode.IndirectBuffer] = IndirectBuffer;
        handlers[PacketOpcode.DispatchDirect] = DispatchDirect;
        handlers[PacketOpcode.DispatchIndirect] = DispatchIndirect;
        handlers[PacketOpcode.DrawIndirect] = DrawIndirect;
        handlers[PacketOpcode.DrawIndexOffset2] = DrawIndexOffset;
        handlers[PacketOpcode.WaitOnCeCounter] = WaitOnCeCounter;
        handlers[PacketOpcode.IncrementDeCounter] = IncrementDeCounter;
        handlers[PacketOpcode.SetBase] = PacketDispatchTable.Opcodes[PacketOpcode.SetBase];
        for (var i = 0; i < handlers.Length; i++)
            if (handlers[i] is { } handler)
                handlers[i] = Guard(handler);
        return handlers;
    }

    private static PacketHandler Guard(PacketHandler handler) =>
        (GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload) =>
        {
            // GNM uses EOP labels to retire the UI and vertex upload buffers.
            interpreter.CompletionLabelsRequireGpu = true;
            var consumed = handler(interpreter, in packet, payload);
            if (GnmDiagnostics.Log && consumed + 1 != packet.Length && ReportedConsumption.TryAdd(PacketHeader.Opcode(packet.Header), 0))
                Console.Error.WriteLine($"[GNMMODE][TRACE] GNM packet consumed {consumed + 1} dwords " +
                    $"but its header holds {packet.Length}: opcode=0x{PacketHeader.Opcode(packet.Header):X2} address=0x{packet.PacketAddress:X16}. Further occurrences are omitted.");
            if (consumed >= packet.Remaining || _memory is not { } memory)
                return consumed;
            var after = packet.Remaining - consumed - 1;
            if (after is 0 or >= NopOverrunWindow)
                return consumed;
            Span<byte> next = stackalloc byte[sizeof(uint)];
            var address = packet.PacketAddress + (consumed + 1UL) * sizeof(uint);
            if (!memory.TryRead(address, next))
                return consumed;
            var header = BinaryPrimitives.ReadUInt32LittleEndian(next);
            if (header >> 30 != 3 || PacketHeader.Opcode(header) != PacketOpcode.Nop || PacketHeader.Length(header) <= after)
                return consumed;
            Report("nop-overrun-runtime", $"[GNMMODE][WARN] GNM command buffer ends inside a NOP at execution: address=0x{address:X16} " +
                $"header=0x{header:X8} length={PacketHeader.Length(header)} remaining={after}; the NOP is dropped. Further occurrences are omitted.");
            return consumed + after;
        };

    private static uint EventWriteEndOfShader(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header | 2u };
        return PacketDispatchTable.Opcodes[PacketOpcode.EventWriteEndOfShader]!(interpreter, in normalized, payload);
    }

    private static uint WaitOnCeCounter(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        GnmConstantEngine.WaitOnConstantEngine(interpreter);
        return packet.Length - 1;
    }

    private static uint IncrementDeCounter(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = PacketHeader.Make(2, PacketOpcode.IncrementDeCounter) };
        _ = PacketDispatchTable.Opcodes[PacketOpcode.IncrementDeCounter]!(interpreter, in normalized, [0u]);
        return packet.Length - 1;
    }

    private static uint DispatchDirect(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~2u };
        var dispatch = PacketDispatchTable.Opcodes[PacketOpcode.DispatchDirect]!;
        if (_memory is { } memory && GnmScatterCopy.TryRun(interpreter, in normalized, payload, memory, dispatch))
            return packet.Length - 1;
        return dispatch(interpreter, in normalized, payload);
    }

    private static uint DispatchIndirect(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~3u };
        return PacketDispatchTable.Opcodes[PacketOpcode.DispatchIndirect]!(interpreter, in normalized, payload);
    }

    private static uint DrawIndirect(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~3u };
        var handler = PacketDispatchTable.Opcodes[PacketOpcode.DrawIndirect]!;
        if (packet.Length != 5)
            return handler(interpreter, in normalized, payload);
        Span<uint> fields = stackalloc uint[4];
        payload[..4].CopyTo(fields);
        for (var index = 1; index <= 2; index++)
            if (fields[index] is >= 0x4C and < 0x5C)
                fields[index] += 0x40;
        return handler(interpreter, in normalized, fields);
    }

    private static uint DrawIndexOffset(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~3u };
        return PacketDispatchTable.Opcodes[PacketOpcode.DrawIndexOffset2]!(interpreter, in normalized, payload);
    }

    private static uint IndirectBuffer(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~2u };
        var agcHandler = PacketDispatchTable.Opcodes[PacketOpcode.IndirectBuffer]!;
        if (packet.Length != 4 || _memory is not { } memory)
            return agcHandler(interpreter, in normalized, payload);
        var address = (payload[0] & ~3u) | ((ulong)(payload[1] & 0xFFFF) << 32);
        var words = payload[2] & 0xFFFFF;
        var trimmed = address == 0 || words == 0 ? words : WordsBeforeOverrunningNop(memory, address, words);
        if (trimmed == words || trimmed == 0)
            return agcHandler(interpreter, in normalized, payload);
        Span<uint> fields = stackalloc uint[3];
        payload[..3].CopyTo(fields);
        fields[2] = (fields[2] & ~0xFFFFFu) | trimmed;
        return agcHandler(interpreter, in normalized, fields);
    }

    internal static uint WordsBeforeOverrunningNop(ICpuMemory memory, ulong address, uint words)
    {
        var data = ArrayPool<uint>.Shared.Rent((int)words);
        try
        {
            var span = data.AsSpan(0, (int)words);
            return memory.TryRead(address, MemoryMarshal.AsBytes(span)) ? WordsBeforeOverrunningNop(span, address) : words;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(data);
        }
    }

    internal static uint WordsBeforeOverrunningNop(ReadOnlySpan<uint> data, ulong address)
    {
        var words = (uint)data.Length;
        var complete = CompletePacketWords(data, address, out var header);
        if (complete == words || ((header >> 8) & 0xFF) != PacketOpcode.Nop)
            return words;
        Report("nop-overrun", $"[GNMMODE][WARN] GNM command buffer ends inside a NOP: address=0x{address:X16} offset=0x{complete:X} " +
            $"header=0x{header:X8} length={PacketHeader.Length(header)} remaining={words - complete}; the NOP is dropped. Further occurrences are omitted.");
        return complete;
    }

    internal static uint CompleteRingWords(ICpuMemory memory, ulong address, uint words)
    {
        var data = ArrayPool<uint>.Shared.Rent((int)words);
        try
        {
            var span = data.AsSpan(0, (int)words);
            if (!memory.TryRead(address, MemoryMarshal.AsBytes(span)))
                return words;
            var complete = CompletePacketWords(span, address, out var header);
            if (GnmDiagnostics.Log && complete != words)
                Report("ring-partial", $"[GNMMODE][TRACE] GNM compute ring slice ends inside a packet: address=0x{address:X16} offset=0x{complete:X} " +
                    $"header=0x{header:X8} length={PacketHeader.Length(header)} remaining={words - complete}; the packet waits for the next doorbell. Further occurrences are omitted.");
            return complete;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(data);
        }
    }

    private static uint CompletePacketWords(ReadOnlySpan<uint> data, ulong address, out uint header)
    {
        header = 0;
        var words = (uint)data.Length;
        for (var offset = 0u; offset < words;)
        {
            var word = data[(int)offset];
            if (word == PacketHeader.FillerHeader)
            {
                offset++;
                continue;
            }
            if (word >> 30 != 3)
            {
                Report("walk-stopped", $"[GNMMODE][WARN] GNM command buffer walk stopped at a non-type-3 header: address=0x{address:X16} " +
                    $"offset=0x{offset:X} header=0x{word:X8} dwords={words}. Further occurrences are omitted.");
                return words;
            }
            var length = PacketHeader.Length(word);
            if (length > words - offset)
            {
                header = word;
                return offset;
            }
            offset += length;
        }
        return words;
    }

    private static void Report(string kind, string message)
    {
        if (Reported.TryAdd(kind, 0))
            Console.Error.WriteLine(message);
    }

    private static uint Nop(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (GnmConstantEngine.TryHandleMarker(interpreter, _memory, payload))
            return packet.Length - 1;
        if (packet.Header == ExtentHintHeader && payload.Length == 1 && Extents.TryGetValue(interpreter, out var extents))
        {
            if (packet.PacketAddress == extents.ColorHintAddress)
            {
                extents.ColorHintAddress = ulong.MaxValue;
                extents.Hints[extents.ColorHintSlot] = payload[0];
                TargetExtents.Remember(extents.ColorHints, extents.GuestAddresses[extents.ColorHintSlot], payload[0]);
                TranslateColorTarget(interpreter, extents, extents.ColorHintSlot);
                return packet.Length - 1;
            }
            if (packet.PacketAddress == extents.DepthHintAddress)
            {
                extents.DepthHintAddress = ulong.MaxValue;
                if (IsValidExtent(payload[0]))
                    TargetExtents.Remember(extents.DepthHints, extents.DepthHintTarget, payload[0]);
                ApplyDepthExtent(interpreter, in packet, payload[0]);
                return packet.Length - 1;
            }
        }

        if (packet.CustomCode == PacketCustomCode.Zero && payload.Length != 0 &&
            (payload[0] & 0xFFFF_F000u) == 0x6875_0000u &&
            (payload[0] & 0xFFFu) is 0x777 or 0x778 or 0x781 &&
            interpreter.PendingFlip.Handle == 0)
            return packet.Length - 1;

        var normalized = packet with { Header = packet.Header & ~2u };
        return PacketDispatchTable.Opcodes[PacketOpcode.Nop]!(interpreter, in normalized, payload);
    }

    private static uint EventWriteEndOfPipe(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~2u };
        var agcHandler = PacketDispatchTable.Opcodes[PacketOpcode.EventWriteEndOfPipe]!;
        if (payload.Length != 5)
            return agcHandler(interpreter, in normalized, payload);

        var control = payload[0];
        var cachePolicy = (control >> 25) & 3u;
        var dataSource = (payload[2] >> 29) & 7u;
        var classicBottomOfPipe = (control & 0x3Fu) == 0x28 &&
            ((control >> 8) & 7u) == 5 &&
            (dataSource is 2 or 4 || dataSource == 1 && ((control >> 12) & 0x3Fu) == 0);
        if (!classicBottomOfPipe && cachePolicy is not (1 or 2) && dataSource != 3)
            return agcHandler(interpreter, in normalized, payload);

        Span<uint> fields = stackalloc uint[5];
        payload.CopyTo(fields);
        if (dataSource == 3)
        {
            fields[2] = (fields[2] & ~(7u << 29)) | (4u << 29);
            if ((control & 0x3Fu) == 0x28 && ((control >> 8) & 7u) == 5)
                fields[0] &= ~(7u << 8);
        }
        // PS4 EOP labels can request stream/bypass cache policy; Vulkan owns cache eviction.
        if (cachePolicy is 1 or 2)
            fields[0] &= ~(3u << 25);
        // PS4 BOTTOM_OF_PIPE_TS uses index 5; the shared label writer expects
        // CACHE_FLUSH_TS for 32-bit data, or index 0 for 64-bit data/clock.
        if (classicBottomOfPipe)
        {
            if (dataSource == 1)
                fields[0] = (fields[0] & ~0x3Fu) | 0x04u;
            else
                fields[0] &= ~(7u << 8);
        }
        return agcHandler(interpreter, in normalized, fields);
    }

    private static uint SetContextRegisters(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var offset = RegisterBankLayout.Normalize(payload[0]);
        var normalized = packet with { Header = packet.Header & ~2u };
        var agcHandler = PacketDispatchTable.Opcodes[PacketOpcode.SetContextRegister]!;
        uint? knownDepthExtent = null;
        if (offset == ContextRegisterOffset.DbZInfo && payload.Length - 1 == DepthSetupValues)
        {
            var depthExtents = Extents.GetOrCreateValue(interpreter);
            depthExtents.DepthHintAddress = packet.PacketAddress + (packet.Length + DepthHintDistance) * sizeof(uint);
            depthExtents.DepthHintTarget = (ulong)payload[1 + (int)(ContextRegisterOffset.DbZReadBase - ContextRegisterOffset.DbZInfo)] << 8;
            if (depthExtents.DepthHints.TryGetValue(depthExtents.DepthHintTarget, out var knownDepth))
                knownDepthExtent = knownDepth;
        }
        var hasLegacyFields = false;
        for (var index = 1; index < payload.Length; index++)
        {
            var fieldOffset = offset + (uint)index - 1;
            if (fieldOffset is >= 0x318 and < 0x390 or 0x08E or 0x16 or 0x102 or 0x2A5 or 0x2A8 or 0x2A9 or ShaderStages ||
                IsUnmodeledContextRegister(fieldOffset))
            {
                hasLegacyFields = true;
                break;
            }
        }

        if (offset == ContextRegisterOffset.PaScAaSampleLocations0 && payload.Length - 1 == SampleLocationRegisters)
        {
            Span<uint> location = stackalloc uint[2];
            var single = normalized with { Header = PacketHeader.Make(3, PacketOpcode.SetContextRegister) };
            for (var index = 0; index < SampleLocationRegisters; index++)
            {
                location[0] = payload[0] + (uint)index;
                location[1] = payload[1 + index];
                _ = agcHandler(interpreter, in single, location);
            }
            return packet.Length - 1;
        }

        if (!hasLegacyFields)
        {
            var consumed = agcHandler(interpreter, in normalized, payload);
            if (GnmDepthTargets.Touches(offset, (uint)payload.Length - 1))
                GnmDepthTargets.Observe(interpreter, in packet, offset, payload[1..]);
            return consumed;
        }

        Span<uint> fields = stackalloc uint[2];
        uint changedColors = 0;
        var startOffset = offset;
        normalized = normalized with { Header = PacketHeader.Make(3, PacketOpcode.SetContextRegister) };
        for (var index = 1; index < payload.Length; index++, offset++)
        {
            if (offset >= 0x318 && offset < 0x390)
            {
                interpreter.TypedRegisters.UnmodeledTableRegistersForWrite[offset] = payload[index];
                changedColors |= 1u << (int)((offset - 0x318) / 15);
            }
            if (IsLegacyColorField(offset))
            {
                // PS4 color pitch/slice/dimensions and mask slice fields differ from AGC.
                // Preserve them for the GnmMode render-target translator.
                interpreter.TypedRegisters.UnmodeledTableRegistersForWrite[offset] = payload[index];
                continue;
            }

            if (IsUnmodeledContextRegister(offset))
            {
                interpreter.TypedRegisters.UnmodeledTableRegistersForWrite[offset] = payload[index];
                continue;
            }

            if (offset is 0x2A8 or 0x2A9)
            {
                // GCN VGT_INSTANCE_STEP_RATE_0/1 have no AGC register writer.
                interpreter.TypedRegisters.UnmodeledTableRegistersForWrite[offset] = payload[index];
                if (payload[index] != 1)
                    Console.Error.WriteLine($"[GNMMODE][WARN] GCN instance step rate register 0x{offset:X}={payload[index]}; host vertex divisors need translation.");
                continue;
            }

            if (offset == ContextRegisterOffset.CbTargetMask)
            {
                interpreter.TypedRegisters.UnmodeledTableRegistersForWrite[GcnRequestedColorMaskKey] = payload[index];
                fields[0] = offset;
                fields[1] = ActiveColorMask(interpreter, payload[index]);
                _ = agcHandler(interpreter, in normalized, fields);
                continue;
            }

            if (IsColorInfo(offset))
            {
                fields[0] = offset;
                fields[1] = payload[index];
                _ = agcHandler(interpreter, in normalized, fields);

                // GCN COLOR_INVALID disables an attachment even if the write mask is still set.
                var context = interpreter.TypedRegisters.Context;
                var requested = context.UnmodeledTableRegisters.TryGetValue(GcnRequestedColorMaskKey, out var raw)
                    ? raw : context.RenderTargetMask;
                fields[0] = ContextRegisterOffset.CbTargetMask;
                fields[1] = ActiveColorMask(interpreter, requested);
                _ = agcHandler(interpreter, in normalized, fields);
                continue;
            }

            if (offset == 0x16)
            {
                // GCN DB_DEPTH_SIZE is in 8x8 tiles at 0x28058; RDNA2
                // DB_DEPTH_SIZE_XY uses pixel maxima at context offset 0x007.
                var tiles = payload[index];
                var xMax = (((tiles & 0x7FFu) + 1u) << 3) - 1u;
                var yMax = ((((tiles >> 11) & 0x7FFu) + 1u) << 3) - 1u;
                if (knownDepthExtent is { } known && (known & 0xFFFF) - 1 <= xMax && (known >> 16) - 1 <= yMax)
                {
                    xMax = (known & 0xFFFF) - 1;
                    yMax = (known >> 16) - 1;
                }
                fields[0] = ContextRegisterOffset.DbDepthSizeXy;
                fields[1] = xMax | (yMax << 16);
                _ = agcHandler(interpreter, in normalized, fields);
                continue;
            }

            if (offset == ShaderStages)
            {
                fields[0] = offset;
                fields[1] = TessellationEnabled && (payload[index] & 0xFF) == GcnTessellationStages ? MergedTessellationStages : payload[index];
                _ = agcHandler(interpreter, in normalized, fields);
                continue;
            }

            if (offset is 0x102 or 0x2A5)
            {
                fields[0] = offset == 0x102 ? UserConfigRegisterOffset.GeIndexOffset : UserConfigRegisterOffset.GeMultiPrimIbResetEn;
                fields[1] = payload[index];
                var userPacket = normalized with { Header = PacketHeader.Make(3, PacketOpcode.SetUserConfigRegister) };
                _ = PacketDispatchTable.Opcodes[PacketOpcode.SetUserConfigRegister]!(interpreter, in userPacket, fields);
                continue;
            }

            fields[0] = offset;
            fields[1] = payload[index];
            _ = agcHandler(interpreter, in normalized, fields);
        }
        var extents = Extents.GetOrCreateValue(interpreter);
        var values = (uint)payload.Length - 1;
        var hintedBaseSlot = -1;
        if (startOffset >= ColorBase && startOffset < 0x390 && (startOffset - ColorBase) % ColorStride is 0 or ColorCmask)
        {
            var slot = (int)((startOffset - ColorBase) / ColorStride);
            var isBase = (startOffset - ColorBase) % ColorStride == 0;
            if (isBase ? values is 14 or 13 or 11 : values == 4)
            {
                extents.ColorHintAddress = packet.PacketAddress + packet.Length * sizeof(uint);
                extents.ColorHintSlot = slot;
                if (isBase)
                    hintedBaseSlot = slot;
            }
            else if (isBase)
                extents.Hints[slot] = 0;
        }
        for (var slot = 0; slot < 8; slot++)
        {
            if ((changedColors & (1u << slot)) == 0) continue;
            var baseOffset = ColorBase + (uint)slot * ColorStride;
            if (startOffset <= baseOffset && baseOffset < startOffset + values)
                extents.GuestAddresses[slot] = interpreter.TypedRegisters.ColorTargetsForWrite[slot].BaseAddress;
            if (slot == hintedBaseSlot)
                extents.Hints[slot] = extents.ColorHints.GetValueOrDefault(extents.GuestAddresses[slot]);
            TranslateColorTarget(interpreter, extents, slot);
        }
        if (GnmDepthTargets.Touches(startOffset, values))
            GnmDepthTargets.Observe(interpreter, in packet, startOffset, payload[1..]);
        return (uint)payload.Length;
    }

    private static bool IsValidExtent(uint extent) =>
        (extent & 0xFFFF) is not (0 or > 16384) && (extent >> 16) is not (0 or > 16384);

    private static void ApplyDepthExtent(GpuCommandInterpreter interpreter, in PacketContext packet, uint extent)
    {
        var width = extent & 0xFFFF;
        var height = extent >> 16;
        if (!IsValidExtent(extent))
            return;
        Span<uint> fields = [ContextRegisterOffset.DbDepthSizeXy, (width - 1) | ((height - 1) << 16)];
        var normalized = packet with { Header = PacketHeader.Make(3, PacketOpcode.SetContextRegister) };
        _ = PacketDispatchTable.Opcodes[PacketOpcode.SetContextRegister]!(interpreter, in normalized, fields);
        GnmDepthTargets.Translate(interpreter, in packet);
    }

    private static void TranslateColorTarget(GpuCommandInterpreter interpreter, TargetExtents extents, int slot)
    {
        var raw = interpreter.TypedRegisters.Context.UnmodeledTableRegisters;
        var start = ColorBase + (uint)slot * ColorStride;
        if (!raw.TryGetValue(start + 1, out var pitchWord) || !raw.TryGetValue(start + 2, out var sliceWord))
            return;
        var pitch = ((pitchWord & 0x7FF) + 1) * 8;
        var height = ((sliceWord & 0x3FFFFF) + 1) * 64 / pitch;
        if (height is 0 or > 16384) throw new InvalidDataException($"Invalid GCN color extent: pitch={pitch}, slice=0x{sliceWord:X8}.");
        ref var target = ref interpreter.TypedRegisters.ColorTargetsForWrite[slot];
        var tileIndex = raw.GetValueOrDefault(start + 5) & 31;
        var linear = (target.Info & 0x80) != 0 || tileIndex is 8 or 31;
        var guestAddress = extents.GuestAddresses[slot] != 0 ? extents.GuestAddresses[slot] : target.BaseAddress;
        var hintWidth = extents.Hints[slot] & 0xFFFF;
        var hintHeight = extents.Hints[slot] >> 16;
        var width = hintWidth != 0 && hintWidth <= pitch ? hintWidth : pitch;
        var hostHeight = hintHeight != 0 && hintHeight <= height ? hintHeight : height;
        target = target with { BaseAddress = guestAddress };
        var hostTile = linear ? GuestTileMode.Linear : GuestTileMode.RenderTarget;
        // GNM's CB_COLOR_SLICE includes padded rows (1088 for a 1080p
        // scanout). VideoOut uses the visible extent and, when necessary,
        // a 64 KiB-aligned shadow. Bind the same image for both paths.
        if (!linear && target.Layout != 0 &&
            GnmVideoOutExports.TryGetDisplayTarget(guestAddress, pitch, height,
                out var originalAddress, out var displayAddress, out var displayHeight))
        {
            guestAddress = originalAddress;
            target = target with { BaseAddress = displayAddress };
            hostHeight = displayHeight;
        }
        else if (!linear)
            hostTile = FittingHostTile(in target, width, hostHeight, sliceWord);
        // GCN has no ATTRIB2/3: derive its allocated extent from pitch/slice,
        // and use the shared image cache's layout for GPU-generated targets.
        target = target with
        {
            Attrib2 = (hostHeight - 1) | ((width - 1) << 14),
            Attrib3 = (1u << 24) | ((uint)hostTile << 14),
        };
        interpreter.TypedRegisters.MarkWritten(1u << (int)RegisterStamp.ColorTargets);
        GcnImageDescriptor.ObserveColorTarget(guestAddress, in target, pitch, height, tileIndex);
    }

    private static GuestTileMode FittingHostTile(in ColorTargetWords target, uint width, uint height, uint sliceWord)
    {
        var encoding = GuestPixelFormats.ResolveRenderTargetEncoding(target.Layout, target.NumberType);
        var bytes = encoding.IsValid ? GuestPixelFormats.RenderTargetBytesPerElement(encoding.Format) : 0;
        if (bytes is not (1 or 2 or 4 or 8 or 16) || target.SamplesLog2 != 0 || target.FragmentsLog2 != 0)
            return GuestTileMode.RenderTarget;
        var allocated = ((ulong)(sliceWord & 0x3FFFFF) + 1) * 64 * bytes;
        if (TileGeometry.TryGetRenderTargetSize(width, height, TileGeometry.RenderTargetPitch(width, bytes), bytes, out var tiled) &&
            tiled.Size <= allocated)
            return GuestTileMode.RenderTarget;
        var linearPitch = TileGeometry.TexturePitch(ImageDescription.RenderTargetTransferFormat(bytes), width, GuestTileMode.Linear);
        return linearPitch != 0 && (ulong)linearPitch * height * bytes <= allocated ? GuestTileMode.Linear : GuestTileMode.RenderTarget;
    }

    private static bool IsLegacyColorField(uint offset) =>
        offset >= 0x318 && offset < 0x390 &&
        (offset - 0x318) % 15 is 1 or 2 or 6 or 8 or 10 or 13 or 14;

    private static bool IsColorInfo(uint offset) =>
        offset >= ContextRegisterOffset.CbColor0Info && offset < 0x390 &&
        (offset - ContextRegisterOffset.CbColor0Info) % 15 == 0;

    private static uint ActiveColorMask(GpuCommandInterpreter interpreter, uint requested)
    {
        var targets = interpreter.TypedRegisters.Context.ColorTargets;
        for (var slot = 0; slot < targets.Length; slot++)
            if (((targets[slot].Info >> 2) & 0x1Fu) == 0)
                requested &= ~(0xFu << (slot * 4));
        return requested;
    }

    private static uint SetShaderRegisters(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var offset = payload[0] & 0xFFFF;
        Span<uint> field = stackalloc uint[2];
        var normalized = packet with { Header = PacketHeader.Make(3, PacketOpcode.SetShaderRegister) };
        for (var index = 1; index < payload.Length; index++, offset++)
        {
            if (offset is 0x216 or 0x217 or 0x219 or 0x21A)
            {
                // PS4 compute CU affinity masks are scheduled by the host GPU on Vulkan.
                interpreter.Registers.Shader[offset] = payload[index];
                continue;
            }

            if (TranslateTessellationRegister(interpreter, offset, payload[index], out var target, out var translated))
            {
                if (target == 0) continue;
                field[0] = target;
                field[1] = translated;
                _ = PacketDispatchTable.Opcodes[PacketOpcode.SetShaderRegister]!(interpreter, in normalized, field);
                continue;
            }

            if (IsGeometryStageShaderRegister(offset))
                continue;

            if (offset == 0x4A)
                GcnStageResources.ObserveResource1(interpreter.TypedRegisters.Shader.Vertex.ExportAddress, payload[index]);
            else if (offset == 0x4B)
                GcnStageResources.ObserveResource2(interpreter.TypedRegisters.Shader.Vertex.ExportAddress, payload[index]);

            // The common classic vertex path consumes ES code and the GS user-data/resource bank.
            field[0] = offset switch
            {
                0x48 => 0xC8, 0x49 => 0xC9, 0x4A => 0x8A, 0x4B => 0x8B,
                >= 0x4C and < 0x5C => 0x8C + offset - 0x4C,
                _ => offset,
            };
            field[1] = payload[index];
            _ = PacketDispatchTable.Opcodes[PacketOpcode.SetShaderRegister]!(interpreter, in normalized, field);
        }
        return (uint)payload.Length;
    }

    private static bool TranslateTessellationRegister(GpuCommandInterpreter interpreter, uint offset, uint value,
        out uint target, out uint translated)
    {
        target = 0;
        translated = value;
        switch (offset)
        {
            case >= HullUserData and < HullUserData + StageUserScalars:
                target = offset + StageUserScalars;
                return true;
            case >= LocalUserData and < LocalUserData + StageUserScalars:
                target = HullUserData + offset - LocalUserData;
                return true;
            case HullResource1:
                GcnStageResources.ObserveResource1(interpreter.TypedRegisters.Shader.Vertex.HullAddress, value);
                target = offset;
                return true;
            case LocalResource1:
                GcnStageResources.ObserveResource1(interpreter.TypedRegisters.Shader.Vertex.LocalAddress, value);
                return true;
            case not (HullResource2 or LocalResource2):
                return false;
        }
        var resources = HullResources.GetOrCreateValue(interpreter);
        if (offset == HullResource2)
        {
            resources.Hull = value;
            GcnStageResources.ObserveResource2(interpreter.TypedRegisters.Shader.Vertex.HullAddress, value);
        }
        else
        {
            resources.Local = value;
            GcnStageResources.ObserveResource2(interpreter.TypedRegisters.Shader.Vertex.LocalAddress, value);
        }
        target = HullResource2;
        translated = JoinedHullUserScalars | (((resources.Local >> 7) & 0x1FF) << 18) | ((resources.Hull | resources.Local) & 1);
        return true;
    }

    private static bool IsGeometryStageShaderRegister(uint offset) =>
        offset is >= 0x88 and < 0x9C or >= 0xC8 and < 0xDC;

    private static bool IsUnmodeledContextRegister(uint offset) =>
        offset is 0x290 or >= 0x295 and <= 0x29B or 0x2AA or 0x2AC or >= 0x2B4 and <= 0x2C3 or
            >= 0x2CA and <= 0x2CE or >= 0x2D7 and <= 0x2DA or 0x2E5 or 0x2E6;

    private static uint ReleaseMemory(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var normalized = packet with { Header = packet.Header & ~2u };
        var agcHandler = PacketDispatchTable.Opcodes[PacketOpcode.ReleaseMemory]!;
        if (packet.Length != 7)
            return agcHandler(interpreter, in normalized, payload);
        Span<uint> fields = stackalloc uint[7];
        payload.CopyTo(fields);
        if ((fields[0] & 0x3Fu) == 0x2F && ((fields[1] >> 29) & 7u) == 3)
            fields[0] = (fields[0] & ~0x73Fu) | 0x504u;
        // PS4 has no trailing interrupt-context dword; keep the original packet's consumed length.
        fields[6] = 0;
        normalized = normalized with { Header = PacketHeader.Make(8, PacketOpcode.ReleaseMemory) };
        _ = agcHandler(interpreter, in normalized, fields);
        return 6;
    }
}
