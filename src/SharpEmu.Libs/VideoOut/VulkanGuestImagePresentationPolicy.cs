// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal enum VulkanGuestImagePresentationDecision
{
    Wait,
    Present,
    ProducerFailed,
    PendingAfterBoundary,
    DisplayIdentityMismatch,
    ResourceMissing,
    ResourceUninitialized,
    ResourceGenerationMismatch,
}

internal readonly record struct VulkanGuestImagePresentationResourceState(
    bool Exists,
    bool Initialized,
    VulkanGuestImageGenerationToken ContentToken,
    VulkanGuestImageGenerationIdentity Identity);

/// <summary>
/// Applies direct-presentation ordering and content-identity rules without depending on Vulkan.
/// </summary>
internal static class VulkanGuestImagePresentationPolicy
{
    /// <summary>
    /// Prevents work newer than the oldest pending direct flip from changing its captured image.
    /// Work at the flip boundary remains eligible because it contributes to that flip.
    /// </summary>
    public static bool MayDequeueQueuedWork(
        long nextWorkSequence,
        long? pendingDirectFlipBoundary) =>
        pendingDirectFlipBoundary is null ||
        nextWorkSequence <= pendingDirectFlipBoundary.Value;

    /// <summary>
    /// Evaluates whether a captured generation is ready and still names the exact submitted
    /// content held by the presentation resource.
    /// </summary>
    public static VulkanGuestImagePresentationDecision Evaluate(
        VulkanGuestImageGenerationTracker tracker,
        VulkanGuestImageGenerationCapture capture,
        long requiredWorkBoundary,
        long completedWorkBoundary,
        VulkanGuestImageGenerationIdentity expectedDisplayIdentity,
        VulkanGuestImagePresentationResourceState resource)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        if (completedWorkBoundary < requiredWorkBoundary)
        {
            return VulkanGuestImagePresentationDecision.Wait;
        }

        if (!tracker.TryGet(capture.Token, out var generation) ||
            generation.Identity != capture.Identity)
        {
            return VulkanGuestImagePresentationDecision.ResourceGenerationMismatch;
        }

        if (generation.State == VulkanGuestImageGenerationState.Pending)
        {
            return VulkanGuestImagePresentationDecision.PendingAfterBoundary;
        }

        if (generation.State == VulkanGuestImageGenerationState.Failed)
        {
            return VulkanGuestImagePresentationDecision.ProducerFailed;
        }

        if (generation.Identity != expectedDisplayIdentity)
        {
            return VulkanGuestImagePresentationDecision.DisplayIdentityMismatch;
        }

        if (!resource.Exists)
        {
            return VulkanGuestImagePresentationDecision.ResourceMissing;
        }

        if (!resource.Initialized)
        {
            return VulkanGuestImagePresentationDecision.ResourceUninitialized;
        }

        if (!tracker.IsSubmittedContentMatch(
                capture,
                resource.ContentToken,
                resource.Identity))
        {
            return VulkanGuestImagePresentationDecision.ResourceGenerationMismatch;
        }

        return VulkanGuestImagePresentationDecision.Present;
    }
}
