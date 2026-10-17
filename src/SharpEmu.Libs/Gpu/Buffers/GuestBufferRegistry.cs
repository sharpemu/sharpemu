// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

internal enum BufferLifetimeState { Allocated, Registered, Retiring, Released }

// The cache serializes access. GPU completion determines when retirement can finish.
internal sealed class GuestBufferRegistry<TBuffer> : IDisposable where TBuffer : class, IDisposable
{
    private sealed class Entry(ResourceSlotIdentifier identifier, TBuffer resource, ulong address, ulong size)
    {
        public ResourceSlotIdentifier Identifier = identifier;
        public TBuffer? Resource = resource;
        public ulong Address = address;
        public ulong Size = size;
        public BufferLifetimeState State = BufferLifetimeState.Allocated;
        public ulong LastUseTick;
        public LinkedListNode<Entry>? RecencyNode;
    }

    private readonly List<Entry> _allocations = [];
    private readonly Stack<int> _availableIndices = [];
    private readonly SortedList<ulong, Entry> _registered = [];
    private readonly LinkedList<Entry> _recency = [];
    private readonly Entry?[]?[] _pageOwners;
    private readonly ulong _pageSize;
    private readonly ulong _addressSpaceSize;
    private readonly int _pageShift;
    private bool _disposed;
    private const int OwnerBlockBits = 10;
    private const int OwnersPerBlock = 1 << OwnerBlockBits;

    public GuestBufferRegistry(ulong pageSize, ulong addressSpaceSize)
    {
        if (!BitOperations.IsPow2(pageSize) || addressSpaceSize == 0 || addressSpaceSize % pageSize != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        _pageSize = pageSize;
        _pageShift = BitOperations.TrailingZeroCount(pageSize);
        _addressSpaceSize = addressSpaceSize;
        var pageCount = addressSpaceSize / pageSize;
        _pageOwners = new Entry?[checked((int)((pageCount - 1) / OwnersPerBlock + 1))][];
    }

    public int RegisteredCount => _registered.Count;
    public ulong RegisteredBytes { get; private set; }

    public ResourceSlotIdentifier AllocateBuffer(TBuffer resource, ulong address, ulong size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resource);
        var index = _availableIndices.Count == 0 ? _allocations.Count : _availableIndices.Pop();
        var generation = index == _allocations.Count ? 1U : _allocations[index].Identifier.Generation + 1;
        var identifier = new ResourceSlotIdentifier((uint)index, generation);
        if (index == _allocations.Count)
        {
            _allocations.Add(new Entry(identifier, resource, address, size));
        }
        else
        {
            var entry = _allocations[index];
            entry.Identifier = identifier;
            entry.Resource = resource;
            entry.Address = address;
            entry.Size = size;
            entry.State = BufferLifetimeState.Allocated;
            entry.LastUseTick = 0;
        }
        return identifier;
    }

    public TBuffer GetBuffer(ResourceSlotIdentifier identifier) =>
        TryGetBuffer(identifier) ?? throw SubmissionScheduler.Fatal("The buffer identifier is not live.");

    public TBuffer? TryGetBuffer(ResourceSlotIdentifier identifier) => FindEntry(identifier)?.Resource;

    public TBuffer? TryGetRegisteredBuffer(ResourceSlotIdentifier identifier) =>
        FindEntry(identifier) is { State: BufferLifetimeState.Registered } entry ? entry.Resource : null;

    public BufferLifetimeState? GetState(ResourceSlotIdentifier identifier) => FindEntry(identifier)?.State;

    public void RegisterBuffer(ResourceSlotIdentifier identifier, ulong tick)
    {
        var entry = RequireEntry(identifier, BufferLifetimeState.Allocated);
        if (entry.Size == 0 || entry.Address == 0 || entry.Address >= _addressSpaceSize ||
            entry.Size > _addressSpaceSize - entry.Address ||
            (entry.Address | entry.Size) % _pageSize != 0)
            throw SubmissionScheduler.Fatal("The registered buffer range is invalid.");

        var position = FindInsertionIndex(entry.Address);
        if ((position > 0 && _registered.Values[position - 1].Address + _registered.Values[position - 1].Size > entry.Address) ||
            (position < _registered.Count && _registered.Keys[position] < entry.Address + entry.Size))
            throw SubmissionScheduler.Fatal("The registered buffer overlaps an existing owner.");
        if (entry.Size > ulong.MaxValue - RegisteredBytes)
            throw SubmissionScheduler.Fatal("The registered buffer total exceeds the address limit.");
        ValidateRecencyTick(tick);

        var firstPage = entry.Address >> _pageShift;
        var endPage = (entry.Address + entry.Size) >> _pageShift;
        for (var page = firstPage; page < endPage; page++)
        {
            if (FindPageOwner(page) != null)
                throw SubmissionScheduler.Fatal("The buffer page already has an owner.");
        }

        // Allocate lookup storage before publishing ownership. Rejected input changes no index.
        for (var block = firstPage >> OwnerBlockBits; block <= (endPage - 1) >> OwnerBlockBits; block++)
            _pageOwners[block] ??= new Entry?[OwnersPerBlock];
        var node = entry.RecencyNode ?? new LinkedListNode<Entry>(entry);
        _registered.Add(entry.Address, entry);
        for (var page = firstPage; page < endPage; page++) SetPageOwner(page, entry);
        entry.LastUseTick = tick;
        entry.RecencyNode = node;
        _recency.AddLast(node);
        entry.State = BufferLifetimeState.Registered;
        RegisteredBytes += entry.Size;
    }

