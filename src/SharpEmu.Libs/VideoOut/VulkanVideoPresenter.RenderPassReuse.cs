// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;

[Flags]
internal enum VulkanRenderPassReuseHazard
{
    None = 0,
    MultipleColorTargets = 1 << 0,
    InitialAttachmentLoad = 1 << 1,
    GlobalBufferBarrier = 1 << 2,
    TextureUpload = 1 << 3,
    FeedbackCopy = 1 << 4,
    StorageImage = 1 << 5,
    SampledImageTransition = 1 << 6,
}

internal readonly record struct VulkanRenderPassReuseKey(
    ulong Image,
    ulong DepthImage,
    ulong RenderPass,
    ulong Framebuffer,
    uint Width,
    uint Height);

internal static class VulkanRenderPassReusePolicy
{
    internal static bool IsEnabled(string? disableValue) =>
        !string.Equals(disableValue, "1", StringComparison.Ordinal);

    internal static bool CanKeepOpen(VulkanRenderPassReuseHazard hazards) =>
        hazards == VulkanRenderPassReuseHazard.None;

    internal static bool CanContinue(
        VulkanRenderPassReuseKey openPass,
        VulkanRenderPassReuseKey candidate,
        VulkanRenderPassReuseHazard hazards) =>
        CanKeepOpen(hazards) && openPass == candidate;
}

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool _reuseTranslatedRenderPasses =
        VulkanRenderPassReusePolicy.IsEnabled(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RENDER_PASS_REUSE"));

    private static readonly bool _logTranslatedRenderPassReuse =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_RENDER_PASS_REUSE"),
            "1",
            StringComparison.Ordinal);

    private sealed partial class Presenter
    {
        private VulkanRenderPassReuseKey? _openPassKey;
        private long _renderPassReuseDraws;
        private long _renderPassReuseEligible;
        private long _renderPassReuseContinued;

        private bool NeedsGlobalBufferVisibilityBarrier(
            TranslatedDrawResources resources) =>
            resources.GlobalMemoryBuffers.Length != 0 &&
            _globalBufferBarrierTracker.ShouldRecordBarrier(
                _elideRedundantGlobalBufferBarriers);

        private static VulkanRenderPassReuseHazard GetRenderPassReuseHazards(
            IReadOnlyList<GuestImageResource> targets,
            TranslatedDrawResources resources,
            bool usesInitializedLoadPass,
            bool needsGlobalBufferBarrier)
        {
            var hazards = VulkanRenderPassReuseHazard.None;
            if (targets.Count != 1)
            {
                hazards |= VulkanRenderPassReuseHazard.MultipleColorTargets;
            }
            if (!usesInitializedLoadPass)
            {
                hazards |= VulkanRenderPassReuseHazard.InitialAttachmentLoad;
            }
            if (needsGlobalBufferBarrier)
            {
                hazards |= VulkanRenderPassReuseHazard.GlobalBufferBarrier;
            }

            foreach (var texture in resources.Textures)
            {
                if (texture.NeedsUpload)
                {
                    hazards |= VulkanRenderPassReuseHazard.TextureUpload;
                }
                if (texture.FeedbackSource is not null ||
                    texture.DepthFeedbackSource is not null)
                {
                    hazards |= VulkanRenderPassReuseHazard.FeedbackCopy;
                }
                if (texture.IsStorage)
                {
                    hazards |= VulkanRenderPassReuseHazard.StorageImage;
                }
                if (texture.GuestDepth is { } depth &&
                    depth.Layout != ImageLayout.ShaderReadOnlyOptimal)
                {
                    hazards |= VulkanRenderPassReuseHazard.SampledImageTransition;
                }
                if (texture.GuestImage is { } image &&
                    !image.Initialized &&
                    !image.InitialUploadPending)
                {
                    hazards |= VulkanRenderPassReuseHazard.SampledImageTransition;
                }
            }

            return hazards;
        }

        private void RecordRenderPassReuseDecision(
            VulkanRenderPassReuseHazard hazards,
            bool continued)
        {
            _renderPassReuseDraws++;
            if (VulkanRenderPassReusePolicy.CanKeepOpen(hazards))
            {
                _renderPassReuseEligible++;
            }
            if (continued)
            {
                _renderPassReuseContinued++;
            }

            if (!_logTranslatedRenderPassReuse ||
                (_renderPassReuseDraws > 4 && (_renderPassReuseDraws & 4095) != 0))
            {
                return;
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] vk.render_pass_reuse " +
                $"enabled={(_reuseTranslatedRenderPasses ? 1 : 0)} " +
                $"draws={_renderPassReuseDraws} eligible={_renderPassReuseEligible} " +
                $"continued={_renderPassReuseContinued} hazards={hazards}");
        }
    }
}
