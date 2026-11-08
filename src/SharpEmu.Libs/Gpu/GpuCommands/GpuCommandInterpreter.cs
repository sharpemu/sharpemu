// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The packet a handler executes: header fields plus its position in the cursor.
public readonly record struct PacketContext(uint Header, ulong PacketAddress, uint Offset, uint Remaining, uint Total)
{
    public uint Length => PacketHeader.Length(Header);

    public uint Opcode => PacketHeader.Opcode(Header);

    public uint CustomCode => PacketHeader.CustomCode(Header);
}

public readonly record struct FlipRequest(int Handle, int Index, int FlipMode, long FlipArgument);

// Parses one queue's command stream and keeps that queue's register and packet state.
public sealed partial class GpuCommandInterpreter
{
    public const int ComputeQueueBase = 0x20;
    public const uint ConstantRamDwords = 0x3000;
    public const uint RingChunkBytes = 0x10000;

    private readonly ICommandStreamHost _host;
    private PacketHandler?[] _packetHandlers = PacketDispatchTable.Opcodes;
    private readonly List<uint[]> _payloadScratchByDepth = new();
    private readonly List<PacketWindow> _packetWindowsByDepth = new();
    private long _packetWindowGeneration;
    private PendingCommandSignal _pendingCommands = PendingCommandSignal.AlwaysRaised;
    private readonly uint[] _constantRam = new uint[ConstantRamDwords];
    private PacketCursorStack? _execution;
    private bool _chainRequested;
    private ulong _packetSerial;
    private ReadOnlyMemory<byte> _submittedCommands;
    private IReadOnlyDictionary<ulong, byte[]>? _submittedRegisterTables;
    private ulong _submittedCommandAddress;
    private ulong _lastWriteAddress;
    private ulong _lastWriteValue;
    private int _lastWriteLength;
    private ulong _lastWritePacket;

    public GpuCommandInterpreter(ICommandStreamHost host, int queueId, int interruptEventId)
    {
        _host = host;
        QueueId = queueId;
        InterruptEventId = interruptEventId;
        TypedRegisters = new RegisterBanks(host.Fatal);
    }

    public int QueueId { get; }

    public bool CompletionLabelsRequireGpu { get; set; }

    internal void SetPacketHandlers(PacketHandler?[] handlers)
    {
        if (handlers.Length != 256)
            throw new ArgumentException("A packet dispatch table must contain 256 entries.", nameof(handlers));
        _packetHandlers = handlers;
    }

    public int InterruptEventId { get; }

    public bool IsComputeQueue => InterruptEventId >= ComputeQueueBase;

    public CommandRegisterBanks Registers { get; } = new();

    // The typed banks the render executor reads; the dictionaries feed the translation they replace.
    public RegisterBanks TypedRegisters { get; }

    public ulong SubmitId { get; set; }

    public uint IndexTypeAndSize { get; private set; }

    public uint IndexBufferSize { get; private set; }

    public ulong IndexBaseAddress { get; private set; }

    public ulong DrawIndirectArgumentsBase { get; private set; }

    public ulong DispatchIndirectArgumentsBase { get; private set; }

    // Persistent draw state: indirect draws update it for later draws. An indirect draw
    // resolved on the GPU leaves the count in guest memory; it is read only if a later
    // draw needs it.
    public uint InstanceCount
    {
        get
        {
            if (_deferredInstanceCountAddress != 0)
            {
                _instanceCount = ReadDword(_deferredInstanceCountAddress);
                _deferredInstanceCountAddress = 0;
            }

            return _instanceCount;
        }
        private set
        {
            _instanceCount = value;
            _deferredInstanceCountAddress = 0;
        }
    }

    private uint _instanceCount = 1;
    private ulong _deferredInstanceCountAddress;

    public uint DrawIndexOffset { get; private set; }

    public uint UserDataMarker { get; private set; }

    public bool PredicateSkip { get; private set; }

    public bool ConditionalWaitEnabled { get; private set; }

    public FlipRequest PendingFlip { get; private set; }

    public uint ConstantEngineCount { get; private set; }

    public uint DrawEngineCount { get; private set; }

    public bool ConstantEngineComplete { get; set; }

