// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.GnmMode.Gpu;

internal sealed class GnmConstantEngine
{
    internal const uint BeginMarker = 0x45434E47;
    internal const uint EndMarker = 0x44434E47;
    private const uint IndirectBufferConst = 0x33;
    private const uint LoadConstantRam = 0x80;
    private const int MaxDepth = 8;
    private static readonly ConditionalWeakTable<GpuCommandInterpreter, GnmConstantEngine> Engines = new();
    private static readonly ConcurrentDictionary<uint, byte> ReportedOpcodes = new();

    private readonly ICpuMemory _memory;
    private readonly Stack<Cursor> _buffers = new();

    private sealed class Cursor(ulong address, uint remaining)
    {
        public ulong Address = address;
        public uint Remaining = remaining;
    }

    private GnmConstantEngine(ICpuMemory memory, ulong address, uint words)
    {
        _memory = memory;
        _buffers.Push(new Cursor(address, words));
    }

    internal static uint[] Begin(ulong address, uint words) =>
        [PacketHeader.Make(5, PacketOpcode.Nop), BeginMarker, (uint)address, (uint)(address >> 32), words];

    internal static uint[] End() => [PacketHeader.Make(2, PacketOpcode.Nop), EndMarker];

    internal static bool TryHandleMarker(GpuCommandInterpreter interpreter, ICpuMemory? memory, ReadOnlySpan<uint> payload)
    {
        if (payload.Length == 4 && payload[0] == BeginMarker && memory is not null)
        {
            if (Engines.TryGetValue(interpreter, out var previous))
                previous.Run(interpreter, drain: true);
            Engines.AddOrUpdate(interpreter,
                new GnmConstantEngine(memory, payload[1] | ((ulong)payload[2] << 32), payload[3]));
            return true;
        }
        if (payload.Length == 1 && payload[0] == EndMarker)
        {
            if (Engines.TryGetValue(interpreter, out var engine))
            {
                engine.Run(interpreter, drain: true);
                Engines.Remove(interpreter);
            }
            return true;
        }
        return false;
    }

    internal static void WaitOnConstantEngine(GpuCommandInterpreter interpreter)
    {
        if (Engines.TryGetValue(interpreter, out var engine))
            engine.Run(interpreter, drain: false);
    }

    private void Run(GpuCommandInterpreter interpreter, bool drain)
    {
        Span<uint> header = stackalloc uint[1];
        while (_buffers.TryPeek(out var cursor))
        {
            if (cursor.Remaining == 0)
            {
                _buffers.Pop();
                continue;
            }
            if (!drain && interpreter.ConstantEngineCount > interpreter.DrawEngineCount)
                return;
            if (!_memory.TryRead(cursor.Address, MemoryMarshal.AsBytes(header)))
            {
                Abandon($"unreadable packet at 0x{cursor.Address:X16}");
                return;
            }

            var value = header[0];
            if (value >> 30 == 2)
            {
                Advance(cursor, 1);
                continue;
            }
            if (value >> 30 != 3)
            {
                Abandon($"packet type {value >> 30} at 0x{cursor.Address:X16}");
                return;
            }

            var length = PacketHeader.Length(value);
            if (length < 2 || length > cursor.Remaining)
            {
                Abandon($"packet length {length} exceeds {cursor.Remaining} at 0x{cursor.Address:X16}");
                return;
            }
            var payload = new uint[length - 1];
            if (!_memory.TryRead(cursor.Address + 4, MemoryMarshal.AsBytes(payload.AsSpan())))
            {
                Abandon($"unreadable payload at 0x{cursor.Address:X16}");
                return;
            }

            var opcode = PacketHeader.Opcode(value);
            var context = new PacketContext(value & ~2u, cursor.Address, 0, length, length);
            switch (opcode)
            {
                case PacketOpcode.WaitOnDeCounterDiff:
                    if (!drain && (int)(interpreter.ConstantEngineCount - interpreter.DrawEngineCount) >= (int)payload[0])
                        return;
                    break;
                case PacketOpcode.WriteConstantRam:
                    payload[0] &= 0xFFFF;
                    _ = PacketDispatchTable.Opcodes[opcode]!(interpreter, in context, payload);
                    break;
                case PacketOpcode.DumpConstantRam:
                    payload[0] &= 0xFFFF;
                    payload[1] &= 0x7FFF;
                    _ = PacketDispatchTable.Opcodes[opcode]!(interpreter, in context, payload);
                    break;
                case PacketOpcode.IncrementCeCounter:
                    var increment = context with { Header = PacketHeader.Make(2, PacketOpcode.IncrementCeCounter) };
                    _ = PacketDispatchTable.Opcodes[opcode]!(interpreter, in increment, [1u]);
                    break;
                case LoadConstantRam:
                    if (!LoadRam(interpreter, in context, payload))
                        return;
                    break;
                case IndirectBufferConst:
                    Advance(cursor, length);
                    if (_buffers.Count >= MaxDepth)
                    {
                        Abandon("constant indirect buffers nest too deeply");
                        return;
                    }
                    _buffers.Push(new Cursor((payload[0] & ~3u) | ((ulong)(payload[1] & 0xFFFF) << 32),
                        payload[2] & 0xFFFFF));
                    continue;
                case PacketOpcode.Nop:
                    break;
                default:
                    if (ReportedOpcodes.TryAdd(opcode, 0))
                        Console.Error.WriteLine($"[GNMMODE][WARN] GNM constant engine skips opcode 0x{opcode:X2} at " +
                            $"0x{cursor.Address:X16}. Further occurrences are omitted.");
                    break;
            }
            Advance(cursor, length);
        }
    }

    private bool LoadRam(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var source = payload[0] | ((ulong)payload[1] << 32);
        var words = payload[2] & 0x7FFF;
        var write = new uint[words + 1];
        write[0] = payload[3] & 0xFFFF;
        if (!_memory.TryRead(source, MemoryMarshal.AsBytes(write.AsSpan(1))))
        {
            Abandon($"unreadable constant RAM source 0x{source:X16}");
            return false;
        }
        var context = packet with { Header = PacketHeader.Make(words + 2, PacketOpcode.WriteConstantRam) };
        _ = PacketDispatchTable.Opcodes[PacketOpcode.WriteConstantRam]!(interpreter, in context, write);
        return true;
    }

    private static void Advance(Cursor cursor, uint words)
    {
        cursor.Address += words * 4UL;
        cursor.Remaining -= words;
    }

    private void Abandon(string reason)
    {
        if (ReportedOpcodes.TryAdd(uint.MaxValue, 0))
            Console.Error.WriteLine($"[GNMMODE][WARN] GNM constant engine stops: {reason}. Further occurrences are omitted.");
        _buffers.Clear();
    }
}
