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

// The open pass: its attachments (identity) and the pass and framebuffer that render them (payload).
internal readonly record struct VulkanRenderPassReuseKey(
    ulong Image,
    ulong DepthImage,
    ulong RenderPass,
    ulong Framebuffer,
    uint Width,
    uint Height,
    uint Layers,
    ulong[] AttachmentViews)
{
    // The same images, extent and layer count, and the same attachment views in the same order.
    public bool HasSameAttachments(in VulkanRenderPassReuseKey other) =>
        Image == other.Image &&
        DepthImage == other.DepthImage &&
        Width == other.Width &&
        Height == other.Height &&
        Layers == other.Layers &&
        AttachmentViews.AsSpan().SequenceEqual(other.AttachmentViews);
}

internal static class VulkanRenderPassReusePolicy
{
    internal static bool IsEnabled(string? disableValue) =>
        !string.Equals(disableValue, "1", StringComparison.Ordinal);

    internal static bool CanKeepOpen(VulkanRenderPassReuseHazard hazards) =>
        hazards == VulkanRenderPassReuseHazard.None;

    internal static bool CanContinue(
        in VulkanRenderPassReuseKey openPass,
        in VulkanRenderPassReuseKey candidate,
        VulkanRenderPassReuseHazard hazards) =>
        CanKeepOpen(hazards) && openPass.HasSameAttachments(candidate);
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

        // The attachments of one draw as a reuse candidate; the pass and framebuffer come from the open pass.
        private static VulkanRenderPassReuseKey CreateRenderPassReuseCandidate(
            IReadOnlyList<ColorAttachment> targets,
            DepthAttachment? depth,
            uint width,
            uint height,
            uint layers)
        {
            var views = new ulong[targets.Count + (depth is null ? 0 : 1)];
            for (var index = 0; index < targets.Count; index++)
            {
                views[index] = targets[index].View.Handle;
            }

            if (depth is not null)
            {
                views[targets.Count] = depth.View.Handle;
            }

            var firstImage = targets.Count > 0 ? targets[0].Image.Backing.Handle.Handle : 0;
            var depthImage = depth?.Image.Backing.Handle.Handle ?? 0;
            return new VulkanRenderPassReuseKey(firstImage, depthImage, 0, 0, width, height, layers, views);
        }

        private static VulkanRenderPassReuseHazard GetRenderPassReuseHazards(
            IReadOnlyList<ColorAttachment> targets,
            IReadOnlyList<TextureResource> textures,
            bool anyAttachmentClear,
            bool needsGlobalBufferBarrier)
        {
            var hazards = VulkanRenderPassReuseHazard.None;
            if (targets.Count != 1)
            {
                hazards |= VulkanRenderPassReuseHazard.MultipleColorTargets;
            }
            if (anyAttachmentClear)
            {
                hazards |= VulkanRenderPassReuseHazard.InitialAttachmentLoad;
            }
            if (needsGlobalBufferBarrier)
            {
                hazards |= VulkanRenderPassReuseHazard.GlobalBufferBarrier;
            }

            foreach (var texture in textures)
            {
                if (texture.NeedsUpload)
                {
                    hazards |= VulkanRenderPassReuseHazard.TextureUpload;
                }
                if (texture.IsStorage)
                {
                    hazards |= VulkanRenderPassReuseHazard.StorageImage;
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
