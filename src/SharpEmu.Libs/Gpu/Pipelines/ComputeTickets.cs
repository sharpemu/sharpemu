// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// SHARPEMU_COMPUTE_TICKETS (on by default; 0 turns it off, verify checks it): a direct dispatch of a kept compute program
// takes its resource rebind from a worker thread instead of rebinding on the command thread. At a direct dispatch the
// interpreter scans the packets that follow it on a shadow copy of the compute registers and requests a ticket for each
// direct dispatch it finds; a worker runs the program's rebind (user data, stage inputs, resource materialization) with
// every guest read recorded. The dispatch takes the ticket where it would rebind only when the ticket is finished, was made
// for the same kept program, registers, groups and initiator, and its reads still hold (no GPU write, publication, pending
// write or store since); otherwise it rebinds as before. verify rebinds every taken dispatch on the command thread as well,
// compares the two and keeps the serial result.
public sealed class ComputeTickets : IDisposable
{
    public enum TicketMode
    {
        Off,
        On,
        Verify,
    }

    public static readonly TicketMode Mode = Environment.GetEnvironmentVariable("SHARPEMU_COMPUTE_TICKETS") switch
    {
        "0" => TicketMode.Off,
        "verify" => TicketMode.Verify,
        _ => TicketMode.On,
    };

    public const int WorkerCount = 3;

    // Dispatches requested ahead of the command thread, and the lead below which a dispatch scans again.
    public const int MaxLead = 48;
    public const int LowWater = 8;
    public const int MaxScanDwords = 4096;

    internal sealed class Ticket
    {
        public object? Owner;
        public ulong Address;
        public ComputeStageRegisters Compute = null!;
        public uint GroupsX;
        public uint GroupsY;
        public uint GroupsZ;
        public uint Initiator;
        public KeptComputeProgram? Kept;
        public ProgramSourceEntry? Entry;
        public ShaderSource? BaseSource;
        public Gen5ComputeSystemRegisters SystemRegisters;
        public bool GpuThreadCounts;
        public long Registrations;
        public readonly ResolveReadLog Reads = new();
        public ComputeProgram? Program;
        public ShaderSource? Source;
        public bool Refused;

        // 0 queued or running, 1 done, 2 abandoned by the command thread.
        public int State;

        public void Reset()
        {
            Owner = null;
            Compute = null!;
            Kept = null;
            Entry = null;
            BaseSource = null;
            Program = null;
            Source = null;
            Refused = false;
            Reads.ClearForPool();
            State = 0;
        }
    }

    private static ComputeTickets? _reported;

    private readonly ShaderPipelineCache _pipelines;
    private readonly Action<ResolveReadLog> _beginWorker;
    private readonly Action _endWorker;
    private readonly Func<ResolveReadLog, bool> _validate;
    private readonly BlockingCollection<Ticket>? _queue;
    private readonly List<Ticket>? _manual;
    private readonly Thread[] _threads;
    private readonly Queue<Ticket> _lead = new();
    private readonly ConcurrentQueue<Ticket> _spare = new();
    private object? _currentOwner;
    private ulong _currentAddress;
    private bool _hasCurrent;
    private object? _scanOwner;
    private ulong _scanFrontier;
    private ulong _rescanAt;

    public long Requested;
    public long Served;
    public long NotReady;
    public long NoTicket;
    public long Unused;
    public long Refused;
    public long Verified;
    public long Mismatches;

    // workers > 0: that many threads; 0: each ticket runs when it is requested; below 0: tickets wait for RunManual (tests).
    internal ComputeTickets(ShaderPipelineCache pipelines, Action<ResolveReadLog> beginWorker, Action endWorker, Func<ResolveReadLog, bool> validate,
        int workers, bool verify = false)
    {
        _pipelines = pipelines;
        _beginWorker = beginWorker;
        _endWorker = endWorker;
        _validate = validate;
        Verify = verify;
        _threads = [];
        if (workers > 0)
        {
            _queue = new BlockingCollection<Ticket>();
            _threads = new Thread[workers];
            for (var index = 0; index < workers; index++)
            {
                _threads[index] = new Thread(Run) { IsBackground = true, Name = $"SharpEmu compute ticket worker {index}" };
                _threads[index].Start();
            }
        }
        else if (workers < 0)
        {
            _manual = [];
        }

        _reported = this;
    }