    private readonly ulong[] _syntheticOcclusionCounters = new ulong[4];
    private ulong _occlusionCounterStart;
    private int _selectedOcclusionCounter;
    private uint _occlusionDumpStrideBytes = 16;
    private uint _occlusionDumpInstanceMask = 0xFFFF;

    public ulong SyntheticOcclusionCounter => _syntheticOcclusionCounters[_selectedOcclusionCounter];

    private static long _occlusionWrites;
    private static long _pixelPipeStatControls;
    private static long _pixelPipeStatResets;
    private static long _predicationClears;
    private static long _predicationBoolWaiting;
    private static long _predicationBoolUnpredicated;

    public static string TakePredicationReport() =>
        FormattableString.Invariant($"[PERF][PREDICATION] occlusion_writes={Interlocked.Exchange(ref _occlusionWrites, 0)} clears={Interlocked.Exchange(ref _predicationClears, 0)} ") +
        FormattableString.Invariant($"bool_wait_flushes={Interlocked.Exchange(ref _predicationBoolWaiting, 0)} bool_unpredicated={Interlocked.Exchange(ref _predicationBoolUnpredicated, 0)} ") +
        FormattableString.Invariant($"stat_control={Interlocked.Exchange(ref _pixelPipeStatControls, 0)} stat_reset={Interlocked.Exchange(ref _pixelPipeStatResets, 0)} ") +
        FormattableString.Invariant($"count_control_writes={Interlocked.Exchange(ref global::SharpEmu.Libs.Gpu.GpuCommands.Registers.RegisterWriters.DbCountControlWrites, 0)} count_control_last=0x{Interlocked.Read(ref global::SharpEmu.Libs.Gpu.GpuCommands.Registers.RegisterWriters.DbCountControlLastValue):X}");

    public ulong AtomicReturnMeData { get; private set; }

    public bool AtomicReturnMeValid { get; private set; }

    public ulong AtomicReturnPfpData { get; private set; }

    public bool AtomicReturnPfpValid { get; private set; }

    public ReadOnlySpan<uint> ConstantRam => _constantRam;

    public void Reset()
    {
        var before = TypedRegisters.Context;
        _lastWriteLength = 0;
        Registers.Reset();
        TypedRegisters.Reset();
        IndexTypeAndSize = 0;
        IndexBufferSize = 0;
        UserDataMarker = 0;
        DrawIndirectArgumentsBase = 0;
        DispatchIndirectArgumentsBase = 0;
        Array.Clear(_constantRam);
        if (Rendering.ScissorTrace.Instance is { Active: true } trace)
            trace.ContextOperation(SubmitId, QueueId, 0, "Reset", TypedRegisters, before);
    }

    // The queue-reset packets also drop the index base, the instance count and the wait condition.
    public void ResetForQueueReset()
    {
        Reset();
        IndexBaseAddress = 0;
        InstanceCount = 1;
        DrawIndexOffset = 0;
        ConditionalWaitEnabled = false;
        _host.OnQueueReset(QueueId);
    }

    public void ResetCounters()
    {
        DrawEngineCount = 0;
        ConstantEngineCount = 0;
        ConstantEngineComplete = false;
    }

    public void SetFlip(FlipRequest flip) => PendingFlip = flip;

    public SubmissionProgress Process(PacketCursorStack execution, ulong address, uint dwordCount,
        ReadOnlyMemory<byte> commandBytes = default, IReadOnlyDictionary<ulong, byte[]>? registerTables = null)
    {
        if (_execution is not null)
        {
            throw _host.Fatal($"The command interpreter is already running a stream: queue={QueueId}.");
        }

        if (execution.IsEmpty && dwordCount != 0)
        {
            execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        }

        execution.Suspended = false;
        execution.MadeProgress = false;
        // Forwarding is local to this slice, never a cached signal from an older submission.
        _lastWriteLength = 0;
        _pendingCommands = _host.CommandSignal;
        _packetWindowGeneration++;
        _submittedCommands = commandBytes;
        _submittedRegisterTables = registerTables;
        _submittedCommandAddress = address;
        _execution = execution;
        try
        {
            RunPackets(execution, 0);
        }
        finally
        {
            _execution = null;
            _submittedCommands = default;
            _submittedRegisterTables = null;
        }

        return execution.IsEmpty ? SubmissionProgress.Complete : SubmissionProgress.Blocked;
    }

