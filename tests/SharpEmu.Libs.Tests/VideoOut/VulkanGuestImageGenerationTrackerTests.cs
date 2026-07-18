// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageGenerationTrackerTests
{
    private const ulong Address = 0xA1_0000;

    [Fact]
    public void Reserve_PublishesPendingGenerationForCapture()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();

        var token = tracker.Reserve(Address, identity);

        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(token, capture.Token);
        Assert.Equal(identity, capture.Identity);
        Assert.Equal(VulkanGuestImageGenerationState.Pending, capture.State);
        Assert.False(tracker.TryAcquireLatest(Address + 1, out _));
        Assert.False(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
        Assert.False(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void Submit_TransitionsPendingGenerationExactlyOnce()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var token = tracker.Reserve(Address, Identity());

        Assert.True(tracker.TryMarkSubmitted(token));
        Assert.False(tracker.TryMarkSubmitted(token));
        Assert.False(tracker.TryMarkFailed(token));
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.False(tracker.TryReleaseProducer(token));

        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(VulkanGuestImageGenerationState.Submitted, capture.State);
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void Fail_TransitionsPendingGenerationExactlyOnce()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var token = tracker.Reserve(Address, Identity());

        Assert.True(tracker.TryMarkFailed(token));
        Assert.False(tracker.TryMarkFailed(token));
        Assert.False(tracker.TryMarkSubmitted(token));
        Assert.True(tracker.TryReleaseProducer(token));

        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(VulkanGuestImageGenerationState.Failed, capture.State);
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void StaleSubmittedGeneration_RemainsUntilProducerAndPresentationRelease()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var staleIdentity = Identity(width: 128);
        var stale = tracker.Reserve(Address, staleIdentity);
        Assert.True(tracker.TryAcquireLatest(Address, out var staleCapture));
        var current = tracker.Reserve(Address, Identity(width: 256));

        Assert.True(tracker.TryMarkSubmitted(stale));
        Assert.False(tracker.TryMarkFailed(stale));
        Assert.True(tracker.TryGet(stale, out var staleState));
        Assert.Equal(VulkanGuestImageGenerationState.Submitted, staleState.State);
        Assert.True(tracker.IsSubmittedContentMatch(staleCapture, stale, staleIdentity));

        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(current, capture.Token);
        Assert.Equal((uint)256, capture.Identity.Width);
        Assert.Equal(VulkanGuestImageGenerationState.Pending, capture.State);

        Assert.True(tracker.TryReleaseProducer(stale));
        Assert.True(tracker.TryGet(stale, out _));
        Assert.True(tracker.TryReleasePresentation(staleCapture));
        Assert.False(tracker.TryGet(stale, out _));

        Assert.True(tracker.TryMarkSubmitted(current));
        Assert.True(tracker.IsSubmittedContentMatch(capture, current, capture.Identity));
        Assert.True(tracker.TryReleaseProducer(current));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void Capture_ReturnsNewestGenerationIndependentlyPerAddress()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var first = tracker.Reserve(Address, Identity(width: 128));
        var other = tracker.Reserve(Address + 0x1000, Identity(width: 512));
        var newest = tracker.Reserve(Address, Identity(width: 256));

        Assert.True(other.Generation > first.Generation);
        Assert.True(newest.Generation > other.Generation);
        Assert.True(tracker.TryAcquireLatest(Address, out var firstCapture));
        Assert.True(tracker.TryAcquireLatest(Address + 0x1000, out var otherCapture));
        Assert.Equal(newest, firstCapture.Token);
        Assert.Equal(other, otherCapture.Token);
        Assert.True(tracker.TryReleasePresentation(firstCapture));
        Assert.True(tracker.TryReleasePresentation(otherCapture));
    }

    [Fact]
    public void SubmittedContentMatch_RequiresGenerationAddressAndLogicalIdentity()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));

        Assert.True(tracker.IsSubmittedContentMatch(capture, token, identity));
        Assert.False(
            tracker.IsSubmittedContentMatch(
                capture,
                token with { Generation = token.Generation + 1 },
                identity));
        Assert.False(
            tracker.IsSubmittedContentMatch(
                capture,
                token with { Address = token.Address + 1 },
                identity));
        Assert.False(
            tracker.IsSubmittedContentMatch(
                capture,
                token,
                identity with { GuestFormat = identity.GuestFormat + 1 }));
        Assert.False(
            tracker.IsSubmittedContentMatch(
                capture,
                token,
                identity with { MipLevels = identity.MipLevels + 1 }));
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
        Assert.False(tracker.IsSubmittedContentMatch(capture, token, identity));
    }

    [Fact]
    public void SubmittedContentMatch_RejectsPendingAndFailedGenerations()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var pending = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var pendingCapture));

        Assert.False(tracker.IsSubmittedContentMatch(pendingCapture, pending, identity));

        Assert.True(tracker.TryMarkFailed(pending));
        Assert.False(tracker.IsSubmittedContentMatch(pendingCapture, pending, identity));
        Assert.True(tracker.TryReleaseProducer(pending));
        Assert.True(tracker.TryReleasePresentation(pendingCapture));
    }

    [Fact]
    public void PresentationReferences_ReleaseIndependentlyAndLatestRemainsPinned()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var first));
        Assert.True(tracker.TryAcquireLatest(Address, out var second));
        Assert.NotEqual(first.PresentationReference, second.PresentationReference);
        Assert.True(tracker.TryMarkSubmitted(token));
        Assert.True(tracker.TryReleaseProducer(token));

        Assert.True(tracker.TryReleasePresentation(first));
        Assert.False(tracker.TryReleasePresentation(first));
        Assert.True(tracker.IsSubmittedContentMatch(second, token, identity));
        Assert.True(tracker.TryReleasePresentation(second));
        Assert.True(tracker.TryGet(token, out _));

        tracker.Reserve(Address, identity);
        Assert.False(tracker.TryGet(token, out _));
    }

    [Fact]
    public void ContentPreservingArrayGrowth_RetainsLogicalGenerationIdentity()
    {
        var resource = new VulkanGpuGuestImageIdentity(
            GuestFormat: 56,
            Width: 1920,
            Height: 1080,
            MipLevels: 1,
            ResourceArrayLayers: 1);
        var grownResource = resource with { ResourceArrayLayers = 6 };

        var identity = VulkanGuestImageGenerationIdentity.FromResourceIdentity(resource);
        var grownIdentity = VulkanGuestImageGenerationIdentity.FromResourceIdentity(grownResource);
        var tracker = new VulkanGuestImageGenerationTracker();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryMarkSubmitted(token));
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));

        Assert.Equal(identity, grownIdentity);
        Assert.True(
            tracker.IsSubmittedContentMatch(
                capture,
                token,
                grownIdentity));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void FailedNewerGeneration_ShadowsOlderSubmittedContent()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var submitted = tracker.Reserve(Address, Identity(width: 128));
        Assert.True(tracker.TryMarkSubmitted(submitted));
        Assert.True(tracker.TryAcquireLatest(Address, out var submittedCapture));
        Assert.True(tracker.TryReleaseProducer(submitted));

        var failed = tracker.Reserve(Address, Identity(width: 256));
        Assert.True(tracker.TryMarkFailed(failed));
        Assert.True(tracker.TryReleaseProducer(failed));

        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(failed, capture.Token);
        Assert.Equal((uint)256, capture.Identity.Width);
        Assert.Equal(VulkanGuestImageGenerationState.Failed, capture.State);
        Assert.NotEqual(submitted, capture.Token);
        Assert.False(
            tracker.IsSubmittedContentMatch(
                capture,
                submitted,
                Identity(width: 128)));
        Assert.True(tracker.TryGet(submitted, out var submittedState));
        Assert.Equal(VulkanGuestImageGenerationState.Submitted, submittedState.State);
        Assert.True(tracker.TryReleasePresentation(submittedCapture));
        Assert.False(tracker.TryGet(submitted, out _));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void ConcurrentReserves_AreUniqueMonotonicAndPublishHighestGeneration()
    {
        const int count = 256;
        var tracker = new VulkanGuestImageGenerationTracker();
        var tokens = new ConcurrentBag<VulkanGuestImageGenerationToken>();

        Parallel.For(
            fromInclusive: 0,
            toExclusive: count,
            _ => tokens.Add(tracker.Reserve(Address, Identity())));

        var generations = tokens
            .Select(static token => token.Generation)
            .Order()
            .ToArray();
        Assert.Equal(count, generations.Length);
        for (var index = 0; index < generations.Length; index++)
        {
            Assert.Equal((ulong)(index + 1), generations[index]);
        }

        Assert.True(tracker.TryAcquireLatest(Address, out var latest));
        Assert.Equal(generations[^1], latest.Token.Generation);
        Assert.True(tracker.TryReleasePresentation(latest));
        tracker.Reset();
    }

    [Fact]
    public void InvalidateAddress_RemovesEveryGenerationWithoutAffectingOtherAddresses()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var first = tracker.Reserve(Address, Identity(width: 128));
        var second = tracker.Reserve(Address, Identity(width: 256));
        var other = tracker.Reserve(Address + 0x1000, Identity(width: 512));
        Assert.True(tracker.TryAcquireLatest(Address, out var staleCapture));
        Assert.True(tracker.TryMarkSubmitted(second));

        Assert.True(tracker.InvalidateAddress(Address));

        Assert.False(tracker.TryAcquireLatest(Address, out _));
        Assert.False(tracker.TryGet(first, out _));
        Assert.False(tracker.TryGet(second, out _));
        Assert.False(tracker.IsSubmittedContentMatch(staleCapture, second, staleCapture.Identity));
        Assert.False(tracker.TryReleasePresentation(staleCapture));
        Assert.True(tracker.TryAcquireLatest(Address + 0x1000, out var otherCapture));
        Assert.Equal(other, otherCapture.Token);
        Assert.True(tracker.TryReleasePresentation(otherCapture));
        Assert.False(tracker.InvalidateAddress(Address));

        var replacement = tracker.Reserve(Address, Identity());
        Assert.True(replacement.Generation > other.Generation);
    }

    [Fact]
    public void Reset_RemovesAllStateWithoutReusingGenerationTokens()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var first = tracker.Reserve(Address, Identity());
        var second = tracker.Reserve(Address + 0x1000, Identity());
        Assert.True(tracker.TryAcquireLatest(Address, out var firstCapture));
        Assert.True(tracker.TryAcquireLatest(Address + 0x1000, out var secondCapture));

        tracker.Reset();

        Assert.False(tracker.TryAcquireLatest(Address, out _));
        Assert.False(tracker.TryAcquireLatest(Address + 0x1000, out _));
        Assert.False(tracker.TryGet(first, out _));
        Assert.False(tracker.TryGet(second, out _));
        Assert.False(tracker.TryReleasePresentation(firstCapture));
        Assert.False(tracker.TryReleasePresentation(secondCapture));

        var replacement = tracker.Reserve(Address, Identity());
        Assert.True(replacement.Generation > second.Generation);
    }

    private static VulkanGuestImageGenerationIdentity Identity(
        uint width = 1920,
        uint height = 1080) =>
        new(
            GuestFormat: 56,
            Width: width,
            Height: height,
            MipLevels: 1);
}
