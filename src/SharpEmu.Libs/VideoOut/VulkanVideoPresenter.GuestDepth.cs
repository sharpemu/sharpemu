// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns guest depth targets and depth-as-texture resources.

    internal static bool IsCompatibleGuestDepthTextureDescriptor(
        GuestDrawTexture texture,
        uint depthWidth,
        uint depthHeight,
        uint depthGuestFormat)
    {
        var expectedFormat = depthGuestFormat switch
        {
            1 => Format.R16Unorm,
            3 => Format.R32Sfloat,
            _ => Format.Undefined,
        };

        return expectedFormat != Format.Undefined &&
            Presenter.GetTextureFormat(texture.Format, texture.NumberType) == expectedFormat &&
            !texture.IsFallback &&
            !texture.IsStorage &&
            texture.Width == depthWidth &&
            texture.Height == depthHeight &&
            texture.Type == Gen5TextureType2D &&
            texture.Depth == 1 &&
            texture.ArrayLayers == 1 &&
            texture.MipLevel == 0 &&
            texture.BaseMipLevel == 0 &&
            texture.MipLevels == 1 &&
            texture.ResourceMipLevels == 1 &&
            texture.TileMode == Gen5DepthTileMode &&
            texture.Pitch >= texture.Width;
    }

    internal static bool ShouldAttachGuestDepth(
        GuestDepthTarget? target,
        GuestDepthState state) =>
        target is not null &&
        (state.TestEnable || state.WriteEnable || state.ClearEnable);

    private sealed partial class Presenter
    {
        private readonly record struct GuestDepthKey(
            ulong Address,
            ulong ReadAddress,
            uint Width,
            uint Height,
            uint GuestFormat,
            uint SwizzleMode);

        private readonly Dictionary<GuestDepthKey, GuestDepthResource> _guestDepthImages = new();
        private readonly Dictionary<GuestDepthKey, ulong> _depthOnlyColorAddresses = new();
        private ulong _nextDepthOnlyColorAddress = 0xFFFF_FF00_0000_0000UL;
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint DstSelect)> _tracedDepthTextureAliases = new();
        private readonly HashSet<(
            ulong Address,
            uint Width,
            uint Height,
            uint Format,
            uint NumberType,
            uint TileMode)> _tracedDepthTextureAliasRejects = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height)> _tracedDepthExtentFallbacks = new();
        private const Format DepthFormat = Format.D32Sfloat;

        private sealed class GuestDepthResource
        {
            public GuestDepthKey Key;
            public ulong Address;
            public ulong ReadAddress;
            public ulong WriteAddress;
            public uint Width;
            public uint Height;
            // Unscaled guest-requested size; Width/Height are the physical (scaled) backing size.
            public uint LogicalWidth;
            public uint LogicalHeight;
            public uint GuestFormat;
            public uint SwizzleMode;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public Dictionary<(uint DstSelect, bool Arrayed), ImageView> SampleViews { get; } = new();
            public bool Initialized;
            public ImageLayout Layout = ImageLayout.Undefined;
            public float GuestClearDepth = 1f;
            public float ClearDepth = 1f;
            public string InitializationSource = "none";
        }

        private sealed class DepthFramebufferResource
        {
            public required GuestDepthResource Depth;
            public RenderPass LoadRenderPass;
            public RenderPass ColorClearRenderPass;
            public RenderPass DepthClearRenderPass;
            public RenderPass BothClearRenderPass;
            public Framebuffer Framebuffer;
            public RenderPass ReadOnlyLoadRenderPass;
            public RenderPass ReadOnlyColorClearRenderPass;
            public Framebuffer ReadOnlyFramebuffer;
        }

        private readonly record struct DepthFramebufferKey(
            GuestDepthKey Depth,
            Format ColorFormat,
            ulong ColorView);

        private bool TryResolveGuestDepthTexture(
            GuestDrawTexture texture,
            out TextureResource resource)
        {
            if (IsGuestTexture3D(texture.Type))
            {
                resource = null!;
                return false;
            }

            foreach (var depth in _guestDepthImages.Values)
            {
                if (texture.Address != depth.Address &&
                    texture.Address != depth.ReadAddress &&
                    texture.Address != depth.WriteAddress)
                {
                    continue;
                }

                if (!IsCompatibleGuestDepthTextureDescriptor(
                        texture,
                        depth.LogicalWidth,
                        depth.LogicalHeight,
                        depth.GuestFormat))
                {
                    if ((_traceGuestImageEvents || _traceVulkanShaderEnabled) &&
                        _tracedDepthTextureAliasRejects.Add((
                            texture.Address,
                            texture.Width,
                            texture.Height,
                            texture.Format,
                            texture.NumberType,
                            texture.TileMode)))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.depth_texture_alias_reject " +
                            $"addr=0x{texture.Address:X16} " +
                            $"texture={texture.Width}x{texture.Height} " +
                            $"fmt={texture.Format}/{texture.NumberType} " +
                            $"tile={texture.TileMode} pitch={texture.Pitch} " +
                            $"arrayed={texture.ArrayedView} array_layers={texture.ArrayLayers} " +
                            $"depth=0x{depth.Address:X16} " +
                            $"surface={depth.LogicalWidth}x{depth.LogicalHeight} " +
                            $"zfmt={depth.GuestFormat}");
                    }
                    continue;
                }

                var view = GetOrCreateGuestDepthSampleView(
                    depth,
                    texture.DstSelect,
                    texture.ArrayedView);

                if ((_traceGuestImageEvents || _traceVulkanShaderEnabled) &&
                    _tracedDepthTextureAliases.Add(
                        (texture.Address, texture.Width, texture.Height, texture.DstSelect)))
                {
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] " +
                        $"vk.depth_texture_alias addr=0x{texture.Address:X16} " +
                        $"depth=0x{depth.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"surface={depth.Width}x{depth.Height} " +
                        $"fmt={texture.Format}/{texture.NumberType} " +
                        $"tile={texture.TileMode} pitch={texture.Pitch} " +
                        $"dst=0x{texture.DstSelect:X3}");
                }
                resource = new TextureResource
                {
                    Address = texture.Address,
                    Image = depth.Image,
                    View = view,
                    Width = depth.Width,
                    Height = depth.Height,
                    RowLength = depth.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestDepth = depth,
                };
                return true;
            }

            resource = null!;
            return false;
        }

        private static bool IsMatchingGuestDepthTexture(
            GuestDrawTexture texture,
            GuestDepthResource depth) =>
            !texture.IsStorage &&
            (texture.Address == depth.Address ||
             texture.Address == depth.ReadAddress ||
             texture.Address == depth.WriteAddress) &&
            IsCompatibleGuestDepthTextureDescriptor(
                texture,
                depth.LogicalWidth,
                depth.LogicalHeight,
                depth.GuestFormat);

        private ImageView GetOrCreateGuestDepthSampleView(
            GuestDepthResource depth,
            uint dstSelect,
            bool arrayed)
        {
            var key = (dstSelect, arrayed);
            if (depth.SampleViews.TryGetValue(key, out var view))
            {
                return view;
            }

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depth.Image,
                ViewType = arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                Format = DepthFormat,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(depth sample)");
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest depth sample 0x{depth.Address:X16} " +
                $"dst=0x{dstSelect:X3}");
            depth.SampleViews.Add(key, view);
            return view;
        }

        private TextureResource CreateReadOnlyDepthFeedbackResource(
            GuestDrawTexture texture,
            GuestDepthResource source) =>
            new()
            {
                Address = texture.Address,
                Image = source.Image,
                View = GetOrCreateGuestDepthSampleView(
                    source,
                    texture.DstSelect,
                    texture.ArrayedView),
                Width = source.Width,
                Height = source.Height,
                RowLength = source.Width,
                DstSelect = texture.DstSelect,
                SamplerState = texture.Sampler,
                GuestDepth = source,
                ReadOnlyDepthFeedback = true,
            };

        private (Image Image, DeviceMemory Memory, ImageView View) CreateDepthAttachment(
            uint width,
            uint height)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = DepthFormat,
                Extent = new Extent3D(Math.Max(width, 1), Math.Max(height, 1), 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.DepthStencilAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(depth)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var memory), "vkAllocateMemory(depth)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(depth)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(depth)");
            return (image, memory, view);
        }

        private GuestDepthResource GetOrCreateGuestDepth(GuestDepthTarget target)
        {
            var key = new GuestDepthKey(
                target.Address,
                target.ReadAddress,
                target.Width,
                target.Height,
                target.GuestFormat,
                target.SwizzleMode);
            if (_guestDepthImages.TryGetValue(key, out var existing))
            {
                existing.GuestClearDepth = target.ClearDepth;
                if (!existing.Initialized && existing.InitializationSource == "none")
                {
                    existing.ClearDepth = target.ClearDepth;
                }
                return existing;
            }

            var physicalWidth = ScaleGuestDimension(target.Width);
            var physicalHeight = ScaleGuestDimension(target.Height);
            var (image, memory, view) = CreateDepthAttachment(physicalWidth, physicalHeight);
            var resource = new GuestDepthResource
            {
                Key = key,
                Address = target.Address,
                ReadAddress = target.ReadAddress,
                WriteAddress = target.WriteAddress,
                Width = physicalWidth,
                Height = physicalHeight,
                LogicalWidth = target.Width,
                LogicalHeight = target.Height,
                GuestFormat = target.GuestFormat,
                SwizzleMode = target.SwizzleMode,
                Image = image,
                Memory = memory,
                View = view,
                GuestClearDepth = target.ClearDepth,
                ClearDepth = target.ClearDepth,
            };
            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"SharpEmu guest depth 0x{target.Address:X16} {target.Width}x{target.Height}");
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest depth view 0x{target.Address:X16}");
            _guestDepthImages.Add(key, resource);
            if (_traceGuestImageEvents || _traceVulkanShaderEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-depth addr=0x{target.Address:X} " +
                    $"read=0x{target.ReadAddress:X} write=0x{target.WriteAddress:X} " +
                    $"{target.Width}x{target.Height} zfmt={target.GuestFormat} " +
                    $"sw={target.SwizzleMode} clear={target.ClearDepth:0.######}");
            }

            return resource;
        }

        private static void PrepareFirstUseDepth(
            GuestDepthResource depth,
            GuestDepthState state)
        {
            if (depth.Initialized || depth.InitializationSource != "none")
            {
                return;
            }

            var effectiveClear = depth.GuestClearDepth;
            var source = "guest-clear";
            if (state.TestEnable && !state.WriteEnable)
            {
                switch (state.CompareOp)
                {
                    case 1: // Less
                    case 3: // LessOrEqual
                        effectiveClear = 1f;
                        source = "neutral-first-use";
                        break;
                    case 4: // Greater
                    case 6: // GreaterOrEqual
                        effectiveClear = 0f;
                        source = "neutral-first-use";
                        break;
                    case 7: // Always
                        break;
                    default: // Never, Equal, NotEqual
                        source = "guest-clear-ambiguous";
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] Vulkan has no neutral first-use depth clear " +
                            $"addr=0x{depth.Address:X16} compare={state.CompareOp}; " +
                            $"using guest clear {depth.GuestClearDepth:0.######}.");
                        break;
                }
            }

            depth.ClearDepth = effectiveClear;
            depth.InitializationSource = source;
            if (_traceDepthInitialization)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.depth_init " +
                    $"addr=0x{depth.Address:X16} size={depth.Width}x{depth.Height} " +
                    $"source={source} guest_clear={depth.GuestClearDepth:0.######} " +
                    $"effective_clear={effectiveClear:0.######} compare={state.CompareOp} " +
                    $"test={(state.TestEnable ? 1 : 0)} write={(state.WriteEnable ? 1 : 0)} " +
                    $"initialized=0");
            }
        }

        private GuestRenderTarget GetDepthOnlyColorTarget(GuestDepthTarget depth)
        {
            var key = new GuestDepthKey(
                depth.Address,
                depth.ReadAddress,
                depth.Width,
                depth.Height,
                depth.GuestFormat,
                depth.SwizzleMode);
            if (!_depthOnlyColorAddresses.TryGetValue(key, out var address))
            {
                address = _nextDepthOnlyColorAddress;
                _nextDepthOnlyColorAddress = checked(_nextDepthOnlyColorAddress + 0x1000_0000UL);
                _depthOnlyColorAddresses.Add(key, address);
            }

            // The translated fragment module still declares a color output,
            // even for a guest depth-only pass.  A private, never-published
            // color attachment keeps that output legal while the persistent
            // guest DB surface remains the only observable result.
            return new GuestRenderTarget(
                address,
                depth.Width,
                depth.Height,
                Format: 10,
                NumberType: 0);
        }

        private DepthFramebufferResource GetOrCreateDepthFramebuffer(
            GuestImageResource color,
            GuestDepthResource depth)
        {
            var attachmentView = color.MipViews.Length > 0
                ? color.MipViews[0]
                : color.View;
            var key = new DepthFramebufferKey(
                depth.Key,
                color.Format,
                attachmentView.Handle);
            if (color.DepthFramebuffers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var loadRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: false);
            var colorClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: false);
            var depthClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: true);
            var bothClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: true);
            var readOnlyLoadRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: false,
                readOnlyDepth: true);
            var readOnlyColorClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: false,
                readOnlyDepth: true);
            var attachments = stackalloc ImageView[2];
            attachments[0] = attachmentView;
            attachments[1] = depth.View;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = loadRenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = Math.Min(color.Width, depth.Width),
                Height = Math.Min(color.Height, depth.Height),
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen depth)");
            framebufferInfo.RenderPass = readOnlyLoadRenderPass;
            Check(
                _vk.CreateFramebuffer(
                    _device,
                    &framebufferInfo,
                    null,
                    out var readOnlyFramebuffer),
                "vkCreateFramebuffer(offscreen read-only depth)");
            var resource = new DepthFramebufferResource
            {
                Depth = depth,
                LoadRenderPass = loadRenderPass,
                ColorClearRenderPass = colorClearRenderPass,
                DepthClearRenderPass = depthClearRenderPass,
                BothClearRenderPass = bothClearRenderPass,
                Framebuffer = framebuffer,
                ReadOnlyLoadRenderPass = readOnlyLoadRenderPass,
                ReadOnlyColorClearRenderPass = readOnlyColorClearRenderPass,
                ReadOnlyFramebuffer = readOnlyFramebuffer,
            };
            var name = $"SharpEmu color 0x{color.Address:X16} depth 0x{depth.Address:X16}";
            SetDebugName(ObjectType.RenderPass, loadRenderPass.Handle, $"{name} load");
            SetDebugName(ObjectType.RenderPass, colorClearRenderPass.Handle, $"{name} color-clear");
            SetDebugName(ObjectType.RenderPass, depthClearRenderPass.Handle, $"{name} depth-clear");
            SetDebugName(ObjectType.RenderPass, bothClearRenderPass.Handle, $"{name} both-clear");
            SetDebugName(
                ObjectType.RenderPass,
                readOnlyLoadRenderPass.Handle,
                $"{name} read-only load");
            SetDebugName(
                ObjectType.RenderPass,
                readOnlyColorClearRenderPass.Handle,
                $"{name} read-only color-clear");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{name} framebuffer");
            SetDebugName(
                ObjectType.Framebuffer,
                readOnlyFramebuffer.Handle,
                $"{name} read-only framebuffer");
            color.DepthFramebuffers.Add(key, resource);
            return resource;
        }

        private RenderPass CreateDepthRenderPass(
            Format colorFormat,
            bool clearColor,
            bool clearDepth,
            bool readOnlyDepth = false)
        {
            if (readOnlyDepth && clearDepth)
            {
                throw new InvalidOperationException(
                    "a read-only depth render pass cannot clear depth");
            }

            var depthLayout = readOnlyDepth
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = colorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearColor ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = DepthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = clearDepth
                    ? ImageLayout.Undefined
                    : depthLayout,
                FinalLayout = depthLayout,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var depthReference = new AttachmentReference
            {
                Attachment = 1,
                Layout = depthLayout,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
                PDepthStencilAttachment = &depthReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = readOnlyDepth
                    ? PipelineStageFlags.EarlyFragmentTestsBit |
                      PipelineStageFlags.LateFragmentTestsBit |
                      PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.LateFragmentTestsBit,
                DstStageMask =
                    PipelineStageFlags.EarlyFragmentTestsBit |
                    PipelineStageFlags.LateFragmentTestsBit |
                    (readOnlyDepth
                        ? PipelineStageFlags.FragmentShaderBit
                        : (PipelineStageFlags)0),
                SrcAccessMask = readOnlyDepth
                    ? AccessFlags.DepthStencilAttachmentWriteBit |
                      AccessFlags.ShaderReadBit
                    : AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask =
                    AccessFlags.DepthStencilAttachmentReadBit |
                    (readOnlyDepth
                        ? AccessFlags.ShaderReadBit
                        : AccessFlags.DepthStencilAttachmentWriteBit),
                DependencyFlags = DependencyFlags.ByRegionBit,
            };
            var createInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(_device, &createInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen depth)");
            return renderPass;
        }

        private void DestroyDepthFramebuffer(DepthFramebufferResource resource)
        {
            if (resource.ReadOnlyFramebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(
                    _device,
                    resource.ReadOnlyFramebuffer,
                    null);
            }
            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.LoadRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.LoadRenderPass, null);
            }
            if (resource.ColorClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.ColorClearRenderPass, null);
            }
            if (resource.DepthClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.DepthClearRenderPass, null);
            }
            if (resource.BothClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.BothClearRenderPass, null);
            }
            if (resource.ReadOnlyLoadRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(
                    _device,
                    resource.ReadOnlyLoadRenderPass,
                    null);
            }
            if (resource.ReadOnlyColorClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(
                    _device,
                    resource.ReadOnlyColorClearRenderPass,
                    null);
            }
        }

        private void DestroyGuestDepth(GuestDepthResource resource)
        {
            foreach (var sampleView in resource.SampleViews.Values)
            {
                if (sampleView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, sampleView, null);
                }
            }
            resource.SampleViews.Clear();

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }
            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }
            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }
        }

        private TextureResource CreateDepthFeedbackSnapshot(
            GuestDrawTexture texture,
            GuestDepthResource source)
        {
            var key = new VulkanFeedbackSnapshotKey(
                VulkanFeedbackSnapshotKind.Depth,
                DepthFormat,
                DepthFormat,
                source.Width,
                source.Height,
                1,
                texture.DstSelect);
            if (TryRentFeedbackSnapshot(key, out var pooled))
            {
                RecordFeedbackSnapshotAcquired(
                    VulkanFeedbackSnapshotKind.Depth,
                    pooled.AllocationBytes,
                    allocated: false);
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = pooled.Image,
                    ImageMemory = pooled.Memory,
                    View = pooled.View,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    FeedbackSnapshotKey = key,
                    FeedbackAllocationBytes = pooled.AllocationBytes,
                    SamplerState = texture.Sampler,
                    DepthFeedbackSource = source,
                };
            }

            var image = default(Image);
            var memory = default(DeviceMemory);
            var view = default(ImageView);
            try
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = DepthFormat,
                    Extent = new Extent3D(source.Width, source.Height, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out image),
                    "vkCreateImage(depth feedback snapshot)");
                _vk.GetImageMemoryRequirements(_device, image, out var requirements);
                var memoryInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(_device, &memoryInfo, null, out memory),
                    "vkAllocateMemory(depth feedback snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(depth feedback snapshot)");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = DepthFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = new ImageSubresourceRange(
                        ImageAspectFlags.DepthBit,
                        0,
                        1,
                        0,
                        1),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out view),
                    "vkCreateImageView(depth feedback snapshot)");
                SetDebugName(
                    ObjectType.Image,
                    image.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} image");
                SetDebugName(
                    ObjectType.ImageView,
                    view.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} view");
                RecordFeedbackSnapshotAcquired(
                    VulkanFeedbackSnapshotKind.Depth,
                    requirements.Size,
                    allocated: true);
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = image,
                    ImageMemory = memory,
                    View = view,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    FeedbackSnapshotKey = key,
                    FeedbackAllocationBytes = requirements.Size,
                    SamplerState = texture.Sampler,
                    DepthFeedbackSource = source,
                };
            }
            catch
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }
                throw;
            }
        }

        private void RecordGuestDepthForSampling(
            GuestDepthResource depth,
            PipelineStageFlags shaderStage)
        {
            if (depth.Layout == ImageLayout.ShaderReadOnlyOptimal)
            {
                return;
            }

            if (!depth.Initialized)
            {
                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = depth.Layout,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);
                var clearValue = new ClearDepthStencilValue(depth.ClearDepth, 0);
                _vk.CmdClearDepthStencilImage(
                    _commandBuffer,
                    depth.Image,
                    ImageLayout.TransferDstOptimal,
                    &clearValue,
                    1,
                    &depthRange);
                depth.Initialized = true;
                if (depth.InitializationSource == "none")
                {
                    depth.InitializationSource = "sample-clear";
                }
                depth.Layout = ImageLayout.TransferDstOptimal;
            }

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = depth.Layout == ImageLayout.TransferDstOptimal
                    ? AccessFlags.TransferWriteBit
                    : depth.Layout == ImageLayout.DepthStencilReadOnlyOptimal
                        ? AccessFlags.DepthStencilAttachmentReadBit |
                          AccessFlags.ShaderReadBit
                        : AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = depth.Layout,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = depth.Image,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                depth.Layout == ImageLayout.TransferDstOptimal
                    ? PipelineStageFlags.TransferBit
                    : depth.Layout == ImageLayout.DepthStencilReadOnlyOptimal
                        ? PipelineStageFlags.EarlyFragmentTestsBit |
                          PipelineStageFlags.LateFragmentTestsBit |
                          PipelineStageFlags.FragmentShaderBit
                        : PipelineStageFlags.LateFragmentTestsBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            depth.Layout = ImageLayout.ShaderReadOnlyOptimal;
        }

        private void RecordStandaloneGuestDepthClear(GuestDepthResource depth)
        {
            var depthRange = new ImageSubresourceRange(
                ImageAspectFlags.DepthBit,
                0,
                1,
                0,
                1);
            var sourceStage = PipelineStageFlags.TopOfPipeBit;
            var sourceAccess = AccessFlags.None;
            switch (depth.Layout)
            {
                case ImageLayout.ShaderReadOnlyOptimal:
                    sourceStage =
                        PipelineStageFlags.VertexShaderBit |
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit;
                    sourceAccess = AccessFlags.ShaderReadBit;
                    break;
                case ImageLayout.DepthStencilAttachmentOptimal:
                    sourceStage =
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit;
                    sourceAccess =
                        AccessFlags.DepthStencilAttachmentReadBit |
                        AccessFlags.DepthStencilAttachmentWriteBit;
                    break;
                case ImageLayout.DepthStencilReadOnlyOptimal:
                    sourceStage =
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit |
                        PipelineStageFlags.FragmentShaderBit;
                    sourceAccess =
                        AccessFlags.DepthStencilAttachmentReadBit |
                        AccessFlags.ShaderReadBit;
                    break;
                case ImageLayout.TransferDstOptimal:
                    sourceStage = PipelineStageFlags.TransferBit;
                    sourceAccess = AccessFlags.TransferWriteBit;
                    break;
            }

            if (depth.Layout != ImageLayout.TransferDstOptimal)
            {
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = sourceAccess,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = depth.Layout,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    sourceStage,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);
            }

            var clearValue = new ClearDepthStencilValue(depth.ClearDepth, 0);
            _vk.CmdClearDepthStencilImage(
                _commandBuffer,
                depth.Image,
                ImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &depthRange);
            depth.Initialized = true;
            depth.Layout = ImageLayout.TransferDstOptimal;
            depth.InitializationSource = "guest-depth-clear";
        }

        private void RecordDepthFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.DepthFeedbackSource is not { } source)
                {
                    continue;
                }

                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = source.Layout == ImageLayout.ShaderReadOnlyOptimal
                            ? AccessFlags.ShaderReadBit
                            : source.Layout == ImageLayout.DepthStencilReadOnlyOptimal
                                ? AccessFlags.DepthStencilAttachmentReadBit |
                                  AccessFlags.ShaderReadBit
                                : AccessFlags.DepthStencilAttachmentWriteBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = source.Layout,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);
                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);
                    RecordFeedbackSnapshotCopy(
                        DepthFormat,
                        source.Width,
                        source.Height);
                    var sourceToAttachment = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask =
                            AccessFlags.DepthStencilAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToAttachment);
                    source.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                }
                else
                {
                    var clearValue = new ClearDepthStencilValue(source.ClearDepth, 0);
                    _vk.CmdClearDepthStencilImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &depthRange);
                }

                var destinationToShader = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShader);
            }
        }

        private static readonly bool _traceDepthInitialization =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DEPTH_INIT"),
                "1",
                StringComparison.Ordinal);
    }
}