    public bool Verify { get; }

    internal int LeadCount => _lead.Count;

    // The interpreter marks the direct dispatch packet it executes; only that dispatch takes a ticket.
    public void BeginDispatch(object owner, ulong address)
    {
        _currentOwner = owner;
        _currentAddress = address;
        _hasCurrent = true;
    }

    public void EndDispatch() => _hasCurrent = false;

    // Whether the interpreter scans ahead at this dispatch, and the last dispatch the previous scan already covered (the scan
    // skips dispatches up to it). Inside the range of the previous scan it scans again only from the dispatch that scan marked,
    // and never when that scan ended at a packet it cannot pass.
    public bool NeedsScan(object owner, ulong address, out ulong requestedThrough)
    {
        requestedThrough = 0;
        if (_lead.TryPeek(out var head) && !ReferenceEquals(head.Owner, owner))
        {
            DropAll();
        }

        DropBefore(owner, address);
        var inside = ReferenceEquals(_scanOwner, owner) && address <= _scanFrontier;
        if (inside && address < _rescanAt)
        {
            return false;
        }

        requestedThrough = inside ? _scanFrontier : 0;
        return true;
    }

    // The end of a scan from the dispatch at address: the dispatches it covered, so the next scan starts from the one LowWater
    // dispatches before its end, never inside its range when it ended at a packet it cannot pass.
    public void EndScan(object owner, ulong address, ReadOnlySpan<ulong> found, bool stopped)
    {
        var continued = ReferenceEquals(_scanOwner, owner) && address <= _scanFrontier;
        _scanOwner = owner;
        if (found.IsEmpty)
        {
            _scanFrontier = continued ? _scanFrontier : address;
            _rescanAt = ulong.MaxValue;
            return;
        }

        _scanFrontier = found[^1];
        _rescanAt = stopped ? ulong.MaxValue : found[Math.Max(0, found.Length - LowWater)];
    }

    // Tests observe what the scan predicted.
    internal Action<ulong, ComputeStageRegisters, uint, uint, uint, uint>? RequestObserver { get; set; }

    // A dispatch the scan predicted: copied registers, then a worker rebinds it if a kept program matches.
    internal void Request(object owner, ulong address, ComputeStageRegisters compute, uint groupsX, uint groupsY, uint groupsZ, uint initiator)
    {
        RequestObserver?.Invoke(address, compute, groupsX, groupsY, groupsZ, initiator);
        if (_lead.Count >= MaxLead)
        {
            return;
        }

        var ticket = _spare.TryDequeue(out var spare) ? spare : new Ticket();
        ticket.Owner = owner;
        ticket.Address = address;
        ticket.Compute = compute.Copy();
        ticket.GroupsX = groupsX;
        ticket.GroupsY = groupsY;
        ticket.GroupsZ = groupsZ;
        ticket.Initiator = initiator;
        ticket.GpuThreadCounts = false;
        if (!_pipelines.PrepareComputeTicket(ticket))
        {
            Recycle(ticket);
            return;
        }

        Interlocked.Increment(ref Requested);
        _lead.Enqueue(ticket);
        if (_queue is not null)
        {
            _queue.Add(ticket);
        }
        else if (_manual is not null)
        {
            _manual.Add(ticket);
        }
        else
        {
            RunTicket(ticket);
        }
    }

    internal void RunManual()
    {
        foreach (var ticket in _manual!)
        {
            RunTicket(ticket);
        }

        _manual.Clear();
    }

