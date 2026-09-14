// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Agc;

// This partial decodes, evaluates and compiles the shader programs behind the pipeline provider.
public static partial class AgcExports
{
    private static readonly ConditionalWeakTable<Gen5ShaderProgram, StageInstructionMetadata> _stageInstructionMetadata = new();

    internal static StageInstructionMetadata GetStageInstructionMetadata(Gen5ShaderProgram program) =>
        _stageInstructionMetadata.GetValue(program, static decodedProgram => new StageInstructionMetadata(decodedProgram));

    internal sealed class StageInstructionMetadata
    {
        internal IReadOnlyDictionary<uint, Gen5ShaderInstruction> InstructionsByAddress { get; }
        internal bool HasBitwiseExclusiveOr { get; }

        internal StageInstructionMetadata(Gen5ShaderProgram program)
        {
            var instructions = new Dictionary<uint, Gen5ShaderInstruction>(program.Instructions.Count);
            foreach (var instruction in program.Instructions)
            {
                instructions[instruction.Pc] = instruction;
                HasBitwiseExclusiveOr |= instruction.Opcode.Contains("Xor", StringComparison.Ordinal);
            }

            InstructionsByAddress = instructions;
        }
    }

    // BCn block-compressed guest formats and the bytes per 4x4 block.
    internal static int GetBlockCompressedBlockBytes(uint format) => format switch
    {
        169 or 170 or 175 or 176 => 8,
        171 or 172 or 173 or 174 or 177 or 178 or 179 or 180 or 181 or 182 => 16,
        _ => 0,
    };

