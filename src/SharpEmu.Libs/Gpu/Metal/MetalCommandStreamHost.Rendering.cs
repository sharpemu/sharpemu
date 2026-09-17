// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using ImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;

namespace SharpEmu.Libs.Gpu.Metal;

// The render host over the Metal seam: the executor's resolved state becomes one copied draw record per draw.
internal sealed partial class MetalCommandStreamHost : IRenderHost, IShaderPipelineHost
{
    private const uint MaxDimension = 16384;
    private const ulong MappedPageSize = 4096;
    private const uint SingleRectangleVertexCount = 4;
    private const ulong NullBufferBytes = 16;
    private const ulong DeviceAddressPageSize = DeviceAddressPaging.PageSize;
    private const uint AcceptedSignedShortPairFormat = 113;
    private const uint AcceptedHalfPairFormat = 121;
    private const uint DataFormat32x4 = 14;
    private const uint DataFormat16x2 = 5;
    private const uint NumberFormatFloat = 7;

    // Every image format the target builders ask about is accepted; the Metal presenter picks its own formats.
    private sealed class AcceptingFormatSupport : IImageFormatSupport
    {
        public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
        {
            properties = new ImageFormatProperties
            {
                SampleCounts = SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit,
                MaxExtent = new Extent3D(MaxDimension, MaxDimension, 1),
                MaxMipLevels = 15,
                MaxArrayLayers = 2048,
            };
            return true;
        }
    }

    // One stage of a draw: its snapshots, samplers and shader data, then the copied buffers once bound.
    private sealed class PreparedStage(ShaderStageResources stage, ShaderProgramInfo program) : IPreparedBindings
    {
        public ShaderStageResources Stage => stage;

        public ShaderProgramInfo Program => program;

        public SpecializedResourceInfo Resources => program.Resources!;

        public BindingLayout Layout => program.Bindings!;

        public GuestDrawTexture[] Textures { get; set; } = [];

        public GuestSampler[] Samplers { get; set; } = [];

        public uint[] ShaderData { get; set; } = [];

        public GuestMemoryBuffer[] Buffers { get; set; } = [];

        public GuestMemoryBuffer[] RangeBuffers { get; set; } = [];

        public GuestAddressRange[] Ranges { get; set; } = [];

        public bool Bound { get; set; }
    }

    private sealed class Preparation(MetalCommandStreamHost owner) : IResourcePreparation
    {
        public bool DeviceAddressesPrepared { get; set; }

        public void Dispose()
        {
            if (ReferenceEquals(owner._preparation, this))
            {
                owner._preparation = null;
            }
        }
    }

    // A guest range the executor obtained, or bytes it uploaded; the draw record copies from it.
    private sealed record BoundBuffer(ulong Address, ulong Size, byte[]? Bytes);

    private sealed record GraphicsPipelineRecord(
        MetalCompiledGuestShader VertexShader,
        MetalCompiledGuestShader? PixelShader,
        ShaderProgramInfo VertexStage,
        uint AttributeCount,
        PipelineStaticParameters Parameters);

    private sealed record ComputePipelineRecord(MetalCompiledGuestShader Shader, ComputeInputInfo Input, ShaderProgramInfo Stage);

    private static readonly IImageFormatSupport _formatSupport = new AcceptingFormatSupport();
    private readonly CpuContext _context;
    private readonly Dictionary<ulong, ResourceSlotIdentifier> _imageIdentifiers = new();
    private readonly Dictionary<ResourceSlotIdentifier, GuestRenderTarget> _guestTargets = new();
    private readonly Dictionary<ulong, BoundBuffer> _buffers = new();
    private readonly Dictionary<ulong, MetalCompiledGuestShader> _modules = new();
    private readonly Dictionary<ulong, object> _pipelineRecords = new();
    private readonly List<ColorTargetState> _colorTargets = new();
    private readonly List<PreparedStage> _committedStages = new();
    private ulong _nextBufferHandle = 1;
    private ulong _nextModuleHandle = 1;
    private ulong _nextPipelineHandle = 1;
    private uint _nextImageIndex = 1;
    private Preparation? _preparation;
    private DepthAttachmentState _depth;
    private bool _hasDepth;
    private DynamicDrawState _dynamicState;
    private BufferBinding[] _vertexBindings = [];
    private VertexInputInfo? _vertexInput;
    private BufferBinding? _indexBinding;
    private IndexType _indexType;
    private object? _boundPipeline;

    private IGuestGpuBackend Backend => _backend;

    private ContextRegisters RequireContext() =>
        Translation.CurrentContextRegisters ?? throw Fatal("The command stream has no current context registers.");

    public bool TryResolveColorOutput(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out Gen5PixelOutputKind outputKind,
        out Gen5ColorComponentMapping componentMapping)
    {
        if (MetalGuestFormats.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                componentSwap,
                out var format))
        {
            outputKind = format.OutputKind;
            componentMapping = format.ExportMapping;
            return true;
        }

