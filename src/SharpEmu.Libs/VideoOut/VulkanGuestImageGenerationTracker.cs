// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

internal enum VulkanGuestImageGenerationState
{
    Pending,
    Submitted,
    Failed,
}

internal readonly record struct VulkanGuestImageGenerationToken(
    ulong Address,
    ulong Generation);

/// <summary>
/// Identifies logical image content without including replaceable physical array capacity.
/// </summary>
internal readonly record struct VulkanGuestImageGenerationIdentity(
    uint GuestFormat,
    uint Width,
    uint Height,
    uint MipLevels,
    uint Depth = 1,
    GuestImageKind ImageKind = GuestImageKind.Type2D)
{
    public static VulkanGuestImageGenerationIdentity FromResourceIdentity(
        VulkanGpuGuestImageIdentity identity) =>
        new(
            identity.GuestFormat,
            identity.Width,
            identity.Height,
            identity.MipLevels,
            identity.Depth,
            identity.ImageKind);
}

internal readonly record struct VulkanGuestImageGenerationSnapshot(
    VulkanGuestImageGenerationToken Token,
    VulkanGuestImageGenerationIdentity Identity,
    VulkanGuestImageGenerationState State);

internal readonly record struct VulkanGuestImageGenerationCapture(
    VulkanGuestImageGenerationToken Token,
    VulkanGuestImageGenerationIdentity Identity,
    VulkanGuestImageGenerationState State,
    ulong PresentationReference);

/// <summary>
/// Tracks the newest producer generation for each guest-image address independently of Vulkan
/// resources. A newer pending or failed generation intentionally hides older submitted content.
/// Reservations and acquired captures own references that callers must release after terminal
/// producer completion and presentation consumption respectively.
/// </summary>
internal sealed class VulkanGuestImageGenerationTracker
{
    private sealed class GenerationEntry(
        VulkanGuestImageGenerationSnapshot snapshot)
    {
        public VulkanGuestImageGenerationSnapshot Snapshot { get; set; } = snapshot;
        public bool ProducerOwned { get; set; } = true;
        public HashSet<ulong> PresentationReferences { get; } = new();
    }

