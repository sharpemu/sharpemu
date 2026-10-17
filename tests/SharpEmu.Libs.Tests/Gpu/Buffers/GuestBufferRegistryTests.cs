// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferRegistryTests
{
    private sealed class TestBuffer : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void LifetimeSeparatesLookupRemovalFromResourceRelease()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var resource = new TestBuffer();
        var identifier = registry.AllocateBuffer(resource, 32, 32);
        Assert.Equal(BufferLifetimeState.Allocated, registry.GetState(identifier));
        Assert.False(registry.FindContainingBuffer(32, 1).IsValid);
        registry.RegisterBuffer(identifier, 0);
        Assert.Equal(BufferLifetimeState.Registered, registry.GetState(identifier));
        Assert.Equal(identifier, registry.FindContainingBuffer(33, 31));
        Assert.False(registry.FindContainingBuffer(33, 32).IsValid);
        Assert.Equal(32UL, registry.RegisteredBytes);
        registry.BeginRetirement(identifier);
        Assert.Equal(BufferLifetimeState.Retiring, registry.GetState(identifier));
        Assert.Null(registry.TryGetRegisteredBuffer(identifier));
        Assert.Same(resource, registry.GetBuffer(identifier));
        Assert.False(registry.HasOverlap(32, 32));
        Assert.Equal(0UL, registry.RegisteredBytes);
        Assert.Equal(0, resource.DisposeCount);
        Assert.True(registry.CompleteRetirement(identifier));
        Assert.Equal(BufferLifetimeState.Released, registry.GetState(identifier));
        Assert.Equal(1, resource.DisposeCount);
        Assert.Null(registry.TryGetBuffer(identifier));
        Assert.False(registry.CompleteRetirement(identifier));
    }

    [Theory]
    [InlineData(32UL, 32UL)]
    [InlineData(16UL, 32UL)]
    [InlineData(48UL, 32UL)]
    [InlineData(16UL, 64UL)]
    [InlineData(48UL, 16UL)]
    public void OverlapRejectionPreservesEveryLookupAndAccounting(ulong address, ulong size)
    {
        using var fatal = new FatalScope();
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var owner = registry.AllocateBuffer(new TestBuffer(), 32, 32);
        registry.RegisterBuffer(owner, 0);
        var rejected = registry.AllocateBuffer(new TestBuffer(), address, size);
        Assert.Throws<SchedulerFatalException>(() => registry.RegisterBuffer(rejected, 0));
        Assert.Equal(BufferLifetimeState.Allocated, registry.GetState(rejected));
        Assert.Equal(1, registry.RegisteredCount);
        Assert.Equal(32UL, registry.RegisteredBytes);
        Assert.Equal(owner, registry.FindContainingBuffer(32, 32));
        Assert.Equal(new[] { owner }, registry.SnapshotRegisteredIdentifiers());
        Assert.False(registry.FindContainingBuffer(16, 1).IsValid);
        Assert.False(registry.FindContainingBuffer(64, 1).IsValid);
    }

    [Theory]
    [InlineData(0UL, 16UL)]
    [InlineData(16UL, 0UL)]
    [InlineData(17UL, 16UL)]
    [InlineData(16UL, 17UL)]
    [InlineData(4096UL, 16UL)]
    [InlineData(4080UL, 32UL)]
    [InlineData(ulong.MaxValue - 15, 32UL)]
    public void InvalidRangeIsRejectedBeforePublication(ulong address, ulong size)
    {
        using var fatal = new FatalScope();
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var identifier = registry.AllocateBuffer(new TestBuffer(), address, size);
        Assert.Throws<SchedulerFatalException>(() => registry.RegisterBuffer(identifier, 0));
        Assert.Equal(0, registry.RegisteredCount);
        Assert.Equal(0UL, registry.RegisteredBytes);
        Assert.Equal(BufferLifetimeState.Allocated, registry.GetState(identifier));
    }

    [Fact]
    public void InvalidTransitionsDoNotChangeRegisteredOwnership()
    {
        using var fatal = new FatalScope();
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var identifier = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        Assert.Throws<SchedulerFatalException>(() => registry.BeginRetirement(identifier));
        registry.RegisterBuffer(identifier, 0);
        Assert.Throws<SchedulerFatalException>(() => registry.RegisterBuffer(identifier, 0));
        Assert.Throws<SchedulerFatalException>(() => registry.CompleteRetirement(identifier));
        Assert.Equal(identifier, registry.FindContainingBuffer(32, 16));
        Assert.Equal(16UL, registry.RegisteredBytes);
    }

    [Fact]
    public void StaleCompletionCannotReleaseReusedIdentifierIndex()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var old = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        registry.RegisterBuffer(old, 0);
        registry.BeginRetirement(old);
        registry.CompleteRetirement(old);
        var replacement = new TestBuffer();
        var current = registry.AllocateBuffer(replacement, 32, 16);
        registry.RegisterBuffer(current, 1);
        Assert.Equal(old.Index, current.Index);
        Assert.NotEqual(old.Generation, current.Generation);
        Assert.Null(registry.TryGetBuffer(old));
        Assert.False(registry.CompleteRetirement(old));
        Assert.Equal(current, registry.FindContainingBuffer(32, 16));
        Assert.Equal(0, replacement.DisposeCount);
    }

    [Fact]
    public void RetiringOwnerDoesNotBlockReplacementOrClearItsPagesOnCompletion()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var old = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        registry.RegisterBuffer(old, 0);
        registry.BeginRetirement(old);
        var current = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        registry.RegisterBuffer(current, 0);
        Assert.NotEqual(old.Index, current.Index);
        registry.CompleteRetirement(old);
        Assert.Equal(current, registry.FindContainingBuffer(32, 16));
        Assert.Equal(16UL, registry.RegisteredBytes);
    }

    [Fact]
    public void RecencySupportsTouchAndRemovalDuringCandidateVisit()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var first = registry.AllocateBuffer(new TestBuffer(), 16, 16);
        var second = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        var third = registry.AllocateBuffer(new TestBuffer(), 48, 16);
        foreach (var identifier in new[] { first, second, third }) registry.RegisterBuffer(identifier, 0);
        registry.MarkBufferUsed(first, 1);
        var visited = new List<ResourceSlotIdentifier>();
        registry.VisitRetirementCandidates(0, identifier =>
        {
            visited.Add(identifier);
            registry.BeginRetirement(identifier);
            registry.CompleteRetirement(identifier);
            return false;
        });
        Assert.Equal(new[] { second, third }, visited);
        visited.Clear();
        registry.VisitRetirementCandidates(1, identifier => { visited.Add(identifier); return true; });
        Assert.Equal(new[] { first }, visited);
    }

    [Fact]
    public void PageLookupCrossesStorageBlocksAndHonorsExclusiveEnd()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 65536);
        var identifier = registry.AllocateBuffer(new TestBuffer(), 16368, 32);
        registry.RegisterBuffer(identifier, 0);
        Assert.Equal(identifier, registry.FindContainingBuffer(16368, 32));
        Assert.Equal(identifier, registry.FindContainingBuffer(16384, 16));
        Assert.False(registry.FindContainingBuffer(16400, 1).IsValid);
        Assert.False(registry.FindContainingBuffer(ulong.MaxValue, 2).IsValid);
        Assert.False(registry.HasOverlap(16352, 16));
        Assert.True(registry.HasOverlap(16352, 17));
    }

    [Fact]
    public void DisposalReleasesAllLifetimeStatesExactlyOnce()
    {
        var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var resources = Enumerable.Range(0, 4).Select(_ => new TestBuffer()).ToArray();
        var identifiers = resources.Select((resource, index) => registry.AllocateBuffer(resource, (ulong)(index + 1) * 16, 16)).ToArray();
        for (var index = 1; index < 4; index++) registry.RegisterBuffer(identifiers[index], 0);
        registry.BeginRetirement(identifiers[2]);
        registry.BeginRetirement(identifiers[3]);
        registry.CompleteRetirement(identifiers[3]);
        registry.Dispose();
        registry.Dispose();
        Assert.All(resources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.False(registry.CompleteRetirement(identifiers[2]));
        Assert.Equal(0, registry.RegisteredCount);
        Assert.Equal(0UL, registry.RegisteredBytes);
        Assert.False(registry.FindContainingBuffer(32, 16).IsValid);
    }

    [Fact]
    public void RandomizedOperationsMatchSimpleRangeModel()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var random = new System.Random(713);
        var owners = new Dictionary<int, ResourceSlotIdentifier>();
        var retiring = new List<ResourceSlotIdentifier>();
        for (var operation = 0; operation < 2000; operation++)
        {
            var page = random.Next(1, 255);
            if (owners.Remove(page, out var previous))
            {
                registry.BeginRetirement(previous);
                retiring.Add(previous);
            }
            else
            {
                var identifier = registry.AllocateBuffer(new TestBuffer(), (ulong)page * 16, 16);
                registry.RegisterBuffer(identifier, (ulong)operation);
                owners.Add(page, identifier);
            }
            if (retiring.Count > 0 && random.Next(2) == 0)
            {
                registry.CompleteRetirement(retiring[0]);
                retiring.RemoveAt(0);
            }
            Assert.Equal(owners.Count, registry.RegisteredCount);
            Assert.Equal((ulong)owners.Count * 16, registry.RegisteredBytes);
            var probe = random.Next(0, 256);
            var expected = owners.GetValueOrDefault(probe);
            Assert.Equal(expected, registry.FindContainingBuffer((ulong)probe * 16, 16));
            Assert.Equal(expected.IsValid, registry.HasOverlap((ulong)probe * 16, 16));
            Assert.Equal(owners.OrderBy(pair => pair.Key).Select(pair => pair.Value), registry.SnapshotRegisteredIdentifiers());
        }
    }

    [Fact]
    public void UnregisteredUtilityAllocationHasNoGuestOwnership()
    {
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var resource = new TestBuffer();
        var identifier = registry.AllocateBuffer(resource, 0, 16);
        Assert.Equal(new ResourceSlotIdentifier(0, 1), identifier);
        Assert.Same(resource, registry.GetBuffer(identifier));
        Assert.Equal(0, registry.RegisteredCount);
        Assert.Equal(0UL, registry.RegisteredBytes);
        Assert.False(registry.FindContainingBuffer(0, 1).IsValid);
    }

    [Fact]
    public void OutOfOrderRegistrationCannotBreakRetirementTraversal()
    {
        using var fatal = new FatalScope();
        using var registry = new GuestBufferRegistry<TestBuffer>(16, 4096);
        var first = registry.AllocateBuffer(new TestBuffer(), 16, 16);
        var second = registry.AllocateBuffer(new TestBuffer(), 32, 16);
        registry.RegisterBuffer(first, 10);
        Assert.Throws<SchedulerFatalException>(() => registry.RegisterBuffer(second, 9));
        Assert.Equal(BufferLifetimeState.Allocated, registry.GetState(second));
        Assert.Equal(16UL, registry.RegisteredBytes);
        var visited = new List<ResourceSlotIdentifier>();
        registry.VisitRetirementCandidates(10, identifier => { visited.Add(identifier); return false; });
        Assert.Equal(new[] { first }, visited);
    }
}