        outputKind = default;
        componentMapping = default;
        return false;
    }

    private bool CanReadGuest(ulong address)
    {
        Span<byte> probe = stackalloc byte[1];
        return Memory.TryRead(address, probe);
    }

    // ---- IShaderPipelineHost ----

    // Argument buffers replace push descriptors; the wave64 kernel and subgroup operations come from the translator.
    uint IShaderPipelineHost.MaxPushDescriptors => 0;

    bool IShaderPipelineHost.ComputeWave64Supported => true;

    bool IShaderPipelineHost.GraphicsSubgroupOperationsEnabled => true;

    RenderHostLimits IShaderPipelineHost.Limits => new(MaxDimension, MaxDimension, MaxDimension, MaxDimension);

    SampleCountFlags IShaderPipelineHost.NoAttachmentSampleCounts =>
        SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit;

    // Guest memory is the only backing on Metal; GPU writes reach it through the completion write-back.
    bool IShaderPipelineHost.TryReadGuestWord(ulong address, out uint word) => TryReadWord(address, out word);

    bool IShaderPipelineHost.TryReadCleanGuestWord(ulong address, out uint word) => TryReadWord(address, out word);

    private bool TryReadWord(ulong address, out uint word)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!Memory.TryRead(address, bytes))
        {
            word = 0;
            return false;
        }

        word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    ulong IShaderPipelineHost.CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
    {
        var compiled = shader as MetalCompiledGuestShader
            ?? throw Fatal($"The compiled shader is not a Metal shader: stage={stage} hash=0x{hash:X16} program={programId} type={shader.GetType().Name}.");
        var handle = _nextModuleHandle++;
        _modules[handle] = compiled;
        return handle;
    }

    private MetalCompiledGuestShader RequireModule(ShaderProgram program) =>
        _modules.TryGetValue(program.Module, out var shader) ? shader : throw Fatal($"The program has no Metal module: program={program.Id} module={program.Module}.");

    PipelineHandle IShaderPipelineHost.CreateGraphicsPipeline(GraphicsPipelineDescription description)
    {
        var record = new GraphicsPipelineRecord(
            RequireModule(description.VertexProgram),
            description.PixelStage is null ? null : RequireModule(description.PixelProgram),
            description.VertexStage,
            description.PixelInfo?.InputCount ?? 0,
            description.StaticParameters);
        var handle = _nextPipelineHandle++;
        _pipelineRecords[handle] = record;
        return new PipelineHandle(handle, handle, UsesPushDescriptors: false);
    }

    PipelineHandle IShaderPipelineHost.CreateComputePipeline(ComputePipelineDescription description)
    {
        var handle = _nextPipelineHandle++;
        _pipelineRecords[handle] = new ComputePipelineRecord(RequireModule(description.Program), description.Input, description.Stage);
        return new PipelineHandle(handle, handle, UsesPushDescriptors: false);
    }

    // ---- IRenderHost ----

    RenderHostLimits IRenderHost.Limits => new(MaxDimension, MaxDimension, MaxDimension, MaxDimension);

    IImageFormatSupport IRenderHost.FormatSupport => _formatSupport;

    // The ordered queue of the presenter takes every record; nothing waits on a command buffer.
    bool IRenderHost.IsRecording => true;

    void IRenderHost.RunPendingOperations() => RunPendingCommands();

    void IRenderHost.SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4)
    {
        _ = (operation, submitId, argument0, argument1, argument2, argument3, argument4);
    }

    ulong IRenderHost.ClampMappedSize(ulong address, ulong size) => ClampMappedSize(address, size);

    private ulong ClampMappedSize(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || size > ulong.MaxValue - address || !CanReadGuest(address))
        {
            throw Fatal($"The buffer range starts in unmapped memory: address=0x{address:X16} size=0x{size:X16}.");
        }

        var end = address + size;
        var page = (address & ~(MappedPageSize - 1)) + MappedPageSize;
        while (page < end && CanReadGuest(page))
        {
            page += MappedPageSize;
        }

        return Math.Min(size, page - address);
    }

    ResourceSlotIdentifier IRenderHost.FindImage(ref ImageRequest request, bool exactFormat)
    {
        _ = exactFormat;
        var address = request.Description.Data.Address;
        if (!_imageIdentifiers.TryGetValue(address, out var identifier))
        {
            identifier = new ResourceSlotIdentifier(_nextImageIndex++, 1);
            _imageIdentifiers.Add(address, identifier);
        }

        return identifier;
    }

    void IRenderHost.BindRenderTarget(ResourceSlotIdentifier image)
    {
        _ = image;
    }

    // The records of one draw or dispatch live until the executor resets; the submitted copy owns its bytes.
    void IRenderHost.ResetBindings()
    {
        _colorTargets.Clear();
        _committedStages.Clear();
        _buffers.Clear();
        _hasDepth = false;
        _vertexBindings = [];
        _vertexInput = null;
        _indexBinding = null;
        _boundPipeline = null;
    }

    // The view handle carries the image identifier; the presenter keys its images by guest address.
    ColorAttachmentAcquisition IRenderHost.AcquireColorAttachment(in ColorTargetState target)
    {
        var context = RequireContext();
        _guestTargets[target.Image] = GuestTargetOf(in target, context.ColorTargets[target.Slot], context.RenderTargetMaskForSlot(target.Slot));
        _colorTargets.Add(target);
        return new ColorAttachmentAcquisition(target.Image, new ImageView(target.Image.Index), ImageLayout.General, target.Resolution.Samples, false, default);
    }

    DepthAttachmentAcquisition IRenderHost.AcquireDepthAttachment(in DepthAttachmentState depth) =>
        new(new ImageView(depth.Image.Index), depth.Target.Target.Samples, false);

    void IRenderHost.TransitionDepthAttachment(in DepthAttachmentState depth, ImageLayout layout, ImageAspectFlags writeAspects)
    {
        _ = (layout, writeAspects);
        _depth = depth;
        _hasDepth = true;
    }

    BufferBinding IRenderHost.NullBuffer => new(0, 0);

    BufferBinding IRenderHost.ObtainBuffer(ulong address, ulong size, bool isWritten)
    {
        _ = isWritten;
        var handle = _nextBufferHandle++;
        _buffers[handle] = new BoundBuffer(address, size, null);
        return new BufferBinding(handle, 0);
    }

    BufferBinding IRenderHost.UploadTransient(ReadOnlySpan<byte> data, uint alignment)
    {
        _ = alignment;
        var handle = _nextBufferHandle++;
        _buffers[handle] = new BoundBuffer(0, (ulong)data.Length, data.ToArray());
        return new BufferBinding(handle, 0);
    }

    void IRenderHost.BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input)
    {
        _vertexBindings = bindings.ToArray();
        _vertexInput = input;
    }

    void IRenderHost.BindIndexBuffer(BufferBinding binding, IndexType type)
    {
        _indexBinding = binding;
        _indexType = type;
    }

    IResourcePreparation IRenderHost.BeginPreparation()
    {
        if (_preparation is not null)
        {
            throw Fatal("A resource preparation is already open.");
        }

        _preparation = new Preparation(this);
        return _preparation;
    }

    private Preparation RequirePreparation() => _preparation ?? throw Fatal("No resource preparation is open.");

    private ShaderProgramInfo RequireProgram(ShaderStageResources stage)
    {
        var program = stage.Program ?? throw Fatal("The stage has no program.");
        if (program.Resources is null || program.Bindings is null)
        {
            throw Fatal($"The stage program has no resource plan: stage={program.Stage} hash=0x{program.Hash:X16}.");
        }

        return program;
    }

    private static GuestStageKind StageKindOf(ShaderProgramInfo program) => program.Stage switch
    {
        ShaderStageKind.Vertex => GuestStageKind.Vertex,
        ShaderStageKind.Pixel => GuestStageKind.Pixel,
        ShaderStageKind.Compute => GuestStageKind.Compute,
        _ => throw SubmissionScheduler.Fatal($"The stage kind is unknown: stage={program.Stage} hash=0x{program.Hash:X16}."),
    };

    // One snapshot per layout element; a dynamic-mip storage image gets one element per mip.
    private GuestDrawTexture[] SnapshotImages(ShaderProgramInfo program, PreparedStage prepared)
    {
        var info = prepared.Resources.Info;
        var snapshot = prepared.Stage.Resources;
        var elements = new List<(int Image, uint Mip)>();
        foreach (var binding in prepared.Layout.Descriptors)
        {
            if (ImageDescriptorBinding.ResourceClass(binding.Kind) == ImageResourceClass.None)
            {
                continue;
            }

            var previous = -1;
            uint mip = 0;
            foreach (var resource in binding.Resources)
            {
                mip = (int)resource == previous ? mip + 1 : 0;
                previous = (int)resource;
                elements.Add(((int)resource, mip));
            }
        }

        var textures = new GuestDrawTexture[elements.Count];
        var bindings = new List<AgcExports.TranslatedImageBinding>(elements.Count);
        var bindingElements = new List<int>(elements.Count);
        for (var element = 0; element < elements.Count; element++)
        {
            var (imageIndex, mip) = elements[element];
            var image = info.Images[imageIndex];
            var words = snapshot.Images[imageIndex];
            var storage = image.ResourceClass == ImageResourceClass.Storage;
            if (words.Length < 4)
            {
                throw Fatal($"An image descriptor is too short: image={imageIndex} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            if (!AgcExports.TryDecodeTextureDescriptor(words, out var descriptor))
            {
                if (new TextureDescriptorWords(words.Length >= 8 ? words : PadWords(words)).BaseAddress != 0)
                {
                    throw Fatal($"An image descriptor cannot be decoded: image={imageIndex} word1=0x{words[1]:X8} word3=0x{words[3]:X8} hash=0x{program.Hash:X16}.");
                }

                textures[element] = new GuestDrawTexture(0, 1, 1, 0, 0, [0, 0, 0, 255], IsFallback: true, IsStorage: storage);
                continue;
            }

            var arrayed = image.Cube || image.Dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
            bindings.Add(new AgcExports.TranslatedImageBinding(
                descriptor,
                storage,
                descriptor.ViewBaseLevel + mip,
                [],
                arrayed,
                words,
                image.MipMode == ImageMipMode.DynamicStorage,
                image.Dimension == ImageDimension.Dim3D ? 2u : 1u));
            bindingElements.Add(element);
        }

        var snapshots = MetalTextureSnapshots.CreateTextureSnapshots(_context, bindings, out _);
        if (snapshots.Count != bindings.Count)
        {
            throw Fatal($"A stage image has no snapshot: stage={program.Stage} images={bindings.Count} snapshots={snapshots.Count} hash=0x{program.Hash:X16}.");
        }

        for (var index = 0; index < bindingElements.Count; index++)
        {
            textures[bindingElements[index]] = snapshots[index];
        }

        return textures;
    }

    private static uint[] PadWords(uint[] words)
    {
        var padded = new uint[8];
        words.AsSpan(0, Math.Min(words.Length, 8)).CopyTo(padded);
        return padded;
    }

    // Compare bits stay only on depth-compare samplers; a forced point sampler drops its filters.
    private GuestSampler ResolveSampler(SamplerResource sampler, uint[] words, ShaderProgramInfo program, int index)
    {
        if (words.Length < 4)
        {
            throw Fatal($"A sampler descriptor is too short: sampler={index} words={words.Length} hash=0x{program.Hash:X16}.");
        }

        var word0 = words[0];
        var word2 = words[2];
        if (!sampler.DepthCompare)
        {
            word0 &= ~(0x7u << 12);
        }

        if (sampler.ForcePointFiltering)
        {
            var mipmapped = ((word2 >> 26) & 0x3u) != 0;
            word2 &= ~(0xFFu << 20);
            word2 |= 1u << 24;
            if (mipmapped)
            {
                word2 |= 1u << 26;
            }
        }

        return new GuestSampler(word0, words[1], word2, words[3]);
    }

    IPreparedBindings IRenderHost.PrepareBindings(ShaderStageResources stage)
    {
        using var profileScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DescriptorPreparation);
        _ = RequirePreparation();
        var program = RequireProgram(stage);
        var prepared = new PreparedStage(stage, program);
        var info = prepared.Resources.Info;
        var layout = prepared.Layout;
        var snapshot = stage.Resources;
        if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count || snapshot.Buffers.Length != info.Buffers.Count)
        {
            throw Fatal(
                $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} images={snapshot.Images.Length}/{info.Images.Count} " +
                $"samplers={snapshot.Samplers.Length}/{info.Samplers.Count} buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
        }

        prepared.Textures = SnapshotImages(program, prepared);
        var samplers = new GuestSampler[info.Samplers.Count];
        for (var index = 0; index < samplers.Length; index++)
        {
            samplers[index] = ResolveSampler(info.Samplers[index], snapshot.Samplers[index], program, index);
        }

        prepared.Samplers = samplers;
        var shaderData = new uint[layout.ShaderDataDwordCount];
        for (var index = 0; index < layout.UserDataRegisters.Count; index++)
        {
            var register = layout.UserDataRegisters[index];
            var userIndex = (int)(register - program.UserDataBase);
            if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
            {
                throw Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
            }

            shaderData[index] = snapshot.UserData[userIndex];
        }

        if (layout.UsesShaderBase)
        {
            shaderData[layout.ShaderBaseDword] = (uint)stage.ShaderBase;
            shaderData[layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
        }

        stage.WriteDispatchThreadLimits(shaderData);
        prepared.ShaderData = shaderData;
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write(
                $"Bindings prepared stage={program.Stage} hash=0x{program.Hash:X16} buffers={info.Buffers.Count} images={prepared.Textures.Length} " +
                $"samplers={info.Samplers.Count} userData={snapshot.UserData.Length} flattened={snapshot.FlattenedResourceTable.Length} shaderData={shaderData.Length}");
        }

        return prepared;
    }

    // Guest memory is the device-address backing; the planned ranges are copied when the stage binds.
    void IRenderHost.PrepareDeviceAddresses() => RequirePreparation().DeviceAddressesPrepared = true;

    private byte[] ReadGuestBytes(ulong address, ulong size, string what, ShaderProgramInfo program)
    {
        var bytes = new byte[checked((int)size)];
        if (!Memory.TryRead(address, bytes))
        {
            throw Fatal($"The {what} is unreadable: address=0x{address:X16} size=0x{size:X} hash=0x{program.Hash:X16}.");
        }

        return bytes;
    }

    // Each storage buffer is copied whole; a null descriptor becomes a zeroed null buffer.
    private GuestMemoryBuffer[] CopyStorageBuffers(PreparedStage prepared)
    {
        var program = prepared.Program;
        var info = prepared.Resources.Info;
        var snapshot = prepared.Stage.Resources;
        var buffers = new GuestMemoryBuffer[info.Buffers.Count];
        for (var index = 0; index < buffers.Length; index++)
        {
            var words = snapshot.Buffers[index];
            if (words.Length < 4)
            {
                throw Fatal($"A buffer descriptor is too short: buffer={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            var descriptor = BufferDescriptorWords.From(words);
            var requested = descriptor.Footprint() ?? throw Fatal(
                $"A storage buffer descriptor footprint overflows: buffer={index} stride={descriptor.Stride} records={descriptor.RecordCount} hash=0x{program.Hash:X16}.");
            if (descriptor.Address == 0 || requested == 0)
            {
                buffers[index] = new GuestMemoryBuffer(0, new byte[NullBufferBytes], (int)NullBufferBytes, NullBufferBytes, Pooled: false);
                continue;
            }

            var size = ClampMappedSize(descriptor.Address, requested);
            var bytes = ReadGuestBytes(descriptor.Address, size, "storage buffer", program);
            buffers[index] = new GuestMemoryBuffer(descriptor.Address, bytes, bytes.Length, size, Pooled: false, Writable: info.Buffers[index].Written);
        }

        return buffers;
    }

    // Planned ranges widened to pages, merged when they touch, and copied whole; an unplanned handle is fatal.
    private void CopyDeviceAddressRanges(PreparedStage prepared)
    {
        var program = prepared.Program;
        var ranges = prepared.Stage.Resources.DeviceAddressRanges;
        var pages = new List<(ulong First, ulong Last, bool Written)>();
        foreach (var range in ranges)
        {
            if (!range.Planned)
            {
                throw Fatal($"A device-address range cannot be planned on Metal: handle={range.Handle} written={range.Written} hash=0x{program.Hash:X16}.");
            }

            if (range.Size == 0)
            {
                continue;
            }

            var limit = PageOwnerTable.AddressSpaceSize;
            if (range.Base >= limit || range.Size > limit - range.Base)
            {
                throw Fatal($"A device-address range is outside the address space: handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} hash=0x{program.Hash:X16}.");
            }

            var size = ClampMappedSize(range.Base, Math.Min(range.Size, DeviceAddressRangePlanner.MaxRangeBytes));
            pages.Add((range.Base >> DeviceAddressPaging.PageBits, (range.Base + size - 1) >> DeviceAddressPaging.PageBits, range.Written));
        }

        pages.Sort((left, right) => left.First.CompareTo(right.First));
        var merged = new List<(ulong First, ulong Last, bool Written)>();
        foreach (var page in pages)
        {
            if (merged.Count != 0 && page.First <= merged[^1].Last + 1)
            {
                var last = merged[^1];
                merged[^1] = (last.First, Math.Max(last.Last, page.Last), last.Written || page.Written);
                continue;
            }

            merged.Add(page);
        }

        if (merged.Count > Gen5MslTranslator.MaxAddressRangeCount)
        {
            throw Fatal($"A draw needs more device-address ranges than the table holds: ranges={merged.Count} limit={Gen5MslTranslator.MaxAddressRangeCount} hash=0x{program.Hash:X16}.");
        }

        var buffers = new GuestMemoryBuffer[merged.Count];
        var table = new GuestAddressRange[merged.Count];
        for (var index = 0; index < merged.Count; index++)
        {
            var (first, last, written) = merged[index];
            var start = first << DeviceAddressPaging.PageBits;
            var pageCount = last - first + 1;
            var bytes = new byte[checked((int)(pageCount * DeviceAddressPageSize))];
            for (var page = 0UL; page < pageCount; page++)
            {
                ReadMappedPage(start + (page * DeviceAddressPageSize), bytes.AsSpan((int)(page * DeviceAddressPageSize), (int)DeviceAddressPageSize));
            }

            buffers[index] = new GuestMemoryBuffer(start, bytes, bytes.Length, (ulong)bytes.Length, Pooled: false, Writable: written, WriteBackToGuest: written);
            table[index] = new GuestAddressRange((uint)first, (uint)pageCount, index);
        }

        prepared.RangeBuffers = buffers;
        prepared.Ranges = table;
    }

    // Every mapped part of one page is read; the unmapped parts stay zero, like a bounded read.
    private void ReadMappedPage(ulong address, Span<byte> page)
    {
        if (Memory.TryRead(address, page))
        {
            return;
        }

        const int probeBytes = 256;
        for (var offset = 0; offset < page.Length; offset += (int)MappedPageSize)
        {
            var chunk = page.Slice(offset, (int)Math.Min(MappedPageSize, (ulong)(page.Length - offset)));
            if (Memory.TryRead(address + (ulong)offset, chunk))
            {
                continue;
            }

            for (var probe = 0; probe < chunk.Length; probe += probeBytes)
            {
                var piece = chunk.Slice(probe, Math.Min(probeBytes, chunk.Length - probe));
                if (!Memory.TryRead(address + (ulong)offset + (ulong)probe, piece))
                {
                    piece.Clear();
                }
            }
        }
    }

    void IRenderHost.BindResources(IPreparedBindings prepared)
    {
        using var profileScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DescriptorPreparation);
        var preparation = RequirePreparation();
        var stage = (PreparedStage)prepared;
        stage.Buffers = CopyStorageBuffers(stage);
        if (stage.Resources.Info.UsesDeviceAddresses)
        {
            if (!preparation.DeviceAddressesPrepared)
            {
                throw Fatal($"A device-address program binds before its addresses are prepared: hash=0x{stage.Program.Hash:X16}.");
            }

            CopyDeviceAddressRanges(stage);
        }

        stage.Bound = true;
    }

    void IRenderHost.CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
    {
        _ = (bindPoint, pipeline);
        _committedStages.Clear();
        foreach (var stage in stages)
        {
            var prepared = (PreparedStage)stage;
            if (!prepared.Bound)
            {
                throw Fatal($"A stage commits before its resources are bound: stage={prepared.Program.Stage} hash=0x{prepared.Program.Hash:X16}.");
            }

            _committedStages.Add(prepared);
        }
    }

    void IRenderHost.SetDynamicState(in DynamicDrawState state) => _dynamicState = state;

    void IRenderHost.BeginRendering(in RenderingState state)
    {
        _ = state;
    }

    void IRenderHost.EndRendering()
    {
    }

    void IRenderHost.BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline)
    {
        _ = bindPoint;
        _boundPipeline = _pipelineRecords.TryGetValue(pipeline.Pipeline, out var record)
            ? record
            : throw Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline} layout={pipeline.Layout}.");
    }

    private GraphicsPipelineRecord RequireGraphicsPipeline() =>
        _boundPipeline as GraphicsPipelineRecord ?? throw Fatal("No graphics pipeline is bound for the draw.");

    // The guest primitive code the presenter maps itself; a patch list is the rectangle list.
    private static uint PrimitiveTypeOf(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => 1,
        PrimitiveTopology.LineList => 2,
        PrimitiveTopology.LineStrip => 3,
        PrimitiveTopology.TriangleFan => 5,
        PrimitiveTopology.TriangleStrip => 6,
        PrimitiveTopology.PatchList => 7,
        _ => 4,
    };

    private BoundBuffer RequireBuffer(in BufferBinding binding) =>
        _buffers.TryGetValue(binding.Handle, out var buffer) ? buffer : throw Fatal($"The buffer handle is unknown: handle={binding.Handle} offset=0x{binding.Offset:X}.");

    private byte[] ReadBound(BoundBuffer buffer, ulong offset, ulong size)
    {
        if (buffer.Bytes is { } bytes)
        {
            return bytes.AsSpan((int)offset, (int)Math.Min(size, (ulong)bytes.Length - offset)).ToArray();
        }

        var data = new byte[checked((int)size)];
        if (!Memory.TryRead(buffer.Address + offset, data))
        {
            throw Fatal($"The bound buffer is unreadable: address=0x{buffer.Address + offset:X16} size=0x{size:X}.");
        }

        return data;
    }

    // The raw data and number format codes of an attribute descriptor; two known formats are accepted as floats.
    private (uint DataFormat, uint NumberFormat) VertexFormatOf(in BufferDescriptorWords descriptor, int attribute, ShaderProgramInfo program)
    {
        switch (descriptor.Format)
        {
            case AcceptedSignedShortPairFormat:
                return (DataFormat32x4, NumberFormatFloat);
            case AcceptedHalfPairFormat:
                return (DataFormat16x2, NumberFormatFloat);
        }

        if (!Gfx10UnifiedFormat.TryDecode(descriptor.Format, out var dataFormat, out var numberFormat))
        {
            throw Fatal($"A vertex attribute buffer has an unknown format: attribute={attribute} format={descriptor.Format} hash=0x{program.Hash:X16}.");
        }

        return (dataFormat, numberFormat);
    }

    private IReadOnlyList<GuestVertexBuffer> CopyVertexBuffers(GraphicsPipelineRecord pipeline)
    {
        var input = _vertexInput ?? throw Fatal("The draw has no bound vertex input.");
        var buffers = input.Buffers;
        var attributes = input.Attributes;
        var bytes = new byte[buffers.Length][];
        var submitted = Translation.FindSubmittedVertexData(input);
        for (var index = 0; index < buffers.Length; index++)
        {
            if (submitted is not null)
            {
                bytes[index] = submitted.CopyBuffer(index);
                continue;
            }

            bytes[index] = index < _vertexBindings.Length && _vertexBindings[index].Handle != 0 && buffers[index].Size != 0
                ? ReadBound(RequireBuffer(in _vertexBindings[index]), _vertexBindings[index].Offset, buffers[index].Size)
                : [];
        }

        var result = new GuestVertexBuffer[attributes.Length];
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = attributes[index];
            var descriptor = attribute.Descriptor;
            if (descriptor.AddThreadId)
            {
                throw Fatal($"A vertex attribute buffer adds the thread index: attribute={index} hash=0x{pipeline.VertexStage.Hash:X16}.");
            }

            if (descriptor.SwizzleEnabled)
            {
                throw Fatal($"A vertex attribute buffer is swizzled: attribute={index} hash=0x{pipeline.VertexStage.Hash:X16}.");
            }

            var compiledComponents = pipeline.VertexStage.VertexFetchComponents[index];
            var components = compiledComponents > 0 ? compiledComponents : (uint)attribute.RegisterCount;
            var (dataFormat, numberFormat) = VertexFormatOf(in descriptor, index, pipeline.VertexStage);
            var data = bytes[attribute.BufferIndex];
            result[index] = new GuestVertexBuffer(
                (uint)index,
                components,
                dataFormat,
                numberFormat,
                buffers[attribute.BufferIndex].Address,
                buffers[attribute.BufferIndex].Stride,
                attribute.OffsetBytes,
                data,
                data.Length,
                Pooled: false,
                buffers[attribute.BufferIndex].PerInstance);
        }

        return result;
    }

    private GuestIndexBuffer? CopyIndexBuffer(uint indexCount)
    {
        if (_indexBinding is not { } binding)
        {
            return null;
        }

        var buffer = RequireBuffer(in binding);
        var indexSize = _indexType == IndexType.Uint32 ? 4u : 2u;
        var bytes = ReadBound(buffer, binding.Offset, Math.Min(buffer.Size - binding.Offset, indexCount * (ulong)indexSize));
        return new GuestIndexBuffer(bytes, bytes.Length, _indexType == IndexType.Uint32, Pooled: false) { GuestAddress = buffer.Bytes is null ? buffer.Address + binding.Offset : 0 };
    }

    private static GuestRenderTarget GuestTargetOf(in ColorTargetState target, in ColorTargetWords words, uint writeMask) =>
        new(
            target.Resolution.BaseAddress,
            target.Resolution.Extent.Width,
            target.Resolution.Extent.Height,
            (uint)words.Layout,
            (uint)words.NumberType,
            MipLevels: 1,
            ComponentSwap: (uint)words.Order,
            TileMode: (uint)words.TileMode,
            Registers: words,
            WriteMask: writeMask);

    private GuestDepthTarget? GuestDepthOf(ContextRegisters context)
    {
        if (!_hasDepth)
        {
            return null;
        }

        var resolution = _depth.Target.Target;
        var state = _depth.Target.State;
        var words = context.DepthTarget;
        return new GuestDepthTarget(
            words.ZReadBase,
            words.ZWriteBase,
            resolution.Width,
            resolution.Height,
            (uint)words.DepthFormat,
            (words.ZInfo >> 4) & 0x1Fu,
            state.DepthClearValue,
            ReadOnly: words.DepthWriteDisabled || words.ZWriteBase == 0,
            HtileAddress: resolution.HtileAddress,
            HtileBaseLayer: words.SliceStart,
            HtileAcceleration: resolution.HasHtile && words.HtileAcceleration,
            HasStencil: resolution.HasStencil,
            StencilReadAddress: words.StencilReadBase,
            StencilWriteAddress: words.StencilWriteBase,
            StencilReadOnly: words.StencilWriteDisabled || words.StencilWriteBase == 0,
            Registers: words);
    }

    private static GuestStencilFaceState StencilFaceOf(in StencilOperations operations, in StencilMasks masks, byte operationValue) =>
        new((uint)operations.FailOperation, (uint)operations.PassOperation, (uint)operations.DepthFailOperation, (uint)operations.Compare, masks.CompareMask, masks.WriteMask, masks.Reference, operationValue);

    // Blend and cull state come from the pipeline's static parameters; the rest from the dynamic state.
    private GuestRenderState RenderStateOf(ContextRegisters context, IReadOnlyList<ColorTargetState> targets, PipelineStaticParameters parameters)
    {
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            blends[index] = new GuestBlendState(
                parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index),
                parameters.GetColorSourceBlend(index),
                parameters.GetColorDestinationBlend(index),
                parameters.GetColorBlendFunction(index),
                parameters.GetAlphaSourceBlend(index),
                parameters.GetAlphaDestinationBlend(index),
                parameters.GetAlphaBlendFunction(index),
                parameters.GetSeparateAlphaBlend(index),
                parameters.GetColorMask(index));
        }

        var state = _dynamicState;
        var scissor = state.Scissor;
        var depthState = _hasDepth ? _depth.Target.State : default;
        return new GuestRenderState(
            blends,
            new GuestRect(scissor.Left, scissor.Top, (uint)Math.Max(scissor.Right - scissor.Left, 0), (uint)Math.Max(scissor.Bottom - scissor.Top, 0)),
            new GuestViewport(state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight, state.ViewportMinDepth, state.ViewportMaxDepth),
            new GuestRasterState(
                parameters.CullFront,
                parameters.CullBack,
                parameters.FrontFaceClockwise,
                context.RasterMode.PolygonMode != 0,
                state.DepthBiasEnabled,
                state.DepthBiasConstantFactor,
                state.DepthBiasClamp,
                state.DepthBiasSlopeFactor,
                context.PolygonOffset.NegativeDepthBits,
                context.PolygonOffset.DepthIsFloat),
            new GuestDepthState(
                state.DepthTestEnabled,
                state.DepthWriteEnabled,
                (uint)state.DepthCompare,
                _hasDepth && _depth.LoadClear,
                state.StencilTestEnabled,
                _hasDepth && depthState.StencilClearEnabled,
                depthState.StencilClearValue,
                StencilFaceOf(depthState.FrontOperations, state.FrontStencil, context.StencilMask.OperationValue),
                StencilFaceOf(depthState.BackOperations, state.BackStencil, context.StencilMask.OperationValueBack)),
            new GuestBlendConstant(state.BlendRed, state.BlendGreen, state.BlendBlue, state.BlendAlpha));
    }

    // The draw's buffer and texture lists, and each stage's indices into them in its layout's order.
    private (GuestDrawTexture[] Textures, GuestMemoryBuffer[] Buffers, GuestStageBindings[] Stages, bool WritesGlobalMemory) CollectResources(IReadOnlyList<PreparedStage> stages)
    {
        var textures = new List<GuestDrawTexture>();
        var buffers = new List<GuestMemoryBuffer>();
        var bindings = new GuestStageBindings[stages.Count];
        var writesGlobalMemory = false;
        for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            var stage = stages[stageIndex];
            var layout = stage.Layout;
            var bufferIndices = new int[stage.Buffers.Length];
            for (var index = 0; index < bufferIndices.Length; index++)
            {
                bufferIndices[index] = buffers.Count;
                buffers.Add(stage.Buffers[index]);
                writesGlobalMemory |= stage.Buffers[index].Writable;
            }

            var ranges = new GuestAddressRange[stage.Ranges.Length];
            for (var index = 0; index < ranges.Length; index++)
            {
                ranges[index] = stage.Ranges[index] with { BufferIndex = buffers.Count };
                buffers.Add(stage.RangeBuffers[index]);
                writesGlobalMemory |= stage.RangeBuffers[index].Writable;
            }

            var imageElements = new int[stage.Textures.Length];
            for (var index = 0; index < imageElements.Length; index++)
            {
                imageElements[index] = textures.Count;
                textures.Add(stage.Textures[index]);
            }

            uint[]? pushData = null;
            if (layout.UsesPushData)
            {
                pushData = new uint[PushData.DwordCount];
                stage.ShaderData.CopyTo(pushData, (int)layout.PushDataStartDword);
            }

            bindings[stageIndex] = new GuestStageBindings(
                StageKindOf(stage.Program),
                bufferIndices,
                imageElements,
                stage.Samplers,
                stage.ShaderData,
                pushData,
                stage.Stage.Resources.FlattenedResourceTable,
                ranges,
                layout.Find(DescriptorBindingKind.GlobalDataShare) is not null,
                layout.Find(DescriptorBindingKind.DeviceAddressPageTable) is not null,
                stage.Program.Hash);
        }

        return (textures.ToArray(), buffers.ToArray(), bindings, writesGlobalMemory);
    }

    private void SubmitDraw(uint vertexCount, uint instanceCount, int baseVertex, bool indexed)
    {
        var pipeline = RequireGraphicsPipeline();
        var context = RequireContext();
        var (textures, globals, stages, _) = CollectResources(_committedStages);
        var vertexBuffers = CopyVertexBuffers(pipeline);
        var indexBuffer = indexed ? CopyIndexBuffer(vertexCount) : null;
        var topology = pipeline.Parameters.Topology;
        var primitiveType = PrimitiveTypeOf(topology);
        var count = topology == PrimitiveTopology.PatchList && !indexed && vertexCount is 1 or 3 or 4 ? SingleRectangleVertexCount : vertexCount;
        var depthTarget = GuestDepthOf(context);
        var pixelShader = pipeline.PixelShader ?? (MetalCompiledGuestShader)Backend.GetDepthOnlyFragmentShader();
        var renderState = RenderStateOf(context, _colorTargets, pipeline.Parameters);
        var pixelAddress = pipeline.PixelShader is null ? 0 : FindStageAddress(GuestStageKind.Pixel);
        if (_colorTargets.Count == 0)
        {
            if (depthTarget is null)
            {
                return;
            }

            _snapshots.SubmitDepthOnlyTranslatedDraw(
                pixelShader, textures, globals, pipeline.AttributeCount, depthTarget, pipeline.VertexShader, count, instanceCount, primitiveType,
                indexBuffer, vertexBuffers, renderState with { Blends = [GuestBlendState.Default with { WriteMask = 0 }] }, pixelAddress, baseVertex, stages);
            return;
        }

        var targets = new GuestRenderTarget[_colorTargets.Count];
        for (var index = 0; index < targets.Length; index++)
        {
            targets[index] = _guestTargets[_colorTargets[index].Image];
        }

        _snapshots.SubmitOffscreenTranslatedDraw(
            pixelShader, textures, globals, pipeline.AttributeCount, targets, pipeline.VertexShader, count, instanceCount, primitiveType,
            indexBuffer, vertexBuffers, renderState, depthTarget, pixelAddress, baseVertex, stages);
    }

    private ulong FindStageAddress(GuestStageKind kind)
    {
        foreach (var stage in _committedStages)
        {
            if (StageKindOf(stage.Program) == kind)
            {
                return stage.Stage.ShaderBase;
            }
        }

        return 0;
    }

    void IRenderHost.Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        _ = firstInstance;
        SubmitDraw(vertexCount, instanceCount, (int)firstVertex, indexed: false);
    }

    void IRenderHost.DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        _ = (firstIndex, firstInstance);
        SubmitDraw(indexCount, instanceCount, vertexOffset, indexed: true);
    }

    void IRenderHost.Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        var pipeline = _boundPipeline as ComputePipelineRecord ?? throw Fatal("No compute pipeline is bound for the dispatch.");
        var (textures, globals, stages, writesGlobalMemory) = CollectResources(_committedStages);
        if (stages.Length != 1 || stages[0].Stage != GuestStageKind.Compute)
        {
            throw Fatal($"A dispatch commits {stages.Length} stages; one compute stage is expected: hash=0x{pipeline.Stage.Hash:X16}.");
        }

        var input = pipeline.Input;
        _snapshots.SubmitComputeDispatch(
            _committedStages[0].Stage.ShaderBase, pipeline.Shader, textures, globals, groupsX, groupsY, groupsZ, 0, 0, 0,
            input.ThreadsX, input.ThreadsY, input.ThreadsZ, isIndirect: false, writesGlobalMemory,
            input.DispatchThreadDimensions ? input.DispatchThreadsX : uint.MaxValue,
            input.DispatchThreadDimensions ? input.DispatchThreadsY : uint.MaxValue,
            input.DispatchThreadDimensions ? input.DispatchThreadsZ : uint.MaxValue,
            stages[0]);
    }

    // The seam submits whole records; the presenter orders them itself.
    void IRenderHost.ShaderWriteBarrier(PipelineStageFlags sourceStages)
    {
        _ = sourceStages;
    }

    void IRenderHost.ShaderWriteHazardBarrier()
    {
    }

    void IRenderHost.ShaderAccessBarrier()
    {
    }

    // A fullscreen solid draw into every target; the presenter has no clear command on this seam.
    void IRenderHost.ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
    {
        var context = RequireContext();
        var guestTargets = new GuestRenderTarget[targets.Length];
        var blends = new GuestBlendState[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            guestTargets[index] = GuestTargetOf(in targets[index], context.ColorTargets[targets[index].Slot], 0xF);
            blends[index] = GuestBlendState.Default;
        }

        var pixel = new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateSolidFragment(clear.Red, clear.Green, clear.Blue, clear.Alpha), "solid_clear_fs", Gen5MslStage.Pixel, AttributeCount: 0));
        var vertex = new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateFullscreenVertex(0), "solid_clear_vs", Gen5MslStage.Vertex, AttributeCount: 0));
        var extent = targets[0].Resolution.Extent;
        var renderState = new GuestRenderState(
            blends,
            new GuestRect(0, 0, extent.Width, extent.Height),
            new GuestViewport(0, 0, extent.Width, extent.Height, 0, 1),
            GuestRasterState.Default,
            GuestDepthState.Default);
        _snapshots.SubmitOffscreenTranslatedDraw(pixel, [], [], 0, guestTargets, vertex, 3, 1, 4, null, null, renderState);
    }

    bool IRenderHost.TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
    {
        _ = programs;
        Translation.RetainTargetlessDraw(banks, in arguments);
        return true;
    }

    void IRenderHost.MarkGpuWritten(ResourceSlotIdentifier image)
    {
        _ = image;
    }

    void IRenderHost.ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer)
    {
        _ = (sourceMip, sourceLayer, destinationMip, destinationLayer);
        if (!_guestTargets.TryGetValue(source, out var from) || !_guestTargets.TryGetValue(destination, out var to))
        {
            throw Fatal($"The resolve names an image that was never bound as a color target: source={source.Index} destination={destination.Index}.");
        }

        _ = _snapshots.TrySubmitGuestImageBlit(from, to);
    }

    bool IRenderHost.IsMetadata(ulong address)
    {
        _ = address;
        return false;
    }

    bool IRenderHost.ClearMetadata(ulong address)
    {
        _ = address;
        return false;
    }

    // A full fill of a snapshot image replaces its pixels; anything else runs as a dispatch.
    bool IRenderHost.TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        if (!_snapshots.TryGetGuestImageExtent(address, out _, out _, out var imageBytes) || imageBytes == 0 || size < imageBytes)
        {
            return false;
        }

        _snapshots.SubmitGuestImageFill(address, packedClear);
        return true;
    }

    bool IRenderHost.TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        _ = (address, size, fillValue);
        return false;
    }
}
