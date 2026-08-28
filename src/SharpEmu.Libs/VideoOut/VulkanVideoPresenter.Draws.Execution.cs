// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial executes offscreen guest rendering commands.

        private void ExecuteOffscreenDraw(VulkanOffscreenGuestDraw work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            var perfStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteOffscreenDrawCore(work);
            }
            finally
            {
                // Single atomic add per draw: staging -start/+end separately
                // let the stats window reset land between them and report
                // huge negative draw_ms values.
                Interlocked.Add(
                    ref _perfDrawTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - perfStart);
            }
        }

        private void ExecuteOffscreenDrawCore(VulkanOffscreenGuestDraw work)
        {
            if (work.Targets.Count > _maxColorAttachments)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped MRT draw requesting {work.Targets.Count} color attachments; " +
                    $"the selected device supports {_maxColorAttachments}.");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(
                        target.Format,
                        target.NumberType,
                        target.ComponentSwap,
                        out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped MRT draw with unsupported color target " +
                        $"format={target.Format} number_type={target.NumberType}.");
                    ReturnPooledGuestData(work.Draw);
                    return;
                }
            }

            if (work.Draw.RenderState.Blends.Count != targetFormats.Length)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with mismatched attachment/blend counts.");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            var normalizedBlends = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
                work.Draw.RenderState.Blends,
                targetFormats.Select(static format => format.IsInteger).ToArray(),
                out var normalizedBlendCount);
            var draw = normalizedBlendCount == 0
                ? work.Draw
                : work.Draw with
                {
                    RenderState = work.Draw.RenderState with { Blends = normalizedBlends },
                };

            if (!_supportsIndependentBlend)
            {
                for (var index = 1; index < draw.RenderState.Blends.Count; index++)
                {
                    if (draw.RenderState.Blends[index] !=
                        draw.RenderState.Blends[0])
                    {
                        Console.Error.WriteLine(
                            "[LOADER][WARN] Vulkan skipped MRT draw requiring unsupported independentBlend.");
                        ReturnPooledGuestData(work.Draw);
                        return;
                    }
                }
            }

            var formats = new Format[targetFormats.Length];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                formats[index] = targetFormats[index].Format;
            }

            if (!VulkanStorageFeedbackTargetResolver.TryResolve(
                    work.Targets,
                    draw.Textures,
                    draw.RenderState.Blends,
                    out var resolvedTargets,
                    out var usedStorageCompatibilityAttachment,
                    out var unsupportedStorageFeedbackAddress))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped storage render-target feedback loop " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"target=0x{unsupportedStorageFeedbackAddress:X16}; " +
                    "nonzero color writes or aliased MRT storage require pass splitting");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            if (usedStorageCompatibilityAttachment)
            {
                TraceVulkanShader(
                    $"vk.storage_feedback_compat " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"storage=0x{work.Targets[0].Address:X16} " +
                    $"size={work.Targets[0].Width}x{work.Targets[0].Height}");
                work = work with
                {
                    Targets = resolvedTargets,
                    PublishTarget = false,
                };
            }

            var targets = new GuestImageResource[work.Targets.Count];
            EnsureGuestSubmissionCapacity();
            for (var index = 0; index < targets.Length; index++)
            {
                var targetDescriptor = work.Targets[index].Address == 0 &&
                    work.DepthTarget is { } depthOnlyTarget
                        ? GetDepthOnlyColorTarget(depthOnlyTarget)
                        : work.Targets[index];
                targets[index] = GetOrCreateGuestImage(targetDescriptor, formats[index]);
                // A view-compatible alias accept can return an image whose
                // identity differs from the request (sRGB vs UNORM
                // counterpart). The render pass, framebuffer views, and
                // pipeline cache key must all follow the format that actually
                // backs the attachment; a pipeline keyed on the requested
                // format could later be replayed inside a render pass of the
                // other identity.
                formats[index] = targets[index].Format;

                // Guest colour attachments load their previous contents on
                // every pass after the first, so nothing resets one until the
                // guest clears it. Consume a pending clear here, before the
                // render pass is built, so the pass uses LoadOp.Clear.
                if (work.Targets[index].Address != 0 &&
                    _pendingGuestColorClears.TryRemove(work.Targets[index].Address, out _))
                {
                    targets[index].Initialized = false;
                }

                // CMASK meta-state: if the surface's metadata says "all clear",
                // start this pass from LoadOp.Clear and consume the state.
                // CPU-backed targets are skipped (their guest memory contents
                // are uploaded, not cleared) — same rule the flip-arm used.
                if (work.Targets[index].Address != 0 &&
                    !targets[index].IsCpuBacked &&
                    Agc.AgcExports.IsMetaClearedForSurface(work.Targets[index].Address))
                {
                    targets[index].Initialized = false;
                    Agc.AgcExports.ConsumeMetaClear(work.Targets[index].Address);
                }

                if (work.Targets[index].Address != 0 &&
                    TakeGuestImageInitialData(work.Targets[index].Address) is { } initialData &&
                    !targets[index].Initialized &&
                    (ulong)initialData.Length ==
                        GetTextureByteCount(
                            targetDescriptor.Format,
                            targets[index].Width,
                            targets[index].Height))
                {
                    UploadGuestImageInitialData(targets[index], initialData);
                }
            }

            var firstTarget = targets[0];
            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            RenderPass transientRenderPass = default;
            Framebuffer transientFramebuffer = default;
            try
            {
                Span<Extent2D> colorAttachmentExtents = stackalloc Extent2D[targets.Length];
                for (var index = 0; index < targets.Length; index++)
                {
                    colorAttachmentExtents[index] = new Extent2D(
                        targets[index].Width,
                        targets[index].Height);
                }
                var extent = VulkanFramebufferExtentResolver.Resolve(colorAttachmentExtents);
                var depthClearMode = GuestDepthClearMode.Resolve(
                    draw.RenderState.Depth,
                    work.DepthTarget);
                var clearDepthForDraw = depthClearMode.ClearAttachment;
                if (work.DepthTarget?.ReadOnly == true && draw.RenderState.Depth.WriteEnable)
                {
                    draw = draw with
                    {
                        RenderState = draw.RenderState with
                        {
                            Depth = draw.RenderState.Depth with { WriteEnable = false },
                        },
                    };
                }
                GuestDepthResource? depth = null;
                DepthFramebufferResource? depthFramebuffer = null;
                var clearDepthSeparately = false;
                if (ShouldAttachGuestDepth(
                        work.DepthTarget,
                        draw.RenderState.Depth) &&
                    work.DepthTarget is { } depthTarget)
                {
                    // Logical dims: GetOrCreateGuestDepth below scales itself.
                    var resolution = GuestDepthExtentResolver.Resolve(
                        depthTarget,
                        firstTarget.LogicalWidth,
                        firstTarget.LogicalHeight,
                        draw.Textures);
                    var effectiveDepthTarget = resolution.IsUsable &&
                        (resolution.Width != depthTarget.Width ||
                         resolution.Height != depthTarget.Height)
                            ? depthTarget with
                            {
                                Width = resolution.Width,
                                Height = resolution.Height,
                            }
                            : depthTarget;

                    depth = GetOrCreateGuestDepth(effectiveDepthTarget);
                    PrepareFirstUseDepth(depth, draw.RenderState.Depth);
                    if (clearDepthForDraw)
                    {
                        depth.GuestClearDepth = effectiveDepthTarget.ClearDepth;
                        depth.ClearDepth = effectiveDepthTarget.ClearDepth;
                    }
                    clearDepthSeparately = clearDepthForDraw &&
                        (depth.Width < firstTarget.Width ||
                         depth.Height < firstTarget.Height);
                    if (targets.Length == 1 && !clearDepthSeparately)
                    {
                        depthFramebuffer = GetOrCreateDepthFramebuffer(firstTarget, depth);
                    }
                }

                if (depth is not null && !clearDepthSeparately)
                {
                    // Guest color images may be allocated at their maximum
                    // resolution while the active viewport and DB surface use
                    // a smaller dynamic-rendering extent. Vulkan requires the
                    // framebuffer extent to fit every attachment.
                    extent = VulkanFramebufferExtentResolver.Resolve(
                        colorAttachmentExtents,
                        new Extent2D(depth.Width, depth.Height));
                }

                if (depthClearMode.SuppressDrawDepthState)
                {
                    // DB_RENDER_CONTROL.DEPTH_CLEAR_ENABLE makes this a DB
                    // clear operation. The draw still produces color, but its
                    // interpolated vertex Z is not the guest clear value.
                    draw = draw with
                    {
                        RenderState = draw.RenderState with
                        {
                            Depth = draw.RenderState.Depth with
                            {
                                TestEnable = false,
                                WriteEnable = false,
                                ClearEnable = false,
                            },
                        },
                    };
                }

                var hasAttachedDepth = depth is not null && !clearDepthSeparately;
                var hasCompatibleDepthSample = depth is not null &&
                    draw.Textures.Any(texture =>
                        IsMatchingGuestDepthTexture(texture, depth));
                var directReadOnlyDepthFeedback =
                    VulkanReadOnlyDepthFeedbackPolicy.CanUse(
                        _directReadOnlyDepthFeedback,
                        hasAttachedDepth,
                        depth?.Initialized == true,
                        depth?.Layout is ImageLayout.ShaderReadOnlyOptimal or
                            ImageLayout.DepthStencilAttachmentOptimal or
                            ImageLayout.DepthStencilReadOnlyOptimal,
                        draw.RenderState.Depth.TestEnable,
                        draw.RenderState.Depth.WriteEnable,
                        clearDepthForDraw,
                        targets.Length,
                        hasCompatibleDepthSample);

                RenderPass renderPass;
                Framebuffer framebuffer;
                if (directReadOnlyDepthFeedback)
                {
                    renderPass = firstTarget.Initialized
                        ? depthFramebuffer!.ReadOnlyLoadRenderPass
                        : depthFramebuffer!.ReadOnlyColorClearRenderPass;
                    framebuffer = depthFramebuffer.ReadOnlyFramebuffer;
                }
                else
                {
                    renderPass = depthFramebuffer is null
                        ? firstTarget.Initialized
                            ? firstTarget.RenderPass
                            : firstTarget.InitialRenderPass
                            : firstTarget.Initialized
                            ? depth!.Initialized && !clearDepthForDraw
                                ? depthFramebuffer.LoadRenderPass
                                : depthFramebuffer.DepthClearRenderPass
                            : depth!.Initialized && !clearDepthForDraw
                                ? depthFramebuffer.ColorClearRenderPass
                                : depthFramebuffer.BothClearRenderPass;
                    framebuffer = depthFramebuffer?.Framebuffer ?? firstTarget.Framebuffer;
                    if (targets.Length > 1)
                    {
                        var attachedDepth = clearDepthSeparately ? null : depth;
                        (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                            formats,
                            targets.Select(target => target.MipViews.Length > 0
                                ? target.MipViews[0]
                                : target.View).ToArray(),
                            extent.Width,
                            extent.Height,
                            targets.Select(target =>
                                target.Initialized || target.InitialUploadPending).ToArray(),
                            attachedDepth,
                            attachedDepth?.Initialized == true && !clearDepthForDraw);
                        transientRenderPass = renderPass;
                        transientFramebuffer = framebuffer;
                    }
                }

                resources = CreateTranslatedDrawResources(
                    draw,
                    renderPass,
                    formats,
                    extent,
                    targets,
                    hasDepthAttachment: hasAttachedDepth,
                    feedbackDepth: directReadOnlyDepthFeedback || clearDepthSeparately
                        ? null
                        : depth,
                    directReadOnlyDepthFeedback: directReadOnlyDepthFeedback
                        ? depth
                        : null);
                if (directReadOnlyDepthFeedback)
                {
                    RecordDirectReadOnlyDepthFeedback();
                }
                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Length} " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"first=0x{work.Targets[0].Address:X16} " +
                    $"{firstTarget.Width}x{firstTarget.Height}";
                commandBuffer = BeginBatchedGuestCommands();
                _commandBuffer = commandBuffer;

                // Lifetime: recorded commands reference these resources, so
                // they join the batch before recording and are destroyed only
                // after the batch's fence signals.
                _batchResources.Add(resources);
                submitted = true;

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                if (clearDepthSeparately && depth is not null)
                {
                    CloseOpenTranslatedRenderPass();
                    RecordStandaloneGuestDepthClear(depth);
                }
                var hasStorageImages = false;
                foreach (var texture in resources.Textures)
                {
                    if (texture is null)
                    {
                        continue;
                    }

                    hasStorageImages |= texture.IsStorage;
                }

                var hasDepthAttachment = depth is not null && !clearDepthSeparately;
                var usesInitializedColorLoad =
                    firstTarget.Initialized &&
                    !firstTarget.InitialUploadPending;
                var usesInitializedLoadPass =
                    targets.Length == 1 &&
                    usesInitializedColorLoad &&
                    (!hasDepthAttachment
                        ? renderPass.Handle == firstTarget.RenderPass.Handle &&
                          framebuffer.Handle == firstTarget.Framebuffer.Handle
                        : depth is not null &&
                          depthFramebuffer is not null &&
                          depth.Initialized &&
                          !clearDepthForDraw &&
                          renderPass.Handle == depthFramebuffer.LoadRenderPass.Handle &&
                          framebuffer.Handle == depthFramebuffer.Framebuffer.Handle);
                var needsGlobalBufferBarrier =
                    NeedsGlobalBufferVisibilityBarrier(resources);
                var reuseHazards = GetRenderPassReuseHazards(
                    targets,
                    resources,
                    usesInitializedLoadPass,
                    needsGlobalBufferBarrier);
                var passKey = new VulkanRenderPassReuseKey(
                    firstTarget.Image.Handle,
                    hasDepthAttachment ? depth!.Image.Handle : 0,
                    renderPass.Handle,
                    framebuffer.Handle,
                    extent.Width,
                    extent.Height);
                var continueOpenPass =
                    !directReadOnlyDepthFeedback &&
                    _reuseTranslatedRenderPasses &&
                    _openPassKey is { } openPassKey &&
                    VulkanRenderPassReusePolicy.CanContinue(
                        openPassKey,
                        passKey,
                        reuseHazards);
                if (!continueOpenPass)
                {
                    CloseOpenTranslatedRenderPass();
                }
                RecordGlobalBufferVisibilityBarrier(
                    _commandBuffer,
                    resources,
                    PipelineStageFlags.VertexShaderBit |
                    PipelineStageFlags.FragmentShaderBit);
                RecordRenderTargetFeedbackSnapshots(
                    resources,
                    PipelineStageFlags.FragmentShaderBit);
                RecordDepthFeedbackSnapshots(
                    resources,
                    PipelineStageFlags.FragmentShaderBit);
                RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
                RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);

                if (!continueOpenPass)
                {
                    var toColorAttachments = stackalloc ImageMemoryBarrier[targets.Length];
                    var anyPriorContents = false;
                    for (var index = 0; index < targets.Length; index++)
                    {
                        var hasPriorContents =
                            targets[index].Initialized || targets[index].InitialUploadPending;
                        anyPriorContents |= hasPriorContents;
                        toColorAttachments[index] = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = hasPriorContents ? AccessFlags.ShaderReadBit : 0,
                            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
                            OldLayout = hasPriorContents
                                ? ImageLayout.ShaderReadOnlyOptimal
                                : ImageLayout.Undefined,
                            NewLayout = ImageLayout.ColorAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = targets[index].Image,
                            SubresourceRange = ColorSubresourceRange(),
                        };
                    }
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        anyPriorContents
                            ? PipelineStageFlags.AllCommandsBit
                            : PipelineStageFlags.TopOfPipeBit,
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        (uint)targets.Length,
                        toColorAttachments);

                    if (depth is not null &&
                        directReadOnlyDepthFeedback &&
                        depth.Layout != ImageLayout.DepthStencilReadOnlyOptimal)
                    {
                        var fromDepthAttachment =
                            depth.Layout == ImageLayout.DepthStencilAttachmentOptimal;
                        var toReadOnlyDepth = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = fromDepthAttachment
                                ? AccessFlags.DepthStencilAttachmentReadBit |
                                  AccessFlags.DepthStencilAttachmentWriteBit
                                : AccessFlags.ShaderReadBit,
                            DstAccessMask =
                                AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.ShaderReadBit,
                            OldLayout = depth.Layout,
                            NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = depth.Image,
                            SubresourceRange = new ImageSubresourceRange(
                                ImageAspectFlags.DepthBit, 0, 1, 0, 1),
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            fromDepthAttachment
                                ? PipelineStageFlags.EarlyFragmentTestsBit |
                                  PipelineStageFlags.LateFragmentTestsBit
                                : PipelineStageFlags.FragmentShaderBit |
                                  PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit |
                            PipelineStageFlags.FragmentShaderBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &toReadOnlyDepth);
                        depth.Layout = ImageLayout.DepthStencilReadOnlyOptimal;
                    }
                    else if (depth is not null &&
                        !clearDepthSeparately &&
                        (depth.Layout == ImageLayout.ShaderReadOnlyOptimal ||
                         depth.Layout == ImageLayout.DepthStencilReadOnlyOptimal))
                    {
                        var toDepthAttachment = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = depth.Layout ==
                                ImageLayout.DepthStencilReadOnlyOptimal
                                    ? AccessFlags.DepthStencilAttachmentReadBit |
                                      AccessFlags.ShaderReadBit
                                    : AccessFlags.ShaderReadBit,
                            DstAccessMask =
                                AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = depth.Layout,
                            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = depth.Image,
                            SubresourceRange = new ImageSubresourceRange(
                                ImageAspectFlags.DepthBit, 0, 1, 0, 1),
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            PipelineStageFlags.FragmentShaderBit |
                            PipelineStageFlags.ComputeShaderBit |
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit,
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &toDepthAttachment);
                    }

                    ClearColorValue[]? metaClearValues = null;
                    for (var colorIndex = 0; colorIndex < targets.Length; colorIndex++)
                    {
                        if (!targets[colorIndex].Initialized &&
                            work.Targets[colorIndex].Address != 0)
                        {
                            var (clearWord0, clearWord1) = Agc.AgcExports.GetMetaClearValue(
                                work.Targets[colorIndex].Address);
                            if (clearWord0 != 0 || clearWord1 != 0)
                            {
                                metaClearValues ??= new ClearColorValue[targets.Length];
                                metaClearValues[colorIndex] = UnpackMetaClearValue(
                                    work.Targets[colorIndex].Format,
                                    clearWord0,
                                    clearWord1);
                            }
                        }
                    }

                    BeginTranslatedRenderPass(
                        renderPass,
                        framebuffer,
                        extent,
                        colorAttachmentCount: targets.Length,
                        hasDepthAttachment: hasDepthAttachment,
                        clearDepth: depth?.ClearDepth ?? 1f,
                        colorClearValues: metaClearValues);
                }

                RecordTranslatedDrawInPass(resources, extent);
                MarkGlobalBufferShaderWrites(resources);
                var keepPassOpen =
                    !directReadOnlyDepthFeedback &&
                    _reuseTranslatedRenderPasses &&
                    VulkanRenderPassReusePolicy.CanKeepOpen(reuseHazards);
                if (keepPassOpen)
                {
                    _openPassTarget = firstTarget;
                    _openPassKey = passKey;
                }
                else
                {
                    _vk.CmdEndRenderPass(_commandBuffer);

                    var toShaderRead = stackalloc ImageMemoryBarrier[targets.Length];
                    for (var index = 0; index < targets.Length; index++)
                    {
                        toShaderRead[index] = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                            DstAccessMask = AccessFlags.ShaderReadBit,
                            OldLayout = ImageLayout.ColorAttachmentOptimal,
                            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = targets[index].Image,
                            SubresourceRange = ColorSubresourceRange(),
                        };
                    }
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        PipelineStageFlags.FragmentShaderBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        (uint)targets.Length,
                        toShaderRead);

                    if (hasStorageImages)
                    {
                        RecordStorageImagesForRead(
                            resources,
                            PipelineStageFlags.FragmentShaderBit);
                    }
                }

                RecordRenderPassReuseDecision(reuseHazards, continueOpenPass);

                EndDebugLabel(_commandBuffer);

                var traceImages = GetTraceImages(resources, targets, work.ShaderAddress);
                _batchTraceImages.AddRange(traceImages);
                if (++_batchDrawCount >= 64 ||
                    (_traceGuestImageShaderFilterEnabled && traceImages.Count != 0))
                {
                    FlushBatchedGuestCommands();
                }

                foreach (var target in targets)
                {
                    target.Initialized = true;
                    target.InitialUploadPending = false;
                }
                if (depth is not null)
                {
                    depth.Initialized = true;
                    if (!clearDepthSeparately)
                    {
                        depth.Layout = directReadOnlyDepthFeedback
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : ImageLayout.DepthStencilAttachmentOptimal;
                    }
                    if (clearDepthForDraw)
                    {
                        depth.InitializationSource = "guest-depth-clear";
                    }
                    else if (draw.RenderState.Depth.WriteEnable)
                    {
                        depth.InitializationSource = "translated-depth-write";
                    }
                }
                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);

                if (work.PublishTarget)
                {
                    for (var index = 0; index < targets.Length; index++)
                    {
                        var guestTextureFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                            work.Targets[index].Format,
                            work.Targets[index].NumberType);
                        if (guestTextureFormat == 0)
                        {
                            continue;
                        }

                        lock (_gate)
                        {
                            _availableGuestImages[targets[index].Address] = guestTextureFormat;
                        }
                    }
                }

                var tracePixelSpirv = false;
                if (_tracePixelSpirvBytes > 0 &&
                    _tracePixelSpirvBytes == work.Draw.PixelSpirv.Length)
                {
                    var pixelWriteCount = _pixelSpirvWriteCounts.TryGetValue(
                        _tracePixelSpirvBytes,
                        out var previousPixelWriteCount)
                            ? previousPixelWriteCount + 1
                            : 1;
                    _pixelSpirvWriteCounts[_tracePixelSpirvBytes] = pixelWriteCount;
                    tracePixelSpirv =
                        pixelWriteCount == _tracePixelSpirvOccurrence;
                }
                var traceTitleDraw =
                    !_tracedTitleDraw &&
                    _traceTitleDrawEnabled &&
                    IsTitleDraw(work.Draw.VertexBuffers);
                _tracedTitleDraw |= traceTitleDraw;

                foreach (var target in targets)
                {
                    var traceAddressWrite =
                        ShouldTraceGuestImageWriteForDiagnostics(target.Address);
                    var traceSmallWrites = _traceGuestWritesMode == "small" &&
                        target.Width <= 512 && target.Height <= 256;
                    var traceLargeWrites =
                        (_traceGuestWritesMode == "large" ||
                         _traceLargeGuestWriteOrdinal != 0) &&
                        target.Width >= 2560 && target.Height >= 1440;
                    if (traceAddressWrite || traceSmallWrites ||
                        traceLargeWrites || tracePixelSpirv || traceTitleDraw)
                    {
                        var writeCount = _tracedGuestWriteCounts.TryGetValue(
                            target.Address,
                            out var previousCount)
                            ? previousCount + 1
                            : 1;
                        _tracedGuestWriteCounts[target.Address] = writeCount;
                        var shouldTraceWrite = tracePixelSpirv || traceTitleDraw
                            ? true
                            : traceAddressWrite && _traceGuestWriteOrdinal > 0
                                ? writeCount == _traceGuestWriteOrdinal
                            : _traceLargeGuestWriteOrdinal != 0
                                ? writeCount == _traceLargeGuestWriteOrdinal
                            : writeCount <=
                                (traceLargeWrites ? 2 : traceSmallWrites ? 48 : 3);
                        if (traceAddressWrite || shouldTraceWrite)
                        {
                            var sampledTextures = string.Join(
                                ',',
                                work.Draw.Textures.Select(texture =>
                                    $"0x{texture.Address:X}:{texture.Width}x{texture.Height}:" +
                                    $"f{texture.Format}:n{texture.NumberType}:" +
                                    $"storage={(texture.IsStorage ? 1 : 0)}"));
                            var pixelDigest = Convert.ToHexString(
                                SHA256.HashData(work.Draw.PixelSpirv).AsSpan(0, 4));
                            Console.Error.WriteLine(
                                $"[LOADER][TRACE] vk.guest_write_sample " +
                                $"addr=0x{target.Address:X16} write={writeCount} " +
                                $"vs_bytes={work.Draw.VertexSpirv.Length} " +
                                $"ps_bytes={work.Draw.PixelSpirv.Length} ps_hash={pixelDigest} " +
                                $"vertices={work.Draw.VertexCount} instances={work.Draw.InstanceCount} " +
                                $"primitive=0x{work.Draw.PrimitiveType:X} " +
                                $"readback={(shouldTraceWrite ? 1 : 0)} textures=[{sampledTextures}]");
                        }

                        if (shouldTraceWrite)
                        {
                            _commandBuffer = _presentationCommandBuffer;
                            FlushBatchedGuestCommands();
                            Check(
                                _vk.QueueWaitIdle(_queue),
                                "vkQueueWaitIdle(guest write trace)");
                            TraceGuestImageContents(target);
                        }
                    }
                }
                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.offscreen_draw mrt={targets.Length} " +
                        $"size={firstTarget.Width}x{firstTarget.Height} " +
                        $"textures={work.Draw.Textures.Count}");
                }
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan device lost during offscreen " +
                        $"vs=0x{work.ShaderAddress:X16} " +
                        $"mrt={work.Targets.Count} " +
                        $"textures={work.Draw.Textures.Count} " +
                        $"vertices={work.Draw.VertexCount}");
                    return;
                }

                lock (_gate)
                {
                    foreach (var target in work.Targets)
                    {
                        if (!_guestImages.TryGetValue(target.Address, out var failedTarget) ||
                            !failedTarget.Initialized)
                        {
                            _availableGuestImages.Remove(target.Address);
                        }
                    }
                }

                // Non-Vulkan failures here are emulator bugs rather than device
                // state, and the message alone ("overflow", "index out of range")
                // names neither the guest draw nor the code that rejected it.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count} vs=0x{work.ShaderAddress:X16} " +
                    $"size={work.Targets[0].Width}x{work.Targets[0].Height} " +
                    $"format={work.Targets[0].Format}/{work.Targets[0].NumberType} " +
                    $"textures={work.Draw.Textures.Count} " +
                    $"vertices={work.Draw.VertexCount}: {exception}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                // The command buffer is the shared batch; it is submitted and
                // freed by FlushBatchedGuestCommands. Resources joined the
                // batch list before recording, so only pre-recording failures
                // (submitted still false) own their cleanup here.
                if (!submitted && resources is not null)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                if (transientFramebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, transientFramebuffer, null);
                }

                if (transientRenderPass.Handle != 0)
                {
                    _vk.DestroyRenderPass(_device, transientRenderPass, null);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExecuteOffscreenColorClear(VulkanOffscreenColorClear work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(
                        target.Format,
                        target.NumberType,
                        target.ComponentSwap,
                        out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped color clear for unsupported target " +
                        $"0x{target.Address:X16} format={target.Format} number_type={target.NumberType}.");
                    return;
                }
            }

            EnsureGuestSubmissionCapacity();
            var commandBuffer = BeginBatchedGuestCommands();
            CloseOpenTranslatedRenderPass();
            var logicalClear = stackalloc float[4]
            {
                work.Red,
                work.Green,
                work.Blue,
                work.Alpha,
            };

            for (var index = 0; index < work.Targets.Count; index++)
            {
                var targetDescriptor = work.Targets[index];
                var exportMapping = targetFormats[index].ExportMapping;
                var clearValue = new ClearColorValue(
                    logicalClear[exportMapping.Map(0)],
                    logicalClear[exportMapping.Map(1)],
                    logicalClear[exportMapping.Map(2)],
                    logicalClear[exportMapping.Map(3)]);
                var image = GetOrCreateGuestImage(
                    targetDescriptor,
                    targetFormats[index].Format);
                if (TakeGuestImageInitialData(targetDescriptor.Address) is { } initialData &&
                    !image.Initialized &&
                    (ulong)initialData.Length ==
                        (ulong)image.Width * image.Height * 4)
                {
                    UploadGuestImageInitialData(image, initialData);
                }

                var toTransferDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = image.Initialized ? AccessFlags.ShaderReadBit : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = image.Initialized
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(0, image.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    image.Initialized
                        ? PipelineStageFlags.FragmentShaderBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransferDst);

                var range = ColorSubresourceRange(0, image.MipLevels);
                _vk.CmdClearColorImage(
                    commandBuffer,
                    image.Image,
                    ImageLayout.TransferDstOptimal,
                    &clearValue,
                    1,
                    &range);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(0, image.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);
                image.Initialized = true;

                var guestTextureFormat = GetGuestTextureFormat(
                    targetDescriptor.Format,
                    targetDescriptor.NumberType);
                if (guestTextureFormat != 0)
                {
                    lock (_gate)
                    {
                        _availableGuestImages[image.Address] = guestTextureFormat;
                    }
                }
            }

            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.offscreen_color_clear mrt={work.Targets.Count} " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"rgba=({work.Red:0.###},{work.Green:0.###},{work.Blue:0.###},{work.Alpha:0.###})");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExecuteGuestImageWrite(VulkanGuestImageWrite work)
        {
            if (_deviceLost || !_guestImages.TryGetValue(work.Address, out var target))
            {
                return;
            }

            if (work.Pixels is { } pixels)
            {
                if (pixels.Length > 0)
                {
                    UploadGuestImageInitialData(target, pixels, work.RowOffset);
                }

                return;
            }

            // Recorded into the shared batch command buffer: recording order
            // preserves queue-order semantics against earlier batched draws,
            // and the fill no longer costs a submit + full queue drain.
            var commandBuffer = BeginBatchedGuestCommands();
            CloseOpenTranslatedRenderPass();
            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = target.Initialized
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                target.Initialized
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransferDst);

            var clearValue = new ClearColorValue(
                (work.FillValue & 0xFF) / 255f,
                ((work.FillValue >> 8) & 0xFF) / 255f,
                ((work.FillValue >> 16) & 0xFF) / 255f,
                ((work.FillValue >> 24) & 0xFF) / 255f);
            var range = ColorSubresourceRange(0, target.MipLevels);
            _vk.CmdClearColorImage(
                commandBuffer,
                target.Image,
                ImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &range);

            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
            target.Initialized = true;
        }

        // Returns the source row length in texels when the upload is a linear
        // image whose rows are padded to a wider hardware pitch, or 0 when the
        // data is an exact tightly packed match or not a recognisable padded
        // layout (in which case the caller keeps rejecting it). Only a pitch
        // equal to the width rounded up to a common alignment is accepted, so an
        // oversized buffer that still carries mip data is left rejected rather
        // than mis-copied.
        private static uint TryGetPaddedUploadRowLength(
            GuestImageResource target,
            ulong uploadByteCount,
            ulong expectedByteCount)
        {
            if (expectedByteCount == 0
                || target.Width == 0
                || target.Height == 0
                || uploadByteCount <= expectedByteCount)
            {
                return 0;
            }

            var rowsPerVolume = checked((ulong)target.Height * target.Depth);
            var texelCount = checked((ulong)target.Width * rowsPerVolume);
            if (texelCount == 0 || expectedByteCount % texelCount != 0)
            {
                // Block-compressed or otherwise non-linear: no per-texel pitch.
                return 0;
            }

            var bytesPerTexel = expectedByteCount / texelCount;
            if (bytesPerTexel == 0 || uploadByteCount % rowsPerVolume != 0)
            {
                return 0;
            }

            var rowBytes = uploadByteCount / rowsPerVolume;
            if (rowBytes % bytesPerTexel != 0)
            {
                return 0;
            }

            var rowTexels = rowBytes / bytesPerTexel;
            if (rowTexels < target.Width || rowTexels > uint.MaxValue)
            {
                return 0;
            }

            foreach (var alignment in (ReadOnlySpan<uint>)[8, 16, 32, 64, 128, 256])
            {
                if (AlignUp(target.Width, alignment) == rowTexels)
                {
                    return (uint)rowTexels;
                }
            }

            return 0;
        }

        private static uint AlignUp(uint value, uint alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private (RenderPass RenderPass, RenderPass InitialRenderPass, Framebuffer Framebuffer)
            CreateRenderPassAndFramebuffer(
            Format format,
            ImageView attachmentView,
            uint width,
            uint height)
        {
            var load = CreateRenderPassAndFramebuffer(
                [format], [attachmentView], width, height, [true], null, false);
            var initial = CreateRenderPassAndFramebuffer(
                [format], [attachmentView], width, height, [false], null, false);
            _vk.DestroyFramebuffer(_device, initial.Framebuffer, null);
            return (load.RenderPass, initial.RenderPass, load.Framebuffer);
        }

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height) =>
            CreateRenderPassAndFramebuffer(
                formats,
                attachmentViews,
                width,
                height,
                Enumerable.Repeat(true, formats.Count).ToArray(),
                null,
                false);

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height,
            IReadOnlyList<bool> initialized,
            GuestDepthResource? depth,
            bool depthInitialized)
        {
            if (formats.Count == 0 ||
                formats.Count != attachmentViews.Count ||
                formats.Count != initialized.Count)
            {
                throw new InvalidOperationException(
                    "render target formats, views, and initialization states must have matching counts");
            }

            var attachmentCount = formats.Count + (depth is null ? 0 : 1);
            var attachments = stackalloc AttachmentDescription[attachmentCount];
            var colorReferences = stackalloc AttachmentReference[formats.Count];
            var views = stackalloc ImageView[attachmentCount];
            for (var index = 0; index < formats.Count; index++)
            {
                attachments[index] = new AttachmentDescription
                {
                    Format = formats[index],
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = initialized[index]
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.ColorAttachmentOptimal,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                };
                colorReferences[index] = new AttachmentReference
                {
                    Attachment = (uint)index,
                    Layout = ImageLayout.ColorAttachmentOptimal,
                };
                views[index] = attachmentViews[index];
            }

            AttachmentReference depthReference = default;
            if (depth is not null)
            {
                attachments[formats.Count] = new AttachmentDescription
                {
                    Format = DepthFormat,
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = depthInitialized
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = depthInitialized
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.Undefined,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                depthReference = new AttachmentReference
                {
                    Attachment = (uint)formats.Count,
                    Layout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                views[formats.Count] = depth.View;
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)formats.Count,
                PColorAttachments = colorReferences,
                PDepthStencilAttachment = depth is null ? null : &depthReference,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen)");

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = views,
                Width = width,
                Height = height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen)");

            return (renderPass, framebuffer);
        }
    }
}
