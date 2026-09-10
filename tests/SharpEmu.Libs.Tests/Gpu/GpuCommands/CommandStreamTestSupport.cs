// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

internal sealed class CommandStreamFatalException(string message) : Exception(message);

// Records every host call in order and serves guest memory from a fake.
internal sealed class RecordingCommandStreamHost : ICommandStreamHost
{
    public const ulong MemoryBase = 0x1000_0000;
    public const int MemorySize = 0x10_0000;

    public RecordingCommandStreamHost()
    {
        GuestMemory = new FakeCpuMemory(MemoryBase, MemorySize);
    }

    public FakeCpuMemory GuestMemory { get; }

    public ICpuMemory Memory => GuestMemory;

    public List<string> Calls { get; } = new();

    public List<EndOfPipeWrite> EndOfPipeWrites { get; } = new();

    public List<ulong> GuestReads { get; } = new();

    public List<DrawIndexedArguments> IndexedDraws { get; } = new();

    public List<DrawAutoArguments> AutoDraws { get; } = new();

    public Queue<Action> PendingCommands { get; } = new();

    public int PendingCommandRuns { get; private set; }

    public bool FlipSlot { get; set; } = true;

    public bool FlipDone { get; set; } = true;

    public ulong NextFlipRequestId { get; set; } = 1;

    public byte[] Gds { get; } = new byte[ManagedCommandStreamHost.GdsBytes];

    // Qword values the GPU owns; the synchronized read lands them in memory first, as a download would.
    public Dictionary<ulong, ulong> PendingGpuValues { get; } = new();

    public bool TryReadGuest(ulong address, Span<byte> destination)
    {
        GuestReads.Add(address);
        if (PendingGpuValues.Remove(address, out var pending))
        {
            WriteQword(address, pending);
        }

        return GuestMemory.TryRead(address, destination);
    }

    public void RunPendingCommands()
    {
        PendingCommandRuns++;
        while (PendingCommands.Count != 0)
        {
            PendingCommands.Dequeue()();
        }
    }