    // Runs a nested buffer to completion before the calling packet finishes.
    public void RunIndirectBuffer(ulong address, uint dwordCount)
    {
        var execution = RequireExecution();
        if (dwordCount == 0)
        {
            return;
        }

        if (execution.Depth >= PacketCursorStack.MaxDepth)
        {
            throw _host.Fatal($"The indirect buffer nesting is too deep: depth={execution.Depth} address=0x{address:X16} dwords={dwordCount}.");
        }

        var stopDepth = execution.Depth;
        ReadBehindEarlierWork(address);
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address));
        RunPackets(execution, stopDepth);
    }

    // Replaces the current buffer with another one at the same depth; the chaining packet ends its buffer.
    private void ChainToBuffer(ulong address, uint dwordCount, bool followedChunkAdvance = false)
    {
        var execution = RequireExecution();
        if (!followedChunkAdvance)
        {
            ReadBehindEarlierWork(address);
        }

        execution.Pop();
        execution.Push(new PacketCursor(address, dwordCount, ringChunkBase: address, followedChunkAdvance));
        _chainRequested = true;
    }

    // Earlier work in the stream can build the buffer a packet calls or chains to (a GPU pass that writes
    // a dispatch list). An operand read completes that work first, so the packets read next hold what it
    // wrote. A ring chunk the guest still appends to is read as it is written.
    private void ReadBehindEarlierWork(ulong address)
    {
        Span<byte> first = stackalloc byte[sizeof(uint)];
        _ = _host.TryReadGuestOperand(address, first);
    }

    public void Suspend() => RequireExecution().Suspended = true;

    // A paired wait must still observe this queue's immediate label store when the CPU
    // resets the live address for the next frame between the two packets (Dead Cells).
    private bool TryReadOwnWrite(ulong address, bool is64Bit, out ulong value)
    {
        value = 0;
        if (address != _lastWriteAddress || _packetSerial - _lastWritePacket > 2 ||
            _lastWriteLength < (is64Bit ? sizeof(ulong) : sizeof(uint)))
        {
            return false;
        }
        value = is64Bit ? _lastWriteValue : (uint)_lastWriteValue;
        return true;
    }

    private PacketCursorStack RequireExecution() =>
        _execution ?? throw _host.Fatal($"No command stream is running: queue={QueueId}.");

    private void RunPackets(PacketCursorStack execution, int stopDepth)
    {
        while (execution.Depth > stopDepth)
        {
            if (_pendingCommands.IsRaised)
            {
                _host.RunPendingCommands();
            }

            var cursorIndex = execution.Depth - 1;
            ref var cursor = ref execution.At(cursorIndex);
            if (cursor.Offset > cursor.DwordCount)
            {
                throw _host.Fatal($"The packet cursor is past its buffer: offset={cursor.Offset} dwords={cursor.DwordCount} address=0x{cursor.Address:X16}.");
            }

            if (cursor.DeferredAdvance != 0)
            {
                if (cursor.DeferredAdvance > cursor.Remaining)
                {
                    throw _host.Fatal($"The deferred advance is past the buffer: advance={cursor.DeferredAdvance} remaining={cursor.Remaining} address=0x{cursor.Address:X16}.");
                }

                cursor.Offset += cursor.DeferredAdvance;
                cursor.DeferredAdvance = 0;
                execution.MadeProgress = true;
                continue;
            }

            if (cursor.Offset == cursor.DwordCount)
            {
                execution.Pop();
                continue;
            }

            var packetAddress = cursor.Address + ((ulong)cursor.Offset * sizeof(uint));
            uint header;
            PacketWindow? window;
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandPacketRead))
            {
                window = ObtainPacketWindow(cursorIndex, in cursor, packetAddress);
                header = window is null
                    ? ReadDword(packetAddress, RenderPhaseProfile.CommandReadKind.Header)
                    : window.Words[window.IndexOf(packetAddress)];
            }

            _packetSerial++;
            var total = cursor.DwordCount;
            var remaining = cursor.Remaining;
            var offset = cursor.Offset;

            if (header == PacketHeader.FillerHeader)
            {
                cursor.Offset++;
                execution.MadeProgress = true;
                continue;
            }

            // Ring memory the guest has not written yet; retry after it appends more.
            if (header == 0 && cursor.FollowedChunkAdvance)
            {
                execution.Suspended = true;
                return;
            }

            if (remaining < 2)
            {
                throw _host.Fatal($"The packet is shorter than two dwords: offset=0x{offset:X5} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            var length = PacketHeader.Length(header);
            if (length > remaining)
            {
                throw _host.Fatal($"The packet is longer than the buffer: offset=0x{offset:X5} header=0x{header:X8} length={length} remaining={remaining} address=0x{packetAddress:X16}.");
            }

            if (PacketHeader.IsPredicated(header) && PredicateSkip)
            {
                TraceSkippedPackets("predicated", packetAddress, length, PacketHeader.Opcode(header));
                cursor.Offset += length;
                execution.MadeProgress = true;
                continue;
            }

            var opcode = PacketHeader.Opcode(header);
            // Only a wait or an inert NOP/marker may separate the store and its paired wait.
            // Draws, dispatches and transfers may produce a newer GPU value at the same address.
            if (opcode is not (PacketOpcode.WaitRegisterMemory or PacketOpcode.WaitRegisterMemory64) &&
                !(opcode == PacketOpcode.Nop && PacketHeader.CustomCode(header) is
                    0 or PacketCustomCode.PushMarker or PacketCustomCode.PopMarker or
                    PacketCustomCode.WaitMemory32 or PacketCustomCode.WaitMemory64))
            {
                _lastWriteLength = 0;
            }
            var handler = _packetHandlers[opcode];
            if (handler is null)
            {
                DumpUnknownPacket(cursor, offset);
                throw _host.Fatal($"The packet opcode is unknown: offset=0x{offset:X5} header=0x{header:X8} opcode=0x{opcode:X2} address=0x{packetAddress:X16}.");
            }

            ReadOnlySpan<uint> payload;
            if (window is not null && window.Holds(packetAddress, length))
            {
                payload = window.Words.AsSpan(window.IndexOf(packetAddress) + 1, (int)length - 1);
            }
            else
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandPacketRead))
                {
                    payload = ReadPayload(cursorIndex, packetAddress, length - 1);
                }
            }

            if (!KeepsPacketWindows(header, payload))
            {
                _packetWindowGeneration++;
            }

            var packet = new PacketContext(header & ~1u, packetAddress, offset, remaining, total);
            uint consumed;
            var handlerStarted = SharpEmu.Libs.Diagnostics.PacketHandlerTally.Enabled ? Stopwatch.GetTimestamp() : 0;
            var waitedBefore = SharpEmu.Libs.Diagnostics.PacketHandlerTally.WaitedTicks;
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandPacketHandler))
            {
                consumed = handler(this, in packet, payload) + 1;
            }

            if (handlerStarted != 0)
            {
                SharpEmu.Libs.Diagnostics.PacketHandlerTally.Note(opcode, header,
                    Stopwatch.GetTimestamp() - handlerStarted - (SharpEmu.Libs.Diagnostics.PacketHandlerTally.WaitedTicks - waitedBefore));
            }
            if (consumed > remaining)
            {
                throw _host.Fatal($"The handler consumed more than the buffer holds: consumed={consumed} remaining={remaining} header=0x{header:X8} address=0x{packetAddress:X16}.");
            }

            if (execution.Suspended)
            {
                if (execution.Depth > cursorIndex + 1 && !_chainRequested)
                {
                    execution.At(cursorIndex).DeferredAdvance = consumed;
                }

                _chainRequested = false;
                return;
            }

            if (_chainRequested)
            {
                _chainRequested = false;
                execution.MadeProgress = true;
                continue;
            }

            if (execution.Depth != cursorIndex + 1)
            {
                throw _host.Fatal($"The cursor stack changed under a packet: depth={execution.Depth} expected={cursorIndex + 1} header=0x{header:X8}.");
            }

            execution.At(cursorIndex).Offset += consumed;
            execution.MadeProgress = true;
        }
    }

    private sealed class PacketWindow
    {
        public const int CapacityDwords = 256;

        public readonly uint[] Words = new uint[CapacityDwords];
        public ulong Address;
        public uint DwordCount;
        public long Generation = -1;

        public int IndexOf(ulong packetAddress) => (int)((packetAddress - Address) / sizeof(uint));

        public bool Covers(ulong packetAddress, long generation) =>
            Generation == generation && packetAddress >= Address && packetAddress - Address < (ulong)DwordCount * sizeof(uint);

        public bool Holds(ulong packetAddress, uint packetDwords) => (uint)IndexOf(packetAddress) + packetDwords <= DwordCount;
    }

    private const ulong PacketWindowPageBytes = 0x1000;

    private PacketWindow? ObtainPacketWindow(int depth, in PacketCursor cursor, ulong packetAddress)
    {
        if (cursor.FollowedChunkAdvance)
        {
            return null;
        }

        while (_packetWindowsByDepth.Count <= depth)
        {
            _packetWindowsByDepth.Add(new PacketWindow());
        }

        var window = _packetWindowsByDepth[depth];
        if (window.Covers(packetAddress, _packetWindowGeneration))
        {
            return window;
        }

        var toPageEnd = (uint)((PacketWindowPageBytes - (packetAddress & (PacketWindowPageBytes - 1))) / sizeof(uint));
        var dwords = Math.Min(Math.Min((uint)PacketWindow.CapacityDwords, cursor.Remaining), toPageEnd);
        if (dwords < 2)
        {
            return null;
        }

        window.Generation = -1;
        var words = window.Words.AsSpan(0, (int)dwords);
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Header, words.Length * sizeof(uint));
        if (!TryReadCommand(packetAddress, MemoryMarshal.AsBytes(words)))
        {
            return null;
        }

        window.Address = packetAddress;
        window.DwordCount = dwords;
        window.Generation = _packetWindowGeneration;
        return window;
    }

    private static bool KeepsPacketWindows(uint header, ReadOnlySpan<uint> payload)
    {
        var opcode = PacketHeader.Opcode(header);
        if (opcode == PacketOpcode.Nop)
        {
            return PacketHeader.CustomCode(header) switch
            {
                PacketCustomCode.Zero => payload.IsEmpty || (payload[0] & 0xFFFF_0000u) != MarkerMagic,
                PacketCustomCode.PushMarker or PacketCustomCode.PopMarker or PacketCustomCode.IndexCount or PacketCustomCode.DrawIndexAuto or
                    PacketCustomCode.ShaderRegisterTable or PacketCustomCode.ContextRegisterTable or PacketCustomCode.UserConfigRegisterTable => true,
                _ => false,
            };
        }

        return opcode is PacketOpcode.SetContextRegister or PacketOpcode.SetShaderRegister or PacketOpcode.SetUserConfigRegister or
            PacketOpcode.SetUserConfigRegisterIndex or PacketOpcode.SetContextRegisterIndirect or PacketOpcode.SetShaderRegisterIndirect or
            PacketOpcode.SetUserConfigRegisterIndirect or PacketOpcode.IndexBase or PacketOpcode.IndexBufferSize or PacketOpcode.IndexType or
            PacketOpcode.NumInstances or PacketOpcode.SetBase or PacketOpcode.DrawIndex2 or PacketOpcode.DrawIndexOffset2 or
            PacketOpcode.DrawIndexAuto or PacketOpcode.DrawIndexMultiAuto or PacketOpcode.DrawIndirect or PacketOpcode.DrawIndexIndirect or
            PacketOpcode.DrawIndirectMulti or PacketOpcode.DrawIndexIndirectMulti or PacketOpcode.DispatchDirect or
            PacketOpcode.DispatchIndirect or PacketOpcode.DispatchDrawPreamble;
    }

    // The payload lives in a per-depth scratch buffer for the handler call only.
    private ReadOnlySpan<uint> ReadPayload(int depth, ulong packetAddress, uint payloadDwords)
    {
        while (_payloadScratchByDepth.Count <= depth)
        {
            _payloadScratchByDepth.Add(new uint[PacketHeader.MaxLength]);
        }

        var scratch = _payloadScratchByDepth[depth].AsSpan(0, (int)payloadDwords);
        if (payloadDwords != 0)
        {
            RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Payload, scratch.Length * sizeof(uint));
        }
        if (payloadDwords != 0 && !TryReadCommand(packetAddress + sizeof(uint), MemoryMarshal.AsBytes(scratch)))
        {
            throw _host.Fatal($"The command stream cannot read a packet: address=0x{packetAddress:X16} dwords={payloadDwords + 1}.");
        }

        return scratch;
    }

    private void DumpUnknownPacket(in PacketCursor cursor, uint offset)
    {
        var begin = offset > 8 ? offset - 8 : 0;
        var end = Math.Min(cursor.DwordCount, offset + 16);
        Console.Error.WriteLine($"[GPU][FATAL] The packet stream near the unknown packet: buffer=0x{cursor.Address:X16} dwords={cursor.DwordCount} offset=0x{offset:X5}");
        Span<byte> word = stackalloc byte[sizeof(uint)];
        for (var index = begin; index < end; index++)
        {
            var text = TryReadCommand(cursor.Address + ((ulong)index * sizeof(uint)), word)
                ? BinaryPrimitives.ReadUInt32LittleEndian(word).ToString("X8")
                : "????????";
            Console.Error.WriteLine($"\t{index:X5}{(index == offset ? ":" : " ")} {text}");
        }
    }

    private bool TryReadCommand(ulong address, Span<byte> destination)
    {
        if (!_submittedCommands.IsEmpty && address >= _submittedCommandAddress)
        {
            var offset = address - _submittedCommandAddress;
            if (offset <= (ulong)_submittedCommands.Length && (ulong)destination.Length <= (ulong)_submittedCommands.Length - offset)
            {
                _submittedCommands.Span.Slice((int)offset, destination.Length).CopyTo(destination);
                return true;
            }
        }
        return _host.TryReadGuest(address, destination);
    }

    internal uint ReadDword(ulong address, RenderPhaseProfile.CommandReadKind readKind = RenderPhaseProfile.CommandReadKind.Operand32)
    {
        RenderPhaseProfile.RecordCommandRead(readKind, sizeof(uint));
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        // The packets themselves are command data; anything else is an operand earlier work may write.
        var read = readKind is RenderPhaseProfile.CommandReadKind.Header or RenderPhaseProfile.CommandReadKind.Payload or
            RenderPhaseProfile.CommandReadKind.RegisterTable
            ? TryReadCommand(address, bytes)
            : _host.TryReadGuestOperand(address, bytes);
        if (!read)
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=4.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal ulong ReadQword(ulong address)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Operand64, sizeof(ulong));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!_host.TryReadGuestOperand(address, bytes))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size=8.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private ulong ReadWaitOperand(ulong address, bool is64Bit, bool allowStale, out bool stale)
    {
        RenderPhaseProfile.RecordCommandRead(is64Bit ? RenderPhaseProfile.CommandReadKind.Operand64 : RenderPhaseProfile.CommandReadKind.Operand32,
            is64Bit ? sizeof(ulong) : sizeof(uint));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        var size = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (!_host.TryReadGuestWaitOperand(address, bytes[..size], allowStale, out stale))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size={size}.");
        }

        return is64Bit ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal void ReadBytes(ulong address, Span<byte> destination)
    {
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Other, destination.Length);
        if (!_host.TryReadGuestOperand(address, destination))
        {
            throw _host.Fatal($"The command stream cannot read guest memory: address=0x{address:X16} size={destination.Length}.");
        }
    }

    private static long _skippedPacketTraceCount;

    // Skipped packets are silent otherwise; a skipped producer leaves its consumer with stale memory.
    internal void TraceSkippedPackets(string kind, ulong packetAddress, uint dwords, uint detail)
    {
        if (IndexedDrawTrace.Enabled)
            SkippedIndexedDrawScanner.Scan(_host.Memory.TryRead, packetAddress, dwords, IndexTypeAndSize, IndexBaseAddress,
                (count, type, address, packet) => IndexedDrawTrace.Write(
                    IndexedDrawTrace.Capture(count, type, address, _host.Memory.TryRead),
                    $"{kind}-candidate", SubmitId, packet), IndexedDrawTrace.ScanIssue);
        if (!ImageClearTrace.Enabled) return;
        _host.TraceGuestWrite($"command-skip-{kind}", packetAddress, (ulong)dwords * sizeof(uint));
        if (Interlocked.Increment(ref _skippedPacketTraceCount) > 256) return;
        var target = string.Empty;
        Span<byte> body = stackalloc byte[3 * sizeof(uint)];
        if (detail == PacketOpcode.IndirectBuffer && dwords == 4 && _host.TryReadGuest(packetAddress + sizeof(uint), body))
        {
            var words = MemoryMarshal.Cast<byte, uint>(body);
            var targetAddress = words[0] | ((ulong)(words[1] & 0xFFFFu) << 32);
            var targetDwords = words[2] & 0xFFFFFu;
            target = $" target=0x{targetAddress:X16} targetDwords={targetDwords} chain={(words[2] >> 20) & 1u}";
            TraceSkippedTargetPackets(targetAddress, targetDwords);
        }
        Console.Error.WriteLine($"[GPU][TRACE] CommandSkip kind={kind} packet=0x{packetAddress:X16} dwords={dwords} detail=0x{detail:X8}{target}");
    }

    private static readonly HashSet<ulong> _decodedSkipTargets = new();

    // Lists each packet of a skipped buffer once, so the skipped work can be identified.
    private void TraceSkippedTargetPackets(ulong address, uint dwords)
    {
        lock (_decodedSkipTargets)
        {
            if (_decodedSkipTargets.Count >= 16 || !_decodedSkipTargets.Add(address)) return;
        }
        var buffer = new byte[Math.Min(dwords, 4096u) * sizeof(uint)];
        if (buffer.Length == 0 || !_host.TryReadGuest(address, buffer)) return;
        var words = MemoryMarshal.Cast<byte, uint>(buffer);
        var packets = new List<string>();
        for (var offset = 0; offset < words.Length;)
        {
            var header = words[offset];
            if ((header >> 30) != 3)
            {
                packets.Add($"raw:0x{header:X8}");
                offset++;
                continue;
            }
            var length = (int)PacketHeader.Length(header);
            var marker = string.Empty;
            if (PacketHeader.Opcode(header) == PacketOpcode.Nop && PacketHeader.CustomCode(header) == PacketCustomCode.PushMarker &&
                offset + length <= words.Length)
            {
                var text = MemoryMarshal.AsBytes(words.Slice(offset + 1, length - 1));
                var end = text.IndexOf((byte)0);
                marker = $"\"{System.Text.Encoding.ASCII.GetString(end < 0 ? text : text[..end])}\"";
            }
            packets.Add($"0x{PacketHeader.Opcode(header):X2}/{length}{(PacketHeader.IsPredicated(header) ? "p" : "")}" +
                (PacketHeader.Opcode(header) == PacketOpcode.Nop ? $"[r=0x{PacketHeader.CustomCode(header):X2}]" : "") + marker);
            offset += Math.Max(length, 1);
        }
        Console.Error.WriteLine($"[GPU][TRACE] CommandSkipTarget target=0x{address:X16} dwords={dwords} packets={string.Join(',', packets)}");
    }

    internal void WriteDword(ulong address, uint value, [CallerMemberName] string caller = "")
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteBytes(address, bytes, caller);
    }

    internal void WriteQword(ulong address, ulong value, [CallerMemberName] string caller = "")
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteBytes(address, bytes, caller);
    }

    // The caller name labels the packet handler in the image-clear trace.
    internal void WriteBytes(ulong address, ReadOnlySpan<byte> source, [CallerMemberName] string caller = "")
    {
        if (_host.TryDeferOrderedWrite(QueueId, address, source))
        {
            _lastWriteLength = 0;
            return;
        }
        if (!_host.Memory.TryWrite(address, source))
        {
            throw _host.Fatal($"The command stream cannot write guest memory: address=0x{address:X16} size={source.Length}.");
        }
        _lastWriteLength = 0;
        if (source.Length is sizeof(uint) or sizeof(ulong))
        {
            _lastWriteAddress = address;
            _lastWriteValue = source.Length == sizeof(uint)
                ? BinaryPrimitives.ReadUInt32LittleEndian(source)
                : BinaryPrimitives.ReadUInt64LittleEndian(source);
            _lastWriteLength = source.Length;
            _lastWritePacket = _packetSerial;
        }
        if (ImageClearTrace.Enabled) _host.TraceGuestWrite($"command-write-{caller}", address, (ulong)source.Length);
    }

    private static ulong Address(uint low, uint high) => low | ((ulong)high << 32);
}