    private sealed class AddressState
    {
        public VulkanGuestImageGenerationToken Latest { get; set; }
        public Dictionary<ulong, GenerationEntry> Generations { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, AddressState> _statesByAddress = new();
    private ulong _nextGeneration;
    private ulong _nextPresentationReference;

    public VulkanGuestImageGenerationToken Reserve(
        ulong address,
        VulkanGuestImageGenerationIdentity identity)
    {
        lock (_gate)
        {
            var token = new VulkanGuestImageGenerationToken(address, ++_nextGeneration);
            if (!_statesByAddress.TryGetValue(address, out var addressState))
            {
                addressState = new AddressState();
                _statesByAddress.Add(address, addressState);
            }

            var previousLatest = addressState.Latest;
            addressState.Generations.Add(
                token.Generation,
                new GenerationEntry(
                    new VulkanGuestImageGenerationSnapshot(
                        token,
                        identity,
                        VulkanGuestImageGenerationState.Pending)));
            addressState.Latest = token;
            TryPruneLocked(addressState, previousLatest.Generation);
            return token;
        }
    }

    public bool TryMarkSubmitted(VulkanGuestImageGenerationToken token) =>
        TryTransition(token, VulkanGuestImageGenerationState.Submitted);

    public bool TryMarkFailed(VulkanGuestImageGenerationToken token) =>
        TryTransition(token, VulkanGuestImageGenerationState.Failed);

    public bool TryAcquireLatest(
        ulong address,
        out VulkanGuestImageGenerationCapture capture)
    {
        lock (_gate)
        {
            if (_statesByAddress.TryGetValue(address, out var addressState) &&
                addressState.Generations.TryGetValue(
                    addressState.Latest.Generation,
                    out var entry))
            {
                var presentationReference = ++_nextPresentationReference;
                entry.PresentationReferences.Add(presentationReference);
                capture = new VulkanGuestImageGenerationCapture(
                    entry.Snapshot.Token,
                    entry.Snapshot.Identity,
                    entry.Snapshot.State,
                    presentationReference);
                return true;
            }

            capture = default;
            return false;
        }
    }

    public bool TryGet(
        VulkanGuestImageGenerationToken token,
        out VulkanGuestImageGenerationSnapshot generation)
    {
        lock (_gate)
        {
            return TryGetLocked(token, out generation);
        }
    }

    public bool IsSubmittedContentMatch(
        VulkanGuestImageGenerationCapture expected,
        VulkanGuestImageGenerationToken actualToken,
        VulkanGuestImageGenerationIdentity actualIdentity)
    {
        lock (_gate)
        {
            return TryGetEntryLocked(expected.Token, out var entry, out _) &&
                entry.PresentationReferences.Contains(expected.PresentationReference) &&
                entry.Snapshot.State == VulkanGuestImageGenerationState.Submitted &&
                entry.Snapshot.Identity == expected.Identity &&
                entry.Snapshot.Token == actualToken &&
                entry.Snapshot.Identity == actualIdentity;
        }
    }

    public bool TryReleaseProducer(VulkanGuestImageGenerationToken token)
    {
        lock (_gate)
        {
            if (!TryGetEntryLocked(token, out var entry, out var addressState) ||
                !entry.ProducerOwned ||
                entry.Snapshot.State == VulkanGuestImageGenerationState.Pending)
            {
                return false;
            }

            entry.ProducerOwned = false;
            TryPruneLocked(addressState, token.Generation);
            return true;
        }
    }

    public bool TryReleasePresentation(VulkanGuestImageGenerationCapture capture)
    {
        lock (_gate)
        {
            if (!TryGetEntryLocked(capture.Token, out var entry, out var addressState) ||
                entry.Snapshot.Identity != capture.Identity ||
                !entry.PresentationReferences.Remove(capture.PresentationReference))
            {
                return false;
            }

            TryPruneLocked(addressState, capture.Token.Generation);
            return true;
        }
    }

    public bool InvalidateAddress(ulong address)
    {
        lock (_gate)
        {
            return _statesByAddress.Remove(address);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _statesByAddress.Clear();
            // Keep both counters monotonic so captures held across a reset cannot alias new state.
        }
    }

    private bool TryTransition(
        VulkanGuestImageGenerationToken token,
        VulkanGuestImageGenerationState state)
    {
        lock (_gate)
        {
            if (!TryGetEntryLocked(token, out var entry, out var addressState) ||
                entry.Snapshot.State != VulkanGuestImageGenerationState.Pending)
            {
                return false;
            }

            entry.Snapshot = entry.Snapshot with { State = state };
            TryPruneLocked(addressState, token.Generation);
            return true;
        }
    }

    private bool TryGetLocked(
        VulkanGuestImageGenerationToken token,
        out VulkanGuestImageGenerationSnapshot generation)
    {
        if (TryGetEntryLocked(token, out var entry, out _))
        {
            generation = entry.Snapshot;
            return true;
        }

        generation = default;
        return false;
    }

    private bool TryGetEntryLocked(
        VulkanGuestImageGenerationToken token,
        out GenerationEntry entry,
        out AddressState addressState)
    {
        if (_statesByAddress.TryGetValue(token.Address, out var foundAddressState) &&
            foundAddressState.Generations.TryGetValue(token.Generation, out var foundEntry) &&
            foundEntry.Snapshot.Token == token)
        {
            entry = foundEntry;
            addressState = foundAddressState;
            return true;
        }

        entry = null!;
        addressState = null!;
        return false;
    }

    private static void TryPruneLocked(
        AddressState addressState,
        ulong generation)
    {
        if (generation == 0 ||
            addressState.Latest.Generation == generation ||
            !addressState.Generations.TryGetValue(generation, out var entry) ||
            entry.Snapshot.State == VulkanGuestImageGenerationState.Pending ||
            entry.ProducerOwned ||
            entry.PresentationReferences.Count != 0)
        {
            return;
        }

        addressState.Generations.Remove(generation);
    }
}