    public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots) => Calls.Add($"begin {queueId} {submissionId}");

    public void Flush() => Calls.Add("flush");

    public void FlushAndWait() => Calls.Add("flush_and_wait");

    public void SynchronizeGpu() => Calls.Add("synchronize");

    public void RunGarbageCollector() => Calls.Add("gc");

    public void EmitGlobalBarrier() => Calls.Add("barrier");

    public void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        Calls.Add($"fill {address:X} {size:X} {value:X8} gds={isGds}");
        var pattern = new byte[size];
        for (var offset = 0; offset + 4 <= pattern.Length; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(offset), value);
        }

        if (isGds)
        {
            pattern.CopyTo(Gds.AsSpan((int)address));
        }
        else if (!GuestMemory.TryWrite(address, pattern))
        {
            throw Fatal($"fill outside memory {address:X}");
        }
    }

    public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        Calls.Add($"copy {destination:X} {source:X} {size:X} dstGds={destinationIsGds} srcGds={sourceIsGds}");
        var bytes = new byte[size];
        if (sourceIsGds)
        {
            Gds.AsSpan((int)source, (int)size).CopyTo(bytes);
        }
        else if (!GuestMemory.TryRead(source, bytes))
        {
            throw Fatal($"copy source outside memory {source:X}");
        }

        if (destinationIsGds)
        {
            bytes.CopyTo(Gds.AsSpan((int)destination));
        }
        else if (!GuestMemory.TryWrite(destination, bytes))
        {
            throw Fatal($"copy destination outside memory {destination:X}");
        }
    }

    public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
    {
        Calls.Add($"read_gds {wordOffset} {wordCount}");
        EndOfPipe.ReadGdsWords(Gds, destination, wordOffset, wordCount);
    }

    public void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        EndOfPipeWrites.Add(write);
        Calls.Add($"eop {write.Kind}");
    }

    public void TriggerInterrupt(int eventId, uint contextId) => Calls.Add($"interrupt {eventId} {contextId}");

    public bool HasFlipSlot() => FlipSlot;

    public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
    {
        Calls.Add($"prepare_flip {handle} {index} {flipMode} {flipArgument}");
        return NextFlipRequestId++;
    }

    public void PrepareCpuFlip(int handle, int index, ulong requestId) => Calls.Add($"cpu_flip {handle} {index} {requestId}");

    public bool IsFlipDone(int handle, int index)
    {
        Calls.Add($"flip_done? {handle} {index}");
        return FlipDone;
    }

    public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
    {
        IndexedDraws.Add(arguments);
        Calls.Add($"draw_indexed {submitId} {arguments.IndexCount}");
    }

    public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
    {
        AutoDraws.Add(arguments);
        Calls.Add($"draw_auto {submitId} {arguments.VertexCount}");
    }

    public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator) =>
        Calls.Add($"dispatch {submitId} {groupsX} {groupsY} {groupsZ} {dispatchInitiator:X}");

    public void OnQueueReset(int queueId) => Calls.Add($"queue_reset {queueId}");

    public Exception Fatal(string message) => new CommandStreamFatalException(message);

    public uint ReadDword(ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(GuestMemory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public ulong ReadQword(ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(GuestMemory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public void WriteDword(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(GuestMemory.TryWrite(address, bytes));
    }

    public void WriteQword(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(GuestMemory.TryWrite(address, bytes));
    }

    public void WriteWords(ulong address, ReadOnlySpan<uint> words) =>
        Assert.True(GuestMemory.TryWrite(address, MemoryMarshal.AsBytes(words)));
}

// Builds packet words and runs them through one processor, keeping the cursor stack for retries.
internal sealed class StreamRunner
{
    public const ulong CommandAddress = RecordingCommandStreamHost.MemoryBase + 0x1000;
    public const ulong LabelAddress = RecordingCommandStreamHost.MemoryBase + 0x8000;
    public const ulong TableAddress = RecordingCommandStreamHost.MemoryBase + 0x9000;
    public const ulong DataAddress = RecordingCommandStreamHost.MemoryBase + 0xA000;

    public StreamRunner(int queueId = 0)
    {
        Host = new RecordingCommandStreamHost();
        Interpreter = new GpuCommandInterpreter(Host, queueId, queueId == 0 ? 0 : GpuCommandInterpreter.ComputeQueueBase + queueId - 1);
    }

    public RecordingCommandStreamHost Host { get; }

    public GpuCommandInterpreter Interpreter { get; }

    public PacketCursorStack Commands { get; private set; } = new();

    public uint Dwords { get; private set; }

    public static uint[] CustomPacket(uint opcode, uint customCode, params uint[] payload)
    {
        var words = new uint[payload.Length + 1];
        words[0] = PacketHeader.Make((uint)words.Length, opcode, customCode);
        payload.CopyTo(words, 1);
        return words;
    }

    public static uint[] Packet(uint opcode, params uint[] payload) => CustomPacket(opcode, 0, payload);

    public static uint[] Concat(params uint[][] packets)
    {
        var length = 0;
        foreach (var packet in packets)
        {
            length += packet.Length;
        }

        var words = new uint[length];
        var offset = 0;
        foreach (var packet in packets)
        {
            packet.CopyTo(words, offset);
            offset += packet.Length;
        }

        return words;
    }

    public void Load(params uint[][] packets) => Load(CommandAddress, Concat(packets));

    // Loading new packets starts a new stream; Run() without packets continues the current one.
    public void Load(ulong address, uint[] words)
    {
        Host.WriteWords(address, words);
        Dwords = (uint)words.Length;
        Commands = new PacketCursorStack();
    }

    public SubmissionProgress Run() => Interpreter.Process(Commands, CommandAddress, Dwords);

    public SubmissionProgress Run(params uint[][] packets)
    {
        Load(packets);
        return Run();
    }

    public CommandStreamFatalException RunExpectingFatal(params uint[][] packets)
    {
        Load(packets);
        return Assert.Throws<CommandStreamFatalException>(() => Run());
    }

    public static uint Low(ulong value) => (uint)value;

    public static uint High(ulong value) => (uint)(value >> 32);
}
