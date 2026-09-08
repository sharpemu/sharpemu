// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial executes offscreen guest rendering commands through the image store.

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

        // Shader images, then targets, then depth: the binding state decides each attachment layout.
        private void ExecuteOffscreenDrawCore(VulkanOffscreenGuestDraw work)
        {
            var draw = work.Draw;
            if (draw.RenderState.Blends.Count != work.Targets.Count)
            {
                Console.Error.WriteLine("[LOADER][WARN] Vulkan skipped MRT draw with mismatched attachment/blend counts.");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            EnsureGuestSubmissionCapacity();
            TranslatedDrawResources? resources = null;
            var submitted = false;
            RenderPass transientRenderPass = default;
            Framebuffer transientFramebuffer = default;
            try
            {
                var textures = new TextureResource[draw.Textures.Count];
                var hostMovieTextures = FindHostMovieTextureBindings(draw.Textures);
                for (var index = 0; index < textures.Length; index++)
                {
                    textures[index] = index == hostMovieTextures.Luma
                        ? CreateHostMovieTextureResource(draw.Textures[index], plane: 0)
                        : index == hostMovieTextures.Chroma
                            ? CreateHostMovieTextureResource(draw.Textures[index], plane: 1)
                            : ResolveTexture(draw.Textures[index]);
                }

                var targets = new List<ColorAttachment>(work.Targets.Count);
                var blends = new List<GuestBlendState>(work.Targets.Count);
                for (var index = 0; index < work.Targets.Count; index++)
                {
                    if (DiscoverColorTarget(work.Targets[index], ignoreTargetMask: false, exactFormat: false) is { } target)
                    {
                        targets.Add(target);
                        blends.Add(draw.RenderState.Blends[index]);
                    }
                }

                if (targets.Count > _maxColorAttachments)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped MRT draw requesting {targets.Count} color attachments; " +
                        $"the selected device supports {_maxColorAttachments}.");
                    ReturnPooledGuestData(work.Draw);
                    return;
                }

                var depth = work.DepthTarget is { } depthTarget ? DiscoverDepthTarget(depthTarget) : null;
                if (targets.Count == 0 && depth is null)
                {
                    ReturnPooledGuestData(work.Draw);
                    return;
                }

                var depthClearMode = GuestDepthClearMode.Resolve(draw.RenderState.Depth, work.DepthTarget);
                var depthState = draw.RenderState.Depth;
                if (work.DepthTarget?.ReadOnly == true && depthState.WriteEnable)
                {
                    depthState = depthState with { WriteEnable = false };
                }

                if (work.DepthTarget?.StencilReadOnly == true && depthState.StencilTestEnable)
                {
                    depthState = depthState with
                    {
                        StencilFront = depthState.StencilFront with { WriteMask = 0 },
                        StencilBack = depthState.StencilBack with { WriteMask = 0 },
                    };
                }

                if (depthClearMode.SuppressDrawDepthState)
                {
                    // A DB clear draw still produces color; its interpolated Z is not the clear value.
                    depthState = depthState with { TestEnable = false, WriteEnable = false, ClearEnable = false };
                }

                if (depthClearMode.SuppressDrawStencilState)
                {
                    depthState = depthState with { StencilTestEnable = false, StencilClearEnable = false };
                }

                foreach (var target in targets)
                {
                    AcquireColorAttachment(target);
                }

                if (depth is not null)
                {
                    AcquireDepthAttachment(depth, depthState);
                    if (targets.Count != 0 && !depthState.RequiresDrawAttachment)
                    {
                        RecordSeparateDepthClear(depth);
                        depth = null;
                    }
                }

                AcquireTextureViews(textures, draw.Textures);
                RecordDrawTextureTransitions(textures, depth, depthState);

                var width = uint.MaxValue;
                var height = uint.MaxValue;
                uint samples = 0;
                foreach (var target in targets)
                {
                    width = Math.Min(width, target.Resolution.Extent.Width);
                    height = Math.Min(height, target.Resolution.Extent.Height);
                    samples = RequireSameSampleCount(samples, target.Resolution.Samples, "color");
                }

                if (depth is not null)
                {
                    width = Math.Min(width, depth.Resolution.Width);
                    height = Math.Min(height, depth.Resolution.Height);
                    samples = RequireSameSampleCount(samples, depth.Resolution.Samples, "depth");
                }

                var extent = new Extent2D(width, height);
                var layers = uint.MaxValue;
                foreach (var target in targets)
                {
                    layers = Math.Min(layers, target.Request.View.LayerCount);
                }

                if (depth is not null)
                {
                    layers = Math.Min(layers, depth.Request.View.LayerCount);
                }

                var formats = new Format[targets.Count];
                var integerTargets = new bool[targets.Count];
                for (var index = 0; index < targets.Count; index++)
                {
                    formats[index] = targets[index].Format;
                    integerTargets[index] = IsIntegerFormat(formats[index]);
                }

                var normalizedBlends = GuestBlendStateNormalizer.NormalizeIntegerAttachments(blends, integerTargets, out _);
                if (!_supportsIndependentBlend)
                {
                    for (var index = 1; index < normalizedBlends.Length; index++)
                    {
                        if (normalizedBlends[index] != normalizedBlends[0])
                        {
                            Console.Error.WriteLine("[LOADER][WARN] Vulkan skipped MRT draw requiring unsupported independentBlend.");
                            ReturnPooledGuestData(work.Draw);
                            return;
                        }
                    }
                }

                draw = draw with
                {
                    RenderState = draw.RenderState with { Blends = normalizedBlends, Depth = depthState },
                };

                var needsGlobalBufferBarrier = _globalBufferBarrierTracker.ShouldRecordBarrier(_elideRedundantGlobalBufferBarriers) &&
                    draw.GlobalMemoryBuffers.Count != 0;
                var anyClear = depth is { ClearDepth: true } or { ClearStencil: true };
                foreach (var target in targets)
                {
                    anyClear |= target.Clear;
                }

                var hazards = GetRenderPassReuseHazards(targets, textures, anyClear, needsGlobalBufferBarrier);
                var candidate = CreateRenderPassReuseCandidate(targets, depth, width, height, layers);
                var continueOpenPass =
                    _reuseTranslatedRenderPasses &&
                    _openPassActive &&
                    _openPassKey is { } openPassKey &&
                    VulkanRenderPassReusePolicy.CanContinue(openPassKey, candidate, hazards);
                RenderPass renderPass;
                Framebuffer framebuffer;
                if (continueOpenPass)
                {
                    renderPass = new RenderPass(_openPassKey!.Value.RenderPass);
                    framebuffer = new Framebuffer(_openPassKey.Value.Framebuffer);
                }
                else
                {
                    CloseOpenTranslatedRenderPass();
                    (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(targets, depth, extent, samples, layers);
                    transientRenderPass = renderPass;
                    transientFramebuffer = framebuffer;
                }

                resources = CreateTranslatedDrawResources(
                    draw,
                    renderPass,
                    formats,
                    extent,
                    textures,
                    hasDepthAttachment: depth is not null,
                    depthAttachmentFormat: depth?.Format ?? Format.Undefined,
                    samples: ImageDescription.VulkanSampleCount(samples),
                    targetAddresses: targets.Select(static target => target.Address).ToArray());
                if (continueOpenPass && !_openPassActive)
                {
                    // Resource creation ended the tick and closed the pass; this draw needs its own pass.
                    continueOpenPass = false;
                    (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(targets, depth, extent, samples, layers);
                    transientRenderPass = renderPass;
                    transientFramebuffer = framebuffer;
                }

                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Count} ps=0x{work.ShaderAddress:X16} " +
                    $"first=0x{(targets.Count > 0 ? targets[0].Address : depth!.Resolution.DepthAddress):X16} {width}x{height}";

                // Resource creation can end the tick; record into the buffer that is current now.
                var commandBuffer = BeginBatchedGuestCommands();
                _commandBuffer = commandBuffer;

                // Lifetime: recorded commands reference these resources, so
                // they join the batch before recording and are destroyed only
                // after the batch's fence signals.
                _batchResources.Add(resources);
                submitted = true;

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                if (!continueOpenPass)
                {
                    RecordGlobalBufferVisibilityBarrier(
                        _commandBuffer,
                        resources,
                        PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit);
                    RecordHostMovieUploads(textures, PipelineStageFlags.FragmentShaderBit);
                    var colorClearValues = new ClearColorValue[targets.Count];
                    for (var index = 0; index < targets.Count; index++)
                    {
                        colorClearValues[index] = targets[index].ClearValue;
                    }

                    BeginTranslatedRenderPass(
                        renderPass,
                        framebuffer,
                        extent,
                        colorAttachmentCount: targets.Count,
                        hasDepthAttachment: depth is not null,
                        clearDepth: depth?.ClearDepthValue ?? 1f,
                        clearStencil: depth?.ClearStencilValue ?? 0,
                        colorClearValues: colorClearValues);
                }

                RecordTranslatedDrawInPass(resources, extent);
                MarkGlobalBufferShaderWrites(resources);
                if (_reuseTranslatedRenderPasses && VulkanRenderPassReusePolicy.CanKeepOpen(hazards))
                {
                    _openPassActive = true;
                    _openPassKey = candidate with { RenderPass = renderPass.Handle, Framebuffer = framebuffer.Handle };
                }
                else
                {
                    _vk.CmdEndRenderPass(_commandBuffer);
                }

                RecordRenderPassReuseDecision(hazards, continueOpenPass);
                EndDebugLabel(_commandBuffer);
                if (++_batchDrawCount >= 64)
                {
                    FlushBatchedGuestCommands();
                }

                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.offscreen_draw mrt={targets.Count} size={width}x{height} textures={work.Draw.Textures.Count}");
                }
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan device lost during offscreen " +
                        $"vs=0x{work.ShaderAddress:X16} mrt={work.Targets.Count} " +
                        $"textures={work.Draw.Textures.Count} vertices={work.Draw.VertexCount}");
                    return;
                }

                // Non-Vulkan failures here are emulator bugs rather than device
                // state, and the message alone ("overflow", "index out of range")
                // names neither the guest draw nor the code that rejected it.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count} vs=0x{work.ShaderAddress:X16} " +
                    DescribeOffscreenTarget(work.Targets, work.DepthTarget) +
                    $"textures={work.Draw.Textures.Count} vertices={work.Draw.VertexCount}: {exception}");
            }
            finally
            {
                _commandBuffer = default;
                ResetImageBindings();
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

        private void RecordSeparateDepthClear(DepthAttachment depth)
        {
            var aspects = (depth.ClearDepth ? ImageAspectFlags.DepthBit : 0) |
                (depth.ClearStencil ? ImageAspectFlags.StencilBit : 0);
            aspects &= ViewFormatRules.DepthAspects(depth.Format);
            if (aspects == 0)
                return;

            // Clear the depth view without restricting the color draw's extent.
            CloseOpenTranslatedRenderPass();
            var command = BeginBatchedGuestCommands();
            var view = depth.Request.View;
            depth.Image!.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit,
                new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount), command);
            var range = new ImageSubresourceRange(aspects, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
            var value = new ClearDepthStencilValue(depth.ClearDepthValue, depth.ClearStencilValue);
            _vk.CmdClearDepthStencilImage(command, depth.Image.Backing.Handle, ImageLayout.TransferDstOptimal, &value, 1, &range);
        }

        private static string DescribeOffscreenTarget(IReadOnlyList<GuestRenderTarget> targets, GuestDepthTarget? depth) =>
            targets.Count != 0
                ? $"size={targets[0].Width}x{targets[0].Height} format={targets[0].Format}/{targets[0].NumberType} "
                : depth is not null ? $"size={depth.Width}x{depth.Height} depth_only=true " : "targets=none ";

        private static uint RequireSameSampleCount(uint current, uint candidate, string attachment) =>
            current == 0 || current == candidate
                ? candidate
                : throw SubmissionScheduler.Fatal($"The {attachment} attachment sample count differs from the pass: pass={current} attachment={candidate}.");

        private static bool IsIntegerFormat(Format format) =>
            format.ToString().EndsWith("Uint", StringComparison.Ordinal) || format.ToString().EndsWith("Sint", StringComparison.Ordinal);

        // One render pass and framebuffer for the acquired attachments; the pass keeps every acquired layout.
        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<ColorAttachment> targets,
            DepthAttachment? depth,
            Extent2D extent,
            uint samples,
            uint layers)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.RenderPassSetup);
            var attachmentCount = targets.Count + (depth is null ? 0 : 1);
            var attachments = stackalloc AttachmentDescription[Math.Max(attachmentCount, 1)];
            var colorReferences = stackalloc AttachmentReference[Math.Max(targets.Count, 1)];
            var views = stackalloc ImageView[Math.Max(attachmentCount, 1)];
            var sampleCount = ImageDescription.VulkanSampleCount(samples);
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                attachments[index] = new AttachmentDescription
                {
                    Format = target.Format,
                    Samples = sampleCount,
                    LoadOp = target.Clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = target.Layout,
                    FinalLayout = target.Layout,
                };
                colorReferences[index] = new AttachmentReference { Attachment = (uint)index, Layout = target.Layout };
                views[index] = target.View;
            }

            AttachmentReference depthReference = default;
            if (depth is not null)
            {
                var hasStencil = (ViewFormatRules.DepthAspects(depth.Format) & ImageAspectFlags.StencilBit) != 0;
                attachments[targets.Count] = new AttachmentDescription
                {
                    Format = depth.Format,
                    Samples = sampleCount,
                    LoadOp = depth.ClearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = hasStencil ? depth.ClearStencil ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load : AttachmentLoadOp.DontCare,
                    StencilStoreOp = hasStencil ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare,
                    InitialLayout = depth.Layout,
                    FinalLayout = depth.Layout,
                };
                depthReference = new AttachmentReference { Attachment = (uint)targets.Count, Layout = depth.Layout };
                views[targets.Count] = depth.View;
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)targets.Count,
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
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass), "vkCreateRenderPass(offscreen)");

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = views,
                Width = extent.Width,
                Height = extent.Height,
                Layers = layers,
            };
            Check(_vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer), "vkCreateFramebuffer(offscreen)");
            return (renderPass, framebuffer);
        }
    }
}
