// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImagePresentationPolicyTests
{
    private const ulong Address = 0xA1_0000;
    private const long WorkBoundary = 7;

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void PendingDirectFlip_AllowsWorkBeforeAndAtBoundaryOnly(
        long nextWorkSequence,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanGuestImagePresentationPolicy.MayDequeueQueuedWork(
                nextWorkSequence,
                WorkBoundary));
    }

    [Fact]
    public void NoPendingDirectFlip_AllowsQueuedWork()
    {
        Assert.True(
            VulkanGuestImagePresentationPolicy.MayDequeueQueuedWork(
                long.MaxValue,
                pendingDirectFlipBoundary: null));
    }

    [Fact]
    public void PendingProducer_BeforeRequiredBoundaryWaits()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            completedWorkBoundary: WorkBoundary - 1,
            identity,
            Resource(token, identity));

        Assert.Equal(VulkanGuestImagePresentationDecision.Wait, decision);
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void PendingProducer_AtOrAfterRequiredBoundaryIsRejected(
        long completedWorkBoundary)
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            completedWorkBoundary,
            identity,
            Resource(token, identity));

        Assert.Equal(
            VulkanGuestImagePresentationDecision.PendingAfterBoundary,
            decision);
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void FailedNewerGeneration_ShadowsOlderSubmittedResource()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var olderIdentity = Identity(width: 128);
        var older = tracker.Reserve(Address, olderIdentity);
        Assert.True(tracker.TryMarkSubmitted(older));
        Assert.True(tracker.TryReleaseProducer(older));

        var newerIdentity = Identity(width: 256);
        var newer = tracker.Reserve(Address, newerIdentity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkFailed(newer));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            newerIdentity,
            Resource(older, olderIdentity));

        Assert.Equal(VulkanGuestImagePresentationDecision.ProducerFailed, decision);
        Assert.True(tracker.TryReleaseProducer(newer));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithExactDisplayAndResourceContentPresents()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.Equal(VulkanGuestImageGenerationState.Pending, capture.State);
        Assert.True(tracker.TryMarkSubmitted(token));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity,
            Resource(token, identity));

        Assert.Equal(VulkanGuestImagePresentationDecision.Present, decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithWrongResourceGenerationIsRejected()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity,
            Resource(
                token with { Generation = token.Generation + 1 },
                identity));

        Assert.Equal(
            VulkanGuestImagePresentationDecision.ResourceGenerationMismatch,
            decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithWrongDisplayIdentityIsRejected()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity with { Width = identity.Width + 1 },
            Resource(token, identity));

        Assert.Equal(
            VulkanGuestImagePresentationDecision.DisplayIdentityMismatch,
            decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithMissingResourceIsRejected()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));
        var resource = Resource(token, identity) with { Exists = false };

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity,
            resource);

        Assert.Equal(VulkanGuestImagePresentationDecision.ResourceMissing, decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithUninitializedResourceIsRejected()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));
        var resource = Resource(token, identity) with { Initialized = false };

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity,
            resource);

        Assert.Equal(
            VulkanGuestImagePresentationDecision.ResourceUninitialized,
            decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    [Fact]
    public void SubmittedGeneration_WithWrongResourceIdentityIsRejected()
    {
        var tracker = new VulkanGuestImageGenerationTracker();
        var identity = Identity();
        var token = tracker.Reserve(Address, identity);
        Assert.True(tracker.TryAcquireLatest(Address, out var capture));
        Assert.True(tracker.TryMarkSubmitted(token));

        var decision = VulkanGuestImagePresentationPolicy.Evaluate(
            tracker,
            capture,
            WorkBoundary,
            WorkBoundary,
            identity,
            Resource(token, identity with { Height = identity.Height + 1 }));

        Assert.Equal(
            VulkanGuestImagePresentationDecision.ResourceGenerationMismatch,
            decision);
        Assert.True(tracker.TryReleaseProducer(token));
        Assert.True(tracker.TryReleasePresentation(capture));
    }

    private static VulkanGuestImagePresentationResourceState Resource(
        VulkanGuestImageGenerationToken token,
        VulkanGuestImageGenerationIdentity identity) =>
        new(
            Exists: true,
            Initialized: true,
            token,
            identity);

    private static VulkanGuestImageGenerationIdentity Identity(
        uint width = 1920,
        uint height = 1080) =>
        new(
            GuestFormat: 56,
            Width: width,
            Height: height,
            MipLevels: 1);
}
