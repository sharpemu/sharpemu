// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class PhysicalVirtualMemoryDirectReplacementTests
{
    [Fact]
    public void FixedReplacementSpansViewsAndPreservesBothOuterFragments()
    {
        const ulong firstAddress = 0x00005000_0400_8000;
        const ulong viewSize = 0x10000;
        const ulong secondAddress = firstAddress + viewSize;
        const ulong replacementAddress = firstAddress + 0x4000;
        const ulong replacementSize = 0x18000;
        const ulong replacementEnd = replacementAddress + replacementSize;
        const ulong directMemorySize = 0x80000;
        const GuestPageProtection protection =
            GuestPageProtection.Read | GuestPageProtection.Write;
        using var host = new FailingSharedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        Assert.True(memory.TryMapDirectMemory(
            firstAddress,
            viewSize,
            directMemoryOffset: 0,
            directMemorySize,
            protection,
            alignment: 0x4000,
            allowSearch: false,
            out _), "first view");
        Assert.True(memory.TryMapDirectMemory(
            secondAddress,
            viewSize,
            directMemoryOffset: viewSize,
            directMemorySize,
            protection,
            alignment: 0x4000,
            allowSearch: false,
            out _), "second view");
        Assert.True(memory.TryReplaceDirectMemory(
            replacementAddress,
            replacementSize,
            directMemoryOffset: 0x40000,
            directMemorySize,
            protection,
            out var actualAddress), "replacement");
        Assert.Equal(replacementAddress, actualAddress);

        Assert.Equal(
            [
                new SharedView(firstAddress, 0x4000, 0, HostPageProtection.ReadWrite),
                new SharedView(replacementAddress, replacementSize, 0x40000, HostPageProtection.ReadWrite),
                new SharedView(replacementEnd, 0x4000, 0x1C000, HostPageProtection.ReadWrite),
            ],
            host.ActiveViews.OrderBy(static view => view.Address));
        Assert.Equal(3, memory.SnapshotRegions().Count);
        Assert.True(memory.IsAccessible(replacementAddress, replacementSize));

        Assert.True(memory.TryUnmapDirectMemory(firstAddress, 0x4000));
        Assert.True(memory.TryUnmapDirectMemory(replacementAddress, replacementSize));
        Assert.True(memory.TryUnmapDirectMemory(replacementEnd, 0x4000));
        Assert.False(memory.TryUnmapDirectMemory(firstAddress, 0x4000));
        Assert.False(memory.TryUnmapDirectMemory(replacementAddress, replacementSize));
        Assert.False(memory.TryUnmapDirectMemory(replacementEnd, 0x4000));
    }

    [Fact]
    public void FailedFixedReplacementRestoresOriginalDirectViewAndMetadata()
    {
        const ulong originalAddress = 0x00005000_0200_0000;
        const ulong originalSize = 0xC000;
        const ulong originalPhysicalOffset = 0x4000;
        const ulong replacementAddress = originalAddress + 0x4000;
        const ulong replacementSize = 0x8000;
        const ulong replacementPhysicalOffset = 0x20000;
        const ulong directMemorySize = 0x40000;
        const GuestPageProtection guestProtection =
            GuestPageProtection.Read | GuestPageProtection.Write;
        const HostPageProtection hostProtection = HostPageProtection.ReadWrite;

        using var host = new FailingSharedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        Assert.True(memory.TryMapDirectMemory(
            originalAddress,
            originalSize,
            originalPhysicalOffset,
            directMemorySize,
            guestProtection,
            alignment: 0x4000,
            allowSearch: false,
            out var mappedAddress));
        Assert.Equal(originalAddress, mappedAddress);

        host.FailNextMapAfterViewIsInstalled(
            replacementAddress,
            replacementSize,
            requiredViewAddress: originalAddress,
            requiredViewSize: replacementAddress - originalAddress);

        Assert.False(memory.TryReplaceDirectMemory(
            replacementAddress,
            replacementSize,
            replacementPhysicalOffset,
            directMemorySize,
            guestProtection,
            out var replacementMapping));
        Assert.Equal(0UL, replacementMapping);
        Assert.True(host.FailureWasInjected);

        var restoredView = Assert.Single(host.ActiveViews);
        Assert.Equal(
            new SharedView(
                originalAddress,
                originalSize,
                originalPhysicalOffset,
                hostProtection),
            restoredView);
        Assert.True(memory.IsAccessible(originalAddress, originalSize));
        var restoredRegion = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(originalAddress, restoredRegion.VirtualAddress);
        Assert.Equal(originalSize, restoredRegion.MemorySize);

        Assert.True(memory.TryUnmapDirectMemory(originalAddress, originalSize));
        Assert.Empty(host.ActiveViews);
        Assert.Empty(memory.SnapshotRegions());
        Assert.False(memory.IsAccessible(originalAddress, 1));
        Assert.False(memory.TryUnmapDirectMemory(originalAddress, originalSize));
    }

    private sealed class FailingSharedHostMemory : IHostMemory, IDisposable
    {
        private readonly Dictionary<ulong, SharedView> _activeViews = [];
        private FakeSharedMemory? _sharedMemory;
        private ulong _failureAddress;
        private ulong _failureSize;
        private ulong _requiredViewAddress;
        private ulong _requiredViewSize;
        private bool _failureArmed;

        public IReadOnlyCollection<SharedView> ActiveViews => _activeViews.Values;

        public bool FailureWasInjected { get; private set; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public IHostSharedMemory? CreateSharedMemory(ulong size)
        {
            _sharedMemory = new FakeSharedMemory(size);
            return _sharedMemory;
        }

        public ulong MapSharedMemory(
            IHostSharedMemory sharedMemory,
            ulong desiredAddress,
            ulong size,
            ulong offset,
            HostPageProtection protection)
        {
            Assert.Same(_sharedMemory, sharedMemory);
            Assert.NotEqual(0UL, desiredAddress);

            if (_failureArmed &&
                desiredAddress == _failureAddress &&
                size == _failureSize &&
                _activeViews.TryGetValue(_requiredViewAddress, out var requiredView) &&
                requiredView.Size == _requiredViewSize)
            {
                _failureArmed = false;
                FailureWasInjected = true;
                return 0;
            }

            var end = checked(desiredAddress + size);
            if (_activeViews.Values.Any(view =>
                    view.Address < end &&
                    desiredAddress < view.Address + view.Size))
            {
                return 0;
            }

            _activeViews.Add(
                desiredAddress,
                new SharedView(desiredAddress, size, offset, protection));
            return desiredAddress;
        }

        public bool UnmapSharedMemory(ulong address, ulong size) =>
            _activeViews.TryGetValue(address, out var view) &&
            view.Size == size &&
            _activeViews.Remove(address);

        public bool UnmapReservedMemory(ulong address, ulong size) => false;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => false;

        public bool Free(ulong address) => _activeViews.Remove(address);

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return false;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return false;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void FailNextMapAfterViewIsInstalled(
            ulong address,
            ulong size,
            ulong requiredViewAddress,
            ulong requiredViewSize)
        {
            _failureAddress = address;
            _failureSize = size;
            _requiredViewAddress = requiredViewAddress;
            _requiredViewSize = requiredViewSize;
            _failureArmed = true;
        }

        public void Dispose() => _sharedMemory?.Dispose();
    }

    private sealed class FakeSharedMemory(ulong size) : IHostSharedMemory
    {
        public ulong Size { get; } = size;

        public void Dispose()
        {
        }
    }

    private readonly record struct SharedView(
        ulong Address,
        ulong Size,
        ulong Offset,
        HostPageProtection Protection);
}