    // At the rebind of the marked dispatch: its ticket, finished, made for this kept program and inputs, with reads that
    // still hold; otherwise null and the caller rebinds. The caller recycles a returned ticket after it took the result.
    internal Ticket? TryTake(KeptComputeProgram kept, ComputeStageRegisters compute, uint initiator, uint groupsX, uint groupsY, uint groupsZ)
    {
        if (!_hasCurrent)
        {
            return null;
        }

        DropBefore(_currentOwner!, _currentAddress);
        if (!_lead.TryPeek(out var ticket) || !ReferenceEquals(ticket.Owner, _currentOwner) || ticket.Address != _currentAddress)
        {
            Interlocked.Increment(ref NoTicket);
            return null;
        }

        _lead.Dequeue();
        if (Volatile.Read(ref ticket.State) != 1)
        {
            Interlocked.Increment(ref NotReady);
            Abandon(ticket);
            return null;
        }

        if (ticket.Refused || !ShaderPipelineCache.TicketMatches(ticket, kept, compute, initiator, groupsX, groupsY, groupsZ) || !_validate(ticket.Reads))
        {
            Interlocked.Increment(ref Refused);
            Recycle(ticket);
            return null;
        }

        Interlocked.Increment(ref Served);
        return ticket;
    }

    internal void NoteVerify(bool same)
    {
        Interlocked.Increment(ref Verified);
        if (!same)
        {
            Interlocked.Increment(ref Mismatches);
        }
    }

    internal void Recycle(Ticket ticket)
    {
        ticket.Reset();
        _spare.Enqueue(ticket);
    }

    private void DropBefore(object owner, ulong address)
    {
        while (_lead.TryPeek(out var head) && ReferenceEquals(head.Owner, owner) && head.Address < address)
        {
            _lead.Dequeue();
            Interlocked.Increment(ref Unused);
            Abandon(head);
        }
    }

    private void DropAll()
    {
        while (_lead.TryDequeue(out var ticket))
        {
            Interlocked.Increment(ref Unused);
            Abandon(ticket);
        }
    }

    // A ticket the command thread no longer wants: recycled now if it is finished, otherwise by its worker.
    private void Abandon(Ticket ticket)
    {
        if (Interlocked.CompareExchange(ref ticket.State, 2, 0) == 1)
        {
            Recycle(ticket);
        }
    }

    private void Run()
    {
        HostLaneReservation.ApplyToHostWorkerThread();
        VideoOut.RenderPhaseProfile.AttachResolveWorker();
        foreach (var ticket in _queue!.GetConsumingEnumerable())
        {
            RunTicket(ticket);
        }
    }

    private void RunTicket(Ticket ticket)
    {
        _beginWorker(ticket.Reads);
        ShaderProgramCache.SpeculativeResolve = true;
        try
        {
            _pipelines.RunComputeTicket(ticket);
        }
        catch (Exception)
        {
            // The command thread rebinds and reports any real failure itself (SerialResolveRequiredException where it
            // would compile or raise a fatal).
            ticket.Refused = true;
        }
        finally
        {
            ShaderProgramCache.SpeculativeResolve = false;
            _endWorker();
        }

        if (Interlocked.CompareExchange(ref ticket.State, 1, 0) == 2)
        {
            Recycle(ticket);
        }
    }

    // SHARPEMU_PROFILE_RENDER: the counts since the last report.
    public static string TakeReport()
    {
        if (_reported is not { } tickets)
        {
            return "[PERF][TICKETS] off";
        }

        return FormattableString.Invariant(
            $"[PERF][TICKETS] requested={Interlocked.Exchange(ref tickets.Requested, 0)} served={Interlocked.Exchange(ref tickets.Served, 0)} not_ready={Interlocked.Exchange(ref tickets.NotReady, 0)} no_ticket={Interlocked.Exchange(ref tickets.NoTicket, 0)} unused={Interlocked.Exchange(ref tickets.Unused, 0)} refused={Interlocked.Exchange(ref tickets.Refused, 0)} verified={Interlocked.Exchange(ref tickets.Verified, 0)} mismatches={Interlocked.Exchange(ref tickets.Mismatches, 0)}");
    }

    public void Dispose()
    {
        _queue?.CompleteAdding();
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        if (ReferenceEquals(_reported, this))
        {
            _reported = null;
        }
    }
}