    internal readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint TileMode,
        uint Type,
        uint BaseLevel,
        uint LastLevel,
        uint Pitch,
        uint DstSelect,
        uint Depth = 1,
        uint BaseArray = 0,
        uint ArrayPitch = 0,
        uint MaxMip = 0,
        uint MinLod = 0,
        uint MinLodWarn = 0,
        uint BcSwizzle = 0,
        ulong MetadataAddress = 0,
        uint DescriptorFlags = 0,
        bool HasExtendedDescriptor = false)
    {
        public uint ResourceMipLevels
        {
            get
            {
                // RDNA2 table 45 explicitly distinguishes MAX_MIP (the
                // resource allocation) from BASE_LEVEL/LAST_LEVEL (the
                // resource view). Do not size a Vulkan image from a view:
                // another descriptor for the same allocation may expose a
                // different subset of its mip chain.
                var maximumMipLevels = GetMaximumMipLevels();
                var resourceMipLevels = HasExtendedDescriptor
                    ? MaxMip + 1
                    : maximumMipLevels;
                return Math.Min(Math.Max(resourceMipLevels, 1u), maximumMipLevels);
            }
        }

        public uint MipLevels
        {
            get
            {
                var descriptorMipLevels = LastLevel >= ViewBaseLevel
                    ? LastLevel - ViewBaseLevel + 1
                    : 1;
                return Math.Min(
                    descriptorMipLevels,
                    ResourceMipLevels - ViewBaseLevel);
            }
        }

        public uint ViewBaseLevel
        {
            get
            {
                // Clamp inverted mip ranges to the allocation while preserving the requested base level.
                return Math.Min(BaseLevel, ResourceMipLevels - 1);
            }
        }

        internal uint GetMaximumMipLevels()
        {
            var largestDimension = Type == 10
                ? Math.Max(Math.Max(Width, Height), Depth)
                : Math.Max(Width, Height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return maximumMipLevels;
        }
    }

    internal sealed record TranslatedImageBinding(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        IReadOnlyList<uint> SamplerDescriptor,
        bool IsArrayed = false,
        IReadOnlyList<uint>? ResourceDescriptor = null,
        bool DynamicMip = false,
        uint Dimension = 1);

    // A backend without CPU snapshots receives the raw descriptor and the compiled shape only.
    internal static GuestDrawTexture CreateDescriptorDrawTexture(TranslatedImageBinding binding)
    {
        var descriptor = binding.Descriptor;
        // A rejected descriptor must remain a null binding when sent to the image cache.
        var words = descriptor.Address == 0 ? [] : binding.ResourceDescriptor?.ToArray() ?? [];
        Span<uint> padded = stackalloc uint[8];
        words.AsSpan(0, Math.Min(words.Length, 8)).CopyTo(padded);
        var numericClass = GuestPixelFormats.SampledNumericClass(new TextureDescriptorWords(padded).Format);
        var shape = new ShaderImageShape(
            Volume: binding.Dimension == 2,
            Arrayed: binding.IsArrayed,
            Storage: binding.IsStorage,
            DynamicMip: binding.DynamicMip,
            NumericClass: numericClass == TextureNumericClass.Unsupported ? TextureNumericClass.Float : numericClass);
        return new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            [],
            IsFallback: false,
            IsStorage: binding.IsStorage,
            MipLevels: descriptor.MipLevels,
            MipLevel: binding.MipLevel,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: descriptor.Pitch,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: ToGuestSampler(binding.SamplerDescriptor),
            ArrayedView: binding.IsArrayed,
            Type: descriptor.Type,
            Depth: descriptor.Depth,
            Descriptor: words,
            Shape: shape);
    }

    internal static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new GuestSampler(
                descriptor[0],
                descriptor[1],
                descriptor[2],
                descriptor[3])
            : default;

    internal static IReadOnlyList<uint> NormalizeSamplerDescriptorForImageOperation(
        IReadOnlyList<uint> descriptor)
    {
        const uint depthCompareMask = 0x7u << 12;
        if (descriptor.Count < 4 ||
            (descriptor[0] & depthCompareMask) == 0)
        {
            return descriptor;
        }

        // The shader translators perform guest depth comparisons after a
        // normal sample. The native sampler must not compare the value first.
        return
        [
            descriptor[0] & ~depthCompareMask,
            descriptor[1],
            descriptor[2],
            descriptor[3],
        ];
    }

    internal static ulong GetTextureBytesPerTexel(uint format) =>
        format switch
        {
            1 => 1UL,
            2 => 2UL,
            3 => 2UL,
            4 => 4UL,
            5 => 4UL,
            6 => 4UL,
            7 => 4UL,
            9 => 4UL,
            10 => 4UL,
            11 => 8UL,
            12 => 8UL,
            13 => 12UL,
            14 => 16UL,
            _ => 0UL,
        };

    internal static ulong GetTextureByteCount(
        uint format,
        uint width,
        uint height,
        uint depth = 1)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked(
                (ulong)width *
                height *
                Math.Max(depth, 1u) *
                bytesPerTexel);
        }

        var blockBytes = (ulong)GetBlockCompressedBlockBytes(format);
        return blockBytes == 0
            ? 0
            : checked(
                ((ulong)width + 3) / 4 *
                (((ulong)height + 3) / 4) *
                Math.Max(depth, 1u) *
                blockBytes);
    }

    internal static ulong GetGuestSurfaceByteCount(
        uint format,
        uint width,
        uint height,
        uint tileMode)
    {
        var logicalByteCount = GetTextureByteCount(format, width, height);
        if (logicalByteCount == 0 || tileMode == 0)
        {
            return logicalByteCount;
        }

        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 ||
            bytesPerTexel > int.MaxValue ||
            width > int.MaxValue ||
            height > int.MaxValue)
        {
            return 0;
        }

        return GnmTiling.TryGetPhysicalTiledByteCount(
            tileMode,
            (int)width,
            (int)height,
            (int)bytesPerTexel,
            out var tiledByteCount)
                ? tiledByteCount
                : 0;
    }

    internal static uint GetTextureVolumeDepth(uint type, uint depth) =>
        type == Gen5TextureType3D
            ? Math.Max(depth, 1u)
            : 1u;

    internal static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (fields.Count < 4)
        {
            return false;
        }

        // Preserve the high address byte and the full width and height fields.
        var address = (((ulong)(fields[1] & 0xFFu) << 32) | fields[0]) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0x3FFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0xFFFFu) + 1;
        var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
        if (unifiedFormat == 0 ||
            !Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var format,
                out var numberType))
        {
            return false;
        }
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        var baseLevel = (fields[3] >> 12) & 0xFu;
        var lastLevel = (fields[3] >> 16) & 0xFu;
        var bcSwizzle = (fields[3] >> 25) & 0x7u;
        var hasExtendedDescriptor = fields.Count >= 8;
        var word4 = fields.Count >= 5 ? fields[4] : 0u;
        var depthOrLastSlice = (word4 & 0x1FFFu) + 1;
        var baseArray = (word4 >> 16) & 0x1FFFu;
        // Use explicit pitch for full descriptors; compact descriptors use the width.
        var pitch = type is 8u or 9u or 14u && word4 != 0
            ? (word4 & 0x3FFFu) + 1
            : width;
        var depth = type is 10u or 11u or 12u or 13u or 15u
            ? depthOrLastSlice
            : 1u;
        var word5 = fields.Count >= 6 ? fields[5] : 0u;
        var arrayPitch = word5 & 0xFu;
        var maxMip = (word5 >> 4) & 0xFu;
        var minLod = (fields[1] >> 8) & 0xFFFu;
        var minLodWarn = (word5 >> 8) & 0xFFFu;
        var word6 = fields.Count >= 7 ? fields[6] : 0u;
        var word7 = fields.Count >= 8 ? fields[7] : 0u;
        var metadataAddress = ((((ulong)word7 << 8) | (word6 >> 24)) << 8);
        var descriptorFlags = word6 & 0x00FF_FFFFu;
        var dstSelect = fields[3] & 0xFFFu;
        if (address == 0 || width == 0 || height == 0 || type < 8)
        {
            return false;
        }

        descriptor = new TextureDescriptor(
            address,
            width,
            height,
            format,
            numberType,
            tileMode,
            type,
            baseLevel,
            lastLevel,
            pitch,
            dstSelect,
            depth,
            baseArray,
            arrayPitch,
            maxMip,
            minLod,
            minLodWarn,
            bcSwizzle,
            metadataAddress,
            descriptorFlags,
            hasExtendedDescriptor);
        return true;
    }

    internal static TextureDescriptor CreateFallbackTextureDescriptor(
        IReadOnlyList<uint> fields,
        uint instructionDimension)
    {
        var format = Gen5TextureFormatR8G8B8A8Unorm;
        var numberType = 0u;
        var tileMode = 0u;
        if (fields.Count >= 4)
        {
            var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out format,
                    out numberType))
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
                numberType = 0;
            }
            tileMode = (fields[3] >> 20) & 0x1Fu;
            if (format == 0)
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
            }
        }

        return new TextureDescriptor(
            Address: 0,
            Width: 1,
            Height: 1,
            Format: format,
            NumberType: numberType,
            TileMode: tileMode,
            Type: GetFallbackTextureType(instructionDimension),
            BaseLevel: 0,
            LastLevel: 0,
            Pitch: 1,
            DstSelect: 0xFAC);
    }

    internal static uint GetFallbackTextureType(uint instructionDimension) =>
        instructionDimension switch
        {
            0 => Gen5TextureType1D,
            2 => Gen5TextureType3D,
            3 => Gen5TextureTypeCube,
            4 => Gen5TextureType1DArray,
            5 or 7 => Gen5TextureType2DArray,
            _ => Gen5TextureType2D,
        };

    // The pipeline objects the provider asks the presenter for.
    internal interface IHostPipelineFactory
    {
        bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentOrder,
            out Gen5PixelOutputKind outputKind, out Gen5ColorComponentMapping componentMapping);
        PipelineHandle CreateGraphicsPipeline(
            ReadOnlySpan<ColorTargetState> colors,
            in DepthAttachmentState depth,
            VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput,
            ContextRegisters context,
            in RenderingState rendering,
            PrimitiveTopology topology,
            bool primitiveRestartEnabled,
            ShaderProgram vertexProgram,
            ShaderProgram pixelProgram);

        PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program);
    }

    // One vertex attribute of the compiled vertex program and the buffer it reads.
    internal readonly record struct VertexAttributeLayout(uint Location, int BufferIndex, uint ComponentCount, uint DataFormat, uint NumberFormat, uint OffsetBytes, bool PerInstance);

    // A compiled stage with everything the host binds from: the module, its resources and the flat slot layout.
    internal sealed class CompiledStageProgram : ShaderProgramInfo
    {
        public required IGuestCompiledShader Shader { get; init; }
        public ulong Address { get; init; }
        public IReadOnlyList<GuestDrawTexture> Textures { get; init; } = [];
        public IReadOnlyList<GuestMemoryBuffer> GlobalBuffers { get; init; } = [];
        public GuestMemoryBuffer? ScalarBuffer { get; init; }
        public IReadOnlyList<Gen5VertexInputBinding> VertexInputs { get; init; } = [];
        public VertexAttributeLayout[] VertexAttributes { get; set; } = [];
        public int GlobalBufferBase { get; init; }
        public int ImageBindingBase { get; init; }
        public int TotalGlobalBuffers { get; init; }
        public bool DisableBlending { get; init; }

        // The flat slot of the scalar buffer; negative when the scalars are baked into the program.
        public int ScalarBufferIndex { get; init; } = -1;
    }

    // The shader decode behind the pipeline seam: one provider per command-stream translation.
    private sealed class ShaderProgramProvider : IShaderPipelineProvider
    {
        private const uint MaxInterpolators = 32;
        private const uint UseThreadDimensionsBit = 1u << 5;
        private const uint Wave32Bit = 1u << 15;

        private readonly CpuContext _context;
        private readonly IHostPipelineFactory _pipelines;
        private readonly ConditionalWeakTable<IGuestCompiledShader, StrongBox<ulong>> _programIds = new();
        private ulong _nextProgramId;

        public ShaderProgramProvider(CpuContext ctx, IHostPipelineFactory pipelines)
        {
            _context = ctx;
            _pipelines = pipelines;
        }

        // The translation sets the queue state and the dispatch groups before it calls the executor.
        public SubmittedDcbState? CurrentState { get; set; }

        public (uint X, uint Y, uint Z) PendingDispatchGroups { get; set; }

        private SubmittedDcbState RequireState() =>
            CurrentState ?? throw new InvalidOperationException("The shader program provider has no queue state.");

        private ShaderProgram ProgramId(IGuestCompiledShader shader)
        {
            var box = _programIds.GetValue(shader, _ => new StrongBox<ulong>(Interlocked.Increment(ref _nextProgramId)));
            return new ShaderProgram(box.Value);
        }

        public GraphicsPrograms GetGraphicsPrograms(
            VertexStageRegisters vertex,
            PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface,
            ContextRegisters context,
            ReadOnlySpan<ColorComponentMap> targetExportMapping,
            bool pixelActive)
        {
            using var preparationProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexProgramSetup);
            var state = RequireState();
            var registers = state.ShRegisters;
            var exportAddress = vertex.ExportAddress;
            ulong exportHeader;
            ulong pixelHeader;
            lock (_submitTraceGate)
            {
                _shaderHeadersByCode.TryGetValue(exportAddress, out exportHeader);
                _shaderHeadersByCode.TryGetValue(pixel.Address, out pixelHeader);
            }

            TryRegisterEmbeddedFusedProgram(_context, exportAddress, exportHeader);
            registers.TryGetValue(SpiShaderPgmChksumGs, out var exportChecksum);
            if (!Gen5ShaderTranslator.TryCreateState(
                    _context,
                    exportAddress,
                    exportHeader,
                    registers,
                    SelectExportUserDataRegister(registers),
                    out var exportState,
                    out var error,
                    userDataScalarRegisterBase: NggUserDataScalarRegisterBase,
                    shaderChecksum: exportChecksum))
            {
                return Unavailable(exportAddress, pixel.Address, error);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.VertexProgramEvaluation);
            var retainedVertexInputs = state.CurrentVertexSnapshot is { } retainedSnapshot && retainedSnapshot.ExportShaderAddress == exportAddress
                ? retainedSnapshot.Bindings
                : null;
            if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                    _context,
                    exportState,
                    out var exportEvaluation,
                    out error,
                    resolveVertexInputs: true,
                    profileStage: Gen5ShaderEvaluationStage.Vertex,
                    retainedVertexInputs: retainedVertexInputs))
            {
                return Unavailable(exportAddress, pixel.Address, error);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.VertexMetadataResolution);
            ApplySubmittedVertexSnapshot(state, exportAddress, ref exportEvaluation);
            if (exportEvaluation.VertexInputs is { Count: > 0 } discoveredInputs &&
                AgcVertexMetadata.TryGetVertexTableRegisters(_context, exportAddress, exportHeader, out var vertexTables))
            {
                var resolvedVertexTables = AgcVertexMetadata.AddUserDataScalarRegisterBase(vertexTables, exportState.UserDataScalarRegisterBase);
                var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
                    _context,
                    exportEvaluation.InitialScalarRegisters,
                    resolvedVertexTables,
                    exportState.Program,
                    discoveredInputs);
                if (!ReferenceEquals(merged, discoveredInputs))
                {
                    exportEvaluation = exportEvaluation with { VertexInputs = merged };
                }
            }

            if (!pixelActive)
            {
                return GetDepthOnlyPrograms(exportAddress, exportState, exportEvaluation);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.PixelProgramSetup);
            registers.TryGetValue(SpiShaderPgmChksumPs, out var pixelChecksum);
            if (!Gen5ShaderTranslator.TryCreateState(
                    _context,
                    pixel.Address,
                    pixelHeader,
                    registers,
                    PsTextureUserDataRegister,
                    out var pixelState,
                    out error,
                    shaderChecksum: pixelChecksum))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return Unavailable(exportAddress, pixel.Address, error);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.PixelProgramEvaluation);
            if (!Gen5ShaderScalarEvaluator.TryEvaluate(_context, pixelState, out var pixelEvaluation, out error, profileStage: Gen5ShaderEvaluationStage.Pixel))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return Unavailable(exportAddress, pixel.Address, error);
            }

            if (IsEmptyResourceTableDrawRejected(pixel.Address, pixelState, pixelEvaluation, exportAddress, registers, out error))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return Unavailable(exportAddress, pixel.Address, error);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.GraphicsProgramCache);
            var attributeCount = GetInterpolatedAttributeCount(pixelState);
            var boundTargets = ResolveBoundTargets(context, out var outputKinds, out var outputMappings, out var slotIndexes);
            var outputLayout = 0UL;
            for (var index = 0; index < boundTargets.Length; index++)
            {
                outputLayout |= (ulong)(((boundTargets[index] & 0x3Fu) << 2) | (uint)outputKinds[index]) << (index * 8);
            }

            var packedMappings = PackPixelOutputMappings(outputMappings);
            var exportFingerprint = _bakeScalars ? ComputeShaderStateFingerprint(exportEvaluation) : ComputeShaderStructuralFingerprint(exportEvaluation);
            var pixelFingerprint = _bakeScalars ? ComputeShaderStateFingerprint(pixelEvaluation) : ComputeShaderStructuralFingerprint(pixelEvaluation);
            var pixelInputCount = Math.Min(shaderInterface.PixelInputControl & 0x3Fu, MaxInterpolators);
            if (pixelInputCount == 0)
            {
                pixelInputCount = Math.Min(attributeCount, MaxInterpolators);
            }

            var interpolators = new uint[pixelInputCount];
            for (var index = 0u; index < pixelInputCount; index++)
            {
                // An unwritten slot maps attribute n to parameter n.
                interpolators[index] = (shaderInterface.PixelInterpolatorWritten & (1u << (int)index)) != 0
                    ? shaderInterface.PixelInterpolatorSettings[index]
                    : index;
            }

            var pixelInputEnable = shaderInterface.PixelInputEnable;
            var pixelInputAddress = shaderInterface.PixelInputAddress;
            var shaderKey = (
                exportAddress,
                exportState.ShaderChecksum,
                exportFingerprint,
                pixel.Address,
                pixelState.ShaderChecksum,
                pixelFingerprint,
                outputLayout,
                packedMappings,
                (uint)boundTargets.Length,
                attributeCount,
                pixelInputEnable,
                pixelInputAddress,
                ComputePixelInputControlFingerprint(interpolators),
                _storageBufferOffsetAlignment);
            var guestGlobalBuffers = pixelEvaluation.GlobalMemoryBindings.Count + exportEvaluation.GlobalMemoryBindings.Count;
            var totalGlobalBuffers = _bakeScalars ? guestGlobalBuffers : guestGlobalBuffers + 2;
            _graphicsShaderCache.TryGetValue(shaderKey, out var compiled);
            SolidColorClear? solidClear = null;
            if (compiled.Vertex is null || compiled.Pixel is null)
            {
                if (IsProceduralFullscreenClearPair(exportState, exportEvaluation, pixelState, pixelEvaluation))
                {
                    var color = DecodeSolidClearColor(pixelEvaluation);
                    solidClear = new SolidColorClear(color.Red, color.Green, color.Blue, color.Alpha);
                    compiled = (GuestGpu.Current.GetDepthOnlyFragmentShader(), GuestGpu.Current.GetDepthOnlyFragmentShader());
                    _graphicsShaderCache.TryAdd(shaderKey, compiled);
                }
                else
                {
                    var pixelOutputs = new Gen5PixelOutputBinding[boundTargets.Length];
                    for (var location = 0; location < boundTargets.Length; location++)
                    {
                        pixelOutputs[location] = new Gen5PixelOutputBinding(boundTargets[location], (uint)location, outputKinds[location], outputMappings[location]);
                    }

                    if (!GuestGpu.Current.TryCompilePixelShader(
                            pixelState,
                            pixelEvaluation,
                            pixelOutputs,
                            out var pixelShader,
                            out error,
                            globalBufferBase: 0,
                            totalGlobalBufferCount: totalGlobalBuffers,
                            imageBindingBase: 0,
                            scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers,
                            pixelInputEnable: pixelInputEnable,
                            pixelInputAddress: pixelInputAddress,
                            pixelInputCntl: interpolators,
                            storageBufferOffsetAlignment: _storageBufferOffsetAlignment) ||
                        !GuestGpu.Current.TryCompileVertexShader(
                            exportState,
                            exportEvaluation,
                            out var vertexShader,
                            out error,
                            globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                            totalGlobalBufferCount: totalGlobalBuffers,
                            imageBindingBase: pixelEvaluation.ImageBindings.Count,
                            scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                            requiredVertexOutputCount: (int)attributeCount,
                            storageBufferOffsetAlignment: _storageBufferOffsetAlignment))
                    {
                        ReturnPooledEvaluationArrays(exportEvaluation);
                        ReturnPooledEvaluationArrays(pixelEvaluation);
                        return Unavailable(exportAddress, pixel.Address, error);
                    }

                    compiled = (vertexShader!, pixelShader!);
                    DumpCompiledShader("vs", exportAddress, exportFingerprint, compiled.Vertex, exportState.Program);
                    DumpCompiledShader("ps", pixel.Address, pixelFingerprint, compiled.Pixel, pixelState.Program);
                    GuestGpu.Current.CountShaderCompilation();
                    _graphicsShaderCache.TryAdd(shaderKey, compiled);
                }
            }
            else if (IsCachedFixedFullscreenClearPair(exportState, exportEvaluation, pixelState, pixelEvaluation))
            {
                var color = DecodeSolidClearColor(pixelEvaluation);
                solidClear = new SolidColorClear(color.Red, color.Green, color.Blue, color.Alpha);
            }

            preparationProfile.SwitchPhase(RenderPhaseProfile.Phase.GraphicsBindingDescription);
            var pixelTextures = solidClear is null ? DecodeTextures(pixelEvaluation.ImageBindings, pixel.Address, exportAddress) : [];
            var vertexTextures = solidClear is null ? DecodeTextures(exportEvaluation.ImageBindings, pixel.Address, exportAddress) : [];
            var vertexInputs = solidClear is null ? exportEvaluation.VertexInputs ?? [] : [];
            Gen5GlobalMemoryBinding[] runtimeBindings = [.. pixelEvaluation.GlobalMemoryBindings, .. exportEvaluation.GlobalMemoryBindings];
            var disableBlending = solidClear is null && IsTransparentPremultipliedFill(context, slotIndexes, pixelTextures.Count + vertexTextures.Count, vertexInputs.Count, pixelEvaluation.InitialScalarRegisters);
            var pixelProgram = CreateStageProgram(
                ShaderStageKind.Pixel,
                pixel.Address,
                compiled.Pixel,
                pixelState,
                pixelEvaluation,
                pixelTextures,
                [],
                globalBufferBase: 0,
                imageBindingBase: 0,
                totalGlobalBuffers,
                scalarBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers,
                disableBlending,
                runtimeBindings);
            var vertexProgram = CreateStageProgram(
                ShaderStageKind.Vertex,
                exportAddress,
                compiled.Vertex,
                exportState,
                exportEvaluation,
                vertexTextures,
                vertexInputs,
                globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                imageBindingBase: pixelEvaluation.ImageBindings.Count,
                totalGlobalBuffers,
                scalarBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                disableBlending: false,
                runtimeBindings: runtimeBindings);
            return new GraphicsPrograms
            {
                Vertex = ProgramId(compiled.Vertex),
                Pixel = ProgramId(compiled.Pixel),
                VertexInput = CreateVertexInput(vertexProgram, exportEvaluation),
                PixelInput = new PixelInputInfo { InputCount = pixelInputCount, Stage = new ShaderStageResources(pixelProgram, CreateSnapshot(pixelEvaluation, pixelProgram.UserDataBase)) },
                SolidClear = solidClear,
                PositionStream = FindPositionStream(vertexInputs),
            };
        }

        private GraphicsPrograms GetDepthOnlyPrograms(ulong exportAddress, Gen5ShaderState exportState, Gen5ShaderEvaluation exportEvaluation)
        {
            var exportFingerprint = _bakeScalars ? ComputeShaderStateFingerprint(exportEvaluation) : ComputeShaderStructuralFingerprint(exportEvaluation);
            var cacheKey = (exportAddress, exportState.ShaderChecksum, exportFingerprint, _storageBufferOffsetAlignment);
            var guestGlobalBuffers = exportEvaluation.GlobalMemoryBindings.Count;
            // The pixel scalar block stays unused by the fixed fragment stage; the vertex block sits behind it.
            var totalGlobalBuffers = _bakeScalars ? guestGlobalBuffers : guestGlobalBuffers + 2;
            _depthOnlyVertexShaderCache.TryGetValue(cacheKey, out var vertexShader);
            if (vertexShader is null)
            {
                if (!GuestGpu.Current.TryCompileVertexShader(
                        exportState,
                        exportEvaluation,
                        out vertexShader,
                        out var error,
                        globalBufferBase: 0,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: 0,
                        scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                        requiredVertexOutputCount: 0,
                        storageBufferOffsetAlignment: _storageBufferOffsetAlignment))
                {
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    return Unavailable(exportAddress, 0, error);
                }

                DumpCompiledShader("depth-vs", exportAddress, exportFingerprint, vertexShader!, exportState.Program);
                GuestGpu.Current.CountShaderCompilation();
                _depthOnlyVertexShaderCache.TryAdd(cacheKey, vertexShader!);
            }

            var textures = DecodeTextures(exportEvaluation.ImageBindings, 0, exportAddress);
            var vertexInputs = exportEvaluation.VertexInputs ?? [];
            var vertexProgram = CreateStageProgram(
                ShaderStageKind.Vertex,
                exportAddress,
                vertexShader!,
                exportState,
                exportEvaluation,
                textures,
                vertexInputs,
                globalBufferBase: 0,
                imageBindingBase: 0,
                totalGlobalBuffers,
                scalarBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                disableBlending: false);
            return new GraphicsPrograms
            {
                Vertex = ProgramId(vertexShader!),
                Pixel = default,
                VertexInput = CreateVertexInput(vertexProgram, exportEvaluation),
                PixelInput = new PixelInputInfo(),
                PositionStream = FindPositionStream(vertexInputs),
            };
        }

        private static GraphicsPrograms Unavailable(ulong exportAddress, ulong pixelAddress, string error)
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"The graphics programs are unavailable: export=0x{exportAddress:X16} pixel=0x{pixelAddress:X16} error={error}");
            }

            return new GraphicsPrograms { Available = false };
        }

        // The bound colour slots in order, with the output kind and export mapping the pixel program compiles against.
        private uint[] ResolveBoundTargets(ContextRegisters context, out Gen5PixelOutputKind[] kinds, out Gen5ColorComponentMapping[] mappings, out int[] slotIndexes)
        {
            var slots = new List<uint>(ContextRegisters.ColorTargetCount);
            for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
            {
                if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
                {
                    continue;
                }

                if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is not null)
                {
                    slots.Add(slot);
                }
            }

            kinds = new Gen5PixelOutputKind[slots.Count];
            mappings = new Gen5ColorComponentMapping[slots.Count];
            slotIndexes = new int[slots.Count];
            for (var index = 0; index < slots.Count; index++)
            {
                var words = context.ColorTargets[slots[index]];
                slotIndexes[index] = (int)slots[index];
                if (!_pipelines.TryResolveColorOutput((uint)words.Layout, (uint)words.NumberType, (uint)words.Order, out kinds[index], out mappings[index]))
                {
                    throw SubmissionScheduler.Fatal($"The color target format has no pixel output kind: slot={slots[index]} layout={(uint)words.Layout} numberType={(uint)words.NumberType} order={(uint)words.Order}.");
                }
            }

            return slots.ToArray();
        }

        // An untextured transparent fill through premultiplied blending overwrites its targets.
        private static bool IsTransparentPremultipliedFill(ContextRegisters context, int[] slots, int textureCount, int vertexInputCount, IReadOnlyList<uint> pixelUserData)
        {
            if (!_premultipliedFillClearEnabled || textureCount != 0 || vertexInputCount != 0 || pixelUserData.Count < 4 || slots.Length == 0)
            {
                return false;
            }

            foreach (var slot in slots)
            {
                var blend = context.BlendControls[slot];
                if (!blend.Enable || blend.ColorSourceFactor != 1 || blend.ColorDestinationFactor != 5 || blend.ColorFunction != 0)
                {
                    return false;
                }
            }

            for (var index = 0; index < 4; index++)
            {
                if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEmptyResourceTableDrawRejected(
            ulong pixelAddress,
            Gen5ShaderState pixelState,
            Gen5ShaderEvaluation pixelEvaluation,
            ulong exportAddress,
            IReadOnlyDictionary<uint, uint> registers,
            out string error)
        {
            error = string.Empty;
            var emptyTables = pixelState.Metadata is { ShaderResourceTableSizeDwords: 0, ExtendedUserDataSizeDwords: 0 };
            if (!emptyTables && !Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(pixelAddress))
            {
                return false;
            }

            var hasAnyImageSlot = pixelEvaluation.ImageBindings.Count > 0;
            var hasUsablePixelImage = false;
            foreach (var binding in pixelEvaluation.ImageBindings)
            {
                if (TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture) && texture.Address != 0)
                {
                    hasUsablePixelImage = true;
                    break;
                }
            }

            var hasUsablePixelGlobal = pixelEvaluation.GlobalMemoryBindings.Any(static binding => binding.BaseAddress != 0);
            if (!hasAnyImageSlot || hasUsablePixelImage || hasUsablePixelGlobal)
            {
                return false;
            }

            error = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(pixelAddress)
                ? "empty-srt-scalar-pointer-fallback"
                : "empty-srt-no-usable-resources";
            lock (_submitTraceGate)
            {
                if (_tracedEmptySrtDrawRejects.Add(pixelAddress))
                {
                    Console.Error.WriteLine($"[LOADER][WARN] agc.draw_reject ps=0x{pixelAddress:X16} es=0x{exportAddress:X16} reason={error}");
                }
            }

            return true;
        }

        private IReadOnlyList<GuestDrawTexture> DecodeTextures(IReadOnlyList<Gen5ImageBinding> bindings, ulong pixelAddress, ulong exportAddress)
        {
            var translated = new List<TranslatedImageBinding>(bindings.Count);
            foreach (var binding in bindings)
            {
                if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
                {
                    if (_strictShaderDescriptors)
                    {
                        throw SubmissionScheduler.Fatal($"The texture descriptor is invalid: pc=0x{binding.Pc:X} pixel=0x{pixelAddress:X16} export=0x{exportAddress:X16}.");
                    }

                    texture = CreateFallbackTextureDescriptor(binding.ResourceDescriptor, binding.Control.Dimension);
                }

                translated.Add(new TranslatedImageBinding(
                    texture,
                    Gen5ShaderTranslator.RequiresStorageImage(binding, bindings),
                    binding.MipLevel ?? 0,
                    NormalizeSamplerDescriptorForImageOperation(binding.SamplerDescriptor),
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding),
                    binding.ResourceDescriptor,
                    binding.HasDynamicMip,
                    Gen5ShaderTranslator.IsVolumeImageBinding(binding) ? 2u : 1u));
            }

            return GuestGpu.Current is IGuestImageSnapshotBackend
                ? SharpEmu.Libs.Gpu.Metal.MetalTextureSnapshots.CreateTextureSnapshots(_context, translated, out _)
                : translated.Select(CreateDescriptorDrawTexture).ToArray();
        }

        private static CompiledStageProgram CreateStageProgram(
            ShaderStageKind stage,
            ulong address,
            IGuestCompiledShader shader,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            IReadOnlyList<GuestDrawTexture> textures,
            IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
            int globalBufferBase,
            int imageBindingBase,
            int totalGlobalBuffers,
            int scalarBufferIndex,
            bool disableBlending,
            IReadOnlyList<Gen5GlobalMemoryBinding>? runtimeBindings = null)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.StageResourceDescription);
            var bindings = evaluation.GlobalMemoryBindings;
            var buffers = new BufferResourceInfo[bindings.Count];
            var instructionMetadata = GetStageInstructionMetadata(state.Program);

            for (var index = 0; index < bindings.Count; index++)
            {
                buffers[index] = DescribeBuffer(bindings[index], instructionMetadata.InstructionsByAddress);
            }

            var images = new ImageResourceInfo[evaluation.ImageBindings.Count];
            var samplerCount = 0;
            for (var index = 0; index < images.Length; index++)
            {
                var binding = evaluation.ImageBindings[index];
                var storage = Gen5ShaderTranslator.RequiresStorageImage(binding, evaluation.ImageBindings);
                images[index] = new ImageResourceInfo(storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled, Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode));
                samplerCount += storage ? 0 : 1;
            }

            var scalars = evaluation.InitialScalarRegisters.ToArray();
            // Shader offset corrections use the complete descriptor layout, not the stage-local binding indices.
            runtimeBindings ??= bindings;
            return new CompiledStageProgram
            {
                Shader = shader,
                Address = address,
                Stage = stage,
                Hash = state.ShaderChecksum | (address << 32),
                UserDataBase = state.UserDataScalarRegisterBase,
                VertexOffsetScalarRegister = stage == ShaderStageKind.Vertex &&
                    Gen5ShaderTranslator.TryGetEmbeddedVertexOffsetRegister(state, vertexInputs, out var vertexOffsetRegister)
                        ? vertexOffsetRegister
                        : ShaderProgramInfo.NoScalarRegister,
                ParameterExportMask = state.Program.ParameterExportMask,
                Buffers = buffers,
                Images = images,
                SamplerCount = samplerCount,
                HasBitwiseExclusiveOr = instructionMetadata.HasBitwiseExclusiveOr,
                Textures = textures,
                GlobalBuffers = CreateGuestMemoryBuffers(bindings),
                ScalarBuffer = scalarBufferIndex < 0
                    ? null
                    : new GuestMemoryBuffer(0, PackRuntimeScalarState(scalars, runtimeBindings), GetRuntimeScalarBufferLength(runtimeBindings.Count), (ulong)GetRuntimeScalarBufferLength(runtimeBindings.Count), Pooled: true),
                VertexInputs = vertexInputs,
                VertexAttributes = [],
                GlobalBufferBase = globalBufferBase,
                ImageBindingBase = imageBindingBase,
                TotalGlobalBuffers = totalGlobalBuffers,
                DisableBlending = disableBlending,
                ScalarBufferIndex = scalarBufferIndex,
            };
        }

        // The access a program makes to one global memory binding, from the instructions that read or write it.
        private static BufferResourceInfo DescribeBuffer(Gen5GlobalMemoryBinding binding, IReadOnlyDictionary<uint, Gen5ShaderInstruction> instructions)
        {
            var read = false;
            var formatted = false;
            var atomic = false;
            var scalar = false;
            var maxByteExtent = 0u;
            foreach (var pc in binding.InstructionPcs)
            {
                if (!instructions.TryGetValue(pc, out var instruction))
                {
                    continue;
                }

                var opcode = instruction.Opcode;
                var isStore = opcode.Contains("Store", StringComparison.Ordinal);
                atomic |= opcode.Contains("Atomic", StringComparison.Ordinal);
                read |= !isStore || atomic;
                scalar |= opcode.StartsWith('S') && opcode.Contains("Load", StringComparison.Ordinal);
                formatted |= opcode.Contains("Format", StringComparison.Ordinal);
                var dwords = instruction.Control switch
                {
                    Gen5BufferMemoryControl control => control.DwordCount,
                    Gen5GlobalMemoryControl control => control.DwordCount,
                    _ => 0u,
                };
                maxByteExtent = Math.Max(maxByteExtent, dwords * sizeof(uint));
            }

            return new BufferResourceInfo(read, binding.Writable, atomic, formatted, scalar, maxByteExtent, PackedStride: 0);
        }

        // The descriptor words of every resource: real when the scalars carry them, else formed from the binding.
        private static ResourceSnapshot CreateSnapshot(Gen5ShaderEvaluation evaluation, uint userDataBase)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ResourceSnapshotCreation);
            var scalars = evaluation.InitialScalarRegisters;
            var bindings = evaluation.GlobalMemoryBindings;
            var buffers = new uint[bindings.Count][];
            for (var index = 0; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                var scalarAddress = (int)binding.ScalarAddress;
                if (scalarAddress + 4 <= scalars.Count)
                {
                    var words = new BufferDescriptorWords(scalars[scalarAddress], scalars[scalarAddress + 1], scalars[scalarAddress + 2], scalars[scalarAddress + 3]);
                    if (words.Address == binding.BaseAddress && words.Address != 0)
                    {
                        buffers[index] = [words.Word0, words.Word1, words.Word2, words.Word3];
                        continue;
                    }
                }

                var records = (uint)Math.Min(binding.Size, uint.MaxValue);
                buffers[index] = [(uint)binding.BaseAddress, (uint)((binding.BaseAddress >> 32) & 0xFFFF), records, 0];
            }

            var images = new uint[evaluation.ImageBindings.Count][];
            var samplers = new List<uint[]>(evaluation.ImageBindings.Count);
            for (var index = 0; index < images.Length; index++)
            {
                var binding = evaluation.ImageBindings[index];
                images[index] = binding.ResourceDescriptor.ToArray();
                if (!Gen5ShaderTranslator.RequiresStorageImage(binding, evaluation.ImageBindings))
                {
                    samplers.Add(binding.SamplerDescriptor.ToArray());
                }
            }

            return new ResourceSnapshot
            {
                Buffers = buffers,
                Images = images,
                Samplers = samplers.ToArray(),
                UserData = scalars.Skip(checked((int)userDataBase)).ToArray(),
            };
        }

        // Vertex inputs group by buffer; the attributes of one buffer share its binding.
        private static VertexInputInfo CreateVertexInput(CompiledStageProgram vertexProgram, Gen5ShaderEvaluation evaluation)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputDescription);
            var inputs = vertexProgram.VertexInputs;
            var buffers = new List<VertexInputBuffer>(inputs.Count);
            var keys = new List<(ulong Address, uint Stride, bool PerInstance)>(inputs.Count);
            var attributes = new VertexAttributeLayout[inputs.Count];
            for (var index = 0; index < inputs.Count; index++)
            {
                var input = inputs[index];
                var key = (input.BaseAddress, input.Stride, input.PerInstance);
                var bufferIndex = keys.IndexOf(key);
                if (bufferIndex < 0)
                {
                    bufferIndex = buffers.Count;
                    keys.Add(key);
                    var recordCount = input.Stride == 0 ? (uint)input.DataLength : (uint)(input.DataLength / Math.Max(input.Stride, 1u));
                    buffers.Add(new VertexInputBuffer(input.BaseAddress, input.Stride, recordCount));
                }
                else
                {
                    var existing = buffers[bufferIndex];
                    var recordCount = input.Stride == 0 ? (uint)input.DataLength : (uint)(input.DataLength / Math.Max(input.Stride, 1u));
                    if (recordCount > existing.RecordCount)
                    {
                        buffers[bufferIndex] = existing with { RecordCount = recordCount };
                    }
                }

                attributes[index] = new VertexAttributeLayout(input.Location, bufferIndex, input.ComponentCount, input.DataFormat, input.NumberFormat, input.OffsetBytes, input.PerInstance);
            }

            vertexProgram.VertexAttributes = attributes;
            return new VertexInputInfo
            {
                Buffers = buffers.ToArray(),
                FetchEmbedded = inputs.Count != 0,
                Stage = new ShaderStageResources(vertexProgram, CreateSnapshot(evaluation, vertexProgram.UserDataBase)),
            };
        }

        private static VertexPositionStream? FindPositionStream(IReadOnlyList<Gen5VertexInputBinding> inputs)
        {
            foreach (var input in inputs)
            {
                if (input.DataFormat == PositionDataFormat && input.NumberFormat == PositionNumberFormat)
                {
                    return new VertexPositionStream(input.BaseAddress, input.Stride, input.OffsetBytes);
                }
            }

            return null;
        }

        public PipelineHandle CreateGraphicsPipeline(
            ReadOnlySpan<ColorTargetState> colors,
            in DepthAttachmentState depth,
            VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput,
            ContextRegisters context,
            in RenderingState rendering,
            PrimitiveTopology topology,
            bool primitiveRestartEnabled,
            ShaderProgram vertexProgram,
            ShaderProgram pixelProgram) =>
            _pipelines.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, vertexProgram, pixelProgram);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator)
        {
            var state = RequireState();
            var registers = state.ShRegisters;
            var shaderAddress = compute.Address;
            ulong shaderHeader;
            lock (_submitTraceGate)
            {
                _shaderHeadersByCode.TryGetValue(shaderAddress, out shaderHeader);
            }

            var systemRegisters = DecodeComputeSystemRegisters(registers);
            if (!Gen5ShaderTranslator.TryCreateState(
                    _context,
                    shaderAddress,
                    shaderHeader,
                    registers,
                    ComputeUserDataRegister,
                    out var shaderState,
                    out var error,
                    systemRegisters,
                    shaderChecksum: compute.ProgramChecksum) ||
                !Gen5ShaderScalarEvaluator.TryEvaluate(_context, shaderState, out var evaluation, out error, profileStage: Gen5ShaderEvaluationStage.Compute))
            {
                return UnavailableCompute(shaderAddress, error);
            }

            var localSizeX = Math.Max(compute.ThreadsX & 0xFFFFu, 1u);
            var localSizeY = Math.Max(compute.ThreadsY & 0xFFFFu, 1u);
            var localSizeZ = Math.Max(compute.ThreadsZ & 0xFFFFu, 1u);
            var waveLanes = (dispatchInitiator & Wave32Bit) != 0 ? 32u : 64u;
            var textures = DecodeTextures(evaluation.ImageBindings, 0, shaderAddress);
            var hasStorageBinding = evaluation.ImageBindings.Any(binding => Gen5ShaderTranslator.RequiresStorageImage(binding, evaluation.ImageBindings));
            var writesGlobalMemory = evaluation.GlobalMemoryBindings.Any(static binding => binding.Writable);
            var emptyResourceTables = shaderState.Metadata is { ShaderResourceTableSizeDwords: 0, ExtendedUserDataSizeDwords: 0 };
            if (emptyResourceTables &&
                (Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress) ||
                 (textures.All(static texture => texture.Address == 0) && !evaluation.GlobalMemoryBindings.Any(static binding => binding.BaseAddress != 0))))
            {
                ReturnPooledEvaluationArrays(evaluation);
                return UnavailableCompute(shaderAddress, Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress) ? "empty-srt-scalar-pointer-fallback" : "empty-srt-no-usable-resources");
            }

            var (groupsX, groupsY, groupsZ) = PendingDispatchGroups;
            var dispatch = new ComputeDispatch(groupsX, groupsY, groupsZ, 0, 0, 0, waveLanes, IsIndirect: false, uint.MaxValue, uint.MaxValue, uint.MaxValue);
            if (!hasStorageBinding && writesGlobalMemory &&
                TrySubmitMaskedDwordCopyKernel(_context, shaderState.Program, evaluation, dispatch, localSizeX, localSizeY, localSizeZ, out _, out var copyDescription))
            {
                TraceAgcShader($"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} queue={state.QueueName} submission={state.ActiveSubmissionId} {copyDescription}");
                ReturnPooledEvaluationArrays(evaluation);
                return new ComputeProgram { Consumed = true };
            }

            var shaderKey = (
                Cs: shaderAddress,
                Checksum: shaderState.ShaderChecksum,
                State: _bakeScalars ? ComputeShaderStateFingerprint(evaluation) : ComputeShaderStructuralFingerprint(evaluation),
                LocalX: localSizeX,
                LocalY: localSizeY,
                LocalZ: localSizeZ,
                WaveLanes: waveLanes,
                AliasAlignment: _storageBufferOffsetAlignment);
            var guestGlobalBufferCount = evaluation.GlobalMemoryBindings.Count;
            var totalGlobalBufferCount = _bakeScalars ? guestGlobalBufferCount : guestGlobalBufferCount + 1;
            _computeShaderCache.TryGetValue(shaderKey, out var computeShader);
            if (computeShader is null)
            {
                if (!GuestGpu.Current.TryCompileComputeShader(
                        shaderState,
                        evaluation,
                        localSizeX,
                        localSizeY,
                        localSizeZ,
                        out computeShader,
                        out error,
                        totalGlobalBufferCount,
                        initialScalarBufferIndex: _bakeScalars ? -1 : guestGlobalBufferCount,
                        waveLaneCount: waveLanes,
                        storageBufferOffsetAlignment: _storageBufferOffsetAlignment))
                {
                    ReturnPooledEvaluationArrays(evaluation);
                    return UnavailableCompute(shaderAddress, error);
                }

                DumpCompiledShader("cs", shaderAddress, shaderKey.State, computeShader!, shaderState.Program);
                GuestGpu.Current.CountShaderCompilation();
                _computeShaderCache.TryAdd(shaderKey, computeShader!);
            }

            var program = CreateStageProgram(
                ShaderStageKind.Compute,
                shaderAddress,
                computeShader!,
                shaderState,
                evaluation,
                textures,
                [],
                globalBufferBase: 0,
                imageBindingBase: 0,
                totalGlobalBufferCount,
                scalarBufferIndex: _bakeScalars ? -1 : guestGlobalBufferCount,
                disableBlending: false);
            return new ComputeProgram
            {
                Program = ProgramId(computeShader!),
                Input = new ComputeInputInfo
                {
                    ThreadsX = localSizeX,
                    ThreadsY = localSizeY,
                    ThreadsZ = localSizeZ,
                    DispatchThreadDimensions = (dispatchInitiator & UseThreadDimensionsBit) != 0,
                    GroupIdX = systemRegisters.WorkGroupXRegister is not null,
                    GroupIdY = systemRegisters.WorkGroupYRegister is not null,
                    GroupIdZ = systemRegisters.WorkGroupZRegister is not null,
                    ThreadIdCount = compute.ThreadIdComponentCount + 1,
                    ThreadGroupSizeEnabled = compute.ThreadGroupSizeEnable,
                    WaveSize = waveLanes,
                    LocalDataShareDwords = compute.LocalDataShareSize,
                    Stage = new ShaderStageResources(program, CreateSnapshot(evaluation, program.UserDataBase)),
                },
            };
        }

        private static ComputeProgram UnavailableCompute(ulong shaderAddress, string error)
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"The compute program is unavailable: shader=0x{shaderAddress:X16} error={error}");
            }

            return new ComputeProgram { Available = false };
        }

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            _pipelines.CreateComputePipeline(input, program);
    }

    private static readonly HashSet<ulong> _tracedEmptySrtDrawRejects = new();

    // Keep shader cache lookups independent of trace locking.
    private static readonly ConcurrentDictionary<
        (ulong Es, uint EsChecksum, ulong EsState,
         ulong Ps, uint PsChecksum, ulong PsState, ulong OutputLayout,
         ulong OutputMappings, uint OutputCount, uint Attributes, uint PsInputEna,
         uint PsInputAddr, ulong PsInputCntl, ulong AliasAlignment),
        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel)> _graphicsShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, uint Checksum, ulong State, ulong AliasAlignment),
        IGuestCompiledShader> _depthOnlyVertexShaderCache = new();

    // Optionally reject undecodable descriptors instead of creating fallback bindings.
    private static readonly bool _strictShaderDescriptors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SHADER_DESCRIPTORS"),
        "1",
        StringComparison.Ordinal);

    private static bool IsCachedFixedFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation) =>
        IsProceduralFullscreenClearPair(
            exportState,
            exportEvaluation,
            pixelState,
            pixelEvaluation);

    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation)
    {
        if ((exportEvaluation.VertexInputs?.Count ?? 0) != 0 ||
            exportEvaluation.ImageBindings.Count != 0 ||
            pixelEvaluation.ImageBindings.Count != 0 ||
            exportEvaluation.GlobalMemoryBindings.Count != 0 ||
            pixelEvaluation.GlobalMemoryBindings.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(exportState, target: 12) ||
            !HasExportTarget(pixelState, target: 0))
        {
            return false;
        }

        if (pixelState.Program.Instructions.Count is 0 or > 8 ||
            exportState.Program.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelState.Program.Instructions.All(IsBenignClearPixelInstruction) &&
               exportState.Program.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderState state, uint target) =>
        state.Program.Instructions.Any(instruction =>
            instruction.Control is Gen5ExportControl export &&
            export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: 0 };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or
            Gen5ImageControl or
            Gen5GlobalMemoryControl or
            Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position (12) plus ignored NGG/param exports.
            return export.Target is 12 or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "SSendmsg" or
            "VMovB32" or
            "VAndB32" or
            "VAddI32" or
            "VLshlrevB32" or
            "VCvtF32I32" or
            "VCvtF32U32" ||
            instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk or
                Gen5ShaderEncoding.Sopp;
    }

    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(
        Gen5ShaderEvaluation pixelEvaluation)
    {
        // Default opaque white; guest clear shaders often mov a 1.0 literal into v0.
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelEvaluation.InitialScalarRegisters.Count > 0)
        {
            var bits = pixelEvaluation.InitialScalarRegisters[0];
            if (bits != 0)
            {
                red = green = blue = alpha = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(red) || red < 0f || red > 4f)
                {
                    red = green = blue = alpha = 1f;
                }
            }
        }

        return (red, green, blue, alpha);
    }

    private static readonly bool _premultipliedFillClearEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"),
        "1",
        StringComparison.Ordinal);

    // A float32x3 vertex position stream (BUF_DATA_FORMAT_32_32_32 / FLOAT).
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;

    private static ulong ComputePixelInputControlFingerprint(ReadOnlySpan<uint> inputControls)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var value in inputControls)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    private static uint GetInterpolatedAttributeCount(Gen5ShaderState state)
    {
        var maxAttribute = -1;
        foreach (var instruction in state.Program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                maxAttribute = Math.Max(maxAttribute, (int)interpolation.Attribute);
            }
        }

        return (uint)(maxAttribute + 1);
    }

    private static readonly bool _traceDepthMetadata = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DEPTH_METADATA"),
        "1",
        StringComparison.Ordinal);

    internal static uint ExtractRenderTargetComponentSwap(uint colorInfo) =>
        (colorInfo >> 11) & 0x3u;

    internal static uint ExtractRenderTargetTileMode(uint colorAttrib3) =>
        (colorAttrib3 >> 14) & 0x1Fu;

    private static void DumpCompiledShader(
        string stage,
        ulong shaderAddress,
        ulong stateFingerprint,
        IGuestCompiledShader shader,
        Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 ||
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var addressFilter = Environment.GetEnvironmentVariable(
            "SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(
                    span,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return;
            }
        }

        var directory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        Directory.CreateDirectory(directory);
        var name = $"{shaderAddress:X16}-{stateFingerprint:X16}.{stage}";
        File.WriteAllBytes(
            Path.Combine(directory, $"{name}.{shader.PayloadFileExtension}"),
            shader.Payload);

        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} " +
                $"{string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} " +
                $"{instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- " +
                $"{string.Join(',', instruction.Sources)} " +
                $"{instruction.Control}");
        }

        File.WriteAllLines(Path.Combine(directory, $"{name}.ir.txt"), lines);
    }
    private const uint ComputePgmRsrc2 = 0x213;

    private const uint ComputeUserDataRegister = 0x240;

    private static readonly ConcurrentDictionary<
        (ulong Cs, uint Checksum, ulong State, uint LocalX, uint LocalY, uint LocalZ,
         uint WaveLanes, ulong AliasAlignment),
        IGuestCompiledShader> _computeShaderCache = new();

    private readonly record struct ComputeDispatch(
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        uint WaveLaneCount,
        bool IsIndirect,
        uint ThreadCountX,
        uint ThreadCountY,
        uint ThreadCountZ);

    private static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(ComputePgmRsrc2, out var resourceControl);
        var nextRegister = (resourceControl >> 1) & 0x1Fu;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;

        if ((resourceControl & (1u << 7)) != 0)
        {
            workGroupX = nextRegister++;
        }

        if ((resourceControl & (1u << 8)) != 0)
        {
            workGroupY = nextRegister++;
        }

        if ((resourceControl & (1u << 9)) != 0)
        {
            workGroupZ = nextRegister++;
        }

        if ((resourceControl & (1u << 10)) != 0)
        {
            threadGroupSize = nextRegister++;
        }

        return new Gen5ComputeSystemRegisters(
            workGroupX,
            workGroupY,
            workGroupZ,
            threadGroupSize);
    }

    // Replace only the exact masked-copy kernel and descriptor shape.
    // Execute its writes in command order over the mapped guest range.
    private static bool TrySubmitMaskedDwordCopyKernel(
        CpuContext ctx,
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out long workSequence,
        out string description)
    {
        workSequence = 0;
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "SMovB32",
            "STtraceData",
            "SInstPrefetch",
            "VLshlAddU32",
            "SBufferLoadDword",
            "SWaitcnt",
            "VCmpxGtU32",
            "SCbranchExecz",
            "SBufferLoadDword",
            "SWaitcnt",
            "VAndB32",
            "BufferLoadFormatX",
            "SWaitcnt",
            "BufferStoreFormatX",
            "SEndpgm",
        ];
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(expectedOpcodes) ||
            !IsExactMaskedDwordCopyInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 12)
        {
            return false;
        }

        var control = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 8 && !binding.Writable);
        var source = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 && !binding.Writable);
        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 4 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        if (control is null || source is null || destination is null ||
            control.DataLength < 2 * sizeof(uint) ||
            source.DataLength < sizeof(uint) ||
            destination.BaseAddress == 0 ||
            destination.DataLength < sizeof(uint) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                source.ScalarAddress,
                source.BaseAddress) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                destination.ScalarAddress,
                destination.BaseAddress))
        {
            return false;
        }

        var elementCount = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(0, sizeof(uint)));
        var sourceMask = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(sizeof(uint), sizeof(uint)));
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableDwords = (uint)(destination.DataLength / sizeof(uint));
        var outputDwords = (uint)Math.Min(
            Math.Min((ulong)elementCount, dispatchedThreads),
            writableDwords);
        if (outputDwords == 0)
        {
            return false;
        }

        var output = new byte[checked((int)outputDwords * sizeof(uint))];
        var outputWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            output.AsSpan());
        if (sourceMask == 0)
        {
            outputWords.Fill(BinaryPrimitives.ReadUInt32LittleEndian(
                source.Data.AsSpan(0, sizeof(uint))));
        }
        else
        {
            var sourceWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                source.Data.AsSpan(0, source.DataLength - (source.DataLength % sizeof(uint))));
            for (uint index = 0; index < outputDwords; index++)
            {
                var sourceIndex = index & sourceMask;
                outputWords[(int)index] = sourceIndex < (uint)sourceWords.Length
                    ? sourceWords[(int)sourceIndex]
                    : 0;
            }
        }

        // The dispatch runs in stream order on the worker, so the replacement writes at once.
        var destinationAddress = destination.BaseAddress;
        if (!ctx.Memory.TryWrite(destinationAddress, output))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] AGC masked-copy fast path failed " +
                $"dst=0x{destinationAddress:X16} bytes={output.Length}");
            return false;
        }

        workSequence = 1;
        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"elements={elementCount} mask=0x{sourceMask:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private static bool IsExactMaskedDwordCopyInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        static bool IsBufferControl(
            Gen5ShaderInstruction instruction,
            uint vectorAddress,
            uint vectorData,
            uint scalarResource) =>
            instruction.Control is Gen5BufferMemoryControl
            {
                DwordCount: 1,
                OffsetBytes: 0,
                IndexEnabled: true,
                OffsetEnabled: false,
            } control &&
            control.VectorAddress == vectorAddress &&
            control.VectorData == vectorData &&
            control.ScalarResource == scalarResource;

        static bool IsScalarLoad(
            Gen5ShaderInstruction instruction,
            int offsetBytes) =>
            instruction.Control is Gen5ScalarMemoryControl
            {
                DestinationCount: 1,
                DynamicOffsetRegister: null,
            } control &&
            control.ImmediateOffsetBytes == offsetBytes &&
            instruction.Destinations.Count == 1 &&
            IsOperand(
                instruction.Destinations[0],
                Gen5OperandKind.ScalarRegister,
                106) &&
            instruction.Sources.Count >= 1 &&
            IsOperand(
                instruction.Sources[0],
                Gen5OperandKind.ScalarRegister,
                8);

        // Require matching operands and opcodes; either change can alter written lanes or addresses.
        var globalId = instructions[3];
        var compare = instructions[6];
        var sourceIndex = instructions[10];
        var load = instructions[11];
        var store = instructions[13];
        return
            globalId.Destinations.Count == 1 &&
            IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 0) &&
            globalId.Sources.Count == 3 &&
            IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 12) &&
            IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) &&
            IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0) &&
            IsScalarLoad(instructions[4], offsetBytes: 0) &&
            compare.Sources.Count == 2 &&
            IsOperand(compare.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(compare.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            instructions[7].Words.Count == 1 &&
            (instructions[7].Words[0] & 0xFFFFu) == 9 &&
            IsScalarLoad(instructions[8], offsetBytes: sizeof(uint)) &&
            sourceIndex.Destinations.Count == 1 &&
            IsOperand(sourceIndex.Destinations[0], Gen5OperandKind.VectorRegister, 1) &&
            sourceIndex.Sources.Count == 2 &&
            IsOperand(sourceIndex.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(sourceIndex.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            IsBufferControl(load, vectorAddress: 1, vectorData: 1, scalarResource: 0) &&
            IsBufferControl(store, vectorAddress: 0, vectorData: 1, scalarResource: 4);
    }

    private static bool IsExactMaskedDwordCopyDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        uint scalarBase,
        ulong expectedBaseAddress)
    {
        if (scalarBase + 3 >= scalarRegisters.Count)
        {
            return false;
        }

        var word0 = scalarRegisters[(int)scalarBase];
        var word1 = scalarRegisters[(int)scalarBase + 1];
        var word3 = scalarRegisters[(int)scalarBase + 3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        // Require structured 32-bit indexing without thread-index or swizzle address changes.
        return baseAddress == expectedBaseAddress &&
               stride == sizeof(uint) &&
               !cacheSwizzle &&
               !swizzleEnabled &&
               unifiedFormat == 20 &&
               !addTidEnabled &&
               outOfBoundsSelect == 0 &&
               type == 0 &&
               dstSelectX == 4;
    }
}
