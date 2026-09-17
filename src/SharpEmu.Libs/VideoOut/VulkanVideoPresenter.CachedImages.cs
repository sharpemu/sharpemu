// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    // Display buffers the guest registered; the only image addresses a flip may capture.
    private static readonly HashSet<ulong> _knownDisplayBuffers = new();

    internal static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _knownDisplayBuffers.Add(address);
        }
    }

    private static bool IsKnownDisplayBuffer(ulong address)
    {
        lock (_gate)
        {
            return _knownDisplayBuffers.Contains(address);
        }
    }

    // Check only for a nonzero address here; image lookup validates the request later.
    internal static bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => address != 0;


    // A presenter-owned copy of a display surface taken by an ordered flip.
    private sealed class GuestImageResource
    {
        public ulong Address;
        public long FlipVersion;
        public uint Width;
        public uint Height;
        public Format Format;
        public Image Image;
        public DeviceMemory Memory;
    }

    // A cached color target bound to one draw or resolve.
    private sealed class ColorAttachment
    {
        public ResourceSlotIdentifier ImageIdentifier;
        public ImageRequest Request;
        public ColorTargetResolution Resolution;
        public CachedImage Image = null!;
        public ImageView View;
        public ImageLayout Layout;
        public bool Clear;
        public ClearColorValue ClearValue;

        public Format Format => Request.View.Format;
        public ulong Address => Resolution.BaseAddress;
    }

    // A cached depth target bound to one draw.
    private sealed class DepthAttachment
    {
        public ResourceSlotIdentifier ImageIdentifier;
        public ImageRequest Request;
        public DepthTargetResolution Resolution;
        public CachedImage Image = null!;
        public ImageView View;
        public ImageLayout Layout;
        public bool ClearDepth;
        public bool ClearStencil;
        public bool QueuedMetadataClear;
        public float ClearDepthValue = 1f;
        public byte ClearStencilValue;

        public Format Format => Resolution.Format;
    }

    // One shader image binding: a cached image, or a host movie plane with its own image.
    private sealed class TextureResource
    {
        public ulong Address;
        public ResourceSlotIdentifier ImageIdentifier;
        public ImageRequest Request;
        public TextureRequestResolution Resolution;
        public CachedImage? CachedImage;
        public uint MipLevel;
        public Image Image;
        public ImageView View;
        public ImageLayout Layout = ImageLayout.ShaderReadOnlyOptimal;
        public Sampler Sampler;
        public GuestSampler SamplerState;
        public bool IsStorage;
        public bool IsHostMovie;
        public int HostMoviePlane = -1;
        public long HostMovieFrameSerial;
        public bool NeedsUpload;
        public VkBuffer StagingBuffer;
        public DeviceMemory StagingMemory;
        public uint Width;
        public uint Height;
        public uint RowLength;
        public uint DestinationSelect;
        // One view per mip level for a storage image the program indexes by mip; empty otherwise.
        public ImageView[] MipViews = [];
    }

    private sealed partial class Presenter
    {
        private readonly List<ResourceSlotIdentifier> _trackedImageBindings = new();
        private static int _guestImageDumpSequence;

        // Shader images for work without color targets: resolve, acquire, transition and release the bindings.
        private TextureResource[] ResolveDrawTextures(IReadOnlyList<GuestDrawTexture> textures)
        {
            var bindings = new TextureResource[textures.Count];
            var hostMovie = FindHostMovieTextureBindings(textures);
            for (var index = 0; index < bindings.Length; index++)
            {
                bindings[index] = index == hostMovie.Luma
                    ? CreateHostMovieTextureResource(textures[index], plane: 0)
                    : index == hostMovie.Chroma
                        ? CreateHostMovieTextureResource(textures[index], plane: 1)
                        : ResolveTexture(textures[index]);
            }

            AcquireTextureViews(bindings, textures);
            RecordTextureTransitions(bindings);
            ResetImageBindings();
            return bindings;
        }

        private void TrackImageBinding(ResourceSlotIdentifier imageIdentifier) => _trackedImageBindings.Add(imageIdentifier);

        // Records that a shader reads or writes the image this draw; a mixed use forces the general layout.
        private void BindImage(ResourceSlotIdentifier imageIdentifier, bool storage)
        {
            var image = _imageCache.GetImage(imageIdentifier);
            if (ImageDescription.IsEmptyRange(image.Description.Data))
            {
                return;
            }

            if (image.Binding.IsBound)
            {
                image.Binding.ForceGeneral |= image.Binding.ShaderWrite != storage;
            }

            image.Binding.IsBound = true;
            image.Binding.ShaderWrite |= storage;
            TrackImageBinding(imageIdentifier);
        }

        public void BindRenderTarget(ResourceSlotIdentifier imageIdentifier)
        {
            _imageCache.GetImage(imageIdentifier).Binding.IsTarget = true;
            TrackImageBinding(imageIdentifier);
        }

        private void ResetImageBindings()
        {
            foreach (var imageIdentifier in _trackedImageBindings)
            {
                if (_imageCache.TryGetImage(imageIdentifier, out var image))
                {
                    image.Binding = default;
                }
            }

            _trackedImageBindings.Clear();
        }

        private static ColorTargetWords RequireColorTargetRegisters(GuestRenderTarget target) =>
            target.Registers ?? throw SubmissionScheduler.Fatal($"The color target carries no registers: address=0x{target.Address:X16} size={target.Width}x{target.Height}.");

        // Render-state discovery for one color slot; null when the slot binds nothing.
        private ColorAttachment? DiscoverColorTarget(GuestRenderTarget target, bool ignoreTargetMask, bool exactFormat)
        {
            if (target.Address == 0 || target.Registers is null)
            {
                return null;
            }

            if (ImageRequestBuilders.ColorTarget(RequireColorTargetRegisters(target), target.WriteMask, 0, ignoreTargetMask) is not { } resolution)
            {
                return null;
            }

            // A lookup needs a recording tick; the scheduler starts with the first batch.
            _ = BeginBatchedGuestCommands();
            var request = resolution.Request;
            var imageIdentifier = _imageCache.FindImage(ref request, exactFormat);
            BindRenderTarget(imageIdentifier);
            return new ColorAttachment { ImageIdentifier = imageIdentifier, Request = request, Resolution = resolution };
        }

        private bool IsStaleImage(ResourceSlotIdentifier imageIdentifier, out CachedImage? image)
        {
            if (!_imageCache.TryGetImage(imageIdentifier, out var found))
            {
                image = null;
                return true;
            }

            image = found;
            return (!found.Registered && !ImageDescription.IsEmptyRange(found.Description.Data)) || found.Binding.NeedsRebind;
        }

        // Final acquisition: the view, the attachment layout and a pending DCC clear.
        private void AcquireColorAttachment(ColorAttachment target)
        {
            if (IsStaleImage(target.ImageIdentifier, out var stale))
            {
                if (stale is not null)
                {
                    stale.Binding = default;
                }

                var request = target.Request;
                target.ImageIdentifier = _imageCache.FindImage(ref request);
                target.Request = request;
                BindRenderTarget(target.ImageIdentifier);
            }

            target.View = _imageCache.AcquireColorTargetView(target.ImageIdentifier, target.Request);
            // The view acquisition can end the tick; the transition records into the buffer that is current now.
            var command = BeginBatchedGuestCommands();
            var image = _imageCache.GetImage(target.ImageIdentifier);
            target.Image = image;
            if (image.Backing.Samples != target.Resolution.Samples || target.View.Handle == 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The color target backing does not match its request: address=0x{target.Address:X16} backingSamples={image.Backing.Samples} samples={target.Resolution.Samples}.");
            }

            var view = target.Request.View;
            target.Layout = image.Binding.IsBound ? ImageLayout.General : ImageLayout.ColorAttachmentOptimal;
            image.Transition(
                target.Layout,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount),
                command);
            target.Clear = ResolveDccAttachmentClear(target, out target.ClearValue);
        }

        // A DCC fast clear may leave the color allocation stale; the deferred value lands when the surface binds.
        private bool ResolveDccAttachmentClear(ColorAttachment target, out ClearColorValue clearValue)
        {
            clearValue = default;
            if (target.Request.Description.Metadata.Kind != MetadataKind.Dcc)
            {
                return false;
            }

            var view = target.Request.View;
            var metadataAddress = target.Request.Description.Metadata.Range.Address;
            if (!_imageCache.IsMetadataCleared(metadataAddress, view.BaseLayer, out var metadataValue))
            {
                return false;
            }

            var resolution = target.Resolution;
            switch ((byte)metadataValue)
            {
                case 0x00:
                    break;
                case 0x20:
                    if (!resolution.MetadataClearSupported)
                    {
                        return false;
                    }

                    clearValue = resolution.ColorClearValue;
                    break;
                case 0x40:
                    if (!resolution.MetadataFixedClearSupported)
                    {
                        return false;
                    }

                    clearValue.Float32_3 = 1f;
                    break;
                case 0x80:
                    if (!resolution.MetadataFixedClearSupported)
                    {
                        return false;
                    }

                    clearValue.Float32_0 = 1f;
                    clearValue.Float32_1 = 1f;
                    clearValue.Float32_2 = 1f;
                    break;
                case 0xc0:
                    if (!resolution.MetadataFixedClearSupported)
                    {
                        return false;
                    }

                    clearValue.Float32_0 = 1f;
                    clearValue.Float32_1 = 1f;
                    clearValue.Float32_2 = 1f;
                    clearValue.Float32_3 = 1f;
                    break;
                default:
                    return false;
            }

            for (uint layer = 1; layer < view.LayerCount; layer++)
            {
                if (!_imageCache.IsMetadataCleared(metadataAddress, view.BaseLayer + layer))
                {
                    return false;
                }
            }

            // Consume only after every layer of the view can be materialized together.
            for (uint layer = 0; layer < view.LayerCount; layer++)
            {
                if (!_imageCache.SetMetadataSlice(metadataAddress, view.BaseLayer + layer, false))
                {
                    throw SubmissionScheduler.Fatal($"The DCC clear state could not be consumed: metadata=0x{metadataAddress:X16} layer={view.BaseLayer + layer}.");
                }
            }

            return true;
        }

        private DepthAttachment? DiscoverDepthTarget(GuestDepthTarget target)
        {
            if (target.Registers is not { } words || ImageRequestBuilders.DepthTarget(words, _deviceInfo) is not { } resolution)
            {
                return null;
            }

            _ = BeginBatchedGuestCommands();
            var request = resolution.Request;
            var imageIdentifier = _imageCache.FindImage(ref request);
            BindRenderTarget(imageIdentifier);
            return new DepthAttachment
            {
                ImageIdentifier = imageIdentifier,
                Request = request,
                Resolution = resolution,
                ClearDepthValue = target.ClearDepth,
                QueuedMetadataClear = target.MetadataClear,
            };
        }

        // The aspects the draw writes: cleared or written depth, cleared or tested-and-written stencil.
        private static ImageAspectFlags GetDepthWriteAspects(DepthAttachment depth, GuestDepthState state)
        {
            var available = ViewFormatRules.DepthAspects(depth.Format);
            var writes = (ImageAspectFlags)0;
            if ((available & ImageAspectFlags.DepthBit) != 0 && (depth.ClearDepth || (state.TestEnable && state.WriteEnable)))
            {
                writes |= ImageAspectFlags.DepthBit;
            }

            if ((available & ImageAspectFlags.StencilBit) == 0)
            {
                return writes;
            }

            bool FaceWrites(in GuestStencilFaceState face)
            {
                if (face.WriteMask == 0)
                {
                    return false;
                }

                var canPass = face.CompareOp != 0;
                var canFail = face.CompareOp != 7;
                if (face.CompareMask == 0)
                {
                    switch (face.CompareOp)
                    {
                        case 2 or 3 or 6 or 7:
                            canPass = true;
                            canFail = false;
                            break;
                        default:
                            canPass = false;
                            canFail = true;
                            break;
                    }
                }

                var depthPass = !state.TestEnable || state.CompareOp != 0;
                var depthFail = state.TestEnable && state.CompareOp != 7;
                return (canFail && face.FailOp != 0) || (canPass && depthPass && face.PassOp != 0) || (canPass && depthFail && face.DepthFailOp != 0);
            }

            if (depth.ClearStencil || (state.StencilTestEnable && (FaceWrites(state.StencilFront) || FaceWrites(state.StencilBack))))
            {
                writes |= ImageAspectFlags.StencilBit;
            }

            return writes;
        }

        // The attachment layout by written aspects; the device enables no separate depth/stencil layouts.
        private static ImageLayout GetDepthAttachmentLayout(DepthAttachment depth, ImageAspectFlags writes)
        {
            var available = ViewFormatRules.DepthAspects(depth.Format);
            var depthWrite = (writes & ImageAspectFlags.DepthBit) != 0;
            var stencilWrite = (writes & ImageAspectFlags.StencilBit) != 0;
            if ((available & ImageAspectFlags.StencilBit) == 0)
            {
                return depthWrite ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal;
            }

            if ((available & ImageAspectFlags.DepthBit) == 0)
            {
                return stencilWrite ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal;
            }

            if (depthWrite && stencilWrite)
            {
                return ImageLayout.DepthStencilAttachmentOptimal;
            }

            if (depthWrite)
            {
                return ImageLayout.DepthAttachmentStencilReadOnlyOptimal;
            }

            return stencilWrite ? ImageLayout.DepthReadOnlyStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal;
        }

        // Final acquisition of the depth target: HTile clear state, the view and the layout.
        private void AcquireDepthAttachment(DepthAttachment depth, in GuestDepthState state)
        {
            if (IsStaleImage(depth.ImageIdentifier, out _))
            {
                throw SubmissionScheduler.Fatal($"The depth target changed after render-state discovery: address=0x{depth.Resolution.DepthAddress:X16}.");
            }

            depth.View = _imageCache.AcquireDepthTargetView(depth.ImageIdentifier, depth.Request);
            var command = BeginBatchedGuestCommands();
            var resolution = depth.Resolution;
            var view = depth.Request.View;
            // View acquisition registers metadata before the queued clear can be recorded.
            if (depth.QueuedMetadataClear && resolution.HasHtile)
            {
                if (!_imageCache.SetMetadataSlice(resolution.HtileAddress, view.BaseLayer, true))
                {
                    throw SubmissionScheduler.Fatal($"The queued depth clear could not be recorded: htile=0x{resolution.HtileAddress:X16} layer={view.BaseLayer}.");
                }

                depth.QueuedMetadataClear = false;
            }
            if (resolution.HasHtile && resolution.DepthClearEnabled && !_imageCache.ClearMetadata(resolution.HtileAddress))
            {
                throw SubmissionScheduler.Fatal($"The HTile metadata could not be acquired for a depth clear: htile=0x{resolution.HtileAddress:X16}.");
            }

            var metadataClear = resolution.HasHtile && _imageCache.IsMetadataCleared(resolution.HtileAddress, view.BaseLayer);
            depth.ClearDepth = resolution.DepthClearEnabled || metadataClear;
            depth.ClearStencil = resolution.StencilClearEnabled;
            depth.ClearStencilValue = state.StencilClearValue;
            if (metadataClear && !_imageCache.SetMetadataSlice(resolution.HtileAddress, view.BaseLayer, false))
            {
                throw SubmissionScheduler.Fatal($"The HTile clear state could not be consumed: htile=0x{resolution.HtileAddress:X16} layer={view.BaseLayer}.");
            }

            var image = _imageCache.GetImage(depth.ImageIdentifier);
            depth.Image = image;
            if (depth.View.Handle == 0 || image.Backing.Samples != resolution.Samples)
            {
                throw SubmissionScheduler.Fatal(
                    $"The depth target backing does not match its request: address=0x{resolution.DepthAddress:X16} backingSamples={image.Backing.Samples} samples={resolution.Samples}.");
            }

            var writes = GetDepthWriteAspects(depth, state);
            depth.Layout = GetDepthAttachmentLayout(depth, writes);
            var access = AccessFlags.DepthStencilAttachmentReadBit | (writes != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0);
            image.Transition(depth.Layout, access, new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount), command);
        }

        // Render-state discovery for one shader image; the view is acquired later with the draw.
        private TextureResource ResolveTexture(GuestDrawTexture texture)
        {
            var resolution = ImageRequestBuilders.Texture(texture.Descriptor ?? [], texture.Shape);
            _ = BeginBatchedGuestCommands();
            var request = resolution.Request;
            var imageIdentifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
            resolution = resolution with { Request = request };
            imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, imageIdentifier, resolution);
            BindImage(imageIdentifier, texture.IsStorage);
            return new TextureResource
            {
                Address = texture.Address,
                ImageIdentifier = imageIdentifier,
                Request = request,
                Resolution = resolution,
                MipLevel = texture.MipLevel,
                IsStorage = texture.IsStorage,
                SamplerState = texture.Sampler,
                DestinationSelect = texture.DstSelect,
                Width = texture.Width,
                Height = texture.Height,
            };
        }

        private Sampler ResolveSampler(in GuestSampler sampler)
        {
            Span<uint> words = stackalloc uint[4] { sampler.Word0, sampler.Word1, sampler.Word2, sampler.Word3 };
            return _samplerStore.GetSampler(new SamplerDescriptorWords(words));
        }

        // Views for every cached binding after the targets are bound; a stale image is found again first.
        private void AcquireTextureViews(TextureResource[] bindings, IReadOnlyList<GuestDrawTexture> textures)
        {
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                if (binding.IsHostMovie)
                {
                    continue;
                }

                if (IsStaleImage(binding.ImageIdentifier, out var stale))
                {
                    if (stale is not null)
                    {
                        stale.Binding = default;
                    }

                    bindings[index] = binding = ResolveTexture(textures[index]);
                }

                var view = binding.Request.View;
                if (binding.IsStorage)
                {
                    // A store addresses one level: the descriptor base plus the instruction's relative mip.
                    if (binding.MipLevel >= view.LevelCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"The storage mip is outside the bound view: address=0x{binding.Address:X16} baseLevel={view.BaseLevel} mip={binding.MipLevel} levels={view.LevelCount}.");
                    }

                    binding.Request = binding.Request with { View = view with { BaseLevel = view.BaseLevel + binding.MipLevel, LevelCount = 1 } };
                }

                var image = _imageCache.GetImage(binding.ImageIdentifier);
                if (binding.IsStorage && image.Description.HasStencil && ViewFormatRules.IsStencilViewFormat(binding.Request.View.Format))
                {
                    image = AcquireStencilStorage(binding, image);
                    binding.View = image.GetOrCreateView(binding.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                }
                else
                {
                    binding.View = _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                }

                image.Uses.Storage |= binding.IsStorage;
                image.Uses.Texture |= !binding.IsStorage;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
                if (!binding.IsStorage)
                {
                    binding.Sampler = ResolveSampler(binding.SamplerState);
                }
            }
        }

        private CachedImage AcquireStencilStorage(TextureResource binding, CachedImage attachment)
        {
            var description = binding.Request.Description;
            if (description.Data.Address != attachment.Description.Stencil.Address || description.Data.Size > attachment.Description.Stencil.Size ||
                description.Extent.Width != attachment.Backing.Extent.Width || description.Extent.Height != attachment.Backing.Extent.Height ||
                description.Resources.Levels != 1 || description.Resources.Layers != attachment.Backing.Layers)
            {
                throw SubmissionScheduler.Fatal("The stencil storage request does not cover its attachment's stencil layout.");
            }

            if (_hasBoundDepth && _boundDepth.Image == binding.ImageIdentifier &&
                (_boundDepthLoadState.StencilTestEnabled || _boundDepthLoadState.StencilClearEnabled))
            {
                throw SubmissionScheduler.Fatal("A stencil storage write cannot share an active stencil attachment.");
            }

            var preparation = RequirePreparation();
            var stencilImages = preparation.StencilStorageImages ??= new();
            if (stencilImages.TryGetValue(attachment, out var storage))
                return storage;

            var sampledRequest = binding.Request with
            {
                Role = ImageRole.Texture,
                View = binding.Request.View with { Usage = ImageUsageFlags.SampledBit },
            };
            _imageCache.AcquireTextureView(binding.ImageIdentifier, sampledRequest);
            _imageCache.MarkGpuWritten(binding.ImageIdentifier);
            storage = attachment.CreateStencilStorageImage();
            stencilImages.Add(attachment, storage);
            storage.Binding.ShaderWrite = true;
            attachment.CopyStencilStorage(storage, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: false);
            if (_hasBoundDepth && _boundDepth.Image == binding.ImageIdentifier)
            {
                var writes = _boundDepthLoadState.AttachmentWriteAspects(attachment.Backing.Format);
                attachment.Transition(_boundDepthLayout,
                    AccessFlags.DepthStencilAttachmentReadBit | (writes != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0),
                    null, BeginBatchedGuestCommands());
            }

            return storage;
        }

        // The layout each binding reads through; a target read by its own draw uses the general layout.
        private void RecordTextureTransitions(TextureResource[] bindings) =>
            RecordDrawTextureTransitions(bindings, null, GuestDepthState.Default);

        private void RecordSeparateDepthClear(DepthAttachment depth)
        {
            var aspects = (depth.ClearDepth ? ImageAspectFlags.DepthBit : 0) |
                (depth.ClearStencil ? ImageAspectFlags.StencilBit : 0);
            aspects &= ViewFormatRules.DepthAspects(depth.Format);
            if (aspects == 0)
                return;

            // Finish rendering before the transfer clear changes the image layout.
            EndRendering();
            var command = BeginBatchedGuestCommands();
            var view = depth.Request.View;
            depth.Image!.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit,
                new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount), command);
            var range = new ImageSubresourceRange(aspects, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
            var value = new ClearDepthStencilValue(depth.ClearDepthValue, depth.ClearStencilValue);
            _vk.CmdClearDepthStencilImage(command, depth.Image.Backing.Handle, ImageLayout.TransferDstOptimal, &value, 1, &range);
        }

        private void RecordDrawTextureTransitions(TextureResource[] bindings, DepthAttachment? depth, GuestDepthState depthState)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTransitions);
            if (depth is not null && (depth.ClearDepth || depth.ClearStencil))
            {
                var attachmentView = depth.Request.View;
                foreach (var binding in bindings)
                {
                    var sampledView = binding.Request.View;
                    if (binding.IsHostMovie || !ReferenceEquals(binding.CachedImage, depth.Image) ||
                        (ulong)sampledView.BaseLevel >= (ulong)attachmentView.BaseLevel + attachmentView.LevelCount ||
                        (ulong)attachmentView.BaseLevel >= (ulong)sampledView.BaseLevel + sampledView.LevelCount ||
                        (ulong)sampledView.BaseLayer >= (ulong)attachmentView.BaseLayer + attachmentView.LayerCount ||
                        (ulong)attachmentView.BaseLayer >= (ulong)sampledView.BaseLayer + sampledView.LayerCount)
                        continue;

                    // Complete load clears before shader reads, then restore the attachment for the draw.
                    RecordSeparateDepthClear(depth);
                    depth.ClearDepth = false;
                    depth.ClearStencil = false;
                    var remainingWrites = GetDepthWriteAspects(depth, depthState);
                    depth.Layout = GetDepthAttachmentLayout(depth, remainingWrites);
                    depth.Image!.Transition(depth.Layout,
                        AccessFlags.DepthStencilAttachmentReadBit |
                        (remainingWrites != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0),
                        new SubresourceRange(attachmentView.BaseLevel, attachmentView.LevelCount,
                            attachmentView.BaseLayer, attachmentView.LayerCount), BeginBatchedGuestCommands());
                    break;
                }
            }
            // The acquisitions can end the tick; the transitions record into the buffer that is current now.
            var command = BeginBatchedGuestCommands();
            foreach (var binding in bindings)
            {
                if (binding.IsHostMovie || binding.CachedImage is not { } image)
                {
                    continue;
                }

                var view = binding.Request.View;
                var range = new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                var storage = binding.IsStorage;
                if (ImageDescription.IsEmptyRange(image.Description.Data))
                {
                    binding.Layout = ImageLayout.General;
                    image.Transition(binding.Layout, storage ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : AccessFlags.ShaderReadBit, range, command);
                }
                else if (depth is not null && ReferenceEquals(image, depth.Image))
                {
                    var sampledAspects = ViewFormatRules.IsStencilViewFormat(view.Format)
                        ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
                    var writes = GetDepthWriteAspects(depth, depthState);
                    var attachmentView = depth.Request.View;
                    var overlaps = (ulong)view.BaseLevel < (ulong)attachmentView.BaseLevel + attachmentView.LevelCount &&
                        (ulong)attachmentView.BaseLevel < (ulong)view.BaseLevel + view.LevelCount &&
                        (ulong)view.BaseLayer < (ulong)attachmentView.BaseLayer + attachmentView.LayerCount &&
                        (ulong)attachmentView.BaseLayer < (ulong)view.BaseLayer + view.LayerCount;
                    if (overlaps && (sampledAspects & writes) != 0)
                    {
                        throw new InvalidOperationException(
                            "A draw cannot sample and write the same depth or stencil aspect without feedback-loop support. " +
                            $"image=0x{image.Description.Data.Address:X16} sampled_aspects={sampledAspects} write_aspects={writes} " +
                            $"view_format={view.Format} view_aspect={view.Aspect} " +
                            $"sample_mip={view.BaseLevel}+{view.LevelCount} sample_layer={view.BaseLayer}+{view.LayerCount} " +
                            $"target_mip={attachmentView.BaseLevel}+{attachmentView.LevelCount} target_layer={attachmentView.BaseLayer}+{attachmentView.LayerCount} " +
                            $"clear_depth={depth.ClearDepth} clear_stencil={depth.ClearStencil} " +
                            $"depth_state={depthState}");
                    }

                    // Keep the sampled view and the attachment in the same layout.
                    binding.Layout = overlaps ? depth.Layout : ImageLayout.DepthStencilReadOnlyOptimal;
                    var attachmentAccess = overlaps
                        ? AccessFlags.DepthStencilAttachmentReadBit | (writes != 0 ? AccessFlags.DepthStencilAttachmentWriteBit : 0)
                        : 0;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit | attachmentAccess, range, command);
                }
                else if ((image.Binding.ForceGeneral || image.Binding.IsTarget) && !image.Description.IsDepth)
                {
                    var storageAccess = image.Binding.ShaderWrite ? AccessFlags.ShaderWriteBit : 0;
                    binding.Layout = ImageLayout.General;
                    image.Transition(
                        binding.Layout,
                        AccessFlags.ShaderReadBit | storageAccess | AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                        null,
                        command);
                }
                else if (storage)
                {
                    binding.Layout = ImageLayout.General;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, range, command);
                }
                else
                {
                    binding.Layout = image.Description.IsDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal;
                    image.Transition(binding.Layout, AccessFlags.ShaderReadBit, range, command);
                }
            }
        }

        // Host movie planes upload from their staging buffer before the draw samples them.
        private void RecordHostMovieUploads(TextureResource[] bindings, PipelineStageFlags shaderStage)
        {
            foreach (var texture in bindings)
            {
                if (!texture.IsHostMovie || !texture.NeedsUpload)
                {
                    continue;
                }

                var initialized = texture.HostMoviePlane == 0 ? _hostMovieImageInitialized : _hostMovieChromaImageInitialized;
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = initialized ? AccessFlags.ShaderReadBit : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = initialized ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer, initialized ? PipelineStageFlags.AllCommandsBit : PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &toTransfer);
                var copyRegion = new BufferImageCopy
                {
                    BufferRowLength = texture.RowLength > texture.Width ? texture.RowLength : 0,
                    ImageSubresource = new ImageSubresourceLayers { AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, LayerCount = 1 },
                    ImageExtent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyBufferToImage(_commandBuffer, texture.StagingBuffer, texture.Image, ImageLayout.TransferDstOptimal, 1, &copyRegion);
                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, shaderStage, 0, 0, null, 0, null, 1, &toShaderRead);
                if (texture.HostMoviePlane == 0)
                {
                    _hostMovieImageInitialized = true;
                    _hostMovieLumaUploadedFrameSerial = texture.HostMovieFrameSerial;
                }
                else if (texture.HostMoviePlane == 1)
                {
                    _hostMovieChromaImageInitialized = true;
                    _hostMovieChromaUploadedFrameSerial = texture.HostMovieFrameSerial;
                }
            }
        }

        private (VkBuffer Buffer, DeviceMemory Memory) CreateTextureStagingBuffer(byte[] pixels, string debugName)
        {
            var buffer = CreateHostBuffer(pixels, BufferUsageFlags.TransferSrcBit, out var memory, out _);
            SetDebugName(ObjectType.Buffer, buffer.Handle, debugName);
            return (buffer, memory);
        }

        // Clears every target of the work item on the GPU; the store owns the result.
        internal void ExecuteOffscreenColorClear(VulkanOffscreenColorClear work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            EnsureGuestSubmissionCapacity();
            EndRendering();
            var logicalClear = stackalloc float[4] { work.Red, work.Green, work.Blue, work.Alpha };
            try
            {
                foreach (var descriptor in work.Targets)
                {
                    var target = DiscoverColorTarget(descriptor, ignoreTargetMask: true, exactFormat: false);
                    if (target is null)
                    {
                        continue;
                    }

                    AcquireColorAttachment(target);
                    var command = BeginBatchedGuestCommands();
                    var mapping = target.Resolution.ExportMapping;
                    var clearValue = new ClearColorValue(
                        logicalClear[mapping.Map(0)], logicalClear[mapping.Map(1)], logicalClear[mapping.Map(2)], logicalClear[mapping.Map(3)]);
                    var view = target.Request.View;
                    var range = new SubresourceRange(view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                    target.Image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, command);
                    var vkRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, view.BaseLevel, view.LevelCount, view.BaseLayer, view.LayerCount);
                    _vk.CmdClearColorImage(command, target.Image.Backing.Handle, ImageLayout.TransferDstOptimal, &clearValue, 1, &vkRange);
                }

                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.offscreen_color_clear mrt={work.Targets.Count} ps=0x{work.ShaderAddress:X16} " +
                        $"rgba=({work.Red:0.###},{work.Green:0.###},{work.Blue:0.###},{work.Alpha:0.###})");
                }
            }
            finally
            {
                ResetImageBindings();
            }
        }

        // A hardware color resolve: the destination takes the source through the store's resolve copy.
        internal void ExecuteGuestImageResolve(VulkanGuestImageResolve work)
        {
            if (_deviceLost)
            {
                return;
            }

            EnsureGuestSubmissionCapacity();
            EndRendering();
            try
            {
                var source = DiscoverColorTarget(work.Source, ignoreTargetMask: true, exactFormat: true);
                var destination = DiscoverColorTarget(work.Destination, ignoreTargetMask: true, exactFormat: true);
                if (source is null || destination is null)
                {
                    return;
                }

                if (source.Address == destination.Address &&
                    source.Resolution.BaseMipLevel == destination.Resolution.BaseMipLevel &&
                    source.Resolution.BaseArrayLayer == destination.Resolution.BaseArrayLayer)
                {
                    return;
                }

                _imageCache.MarkGpuWritten(destination.ImageIdentifier);
                var sourceImage = _imageCache.GetImage(source.ImageIdentifier);
                var destinationImage = _imageCache.GetImage(destination.ImageIdentifier);
                destinationImage.ResolveFrom(
                    sourceImage,
                    new SubresourceRange(source.Resolution.BaseMipLevel, 1, source.Resolution.BaseArrayLayer, 1),
                    new SubresourceRange(destination.Resolution.BaseMipLevel, 1, destination.Resolution.BaseArrayLayer, 1));
                _ = BeginBatchedGuestCommands();
            }
            finally
            {
                ResetImageBindings();
            }
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            _deviceInfo.FreeMemory(resource.Memory);
            resource.Image = default;
            resource.Memory = default;
        }
    }
}
