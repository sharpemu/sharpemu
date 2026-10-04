// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// This partial binds the resources of one stage: storage buffers, images, samplers and push data.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const ulong NullStorageBufferBytes = 16;
        private const uint MaxMemoryOffsetAdjustment = 256;
        private const uint TransientDataAlignment = 256;
        private const int MaxImageOccurrences = 64;
        // These settings are supplied before the emulator child starts. Reading them once
        // keeps disabled diagnostics out of descriptor/image hot paths.
        private static readonly bool TextureTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TEXTURE_BINDINGS"), "1", StringComparison.Ordinal);
        private static readonly string? TextureTraceAddressFilter =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TEXTURE_ADDRESS");
        private static readonly string? TextureTraceHashFilter =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TEXTURE_HASH");
        private static readonly string? ResourceTraceHashFilter =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_RESOURCE_HASH");

        // CommitBindings runs on the presenter's render thread and Vulkan consumes these
        // structures during the descriptor command. Retain their capacity between draws
        // instead of allocating four managed arrays per stage/draw.
        private DescriptorBufferInfo[] _descriptorBufferInfoScratch = [];
        private DescriptorImageInfo[] _descriptorImageInfoScratch = [];
        private WriteDescriptorSet[] _descriptorWriteScratch = [];
        private uint[] _descriptorImageOccurrenceScratch = [];

        private static T[] EnsureDescriptorScratchCapacity<T>(ref T[] scratch, int count)
        {
            var required = Math.Max(count, 1);
            if (scratch.Length >= required)
            {
                return scratch;
            }

            var capacity = Math.Max(scratch.Length, 4);
            while (capacity < required)
            {
                if (capacity > int.MaxValue / 2)
                {
                    capacity = required;
                    break;
                }

                capacity *= 2;
            }

            scratch = new T[capacity];
            return scratch;
        }

        private static bool TextureTraceAddressMatches(ulong address)
        {
            var filter = TextureTraceAddressFilter;
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var span = filter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value == address;
        }

        private static bool TraceHashMatches(string? filter, ulong hash, bool matchWhenUnset)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return matchWhenUnset;
            }

            var span = filter.AsSpan().Trim();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                span,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) && value == hash;
        }

        private static bool TextureTraceHashMatches(ulong hash) =>
            TraceHashMatches(TextureTraceHashFilter, hash, matchWhenUnset: true);

        private static bool ResourceTraceHashMatches(ulong hash) =>
            TraceHashMatches(ResourceTraceHashFilter, hash, matchWhenUnset: false);

        private static string FormatImageDescription(in ImageDescription description) =>
            $"data=0x{description.Data.Address:X16}/0x{description.Data.Size:X} " +
            $"fmt={(uint)description.GuestFormat}/{(int)description.PixelFormat} " +
            $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} " +
            $"levels={description.Resources.Levels} layers={description.Resources.Layers} " +
            $"pitch={description.Pitch} bpb={description.BytesPerBlock} tile={(uint)description.TileMode} " +
            $"meta={(uint)description.Metadata.Kind}@0x{description.Metadata.Range.Address:X16}/0x{description.Metadata.Range.Size:X}";

        private readonly record struct BufferView(VkBuffer Buffer, ulong Offset, ulong Range);

        private sealed class DescriptorScratch
        {
            public readonly RenderScratchPool<TextureResource> Images = new();
            public readonly RenderScratchPool<Sampler> Samplers = new();
            public readonly RenderScratchPool<uint> ShaderData = new();
            public readonly RenderScratchPool<BufferView> Buffers = new();
            public readonly RenderScratchPool<(BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer)> Sources = new();
        }

        private DescriptorScratch? _descriptorScratch;
        private DescriptorScratch Scratch => _descriptorScratch ??= new();

        // The host descriptors of one stage in the order its binding layout names them.
        private sealed class StageDescriptors
        {
            public BufferView[] Buffers = [];
            public TextureResource[] Images = [];
            public Sampler[] Samplers = [];
            public BufferView GlobalDataShare;
            public BufferView FlattenedTable;
            public BufferView ShaderData;
        }

        private sealed class PreparedStageBindings(ShaderStageResources stage, ShaderProgramInfo program) : IPreparedBindings
        {
            public ShaderStageResources Stage => stage;

            public ShaderProgramInfo Program => program;

            public SpecializedResourceInfo Resources => program.Resources!;

            public BindingLayout Layout => program.Bindings!;

            public StageDescriptors Descriptors { get; } = new();

            public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer)[] BufferSources { get; set; } = [];

            public uint[] ShaderData { get; set; } = [];

            public TextureResource[] Textures => Descriptors.Images;

            public bool ScratchReleased { get; set; }
        }

        private static ShaderStage StageOf(ShaderProgramInfo program) => program.Stage switch
        {
            ShaderStageKind.Vertex => ShaderStage.Vertex,
            ShaderStageKind.Pixel => ShaderStage.Pixel,
            ShaderStageKind.Compute => ShaderStage.Compute,
            ShaderStageKind.Mesh => ShaderStage.Mesh,
            _ => throw SubmissionScheduler.Fatal($"The stage kind is unknown: stage={program.Stage} hash=0x{program.Hash:X16}."),
        };

        private static ShaderProgramInfo RequireProgram(ShaderStageResources stage)
        {
            var program = stage.Program ?? throw SubmissionScheduler.Fatal("The stage has no program.");
            if (program.Resources is null || program.Bindings is null)
            {
                throw SubmissionScheduler.Fatal($"The stage program has no resource plan: stage={program.Stage} hash=0x{program.Hash:X16}.");
            }

            return program;
        }

        // Every atomic image binding (integer or float) is declared as a
        // UINT storage image at the SPIR-V level (Gen5SpirvTranslator.
        // Resources.cs's DeclareImageClass forces R32ui format + uint sampled
        // type for all atomics, since the float atomics reach the real bits
        // through a bitcast + integer compare-exchange, not a native SPIR-V
        // float image atomic). The bound VkImageView must match that or
        // vkCmdDispatch fails VUID-vkCmdDispatch-format-07753 - so the view
        // request here ignores the image's logical Float numeric class for
        // an atomic binding and requests the same Uint reinterpretation.
        private static TextureNumericClass NumericClassOf(ImageResource image) =>
            image.Atomic
                ? TextureNumericClass.Uint
                : image.NumericClass switch
                {
                    ImageNumericClass.Uint => TextureNumericClass.Uint,
                    ImageNumericClass.Sint => TextureNumericClass.Sint,
                    _ => TextureNumericClass.Float,
                };

        private static ShaderImageShape ShapeOf(ImageResource image) => new(
            Volume: image.Dimension == ImageDimension.Dim3D,
            Arrayed: image.Cube || image.Dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray,
            Cube: image.Cube,
            Storage: image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage,
            DynamicMip: image.MipMode == ImageMipMode.DynamicStorage,
            NumericClass: NumericClassOf(image),
            OneDimensional: image.Dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray,
            R128: image.R128,
            Multisampled: image.Dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray,
            DepthCompare: image.DepthCompare,
            Atomic: image.Atomic);

        // Render-state discovery for one shader image; the view is acquired later with the draw.
        private TextureResource ResolveImageBinding(ImageResource image, uint[] words, ShaderProgramInfo program, int index)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"An image descriptor is too short: image={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            var storage = image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
            var descriptor = new TextureDescriptorWords(words);
            var resolution = ImageRequestBuilders.Texture(words, ShapeOf(image));
            _ = BeginBatchedGuestCommands();
            var request = resolution.Request;
            var imageIdentifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
            resolution = resolution with { Request = request };
            imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, imageIdentifier, resolution);
            if (ResourceTraceHashMatches(program.Hash))
            {
                var tracedImage = _imageCache.GetImage(imageIdentifier);
                Console.Error.WriteLine(
                    $"[RESOURCE-TRACE] image-resolved stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                    $"words={string.Join(',', words.Select(static word => $"{word:X8}"))} " +
                    $"addr=0x{descriptor.BaseAddress:X16} descriptorExtent={descriptor.Width + 1}x{descriptor.Height + 1}x{descriptor.Depth + 1} " +
                    $"type={(uint)descriptor.Type} format={(uint)descriptor.Format} tile={(uint)descriptor.TileMode} " +
                    $"baseLevel={descriptor.BaseLevel} lastLevel={descriptor.LastLevel} maxMip={descriptor.MaxMip} " +
                    $"requestView={request.View} cached={imageIdentifier} " +
                    FormatImageDescription(tracedImage.Description));
            }
            if (TextureTraceEnabled && TextureTraceHashMatches(program.Hash) && TextureTraceAddressMatches(descriptor.BaseAddress))
            {
                var tracedImage = _imageCache.GetImage(imageIdentifier);
                Console.Error.WriteLine(
                    $"[TEXTURE-TRACE] stage={program.Stage} hash=0x{program.Hash:X16} image={index} " +
                    $"words={string.Join(',', words.Select(static word => $"{word:X8}"))} " +
                    $"addr=0x{descriptor.BaseAddress:X16} view={request.View} " +
                    $"cached={imageIdentifier} registered={tracedImage.Registered} " +
                    $"cpuDirty={tracedImage.IsCpuDirty} definiteCpuDirty={tracedImage.IsDefinitelyCpuDirty} maybeCpuDirty={tracedImage.IsMaybeCpuDirty} " +
                    $"bufferModified={tracedImage.IsBufferModified} gpuModified={tracedImage.IsGpuModified} uses={tracedImage.Uses} " +
                    FormatImageDescription(tracedImage.Description));
            }
            BindImage(imageIdentifier, storage);
            if (ShouldTraceTextureBindings())
            {
                var cached = _imageCache.GetImage(imageIdentifier);
                var description = cached.Description;
                Console.Error.WriteLine(
                    $"TextureBinding stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                    $"address=0x{descriptor.BaseAddress:X16} " +
                    $"descriptor={descriptor.BaseAddress:X16} size={descriptor.Width + 1}x{descriptor.Height + 1} " +
                    $"format={(uint)descriptor.Format} tile={(uint)descriptor.TileMode} " +
                    $"image=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} " +
                    $"extent={description.Extent.Width}x{description.Extent.Height} pitch={description.Pitch} " +
                    $"guestFormat={(uint)description.GuestFormat} imageTile={(uint)description.TileMode} " +
                    $"backing={cached.Backing.Extent.Width}x{cached.Backing.Extent.Height} format={cached.Backing.Format}");
            }
            return new TextureResource
            {
                Address = descriptor.BaseAddress,
                ImageIdentifier = imageIdentifier,
                Request = request,
                IsStorage = storage,
                DestinationSelect = words[3] & 0xFFFu,
                Width = descriptor.Width,
                Height = descriptor.Height,
            };
        }

        // Compare bits stay only on depth-compare samplers; a forced point sampler drops its filters.
        // A sampler takes the numeric class of the views it samples; integer only when every
        // paired view is integer, since a float view needs a float border and filtering.
        private static bool SamplesIntegerViews(ShaderResourceInfo info, TextureResource[] images, int sampler)
        {
            var paired = false;
            foreach (var pair in info.SampledPairs)
            {
                if (pair.Sampler != sampler || pair.Image >= images.Length || images[pair.Image].IsHostMovie)
                {
                    continue;
                }

                if (!ViewFormatRules.IsIntegerFormat(images[pair.Image].Request.View.Format))
                {
                    return false;
                }

                paired = true;
            }

            return paired;
        }

        private Sampler ResolveSampler(SamplerResource sampler, uint[] words, ShaderProgramInfo program, int index, ShaderStageResources stage,
            bool integerView)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"A sampler descriptor is too short: sampler={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            Span<uint> native = stackalloc uint[4] { words[0], words[1], words[2], words[3] };
            if (!sampler.DepthCompare)
            {
                native[0] &= ~(0x7u << 12);
            }

            if (sampler.ForcePointFiltering)
            {
                var mipmapped = ((native[2] >> 26) & 0x3u) != 0;
                native[2] &= ~(0xFFu << 20);
                native[2] |= 1u << 24;
                if (mipmapped)
                {
                    native[2] |= 1u << 26;
                }
            }

            var descriptor = new SamplerDescriptorWords(native);
            if (descriptor.MaxAnisotropyRatio > 4 &&
                (descriptor.MagnifyFilter >= (uint)SamplerFilter.AnisotropicPoint ||
                 descriptor.MinifyFilter >= (uint)SamplerFilter.AnisotropicPoint))
            {
                Console.Error.WriteLine(
                    $"[GPU][ERROR] Sampler source: stage={program.Stage} hash=0x{program.Hash:X16} shader=0x{stage.ShaderBase:X16} " +
                    $"sampler={index} source={sampler.Source} pc=0x{sampler.FirstUsePc:X} " +
                    $"descriptor=[{string.Join(",", words.Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(",", stage.Resources.UserData.Select(word => $"{word:X8}"))}]");
            }

            return _samplerStore.GetSampler(descriptor, integerView);
        }

        // The guest textures the movie path matches; built only while a decoded frame is active.
        private static List<GuestDrawTexture>? MovieCandidates(SpecializedResourceInfo resources, ResourceSnapshot snapshot)
        {
            var textures = new List<GuestDrawTexture>(resources.Info.Images.Count);
            for (var index = 0; index < resources.Info.Images.Count; index++)
            {
                var words = snapshot.Images[index];
                var storage = resources.Info.Images[index].ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
                textures.Add(AgcExports.TryDecodeTextureDescriptor(words, out var descriptor)
                    ? new GuestDrawTexture(descriptor.Address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType, [], false, storage, DstSelect: descriptor.DstSelect)
                    : new GuestDrawTexture(0, 1, 1, 0, 0, [], true, storage));
            }

            return textures;
        }

        public IPreparedBindings PrepareBindings(ShaderStageResources stage)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var preparation = RequirePreparation();
            var program = RequireProgram(stage);
            var resources = program.Resources!;
            var layout = program.Bindings!;
            var snapshot = stage.Resources;
            var info = resources.Info;
            var prepared = new PreparedStageBindings(stage, program);
            // Register immediately so a partially prepared stage returns its rented scratch
            // storage when any later descriptor validation throws.
            preparation.Stages.Add(prepared);
            if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count || snapshot.Buffers.Length != info.Buffers.Count)
            {
                throw SubmissionScheduler.Fatal(
                    $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} images={snapshot.Images.Length}/{info.Images.Count} " +
                    $"samplers={snapshot.Samplers.Length}/{info.Samplers.Count} buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
            }

            if (ResourceTraceHashMatches(program.Hash))
            {
                Console.Error.WriteLine(
                    $"[RESOURCE-TRACE] begin stage={program.Stage} hash=0x{program.Hash:X16} " +
                    $"user={string.Join(',', snapshot.UserData.Select(static word => $"{word:X8}"))} " +
                    $"flat={string.Join(',', snapshot.FlattenedResourceTable.Select(static word => $"{word:X8}"))}");
            }

            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(info.Images.Count);
            var movieCandidates = _hostMovieFramePixels is null ? null : MovieCandidates(resources, snapshot);
            var hostMovie = movieCandidates is null ? HostMovieTextureBindings.None : FindHostMovieTextureBindings(movieCandidates);
            for (var index = 0; index < info.Images.Count; index++)
            {
                descriptors.Images[index] = index == hostMovie.Luma
                    ? CreateHostMovieTextureResource(movieCandidates![index], plane: 0)
                    : index == hostMovie.Chroma
                        ? CreateHostMovieTextureResource(movieCandidates![index], plane: 1)
                        : ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
            }

            descriptors.Samplers = Scratch.Samplers.Rent(info.Samplers.Count);
            for (var index = 0; index < info.Samplers.Count; index++)
            {
                descriptors.Samplers[index] = ResolveSampler(info.Samplers[index], snapshot.Samplers[index], program, index, stage,
                    SamplesIntegerViews(info, descriptors.Images, index));
            }

            var shaderData = Scratch.ShaderData.Rent(checked((int)layout.ShaderDataDwordCount));
            prepared.ShaderData = shaderData;
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                var userIndex = (int)(register - program.UserDataBase);
                if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
                {
                    throw SubmissionScheduler.Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
                }

                shaderData[index] = snapshot.UserData[userIndex];
            }

            if (layout.UsesShaderBase)
            {
                shaderData[layout.ShaderBaseDword] = (uint)stage.ShaderBase;
                shaderData[layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
            }

            stage.WriteDispatchThreadLimits(shaderData);
            if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
            {
                descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
            }

            FindDeviceAddressBuffers(prepared);
            FindBuffers(prepared);
            ValidateDrawImageTypes(prepared);
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"Bindings prepared stage={program.Stage} hash=0x{program.Hash:X16} buffers={info.Buffers.Count} images={info.Images.Count} " +
                    $"samplers={info.Samplers.Count} userData={snapshot.UserData.Length} flattened={snapshot.FlattenedResourceTable.Length} shaderData={shaderData.Length}");
            }

            return prepared;
        }

        // Establish the full device-address ownership range before descriptor subranges are
        // resolved, so a later range merge cannot replace a buffer captured by FindBuffers.
        private void FindDeviceAddressBuffers(PreparedStageBindings prepared)
        {
            foreach (var range in prepared.Stage.Resources.DeviceAddressRanges)
            {
                if (!range.Planned || range.Size == 0 ||
                    !GuestMemoryLayout.ContainsGpuAddressRange(range.Base, range.Size) ||
                    (!range.Written && !_guestMemory.CanRead(range.Base, 1)))
                {
                    continue;
                }

                _ = _bufferCache.FindBuffer(range.Base, ClampMappedSize(range.Base, range.Size));
            }
        }

        // Reject incompatible draw views before buffer writes or image transitions are recorded.
        private void ValidateDrawImageTypes(PreparedStageBindings prepared)
        {
            if (prepared.Program.Stage == ShaderStageKind.Compute)
            {
                return;
            }

            foreach (var binding in prepared.Textures)
            {
                if (binding.IsHostMovie || IsStaleImage(binding.ImageIdentifier, out var image) || image is null)
                {
                    continue;
                }

                var view = binding.IsStorage ? binding.Request.View with { LevelCount = 1 } : binding.Request.View;
                if (!image.SupportsViewType(view))
                {
                    throw new DrawImageTypeMismatchException(
                        prepared.Program.Hash, binding.Address, image.Backing.ImageType, view.Type);
                }
            }
        }

        // The cache buffer each descriptor's range lives in; a null descriptor has none.
        private void FindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var snapshot = prepared.Stage.Resources;
            var sources = Scratch.Sources.Rent(prepared.Resources.Info.Buffers.Count);
            prepared.BufferSources = sources;
            for (var index = 0; index < sources.Length; index++)
            {
                var words = snapshot.Buffers[index];
                if (words.Length < 4)
                {
                    throw SubmissionScheduler.Fatal($"A buffer descriptor is too short: buffer={index} words={words.Length} hash=0x{program.Hash:X16}.");
                }

                var descriptor = BufferDescriptorWords.From(words);
                var requested = descriptor.Footprint() ?? throw SubmissionScheduler.Fatal(
                    $"A storage buffer descriptor footprint overflows: buffer={index} stride={descriptor.Stride} records={descriptor.RecordCount} hash=0x{program.Hash:X16}.");
                if (descriptor.Address == 0 || requested == 0)
                {
                    sources[index] = (descriptor, default);
                    continue;
                }

                var size = ClampMappedSize(descriptor.Address, requested, prepared, index);
                sources[index] = (descriptor, _bufferCache.FindBuffer(descriptor.Address, size));
            }

        }

        // Uploads every mapped range into the cache before a device-address draw; the fault pass follows.
        public void PrepareDeviceAddresses()
        {
            var preparation = RequirePreparation();
            ulong vertexProgramHash = 0, pixelProgramHash = 0, computeProgramHash = 0;
            if (BufferUploadProfile.Enabled)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    switch (stage.Program.Stage)
                    {
                        case ShaderStageKind.Vertex: vertexProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Mesh: vertexProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Pixel: pixelProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Compute: computeProgramHash = stage.Program.Hash; break;
                    }
                }
            }
            using var profileScope = BufferUploadProfile.BeginSweep(vertexProgramHash, pixelProgramHash, computeProgramHash);
            var memory = GuestGpuMemoryHook.Current ?? throw SubmissionScheduler.Fatal("A device-address program needs the guest GPU memory registry.");
            var spans = DeviceAddressSpans(memory);
            var traceAddress = GuestGpuMemoryHook.TraceAddress;
            if (traceAddress != 0)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    GuestGpuMemoryHook.Trace(traceAddress, 1,
                        $"device-address-program submission_tick={_scheduler.CurrentTick} stage={stage.Program.Stage} hash=0x{stage.Program.Hash:X16} shader=0x{stage.Stage.ShaderBase:X16} ranges={stage.Stage.Resources.DeviceAddressRanges.Length}");
                    foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                        GuestGpuMemoryHook.Trace(traceAddress, 1,
                            $"device-address-range submission_tick={_scheduler.CurrentTick} hash=0x{stage.Program.Hash:X16} handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} planned={range.Planned} written={range.Written}");
                }
            }
            if (traceAddress != 0 && !memory.Covers(traceAddress, 1))
                GuestGpuMemoryHook.Trace(traceAddress, 1,
                    $"device-address-mapping-check readable={_guestMemory.CanRead(traceAddress, 1)} backed={_guestBacking.IsBackedView(traceAddress)}");
            _bufferCache.PrepareBda(spans, _bdaSpanMapping);
        }

        private List<GuestSpan>? _bdaSpans;
        private GuestGpuMemory? _bdaSpanMemory;
        private long _bdaSpanVersion = -1;
        private ulong _bdaSpanMapping;

        private List<GuestSpan> DeviceAddressSpans(GuestGpuMemory memory)
        {
            var version = memory.SpanVersion;
            if (_bdaSpans is { } cached && ReferenceEquals(_bdaSpanMemory, memory) && _bdaSpanVersion == version)
            {
                return cached;
            }

            var spans = new List<GuestSpan>();
            memory.ForEachSpan((address, size) => spans.Add(new GuestSpan(address, size)));
            _bdaSpans = spans;
            _bdaSpanMemory = memory;
            _bdaSpanVersion = version;
            _bdaSpanMapping = GuestBufferCache.MappingKey(spans);
            return spans;
        }

        public void BindResources(IPreparedBindings prepared)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var stage = (PreparedStageBindings)prepared;
            BindBuffers(stage);
            ObtainDeviceAddressRanges(stage);
            BindImages(stage);
            _preparedTextures.Add(stage.Textures);
        }

        // Textures bound for the draw being prepared; cleared when its preparation closes.
        private readonly List<TextureResource[]> _preparedTextures = new();

        bool IRenderHost.SamplesDepthAttachment(in DepthAttachmentState depth)
        {
            var image = _imageCache.GetImage(depth.Image);
            var attachmentView = depth.Target.Target.Request.View;
            foreach (var textures in _preparedTextures)
            {
                foreach (var texture in textures)
                {
                    if (!texture.IsHostMovie && ReferenceEquals(texture.CachedImage, image) && ViewsOverlap(texture.Request.View, attachmentView))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private BufferView NullStorageBuffer() => new(_bufferCache.GetBuffer(GuestBufferCache.NullBufferId).Handle, 0, NullStorageBufferBytes);

        // A storage buffer view on the cache buffer, aligned down with the adjustment carried in the memory offsets.
        private BufferView BindStorageBuffer(
            in BufferDescriptorWords descriptor,
            BufferResource resource,
            ShaderProgramInfo program,
            int slot,
            ResourceSlotIdentifier bufferIdentifier,
            out uint memoryOffset)
        {
            memoryOffset = 0;
            var address = descriptor.Address;
            var requested = descriptor.Footprint() ?? throw SubmissionScheduler.Fatal($"A storage buffer descriptor footprint overflows: buffer={slot} hash=0x{program.Hash:X16}.");
            if (address == 0 || requested == 0)
            {
                return NullStorageBuffer();
            }

            var size = ClampMappedSize(address, requested);
            // The same descriptor is aliased as uint[] and ulong[] in shaders
            // that use BUFFER_ATOMIC_*_X2. Keeping every storage-buffer base
            // eight-byte aligned makes the shader's byte bias preserve qword
            // indexing even when the guest descriptor itself starts at +4.
            var alignment = Math.Max(_minStorageBufferOffsetAlignment, (ulong)sizeof(ulong));
            var maxRange = _deviceInfo.MaxStorageBufferRange;
            if (alignment == 0 || size > maxRange)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer range or the device alignment is unsupported: buffer={slot} size=0x{size:X} alignment={alignment} hash=0x{program.Hash:X16}.");
            }

            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, resource.Written, isTexelBuffer: resource.Formatted, bufferIdentifier);
            var alignedOffset = offset - offset % alignment;
            var adjustment = offset - alignedOffset;
            if (adjustment % sizeof(uint) != 0 || adjustment >= MaxMemoryOffsetAdjustment || size > maxRange - adjustment)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer offset adjustment is unsupported: buffer={slot} adjustment={adjustment} hash=0x{program.Hash:X16}.");
            }

            memoryOffset = (uint)adjustment;
            if (resource.Formatted && resource.Written)
            {
                _imageCache.InvalidateMemoryForBoundWrite(address, size);
            }

            return new BufferView(buffer.Handle, alignedOffset, size + adjustment);
        }

        private void TraceBufferBinding(
            ShaderProgramInfo program,
            int index,
            in BufferDescriptorWords descriptor,
            BufferResource resource,
            in BufferView view)
        {
            if (!ResourceTraceHashMatches(program.Hash))
            {
                return;
            }

            var footprint = descriptor.Footprint() ?? 0;
            var sampleSize = descriptor.Address == 0 ? 0 : (int)Math.Min(64ul, footprint);
            Span<byte> guestSample = stackalloc byte[64];
            Span<byte> backingSample = stackalloc byte[64];
            var guestRead = sampleSize != 0 && _guestMemory.TryRead(descriptor.Address, guestSample[..sampleSize]);
            var backingRead = sampleSize != 0 && _guestBacking.TryReadBacking(descriptor.Address, backingSample[..sampleSize]);
            Console.Error.WriteLine(
                $"[RESOURCE-TRACE] buffer stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                $"words={descriptor.Word0:X8},{descriptor.Word1:X8},{descriptor.Word2:X8},{descriptor.Word3:X8} " +
                $"addr=0x{descriptor.Address:X16} stride={descriptor.Stride} records={descriptor.RecordCount} footprint=0x{footprint:X} " +
                $"read={resource.Read} written={resource.Written} atomic={resource.Atomic} formatted={resource.Formatted} scalar={resource.Scalar} " +
                $"maxExtent={resource.MaxByteExtent} packedStride=0x{resource.PackedStride:X} " +
                $"hostBuffer=0x{view.Buffer.Handle:X16} offset=0x{view.Offset:X} range=0x{view.Range:X} " +
                $"guestRead={guestRead} guest={Convert.ToHexString(guestSample[..sampleSize])} " +
                $"backingRead={backingRead} backing={Convert.ToHexString(backingSample[..sampleSize])}");
        }

        private BufferView UploadDwords(uint[] data, string label, ShaderProgramInfo program)
        {
            if (data.Length == 0)
            {
                throw SubmissionScheduler.Fatal($"The {label} upload is empty: hash=0x{program.Hash:X16}.");
            }

            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(data);
            var binding = UploadTransient(bytes, Math.Max(TransientDataAlignment, (uint)_minStorageBufferOffsetAlignment));
            return new BufferView(new VkBuffer(binding.Handle), binding.Offset, (ulong)bytes.Length);
        }

        private void BindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var shaderData = prepared.ShaderData;
            if (prepared.BufferSources.Length != info.Buffers.Count || shaderData.Length != layout.ShaderDataDwordCount)
            {
                throw SubmissionScheduler.Fatal($"The prepared bindings are stale: hash=0x{program.Hash:X16}.");
            }

            // Reset only packed buffer offsets; dispatch limits follow them in shader data.
            Array.Clear(shaderData, (int)layout.MemoryOffsetDword, (int)((layout.MemoryOffsetCount + 3) / 4));
            var views = prepared.Descriptors.Buffers;
            if (views.Length != info.Buffers.Count)
            {
                Scratch.Buffers.Return(views);
                views = Scratch.Buffers.Rent(info.Buffers.Count);
                prepared.Descriptors.Buffers = views;
            }
            for (var index = 0; index < views.Length; index++)
            {
                var (descriptor, bufferIdentifier) = prepared.BufferSources[index];
                views[index] = BindStorageBuffer(in descriptor, info.Buffers[index], program, index, bufferIdentifier, out var memoryOffset);
                TraceBufferBinding(program, index, in descriptor, info.Buffers[index], in views[index]);
                var dword = layout.MemoryOffsetDword + (uint)index / 4;
                var shift = ((uint)index % 4) * 8;
                shaderData[dword] |= memoryOffset << (int)shift;
            }

            if (layout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null)
            {
                prepared.Descriptors.FlattenedTable = UploadDwords(snapshot.FlattenedResourceTable, "flattened resource table", program);
            }

            if (layout.Find(DescriptorBindingKind.ShaderData) is not null)
            {
                prepared.Descriptors.ShaderData = UploadDwords(shaderData, "shader data", program);
            }
        }

        // Device-address reads need persistent page-table entries before the shader runs.
        private void ObtainDeviceAddressRanges(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            foreach (var range in prepared.Stage.Resources.DeviceAddressRanges)
            {
                if (!range.Planned)
                {
                    if (!range.Written) continue;
                    throw SubmissionScheduler.Fatal($"A written device-address range cannot be planned: handle={range.Handle} hash=0x{program.Hash:X16}.");
                }

                if (range.Size == 0)
                {
                    continue;
                }

                if (!GuestMemoryLayout.ContainsGpuAddressRange(range.Base, range.Size))
                {
                    throw SubmissionScheduler.Fatal($"A device-address range is outside the cache: handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} hash=0x{program.Hash:X16}.");
                }

                // Unmapped read-only pointers resolve to zero through the page table.
                if (!range.Written && !_guestMemory.CanRead(range.Base, 1))
                {
                    continue;
                }

                var size = ClampMappedSize(range.Base, range.Size);
                if (range.Written)
                {
                    _ = _bufferCache.ObtainBuffer(range.Base, size, isWritten: true);
                }
                else
                {
                    // Stream buffers do not populate the device-address page table.
                    _ = _bufferCache.FindBuffer(range.Base, size);
                    _bufferCache.SynchronizeBuffersInRange(range.Base, size);
                }
            }
        }

        // Views for every image after the targets are bound; a stale image is found again first.
        private void BindImages(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var images = prepared.Descriptors.Images;
            for (var index = 0; index < images.Length; index++)
            {
                var binding = images[index];
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

                    images[index] = binding = ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
                }
            }

            for (var index = 0; index < images.Length; index++)
            {
                var binding = images[index];
                if (binding.IsHostMovie)
                {
                    continue;
                }

                var resource = info.Images[index];
                var view = binding.Request.View;
                var image = _imageCache.GetImage(binding.ImageIdentifier);
                if (binding.IsStorage && image.Description.HasStencil && ViewFormatRules.IsStencilViewFormat(view.Format))
                {
                    image = AcquireStencilStorage(binding, image);
                    binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    binding.View = image.GetOrCreateView(binding.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                    binding.MipViews = [];
                }
                else if (resource.MipMode == ImageMipMode.DynamicStorage)
                {
                    if (resource.MipCount == 0 || resource.MipCount != view.LevelCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"A storage image's mip count does not match its view: image={index} mips={resource.MipCount} levels={view.LevelCount} hash=0x{program.Hash:X16}.");
                    }

                    var mipViews = new ImageView[resource.MipCount];
                    for (var mip = 0u; mip < resource.MipCount; mip++)
                    {
                        var mipRequest = binding.Request with { View = view with { BaseLevel = view.BaseLevel + mip, LevelCount = 1 } };
                        mipViews[mip] = _imageCache.AcquireTextureView(binding.ImageIdentifier, mipRequest);
                    }

                    binding.MipViews = mipViews;
                    binding.View = mipViews[0];
                }
                else
                {
                    if (binding.IsStorage)
                    {
                        binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    }

                    binding.View = _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                    binding.MipViews = [];
                }

                if (TextureTraceEnabled && TextureTraceHashMatches(program.Hash) && TextureTraceAddressMatches(binding.Address))
                {
                    Console.Error.WriteLine(
                        $"[TEXTURE-TRACE] bound stage={program.Stage} hash=0x{program.Hash:X16} image={index} " +
                        $"addr=0x{binding.Address:X16} imageHandle=0x{binding.Image.Handle:X} view=0x{binding.View.Handle:X} " +
                        $"layout={binding.Layout} requestView={binding.Request.View} " +
                        $"cached={binding.CachedImage is not null} " +
                        (binding.CachedImage is { } cached ? FormatImageDescription(cached.Description) : "host-movie"));
                }

                if (ResourceTraceHashMatches(program.Hash))
                {
                    Console.Error.WriteLine(
                        $"[RESOURCE-TRACE] image-bound stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                        $"addr=0x{binding.Address:X16} imageHandle=0x{image.Backing.Handle.Handle:X16} view=0x{binding.View.Handle:X16} " +
                        $"layout={binding.Layout} storage={binding.IsStorage} requestView={binding.Request.View} " +
                        FormatImageDescription(image.Description));
                }

                image.Uses.Storage |= binding.IsStorage;
                image.Uses.Texture |= !binding.IsStorage;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
            }
        }

        private void DestroyStageBindings(PreparedStageBindings stage)
        {
            ReleaseStageScratch(stage, recycleStagingBuffers: true);
        }

        private void ReleaseStageScratch(PreparedStageBindings stage, bool recycleStagingBuffers)
        {
            if (stage.ScratchReleased)
            {
                return;
            }

            if (recycleStagingBuffers)
            {
                foreach (var texture in stage.Textures)
                {
                    if (texture is { StagingBuffer.Handle: not 0 })
                    {
                        RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                    }
                }
            }

            Scratch.Images.Return(stage.Descriptors.Images);
            Scratch.Samplers.Return(stage.Descriptors.Samplers);
            Scratch.Buffers.Return(stage.Descriptors.Buffers);
            Scratch.Sources.Return(stage.BufferSources);
            Scratch.ShaderData.Return(stage.ShaderData);
            stage.Descriptors.Images = [];
            stage.Descriptors.Samplers = [];
            stage.Descriptors.Buffers = [];
            stage.BufferSources = [];
            stage.ShaderData = [];
            stage.ScratchReleased = true;
        }

        private static DescriptorImageInfo ImageInfo(TextureResource texture, uint element, ShaderProgramInfo program, int index)
        {
            var view = texture.MipViews.Length == 0
                ? (element == 0 ? texture.View : default)
                : (element < (uint)texture.MipViews.Length ? texture.MipViews[element] : default);
            if (view.Handle == 0 || texture.Layout == ImageLayout.Undefined)
            {
                throw SubmissionScheduler.Fatal($"An image binding has no view for its element: image={index} element={element} hash=0x{program.Hash:X16}.");
            }

            return new DescriptorImageInfo { ImageView = view, ImageLayout = texture.Layout };
        }

        // Joins every stage's writes, records the image work and binds the set by push or from the heap.
        public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorCommit);
            if (bindPoint == PipelineBindPoint.Graphics)
            {
                _pendingAttachmentFeedbackLoopAspects = 0;
            }

            var preparation = RequirePreparation();
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestCommands();
            _commandBuffer = command;
            var writeCount = 0;
            var bufferCount = 0;
            var imageCount = 0;
            var textureCount = 0;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                var stageFlag = DescriptorWriter.ShaderStageFlag(StageOf(stage.Program));
                if ((bindPoint == PipelineBindPoint.Graphics &&
                        (stageFlag & (ShaderStageFlags.VertexBit | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit)) == 0) ||
                    (bindPoint == PipelineBindPoint.Compute && stageFlag != ShaderStageFlags.ComputeBit))
                {
                    throw SubmissionScheduler.Fatal($"A stage does not belong to the bind point: stage={stage.Program.Stage} bindPoint={bindPoint}.");
                }

                foreach (var binding in stage.Layout.Descriptors)
                {
                    writeCount++;
                    var count = (int)DescriptorWriter.DescriptorCount(binding);
                    if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None || binding.Kind == DescriptorBindingKind.Samplers)
                    {
                        imageCount += count;
                    }
                    else
                    {
                        bufferCount += count;
                    }
                }

                textureCount += stage.Textures.Length;
            }

            // All stages that read the same stencil bytes share the shader's working image.
            if (preparation.StencilStorageImages is { } stencilImages)
            {
                foreach (var prepared in stages)
                {
                    foreach (var texture in ((PreparedStageBindings)prepared).Textures)
                    {
                        if (texture.CachedImage is { } attachment && ViewFormatRules.IsStencilViewFormat(texture.Request.View.Format) &&
                            stencilImages.TryGetValue(attachment, out var storage))
                        {
                            texture.CachedImage = storage;
                            texture.Image = storage.Backing.Handle;
                            texture.View = storage.GetOrCreateView(texture.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                            texture.MipViews = [];
                        }
                    }
                }
            }

            var bufferInfos = EnsureDescriptorScratchCapacity(ref _descriptorBufferInfoScratch, bufferCount);
            var imageInfos = EnsureDescriptorScratchCapacity(ref _descriptorImageInfoScratch, imageCount);
            var writes = EnsureDescriptorScratchCapacity(ref _descriptorWriteScratch, writeCount);
            var pushData = stackalloc uint[(int)PushData.DwordCount];
            var hasPushData = false;
            var bufferIndex = 0;
            var imageIndex = 0;
            var writeIndex = 0;
            var uploadStage = bindPoint == PipelineBindPoint.Compute ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.FragmentShaderBit;
            var textures = textureCount == 0
                ? []
                : System.Buffers.ArrayPool<TextureResource>.Shared.Rent(textureCount);
            var textureIndex = 0;
            var texturesTransferred = false;
            try
            {
                fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
                fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
                {
                    foreach (var prepared in stages)
                    {
                        var stage = (PreparedStageBindings)prepared;
                        var program = stage.Program;
                        var descriptors = stage.Descriptors;
                        var shaderStage = StageOf(program);
                        var stageFlag = DescriptorWriter.ShaderStageFlag(shaderStage);
                        if (descriptors.GlobalDataShare.Buffer.Handle != 0)
                        {
                            // The host and every earlier queue write to the data share complete before the shader reads it.
                            EndRendering();
                            command = BeginBatchedGuestCommands();
                            var barrier = GlobalDataShareBarrier.Make(descriptors.GlobalDataShare.Buffer);
                            VulkanSynchronization.PipelineBarrier(_vk,command, GlobalDataShareBarrier.SourceStages, DescriptorWriter.PipelineStageFlag(stageFlag), 0, 0, null, 1, &barrier, 0, null);
                        }

                    RecordStageTextureTransitions(stage.Textures);
                    foreach (var texture in stage.Textures)
                    {
                        if (texture.IsHostMovie && texture.NeedsUpload)
                        {
                            EndRendering();
                            break;
                        }
                    }

                    RecordHostMovieUploads(stage.Textures, uploadStage);
                    foreach (var texture in stage.Textures)
                    {
                        textures[textureIndex++] = texture;
                    }

                    var occurrences = EnsureDescriptorScratchCapacity(
                        ref _descriptorImageOccurrenceScratch,
                        descriptors.Images.Length);
                    occurrences.AsSpan(0, descriptors.Images.Length).Clear();
                    foreach (var binding in stage.Layout.Descriptors)
                    {
                        var write = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstBinding = BindingLayout.NativeBindingIndex(shaderStage, binding.Kind),
                            DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                            DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                        };
                        var bufferStart = bufferIndex;
                        var imageStart = imageIndex;
                        if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None)
                        {
                            foreach (var resource in binding.Resources)
                            {
                                imageInfos[imageIndex++] = ImageInfo(descriptors.Images[(int)resource], occurrences[(int)resource]++, program, (int)resource);
                            }
                        }
                        else
                        {
                            switch (binding.Kind)
                            {
                                case DescriptorBindingKind.Buffers:
                                    foreach (var resource in binding.Resources)
                                    {
                                        var view = descriptors.Buffers[(int)resource];
                                        if (view.Buffer.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A storage buffer binding has no buffer: buffer={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    }

                                    break;
                                case DescriptorBindingKind.DeviceAddressPageTable:
                                case DescriptorBindingKind.FaultBuffer:
                                {
                                    var shared = binding.Kind == DescriptorBindingKind.DeviceAddressPageTable ? _bufferCache.BdaPageTableBuffer : _bufferCache.FaultBuffer;
                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = shared.Handle, Offset = 0, Range = shared.Size };
                                    break;
                                }

                                case DescriptorBindingKind.FlattenedResourceTable:
                                case DescriptorBindingKind.ShaderData:
                                case DescriptorBindingKind.GlobalDataShare:
                                {
                                    var view = binding.Kind switch
                                    {
                                        DescriptorBindingKind.FlattenedResourceTable => descriptors.FlattenedTable,
                                        DescriptorBindingKind.ShaderData => descriptors.ShaderData,
                                        _ => descriptors.GlobalDataShare,
                                    };
                                    if (view.Buffer.Handle == 0)
                                    {
                                        throw SubmissionScheduler.Fatal($"A shared buffer binding has no buffer: kind={binding.Kind} hash=0x{program.Hash:X16}.");
                                    }

                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    break;
                                }

                                case DescriptorBindingKind.Samplers:
                                    foreach (var resource in binding.Resources)
                                    {
                                        var sampler = descriptors.Samplers[(int)resource];
                                        if (sampler.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A sampler binding has no sampler: sampler={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        imageInfos[imageIndex++] = new DescriptorImageInfo { Sampler = sampler, ImageLayout = ImageLayout.Undefined };
                                    }

                                    break;
                                default:
                                    throw SubmissionScheduler.Fatal($"The descriptor binding kind is invalid: kind={binding.Kind}.");
                            }
                        }

                        if (bufferIndex != bufferStart)
                        {
                            write.PBufferInfo = bufferInfoPointer + bufferStart;
                        }

                        if (imageIndex != imageStart)
                        {
                            write.PImageInfo = imageInfoPointer + imageStart;
                        }

                        writes[writeIndex++] = write;
                    }

                    for (var index = 0; index < descriptors.Images.Length; index++)
                    {
                        var expected = descriptors.Images[index].MipViews.Length == 0 ? 1u : (uint)descriptors.Images[index].MipViews.Length;
                        if (occurrences[index] != expected)
                        {
                            throw SubmissionScheduler.Fatal($"An image is bound a different number of times than its views: image={index} occurrences={occurrences[index]} views={expected} hash=0x{program.Hash:X16}.");
                        }
                    }

                    if (stage.ShaderData.Length != stage.Layout.ShaderDataDwordCount)
                    {
                        throw SubmissionScheduler.Fatal($"The shader data does not match the layout: dwords={stage.ShaderData.Length} layout={stage.Layout.ShaderDataDwordCount} hash=0x{program.Hash:X16}.");
                    }

                    if (stage.Layout.UsesPushData)
                    {
                        for (var index = 0; index < stage.ShaderData.Length; index++)
                        {
                            pushData[stage.Layout.PushDataStartDword + index] = stage.ShaderData[index];
                        }

                        hasPushData = true;
                    }
                }

                    if (hasPushData)
                    {
                        _vk.CmdPushConstants(
                            command,
                            entry.Layout,
                            entry.PushConstantStages,
                            0,
                            PushData.ByteSize,
                            pushData);
                    }

                    if (writeIndex != 0)
                    {
                        fixed (WriteDescriptorSet* writePointer = writes)
                        {
                            if (entry.UsesPushDescriptors)
                            {
                                _pushDescriptorApi.CmdPushDescriptorSet(command, bindPoint, entry.Layout, 0, (uint)writeIndex, writePointer);
                                ShaderCacheCounters.CountPushSet();
                            }
                            else
                            {
                                var set = _descriptorHeap.Commit(entry.SetLayout, in entry.Demand);
                                for (var index = 0; index < writeIndex; index++)
                                {
                                    writes[index].DstSet = set;
                                }

                                _vk.UpdateDescriptorSets(_device, (uint)writeIndex, writePointer, 0, null);
                                _vk.CmdBindDescriptorSets(command, bindPoint, entry.Layout, 0, 1, &set, 0, null);
                                ShaderCacheCounters.CountHeapSet();
                            }
                        }
                    }
                }

                _batchResources.Add(new SubmissionUploadResources
                {
                    DebugName = bindPoint == PipelineBindPoint.Compute ? "SharpEmu dispatch" : "SharpEmu draw",
                    Textures = textures,
                    TextureCount = textureCount,
                    FeedbackSnapshots = preparation.FeedbackSnapshots?.ToArray() ?? [],
                    OverflowBuffers = preparation.OverflowBuffers.Count == 0 ? null : preparation.OverflowBuffers.ToArray(),
                });
                texturesTransferred = true;
                preparation.OverflowBuffers.Clear();
                preparation.Committed = true;
                foreach (var prepared in stages)
                {
                    ReleaseStageScratch((PreparedStageBindings)prepared, recycleStagingBuffers: false);
                }

                preparation.Stages.Clear();
                if (RenderTrace.Enabled && RenderTrace.Pipeline())
                {
                    RenderTrace.Write($"Bindings committed bindPoint={bindPoint} pipeline={pipeline.Pipeline} writes={writeIndex} push={entry.UsesPushDescriptors} pushData={hasPushData}");
                }
            }
            finally
            {
                if (!texturesTransferred && textures.Length != 0)
                {
                    System.Buffers.ArrayPool<TextureResource>.Shared.Return(textures, clearArray: true);
                }
            }
        }
    }
}