    public void BeginRetirement(ResourceSlotIdentifier identifier)
    {
        var entry = RequireEntry(identifier, BufferLifetimeState.Registered);
        if (!_registered.TryGetValue(entry.Address, out var owner) || !ReferenceEquals(owner, entry) ||
            entry.RecencyNode?.List != _recency || entry.Size > RegisteredBytes)
            throw SubmissionScheduler.Fatal("The registered buffer indexes disagree.");
        var firstPage = entry.Address >> _pageShift;
        var endPage = (entry.Address + entry.Size) >> _pageShift;
        for (var page = firstPage; page < endPage; page++)
        {
            if (!ReferenceEquals(FindPageOwner(page), entry))
                throw SubmissionScheduler.Fatal("The retiring buffer does not own its pages.");
        }

        for (var page = firstPage; page < endPage; page++) SetPageOwner(page, null);
        _registered.Remove(entry.Address);
        _recency.Remove(entry.RecencyNode!);
        RegisteredBytes -= entry.Size;
        entry.State = BufferLifetimeState.Retiring;
    }

    // Late or repeated callbacks cannot release a replacement allocation.
    public bool CompleteRetirement(ResourceSlotIdentifier identifier)
    {
        var entry = FindEntry(identifier);
        if (entry == null || entry.State == BufferLifetimeState.Released) return false;
        if (entry.State != BufferLifetimeState.Retiring)
            throw SubmissionScheduler.Fatal("The buffer has not begun retirement.");
        ReleaseResource(entry);
        return true;
    }

    public ResourceSlotIdentifier FindContainingBuffer(ulong address, ulong size)
    {
        if (size == 0 || address >= _addressSpaceSize || size > _addressSpaceSize - address) return default;
        var owner = FindPageOwner(address >> _pageShift);
        return owner != null && address >= owner.Address && size <= owner.Size - (address - owner.Address)
            ? owner.Identifier : default;
    }

    public int FindFirstOverlappingIndex(ulong address)
    {
        var position = FindInsertionIndex(address);
        if (position > 0)
        {
            var previous = _registered.Values[position - 1];
            if (previous.Address + previous.Size > address) position--;
        }
        return position;
    }

    public bool HasOverlap(ulong address, ulong size)
    {
        if (size == 0 || address >= _addressSpaceSize || size > _addressSpaceSize - address) return false;
        var position = FindFirstOverlappingIndex(address);
        return position < _registered.Count && _registered.Keys[position] < address + size;
    }

    public ulong GetRegisteredAddress(int index) => _registered.Keys[index];
    public ResourceSlotIdentifier GetRegisteredIdentifier(int index) => _registered.Values[index].Identifier;
    public ResourceSlotIdentifier[] SnapshotRegisteredIdentifiers() => _registered.Values.Select(entry => entry.Identifier).ToArray();

    public void MarkBufferUsed(ResourceSlotIdentifier identifier, ulong tick)
    {
        var entry = RequireEntry(identifier, BufferLifetimeState.Registered);
        if (tick <= entry.LastUseTick) return;
        ValidateRecencyTick(tick);
        entry.LastUseTick = tick;
        _recency.Remove(entry.RecencyNode!);
        _recency.AddLast(entry.RecencyNode!);
    }

    public void VisitRetirementCandidates(ulong latestTick, Func<ResourceSlotIdentifier, bool> visit)
    {
        for (var node = _recency.First; node != null;)
        {
            if (node.Value.LastUseTick > latestTick) return;
            var next = node.Next;
            if (visit(node.Value.Identifier)) return;
            node = next;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registered.Clear();
        _recency.Clear();
        Array.Clear(_pageOwners);
        RegisteredBytes = 0;
        foreach (var entry in _allocations)
        {
            if (entry.State != BufferLifetimeState.Released) ReleaseResource(entry);
        }
    }

    private void ReleaseResource(Entry entry)
    {
        var resource = entry.Resource;
        entry.Resource = null;
        entry.State = BufferLifetimeState.Released;
        if (!_disposed && entry.Identifier.Generation != uint.MaxValue)
            _availableIndices.Push((int)entry.Identifier.Index);
        resource?.Dispose();
    }

    private Entry? FindEntry(ResourceSlotIdentifier identifier) =>
        identifier.IsValid && identifier.Index < _allocations.Count && _allocations[(int)identifier.Index].Identifier == identifier
            ? _allocations[(int)identifier.Index] : null;

    private Entry RequireEntry(ResourceSlotIdentifier identifier, BufferLifetimeState state)
    {
        var entry = FindEntry(identifier);
        return entry?.State == state ? entry : throw SubmissionScheduler.Fatal("The buffer lifetime transition is invalid.");
    }

    private Entry? FindPageOwner(ulong page) => _pageOwners[page >> OwnerBlockBits]?[page & (OwnersPerBlock - 1)];
    private void SetPageOwner(ulong page, Entry? entry) => _pageOwners[page >> OwnerBlockBits]![page & (OwnersPerBlock - 1)] = entry;

    private void ValidateRecencyTick(ulong tick)
    {
        if (_recency.Last is { } last && tick < last.Value.LastUseTick)
            throw SubmissionScheduler.Fatal("The buffer recency tick is out of order.");
    }

    private int FindInsertionIndex(ulong address)
    {
        var begin = 0;
        var end = _registered.Count;
        while (begin < end)
        {
            var middle = begin + (end - begin) / 2;
            if (_registered.Keys[middle] < address) begin = middle + 1;
            else end = middle;
        }
        return begin;
    }
}
