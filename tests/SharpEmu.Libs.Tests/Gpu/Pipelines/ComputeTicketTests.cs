// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// SHARPEMU_COMPUTE_TICKETS: worker rebinds a direct dispatch takes in place of its own.
[Collection(SchedulingStateCollection.Name)]
public sealed class ComputeTicketTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong PacketAddress = 0x100;
    private const uint Initiator = 0x8001;
    private static readonly object Owner = new();

    [Fact]
    public void ATicketServesTheRebindTheCommandThreadWouldMake()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: 0);
        var banks = KeptBanks(cache, out var first);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        var taken = Dispatch(cache, tickets, banks, PacketAddress);

        Assert.Equal(1, tickets.Served);
        Assert.Equal(first.Program, taken.Program);
        Assert.Equal(0x1234u, taken.Input.Stage.Resources.UserData[0]);
        var (serialCache, _) = CreateCache(workers: 0);
        serialCache.Tickets = null;
        var serialBanks = KeptBanks(serialCache, out _);
        serialBanks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        var serial = serialCache.GetComputeProgram(serialBanks, Initiator, 1, 1, 1);
        Assert.Equal(serial.Input.Stage.Resources.UserData, taken.Input.Stage.Resources.UserData);
    }

    [Fact]
    public void ChangedRegistersRefuseTheTicket()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: 0);
        var banks = KeptBanks(cache, out _);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);
        banks.Shader.Compute.UserScalars.Set(0, 0x5678, UserScalarKind.Unknown);

        var program = Dispatch(cache, tickets, banks, PacketAddress);

        Assert.Equal(0, tickets.Served);
        Assert.Equal(0x5678u, program.Input.Stage.Resources.UserData[0]);
    }

    [Fact]
    public void ChangedReadsRefuseTheTicket()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: 0, validate: static _ => false);
        var banks = KeptBanks(cache, out _);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        var program = Dispatch(cache, tickets, banks, PacketAddress);

        Assert.Equal(0, tickets.Served);
        Assert.Equal(0x1234u, program.Input.Stage.Resources.UserData[0]);
    }

    [Fact]
    public void AnUnfinishedTicketIsNotTaken()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: -1);
        var banks = KeptBanks(cache, out _);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        var program = Dispatch(cache, tickets, banks, PacketAddress);
        tickets.RunManual();

        Assert.Equal(0, tickets.Served);
        Assert.Equal(1, tickets.NotReady);
        Assert.Equal(0x1234u, program.Input.Stage.Resources.UserData[0]);
    }

    [Fact]
    public void ATicketForAnEarlierPacketIsDropped()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: 0);
        var banks = KeptBanks(cache, out _);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        Dispatch(cache, tickets, banks, PacketAddress + 0x20);

        Assert.Equal(0, tickets.Served);
        Assert.Equal(1, tickets.Unused);
        Assert.Equal(0, tickets.LeadCount);
    }

    [Fact]
    public void ADispatchWithoutAKeptProgramRequestsNoTicket()
    {
        using var fatal = new FatalScope();
        var (_, tickets) = CreateCache(workers: 0);
        var banks = Banks();

        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        Assert.Equal(0, tickets.Requested);
        Assert.Equal(0, tickets.LeadCount);
    }

    [Fact]
    public void VerifyComparesEveryTakenTicketWithTheSerialRebind()
    {
        using var fatal = new FatalScope();
        var (cache, tickets) = CreateCache(workers: 0, verify: true);
        var banks = KeptBanks(cache, out _);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        tickets.Request(Owner, PacketAddress, banks.Shader.Compute, 1, 1, 1, Initiator);

        var program = Dispatch(cache, tickets, banks, PacketAddress);

        Assert.Equal(1, tickets.Served);
        Assert.Equal(1, tickets.Verified);
        Assert.Equal(0, tickets.Mismatches);
        Assert.Equal(0x1234u, program.Input.Stage.Resources.UserData[0]);
    }

    [Fact]
    public void ADispatchScansTheDispatchesThatFollowItUntilAnotherPacket()
    {
        var (_, tickets) = CreateCache(workers: 0);
        var requested = new List<string>();
        tickets.RequestObserver = (_, compute, x, y, z, initiator) =>
            requested.Add($"{compute.UserScalars.ValueSpan(1)[0]:X} {x} {y} {z} {initiator:X}");
        var runner = new StreamRunner();
        runner.Host.ComputeTickets = tickets;

        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeUserData0, 0x11),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 1, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeUserData0, 0x22),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 2, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeUserData0, 0x33),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 3, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetContextRegister, RegisterBankLayout.ContextNop, 0),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeUserData0, 0x44),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 4, 1, 1, 0x41));

        Assert.Equal(new[] { "22 2 1 1 41", "33 3 1 1 41" }, requested);
        Assert.Equal(4, runner.Host.Calls.Count(call => call.StartsWith("dispatch", StringComparison.Ordinal)));
    }

    // Guests set a dispatch's user data through register tables as often as through register writes; events and annotation
    // markers change no register.
    [Fact]
    public void TheScanFollowsShaderRegisterTablesAndPassesEventsAndMarkers()
    {
        var (_, tickets) = CreateCache(workers: 0);
        var requested = new List<string>();
        tickets.RequestObserver = (_, compute, x, _, _, _) => requested.Add($"{compute.UserScalars.ValueSpan(1)[0]:X} {x}");
        var runner = new StreamRunner();
        runner.Host.ComputeTickets = tickets;
        const ulong nopTable = StreamRunner.TableAddress;
        const ulong indirectTable = StreamRunner.TableAddress + 0x100;
        runner.Host.WriteWords(nopTable, new uint[] { ShaderRegisterOffset.ComputeUserData0, 0x22 });
        runner.Host.WriteWords(indirectTable, new uint[] { ShaderRegisterOffset.ComputeUserData0, 0x33 });

        runner.Run(
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 1, 1, 1, 0x41),
            StreamRunner.CustomPacket(PacketOpcode.Nop, PacketCustomCode.ShaderRegisterTable, 1, StreamRunner.Low(nopTable), StreamRunner.High(nopTable)),
            StreamRunner.Packet(PacketOpcode.EventWrite, 0x0E),
            StreamRunner.Packet(PacketOpcode.Nop, 0x6875_06ECu),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 2, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetShaderRegisterIndirect, StreamRunner.Low(indirectTable), StreamRunner.High(indirectTable), 0, 1),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 3, 1, 1, 0x41));

        Assert.Equal(new[] { "22 2", "33 3" }, requested);
        Assert.Equal(0x33u, runner.Interpreter.TypedRegisters.Shader.Compute.UserScalars.ValueSpan(1)[0]);
    }

    // A PS4 title writes the compute CU masks and its own stage registers between dispatches (Bloodborne). The scan predicts
    // through them: the masks are host-scheduled, and a graphics stage register cannot change a compute prediction.
    [Fact]
    public void TheScanPassesCuMasksAndStageRegistersOfAPs4Stream()
    {
        var (_, tickets) = CreateCache(workers: 0);
        var requested = new List<string>();
        tickets.RequestObserver = (_, compute, x, _, _, _) => requested.Add($"{compute.UserScalars.ValueSpan(1)[0]:X} {x}");
        var runner = new StreamRunner();
        runner.Host.ComputeTickets = tickets;
        var packets = typeof(SharpEmu.GnmMode.GnmRuntime).Assembly.GetType("SharpEmu.GnmMode.Gpu.GnmCommandPackets")!;
        runner.Interpreter.SetPacketHandlers((PacketHandler?[])packets.GetField("Handlers",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!);

        runner.Run(
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 1, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeStaticThreadMgmtSe0, 0xFFFF_FFFFu, 0xFFFF_FFFFu),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, 0x4C, 0x55),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeUserData0, 0x22),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 2, 1, 1, 0x41));

        Assert.Equal(new[] { "22 2" }, requested);
        Assert.Equal(2, runner.Host.Calls.Count(call => call.StartsWith("dispatch", StringComparison.Ordinal)));
    }

    // The PS5 bank takes the CU masks too, in a run with the registers around them.
    [Fact]
    public void TheCuMasksAreHostScheduledComputeRegisters()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.ComputeResourceLimits, 1, 2, 3, 4, 5, 6));

        Assert.Equal(0, runner.Host.Calls.Count(call => call.StartsWith("dispatch", StringComparison.Ordinal)));
    }

    // A compute register the shadow cannot apply ends the prediction; the packet that writes it reports it when it runs,
    // after the dispatch that scanned it.
    [Fact]
    public void AnUnsupportedRegisterEndsTheScanWithoutAFatalError()
    {
        var (_, tickets) = CreateCache(workers: 0);
        var requested = 0;
        tickets.RequestObserver = (_, _, _, _, _, _) => requested++;
        var runner = new StreamRunner();
        runner.Host.ComputeTickets = tickets;

        var fatal = runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 1, 1, 1, 0x41),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, UnsupportedComputeRegister, 1),
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 2, 1, 1, 0x41));

        Assert.Contains($"unsupported=0x{UnsupportedComputeRegister:X4}", fatal.Message);
        Assert.Equal(0, requested);
        Assert.Equal(1, runner.Host.Calls.Count(call => call.StartsWith("dispatch", StringComparison.Ordinal)));
    }

    private const uint UnsupportedComputeRegister = 0x21C;

    // A ticket worker never compiles: a missing permutation asks for the serial resolve.
    [Fact]
    public void ASpeculativeResolveNeverCompilesAPermutation()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var source = guest.Source(CodeAddress, SharpEmu.ShaderCompiler.Resources.ShaderStage.Compute, []);
        var cursor = 0u;
        ShaderProgramCache.SpeculativeResolve = true;
        try
        {
            Assert.Throws<SerialResolveRequiredException>(() => guest.Programs.TryGetProgram(
                source, PipelineTestGuest.ComputeOptions(32), false, ref cursor, out _, out _, out _));
            var entry = Assert.IsType<ProgramSourceEntry>(ShaderProgramCache.LastEntry);
            var options = PipelineTestGuest.ComputeOptions(32);
            bool TryRebind() => guest.Programs.TryGetProgramForEntry(entry, source, options, false, ref cursor, out _, out _, out _);
            Assert.False(TryRebind());
        }
        finally
        {
            ShaderProgramCache.SpeculativeResolve = false;
        }

        Assert.Empty(guest.Compiler.Requests);
        Assert.Empty(guest.Host.Modules);
    }

    // A ticket worker never raises a fatal: a strict rejection asks for the serial resolve, which rejects it for real.
    [Fact]
    public void ASpeculativeResolveTurnsAStrictRejectionIntoASerialResolve()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        guest.Compiler.LinkedShaderCallsSupported = false;
        guest.RegisterProgram(CodeAddress, HeaderAddress, [0xBE8E2100, 0xBF810000]);
        var source = guest.Source(CodeAddress, SharpEmu.ShaderCompiler.Resources.ShaderStage.Compute, []);
        var cursor = 0u;
        ShaderProgramCache.SpeculativeResolve = true;
        try
        {
            Assert.Throws<SerialResolveRequiredException>(() => guest.Programs.TryGetProgram(
                source, PipelineTestGuest.ComputeOptions(32), true, ref cursor, out _, out _, out _));
        }
        finally
        {
            ShaderProgramCache.SpeculativeResolve = false;
        }

        Assert.Empty(guest.Compiler.Requests);
    }

    private static (ShaderPipelineCache Cache, ComputeTickets Tickets) CreateCache(int workers, Func<ResolveReadLog, bool>? validate = null, bool verify = false)
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var tickets = new ComputeTickets(cache, static _ => { }, static () => { }, validate ?? (static _ => true), workers, verify);
        cache.Tickets = tickets;
        return (cache, tickets);
    }

    private static RegisterBanks Banks()
    {
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        var compute = banks.Shader.Compute;
        compute.Address = CodeAddress;
        compute.ThreadsX = 1;
        compute.ThreadsY = 1;
        compute.ThreadsZ = 1;
        compute.UserScalarCount = 8;
        return banks;
    }

    // The banks of a dispatch whose program the cache keeps (resolved once, outside any ticket).
    private static RegisterBanks KeptBanks(ShaderPipelineCache cache, out ComputeProgram first)
    {
        var banks = Banks();
        first = cache.GetComputeProgram(banks, Initiator, 1, 1, 1);
        Assert.True(first.Available);
        return banks;
    }

    private static ComputeProgram Dispatch(ShaderPipelineCache cache, ComputeTickets tickets, RegisterBanks banks, ulong packetAddress)
    {
        tickets.BeginDispatch(Owner, packetAddress);
        try
        {
            return cache.GetComputeProgram(banks, Initiator, 1, 1, 1);
        }
        finally
        {
            tickets.EndDispatch();
        }
    }
}
