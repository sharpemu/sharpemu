// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
#if DEBUG
    static AgcExports()
    {
        ValidateWriteDataControlDecoders();
        ValidateDispatchInitiators();
        ValidateSubmittedQueueAndReleaseMemDecoders();
        ValidateAcquireMemAndQueueResetDecoders();
        ValidateDepthTargetDecoder();
    }
#endif

    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ItNop = 0x10;
    private const uint ItSetBase = 0x11;
    private const uint ItIndexBufferSize = 0x13;
    private const uint ItIndexBase = 0x26;
    private const uint ItDrawIndirect = 0x24;
    private const uint ItDrawIndexIndirect = 0x25;
    private const uint ItDrawIndex2 = 0x27;
    private const uint ItIndexType = 0x2A;
    private const uint ItDrawIndexAuto = 0x2D;
    private const uint ItNumInstances = 0x2F;
    private const uint ItDrawIndexMultiAuto = 0x30;
    private const uint ItDrawIndexOffset2 = 0x35;
    private const uint ItWriteData = 0x37;
    private const uint ItDispatchDirect = 0x15;
    private const uint ItDispatchIndirect = 0x16;
    private const uint ItWaitRegMem = 0x3C;
    private const uint ItIndirectBuffer = 0x3F;
    private const uint ItEventWrite = 0x46;
    private const uint ItReleaseMem = 0x49;
    private const uint ItDmaData = 0x50;
    private const uint ItSetContextReg = 0x69;
    private const uint ItSetShReg = 0x76;
    private const uint ItSetUconfigReg = 0x79;
    private const uint ItGetLodStats = 0x8E;
    private const uint RZero = 0x00;
    private const uint RDrawIndexAuto = 0x04;
    private const uint RDrawReset = 0x05;
    private const uint RWaitFlipDone = 0x06;
    private const uint RAcbReset = 0x09;
    private const uint RWaitMem32 = 0x0A;
    private const uint RPushMarker = 0x0B;
    private const uint RPopMarker = 0x0C;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;
    private const uint RAcquireMem = 0x14;
    private const uint RWriteData = 0x15;
    private const uint RWaitMem64 = 0x16;
    private const uint RFlip = 0x17;
    private const uint RReleaseMem = 0x18;
    private const uint RDmaData = 0x19;
    private const uint RIndexBase = 0x1B;
    private const uint RIndexCount = 0x1C;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    private const uint SpiShaderPgmLoGs = 0x8A;
    private const uint SpiShaderPgmHiGs = 0x8B;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint ComputePgmRsrc2 = 0x213;
    private const uint ComputeStartX = 0x204;
    private const uint ComputeStartY = 0x205;
    private const uint ComputeStartZ = 0x206;
    private const uint ComputeNumThreadX = 0x207;
    private const uint ComputeNumThreadY = 0x208;
    private const uint ComputeNumThreadZ = 0x209;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint PaScScreenScissorTl = 0x0C;
    private const uint PaScScreenScissorBr = 0x0D;
    private const uint CbTargetMask = 0x8E;
    private const uint PaScWindowOffset = 0x80;
    private const uint PaScWindowScissorTl = 0x81;
    private const uint PaScWindowScissorBr = 0x82;
    private const uint PaScGenericScissorTl = 0x90;
    private const uint PaScGenericScissorBr = 0x91;
    private const uint PaScVportScissor0Tl = 0x94;
    private const uint PaScVportScissor0Br = 0x95;
    private const uint PaClVportXScale = 0x10F;
    private const uint PaClVportXOffset = 0x110;
    private const uint PaClVportYScale = 0x111;
    private const uint PaClVportYOffset = 0x112;
    private const uint PaScVportZMin0 = 0xB4;
    private const uint PaScVportZMax0 = 0xB5;
    private const uint CbColorControl = 0x202;
    private const uint CbBlendRed = 0x105;
    private const uint CbBlendGreen = 0x106;
    private const uint CbBlendBlue = 0x107;
    private const uint CbBlendAlpha = 0x108;
    private const uint CbColor0Base = 0x318;
    private const uint CbColorRegisterStride = 15;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    private const uint CbBlend0Control = 0x1E0;
    private const uint PaScModeCntl0 = 0x292;
    // GFX10 DB context registers (register byte address minus 0x28000, / 4).
    private const uint DbRenderControl = 0x000;
    private const uint DbDepthView = 0x002;
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbDepthClear = 0x00B;
    private const uint DbZInfo = 0x010;
    private const uint DbZReadBase = 0x012;
    private const uint DbZWriteBase = 0x014;
    private const uint DbZReadBaseHi = 0x01A;
    private const uint DbZWriteBaseHi = 0x01C;
    private const int ColorTargetCount = 8;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint NggUserDataScalarRegisterBase = 8;
    private const uint Gen5TextureFormatR8G8B8A8Unorm = 10;
    private const uint Gen5TextureFormatR16G16B16A16Float = 12;
    private const uint Gen5TextureType1D = 8;
    private const uint Gen5TextureType2D = 9;
    private const ulong MaxPresentedTextureBytes = 128UL * 1024UL * 1024UL;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong VideoOutPixelFormat2R8G8B8A8Srgb = 0x8000000022000000;
    private const ulong VideoOutPixelFormat2B8G8R8A8Srgb = 0x8000000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2 = 0x8100000622000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2 = 0x8100000600000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Srgb = 0x8100000022000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Srgb = 0x8100000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Bt2100Pq = 0x8100070422000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Bt2100Pq = 0x8100070400000000;
    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion10 = 10;
    private const uint RegisterDefaultsVersion13 = 13;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ResourceRegistrationBytesPerResource = 0x118;
    private const ulong ResourceRegistrationBytesPerOwner = 0x1E0;
    private const int ResourceRegistrationMaxNameLength = 256;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferUserDataOffset = 0x28;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;
    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private static readonly object _submitTraceGate = new();
    private static readonly HashSet<uint> _tracedDcbSizes = new();
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedShaderDecodePairs = new();
    private static readonly HashSet<(ulong Es, ulong Ps, ulong Target, ulong Texture, uint VertexCount)> _tracedShaderDraws = new();
    private static readonly HashSet<(ulong Ps, string Error)> _tracedShaderFailures = new();
    private static readonly HashSet<(int Handle, int Index, ulong Address, string Path)> _tracedDisplayBuffers = new();
    private static readonly HashSet<ulong> _tracedComputeShaders = new();
    private static readonly HashSet<(ulong Address, uint X, uint Y, uint Z)>
        _tracedDispatchArguments = new();
    private static readonly HashSet<(ulong Address, uint Initiator, string Reason)>
        _rejectedDispatchArguments = new();
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();
    // Concurrent so the per-draw/per-dispatch hit path is lock-free (and no longer
    // shares _submitTraceGate with tracing).
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong EsState, ulong Ps, ulong PsState, ulong OutputLayout,
         uint OutputCount, uint Attributes, uint PsInputEna, uint PsInputAddr,
         ulong AliasAlignment),
        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel)> _graphicsShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Cs, ulong State, uint LocalX, uint LocalY, uint LocalZ,
         uint WaveLanes, ulong AliasAlignment),
        IGuestCompiledShader> _computeShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong State, ulong AliasAlignment),
        IGuestCompiledShader> _depthOnlyVertexShaderCache = new();
    private static readonly Dictionary<ulong, ulong> _shaderHeadersByCode = new();
    private static readonly bool _traceAgc = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);
    // Drop a draw on an undecodable texture descriptor instead of substituting
    // a 1x1 fallback binding. Off by default so a garbage descriptor degrades
    // the pass rather than dropping it (Demon's Souls composite feeders).
    private static readonly bool _strictShaderDescriptors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SHADER_DESCRIPTORS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcShader =
        _traceAgc ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
            "1",
            StringComparison.Ordinal);
    private static readonly ulong? _traceComputeShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COMPUTE_SHADER_ADDRESS"));
    private static readonly ulong? _tracePixelShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS"));
    private static readonly ulong? _traceRenderTargetAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_RENDER_TARGET_ADDRESS"));
    private static readonly bool _traceDraws = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceFramePackets = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_PACKETS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVertexRanges = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_RANGES"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _compatibilitySubmitCompletionEvent = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT"),
        "1",
        StringComparison.Ordinal);
    // Escape hatch for the cached-texture copy skip (per-draw texel copies
    // are re-enabled unconditionally when set), for A/B-ing rendering issues.
    private static readonly bool _textureCopySkipDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_TEXTURE_SKIP"),
        "1",
        StringComparison.Ordinal);
    private static long _dcbWriteDataTraceCount;
    private static int _tracedVertexRangeCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _createShaderTraceCount;
    private static long _packetPayloadTraceCount;
    private static bool _tracedMissingPixelShaderBindings;
    private static long _unsatisfiedWaitTraceCount;
    private static long _labelProducerSequence;
    private static readonly object _labelProducerGate = new();
    private static readonly List<LabelProducerTrace> _labelProducers = [];
    private static readonly HashSet<(object Memory, ulong Address, ulong SubmissionId)>
        _tracedProducerlessWaits = new();
    private static long _shaderTranslationMissTraceCount;
    private static long _translatedDrawTraceCount;
    private static long _standardDmaTraceCount;
    private static long _packetParseFailureTraceCount;
    private static int _textureFallbackTraceCount;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static readonly Dictionary<(ulong Shader, ulong Source, ulong Destination), ulong> _softwareComputeBlitFingerprints = new();
    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, RegisterDefaultsAllocation> _registerDefaultsAllocations = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
        CreatePrimaryRegisterDefaults();

    private static readonly RegisterDefaultGroup[] InternalRegisterDefaults =
    [
        new(0, 0, 0x8FB4EDB5, [new(0x00E, 0)]),
        new(0, 1, 0xB994AD29, [new(0x2AF, 0)]),
        new(0, 2, 0xD427322F, [new(0x314, 0)]),
        new(0, 3, 0xF58FEA31, [new(0x1B5, 0)]),
        new(1, 0, 0x6AC156EF, [new(0x216, 0)]),
        new(1, 1, 0x6AC15610, [new(0x217, 0)]),
        new(1, 2, 0x6AC15009, [new(0x219, 0)]),
        new(1, 3, 0x6AC153BA, [new(0x21A, 0)]),
        new(1, 4, 0xBE7DCD73, [new(0x27D, 0)]),
        new(1, 5, 0x0C4B1438, [new(0x22A, 0)]),
        new(1, 6, 0xDB00D71A, [new(0x204, 0)]),
        new(1, 7, 0xDB00D249, [new(0x205, 0)]),
        new(1, 8, 0xDB00EC60, [new(0x206, 0)]),
        new(1, 9, 0x0C4D6FE4, [new(0x080, 0)]),
        new(1, 10, 0x0C4A80EF, [new(0x100, 0)]),
        new(1, 11, 0x0DD283E7, [new(0x006, 0)]),
        new(1, 12, 0xC620E68C, [new(0x081, 0)]),
        new(1, 13, 0xC67EFACF, [new(0x101, 0)]),
        new(1, 14, 0xD9E6D9F7, [new(0x001, 0)]),
        new(2, 0, 0x31F34B9F, [new(0x24F, 0)]),
        new(2, 1, 0xAC0F9E76, [new(0x80003FFF, 0)]),
        new(2, 2, 0x929FD95D, [new(0x250, 0)]),
    ];

    private readonly record struct TextureDescriptor(
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
                // Some single-mip Gen5 descriptors use the reserved/inverted
                // 15-0 range as a mip-disabled sentinel. The resource still
                // has exactly one addressable level (MAX_MIP=0). Treating 15
                // literally makes Vulkan reject an otherwise compatible GPU
                // image and falls back to stale guest-memory pixels. For any
                // malformed range, keep BASE_LEVEL's meaning and clamp it to
                // the allocation's last addressable mip. In particular, the
                // common 15-0/MAX_MIP=0 sentinel resolves to mip 0 without
                // making LAST_LEVEL the base of unrelated inverted views.
                return Math.Min(BaseLevel, ResourceMipLevels - 1);
            }
        }

        private uint GetMaximumMipLevels()
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

    private readonly record struct RenderTargetDescriptor(
        uint Slot,
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint TileMode);

    private sealed record TranslatedGuestDraw(
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint PrimitiveType,
        IGuestCompiledShader VertexShader,
        IGuestCompiledShader PixelShader,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        GuestIndexBuffer? IndexBuffer,
        IReadOnlyList<TranslatedImageBinding> Textures,
        IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
        IReadOnlyList<Gen5VertexInputBinding> VertexInputs,
        IReadOnlyList<RenderTargetDescriptor> RenderTargets,
        GuestDepthTarget? DepthTarget,
        // Seam-shaped color targets are built once with the cached translation.
        IReadOnlyList<GuestRenderTarget> GuestTargets,
        GuestRenderState RenderState,
        IReadOnlyList<uint> PixelUserData,
        uint RawBlendControl,
        uint RawColorInfo,
        IReadOnlyList<uint> PixelInitialScalars,
        IReadOnlyList<uint> VertexInitialScalars);

    private sealed record TranslatedImageBinding(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        IReadOnlyList<uint> SamplerDescriptor);

    private readonly record struct RenderTargetWriter(
        ulong Sequence,
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint VertexCount,
        uint PrimitiveType);

    private readonly record struct ComputeImageWriter(
        ulong Sequence,
        ulong ShaderAddress,
        string Opcode);

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

    private readonly record struct SubmittedAcquireMem(
        uint Engine,
        uint CbDbControl,
        ulong BaseAddress,
        ulong SizeBytes,
        uint PollInterval,
        uint GcrControl)
    {
        // GFX10 GCR_CNTL invalidation controls. The host has no separate GLI,
        // GLM, GLK, GLV, GL1 and GL2 caches; they all converge on the guest
        // memory snapshots used to build Vulkan resources.
        private const uint GliInvalidateMask = 0x3u;
        private const int Gl1RangeShift = 2;
        private const uint Gl1RangeMask = 0x3u;
        private const uint GlmInvalidate = 1u << 5;
        private const uint GlkInvalidate = 1u << 7;
        private const uint GlvInvalidate = 1u << 8;
        private const uint Gl1Invalidate = 1u << 9;
        private const uint Gl2Discard = 1u << 13;
        private const uint Gl2Invalidate = 1u << 14;
        private const int Gl2RangeShift = 11;
        private const uint Gl2RangeMask = 0x3u;

        public bool InvalidatesGuestResources =>
            (GcrControl & (GliInvalidateMask |
                           GlmInvalidate |
                           GlkInvalidate |
                           GlvInvalidate |
                           Gl1Invalidate |
                           Gl2Discard |
                           Gl2Invalidate)) != 0;

        // sceAgc encodes its all-memory sentinel with a zero COHER_SIZE. GFX10
        // can also request ALL independently in GLI_INV, GL1_RANGE or
        // GL2_RANGE; in the host's unified resource cache, any invalidated
        // domain with ALL scope expands the operation to all tracked images.
        public bool CoversAllGuestMemory =>
            SizeBytes == 0 ||
            (GcrControl & GliInvalidateMask) == 1u ||
            ((GcrControl & (GlmInvalidate |
                            GlkInvalidate |
                            GlvInvalidate |
                            Gl1Invalidate)) != 0 &&
             ((GcrControl >> Gl1RangeShift) & Gl1RangeMask) == 0) ||
            ((GcrControl & (Gl2Discard | Gl2Invalidate)) != 0 &&
             ((GcrControl >> Gl2RangeShift) & Gl2RangeMask) == 0);
    }

    private sealed class SubmittedDcbState
    {
        public readonly record struct PendingSubmission(
            ulong CommandAddress,
            uint DwordCount,
            ulong SubmissionId,
            bool TracePackets);

        public Dictionary<uint, uint> CxRegisters { get; } = new();
        public Dictionary<uint, uint> ShRegisters { get; } = new();
        public Dictionary<uint, uint> UcRegisters { get; } = new();
        public TextureDescriptor? PresenterTexture { get; set; }
        public GuestDrawKind GuestDrawKind { get; set; }
        public TranslatedGuestDraw? TranslatedDraw { get; set; }
        public TranslatedGuestDraw? PendingTargetlessDraw { get; set; }
        public Dictionary<ulong, RenderTargetDescriptor> KnownRenderTargets { get; } = new();
        public Dictionary<ulong, RenderTargetWriter> RenderTargetWriters { get; } = new();
        public ulong IndirectArgsAddress { get; set; }
        public bool SawIndexedDraw { get; set; }
        public ulong IndexBufferAddress { get; set; }
        public uint IndexBufferCount { get; set; }
        public uint IndexSize { get; set; }
        public uint InstanceCount { get; set; } = 1;
        public uint DrawIndexOffset { get; set; }
        public string QueueName { get; set; } = "graphics";
        public ulong ActiveSubmissionId { get; set; }
        public Queue<PendingSubmission> PendingSubmissions { get; } = new();
        public bool HasActiveSubmission { get; set; }
        public bool IsSuspended { get; set; }
        public ulong CompletionEventNotifiedSubmissionId { get; set; }
        public Dictionary<(uint Op, uint Register), uint> FramePacketCounts { get; } = new();
        public uint FramePacketCount { get; set; }
        public uint FrameDrawCount { get; set; }
        public uint FrameDispatchCount { get; set; }
        public ulong FlipCount { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
        public Dictionary<ulong, ComputeImageWriter> ComputeImageWriters { get; } = new();
        public Dictionary<uint, string> ResourceOwners { get; } = new();
        public Dictionary<uint, RegisteredAgcResource> RegisteredResources { get; } = new();
        public bool ResourceRegistrationInitialized { get; set; }
        public ulong ResourceRegistrationMemory { get; set; }
        public ulong ResourceRegistrationMemorySize { get; set; }
        public uint ResourceRegistrationMaxOwners { get; set; }
        public uint DefaultOwner { get; set; } = DefaultAgcOwner;
        public uint NextOwner { get; set; } = 1;
        public uint NextResource { get; set; } = 1;
        public ulong WorkSequence { get; set; }
        public ulong SubmissionSequence { get; set; }
        public bool WaitMonitorRunning { get; set; }
    }

    private readonly record struct RegisteredAgcResource(
        uint Owner,
        ulong Address,
        ulong Size,
        string Name,
        uint Type,
        uint Flags);

    private sealed class LabelProducerTrace
    {
        public long Sequence;
        public required object Memory;
        public ulong Address;
        public ulong Length;
        public ulong PacketAddress;
        public ulong SubmissionId;
        public required string QueueName;
        public required string DebugName;
        public bool Completed;
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

    // NID captured from shipped titles; 'sceAgcInit' is a working label that collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "23LRUSvYu1M",
        ExportName = "sceAgcInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int Init(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var version = (uint)ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !IsSupportedRegisterDefaultsVersion(version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc($"agc.init state=0x{stateAddress:X16} version={version}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "2JtWUUiYBXs",
        ExportName = "sceAgcGetRegisterDefaults2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: false);

    [SysAbiExport(
        Nid = "wRbq6ZjNop4",
        ExportName = "sceAgcGetRegisterDefaults2Internal",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2Internal(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: true);

    [SysAbiExport(
        Nid = "f3dg2CSgRKY",
        ExportName = "sceAgcCreateShader",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateShader(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var headerAddress = ctx[CpuRegister.Rsi];
        var codeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || codeAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, headerAddress, out var fileHeader) ||
            !TryReadUInt32(ctx, headerAddress + sizeof(uint), out var version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (fileHeader != ShaderFileHeader || version != ShaderVersion)
        {
            TraceCreateShader(destinationAddress, headerAddress, codeAddress, $"invalid-header file=0x{fileHeader:X8} version=0x{version:X8}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!RelocatePointerField(ctx, headerAddress + ShaderCxRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderShRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderUserDataOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderSpecialsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderInputSemanticsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderOutputSemanticsOffset) ||
            !ctx.TryWriteUInt64(headerAddress + ShaderCodeOffset, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderUserDataOffset, out var userDataAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userDataAddress != 0 &&
            (!RelocatePointerField(ctx, userDataAddress) ||
             !RelocatePointerField(ctx, userDataAddress + 0x08) ||
             !RelocatePointerField(ctx, userDataAddress + 0x10) ||
             !RelocatePointerField(ctx, userDataAddress + 0x18) ||
             !RelocatePointerField(ctx, userDataAddress + 0x20)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!PatchShaderProgramRegisters(ctx, headerAddress, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (destinationAddress != 0 &&
            !ctx.TryWriteUInt64(destinationAddress, headerAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (_submitTraceGate)
        {
            _shaderHeadersByCode[codeAddress] = headerAddress;
        }

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "D9sr1xGUriE",
        ExportName = "sceAgcCreatePrimState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreatePrimState(CpuContext ctx)
    {
        var cxRegistersAddress = ctx[CpuRegister.Rdi];
        var ucRegistersAddress = ctx[CpuRegister.Rsi];
        var hullShaderAddress = ctx[CpuRegister.Rdx];
        var geometryShaderAddress = ctx[CpuRegister.Rcx];
        var primitiveType = (uint)ctx[CpuRegister.R8];

        if (cxRegistersAddress == 0 || ucRegistersAddress == 0 || hullShaderAddress != 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, geometryShaderAddress + ShaderTypeOffset, out var shaderType) || !IsEsGeometryShaderType(shaderType) ||
            !TryReadUInt64(ctx, geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtShaderStagesEnOffset, cxRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset, cxRegistersAddress + 8) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeUserVgprEnOffset, ucRegistersAddress + 8) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 16, VgtPrimitiveType) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 20, primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} gs=0x{geometryShaderAddress:X16} type={shaderType} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "HV4j+E0MBHE",
        ExportName = "sceAgcCreateInterpolantMapping",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt32(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0 &&
            (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
             !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out _)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        for (uint i = 0; i < 32; i++)
        {
            uint value = 0;
            if (i < outputSemanticsCount && outputSemanticsAddress != 0)
            {
                var flat = false;
                if (pixelShaderAddress != 0 && inputSemanticsAddress != 0 &&
                    TryReadUInt32(ctx, inputSemanticsAddress + (i * sizeof(uint)), out var inputSemantic))
                {
                    flat = ((inputSemantic >> 22) & 0x1) != 0;
                }

                value = i | (flat ? 0x400u : 0u);
            }

            var destination = registersAddress + (i * 8);
            if (!TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + i) ||
                !TryWriteUInt32(ctx, destination + sizeof(uint), value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAgc($"agc.create_interpolant_mapping regs=0x{registersAddress:X16} gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var payloadAddress = commandAddress + 8;
        if (type == 0)
        {
            if (!TryReadUInt32(ctx, commandAddress, out var header))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            payloadAddress = (header & 0x3FFF_0000u) == 0x3FFF_0000u
                ? 0
                : commandAddress + 4;
        }

        if (!ctx.TryWriteUInt64(outputAddress, payloadAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ShouldTraceHotPath(ref _packetPayloadTraceCount))
        {
            TraceAgc(
                $"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} " +
                $"type={type} payload=0x{payloadAddress:X16}");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "LtTouSCZjHM",
        ExportName = "sceAgcCbNop",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNop(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dwordCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 || dwordCount < 2 || dwordCount > 0x4001)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, dwordCount, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!TryWriteUInt32(ctx, commandAddress + ((ulong)index * sizeof(uint)), 0))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "k3GhuSNmBLU",
        ExportName = "sceAgcCbDispatch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbDispatch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var groupCountX = (uint)ctx[CpuRegister.Rsi];
        var groupCountY = (uint)ctx[CpuRegister.Rdx];
        var groupCountZ = (uint)ctx[CpuRegister.Rcx];
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, groupCountX) ||
            !TryWriteUInt32(ctx, commandAddress + 8, groupCountY) ||
            !TryWriteUInt32(ctx, commandAddress + 12, groupCountZ) ||
            !TryWriteUInt32(ctx, commandAddress + 16, DirectDispatchInitiator(modifier)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    private static uint DirectDispatchInitiator(uint modifier) =>
        // AGC's direct API takes workgroup counts by default. Preserve the
        // caller's USE_THREAD_DIMENSIONS bit when explicitly requested; do not
        // force it. Demon's Souls' 0xF00100 dispatch is paired with a
        // 0x3C004000 element bound (exactly 64 lanes per group), proving the
        // default packet is group-dimensional.
        (modifier & 0xA038u) | 0x41u;

    [SysAbiExport(
        Nid = "UZbQjYAwwXM",
        ExportName = "sceAgcCbSetShRegistersDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegistersDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (registerCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || registersAddress == 0 || registerCount > 4096)
        {
            return ReturnPointer(ctx, 0);
        }

        var registers = new RegisterDefaultValue[registerCount];
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return ReturnPointer(ctx, 0);
            }

            registers[index] = new RegisterDefaultValue(offset, value);
        }

        Array.Sort(registers, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        var startIndex = 0;
        while (startIndex < registers.Length)
        {
            var endIndex = startIndex + 1;
            while (endIndex < registers.Length &&
                   registers[endIndex].Offset == registers[endIndex - 1].Offset + 1)
            {
                endIndex++;
            }

            var valueCount = (uint)(endIndex - startIndex);
            var packetDwords = valueCount + 2;
            if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
                !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!TryWriteUInt32(
                        ctx,
                        commandAddress + 8 + ((ulong)(index - startIndex) * sizeof(uint)),
                        registers[index].Value))
                {
                    return ReturnPointer(ctx, 0);
                }
            }

            startIndex = endIndex;
        }

        return ReturnPointer(ctx, firstCommandAddress);
    }

    [SysAbiExport(
        Nid = "JrtiDtKeS38",
        ExportName = "sceAgcAcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cFazmnXpJOE",
        ExportName = "sceAgcAcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType >= 0x40)
        {
            return ReturnPointer(ctx, 0);
        }

        var hasAddress = (eventType & ~1u) == 0x38;
        var packetDwords = hasAddress ? 4u : 2u;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, hasAddress ? eventType | 0x100u : eventType & 0x3Fu))
        {
            return ReturnPointer(ctx, 0);
        }

        if (hasAddress &&
            (!TryWriteUInt32(ctx, commandAddress + 8, (uint)eventAddress & ~7u) ||
             !TryWriteUInt32(ctx, commandAddress + 12, (uint)(eventAddress >> 32))))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "KT-hTp-Ch14",
        ExportName = "sceAgcAcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var gcrControl = (uint)ctx[CpuRegister.Rsi];
        var baseAddress = ctx[CpuRegister.Rdx];
        var sizeBytes = ctx[CpuRegister.Rcx];
        var pollCycles = (uint)ctx[CpuRegister.R8];
        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "htn36gPnBk4",
        ExportName = "sceAgcAcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var address = ctx[CpuRegister.R8];
        var reference = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
            commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 6u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)address) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, compareFunction) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, compareFunction) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, pollCycles / 40))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "eZ4+17OQz4Q",
        ExportName = "sceAgcAcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWriteData(CpuContext ctx) =>
        DcbWriteData(ctx);

    [SysAbiExport(
        Nid = "j3EtxFkSIhQ",
        ExportName = "sceAgcAcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var argumentsAddress = ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)argumentsAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || offset == 0 || offset > 0x3FF || valueCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, valueCount + 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(valueCount + 2, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "wr23dPKyWc0",
        ExportName = "sceAgcCbReleaseMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbReleaseMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var action = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var gcrControl = (uint)(ctx[CpuRegister.Rdx] & 0xFFFF);
        var destination = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + 8, out var dataSelectionRaw) ||
            !TryReadUInt64(ctx, stackAddress + 16, out var data) ||
            !TryReadUInt64(ctx, stackAddress + 24, out var gdsOffsetRaw) ||
            !TryReadUInt64(ctx, stackAddress + 32, out var gdsSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + 40, out var interruptRaw) ||
            !TryReadUInt64(ctx, stackAddress + 48, out var interruptContextIdRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var dataSelection = (uint)(dataSelectionRaw & 0xFF);
        var gdsOffset = (uint)(gdsOffsetRaw & 0xFFFF);
        var gdsSize = (uint)(gdsSizeRaw & 0xFFFF);
        var interrupt = (uint)(interruptRaw & 0xFF);
        var interruptContextId = (uint)interruptContextIdRaw;
        if (commandBufferAddress == 0 ||
            destination > 1 ||
            dataSelection > 3 ||
            gdsOffset != 0 ||
            gdsSize > 2 ||
            interrupt > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)data) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(data >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        if (commandBufferAddress == 0 || cachePolicy != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItIndexType, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "8N2tmT3jmC8",
        ExportName = "sceAgcDcbSetIndexCount",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCount(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RIndexCount)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mljzuGDZRQ4",
        ExportName = "sceAgcDcbSetIndexCountGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCountGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 7u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "tSBxhAPyytQ",
        ExportName = "sceAgcDcbSetNumInstances",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetNumInstances(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var instanceCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, instanceCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_num_instances buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={instanceCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "q88lQ+GP5Yk",
        ExportName = "sceAgcDcbDrawIndex",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndex(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var indexAddress = ctx[CpuRegister.Rdx];
        var modifier = (uint)ctx[CpuRegister.Rcx];

        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var baseCommand) ||
            !TryWriteUInt32(ctx, baseCommand, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 4, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, baseCommand + 8, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, baseCommand + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        // DRAW_INDEX_2 is six dwords: header, maximum index count, the
        // 64-bit index-buffer base, the draw count and the initiator.  The
        // former five-dword packet omitted both the real base and the count
        // field, so every call made by Unity looked like a zero-count draw to
        // the submitted-command parser and the complete scene was discarded.
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(6, ItDrawIndex2, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 8, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, drawCommand + 12, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, drawCommand + 16, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 20, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index buf=0x{commandBufferAddress:X16} " +
            $"base=0x{baseCommand:X16} draw=0x{drawCommand:X16} " +
            $"count={indexCount} index=0x{indexAddress:X16}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "Yw0jKSqop+E",
        ExportName = "sceAgcDcbDrawIndexAuto",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexAuto(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var modifier = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "t1vNu082-jM",
        ExportName = "sceAgcDcbDrawIndexIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mStuvI0zOtc",
        ExportName = "sceAgcDcbDrawIndexIndirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 5u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "rUuVjyR+Rd4",
        ExportName = "sceAgcDcbGetLodStatsGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStatsGetSize(CpuContext ctx)
    {
        var counterCount = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0x10u + (counterCount * sizeof(uint));
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "vuSXe69VILM",
        ExportName = "sceAgcDcbGetLodStats",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStats(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var cachePolicy = (uint)ctx[CpuRegister.Rsi] & 0x3u;
        var destinationAddress = ctx[CpuRegister.Rdx];
        var control = (uint)ctx[CpuRegister.Rcx];
        var counterMask = (uint)ctx[CpuRegister.R8] & 0xFFu;
        var resetCounters = (uint)ctx[CpuRegister.R9] & 0x1u;
        if (!TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
            commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var enable = (uint)enableRaw & 0x1u;
        var counterSelect = (uint)counterSelectRaw & 0xFFu;
        var packetControl =
            (cachePolicy << 28) |
            (enable << 19) |
            (resetCounters << 18) |
            (counterMask << 10) |
            (counterSelect << 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, packetControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_get_lod_stats buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} control=0x{control:X8} counters=0x{counterMask:X2}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "aJf+j5yntiU",
        ExportName = "sceAgcDcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType > 0x3F || eventAddress != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, eventType))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_event_write buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} type={eventType}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "57labkp+rSQ",
        ExportName = "sceAgcDcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cbDbOp = (uint)ctx[CpuRegister.Rdx];
        var gcrControl = (uint)ctx[CpuRegister.Rcx];
        var baseAddress = ctx[CpuRegister.R8];
        var sizeBytes = ctx[CpuRegister.R9];
        if (!TryReadUInt32(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            engine > 1 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (engine << 31) | cbDbOp) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_acquire_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"engine={engine} cbdb=0x{cbDbOp:X8} gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "i1jyy49AjXU",
        ExportName = "sceAgcDcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWriteData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dwordCount = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var incrementRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var increment = (uint)(incrementRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        if (commandBufferAddress == 0 ||
            destinationAddress == 0 ||
            dataAddress == 0 ||
            dwordCount > 0x3FFD)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = dwordCount + 4;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!TryReadUInt32(ctx, dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !TryWriteUInt32(ctx, commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        if (ShouldTraceHotPath(ref _dcbWriteDataTraceCount))
        {
            TraceAgc(
                $"agc.dcb_write_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"dst={destination} cache={cachePolicy} addr=0x{destinationAddress:X16} count={dwordCount} " +
                $"increment={increment} confirm={writeConfirm}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "VmW0Tdpy420",
        ExportName = "sceAgcDcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var address = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            operation > 4 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var standardWait = operation is 2 or 3;
        var packetDwords = standardWait ? 7u : size == 0 ? 6u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        if (standardWait)
        {
            if (!TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItWaitRegMem, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, compareFunction | ((operation & 1) << 8)) ||
                !TryWriteUInt32(ctx, commandAddress + 8, (uint)address) ||
                !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)) ||
                !TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)mask) ||
                !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !TryWriteUInt32(ctx, commandAddress + 4, (uint)address) ||
                 !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, compareFunction | (operation << 8)) ||
                !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, compareFunction | (operation << 8)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, pollCycles / 40))
        {
            return ReturnPointer(ctx, 0);
        }

        if (ShouldTraceHotPath(ref _dcbWaitRegMemTraceCount))
        {
            TraceAgc(
                $"agc.dcb_wait_reg_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"size={size} compare={compareFunction} op={operation} cache={cachePolicy} " +
                $"addr=0x{address:X16} ref=0x{reference:X16} mask=0x{mask:X16} poll={pollCycles}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "u2T2DiA5hRI",
        ExportName = "sceAgcDcbStallCommandBufferParser",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParser(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var address = ctx[CpuRegister.Rdx];
        var reference = ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || size > 1 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        // Direct execution submits work synchronously, so there is no independent
        // hardware command processor to stall. Keep a well-formed no-op in the DCB
        // so packet addresses and the command-buffer cursor remain coherent.
        TraceAgc(
            $"agc.dcb_stall_parser buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"size={size} addr=0x{address:X16} reference=0x{reference:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+u6dKSLWM2o",
        ExportName = "sceAgcDcbStallCommandBufferParserGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParserGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var control4Raw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var control9Raw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control4 = (uint)(control4Raw & 0xFF);
        var control7 = (uint)(control7Raw & 0xFF);
        var control8 = (uint)(control8Raw & 0xFF);
        var control9 = (uint)(control9Raw & 0xFF);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_dma_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} bytes={byteCount} " +
            $"control0=0x{destination | (destinationCachePolicy << 8) | (source << 16) | (sourceCachePolicy << 24):X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "2ccJz9LQI+w",
        ExportName = "sceAgcDcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "-RnpfpxIhec",
        ExportName = "sceAgcAcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var sourceSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationSelector = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceOrImmediate) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var byteCount) ||
            commandBufferAddress == 0 ||
            byteCount == 0 ||
            byteCount > 256u * 1024u * 1024u ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDmaData)) ||
            !ctx.TryWriteUInt64(commandAddress + 4, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 12, sourceOrImmediate) ||
            !TryWriteUInt32(ctx, commandAddress + 20, byteCount) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 24,
                sourceSelector | (destinationSelector << 8)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "M0ttm8h7SKA",
        ExportName = "sceAgcAcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "RmaJwLtc8rY",
        ExportName = "sceAgcDcbSetBaseIndirectArgs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetBaseIndirectArgs(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var baseIndex = (uint)ctx[CpuRegister.Rsi];
        var address = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 1) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)address & ~7u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "CtB+A9-VxO0",
        ExportName = "sceAgcDcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+kSrjIVxKFE",
        ExportName = "sceAgcDcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPushMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var markerAddress = ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryReadGuestCString(ctx, markerAddress, 4095, out var marker))
        {
            return ReturnPointer(ctx, 0);
        }

        var payloadDwords = Math.Max(((uint)marker.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < payloadDwords; index++)
        {
            uint value = 0;
            for (uint byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                var markerIndex = (index * sizeof(uint)) + byteIndex;
                if (markerIndex < (uint)marker.Length)
                {
                    value |= (uint)marker[(int)markerIndex] << ((int)byteIndex * 8);
                }
            }

            if (!TryWriteUInt32(ctx, commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cpCILPya5Zk",
        ExportName = "sceAgcAcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPushMarker(CpuContext ctx) => DcbPushMarker(ctx);

    [SysAbiExport(
        Nid = "H7uZqCoNuWk",
        ExportName = "sceAgcDcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPopMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "6mFxkVqdmbQ",
        ExportName = "sceAgcAcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPopMarker(CpuContext ctx) => DcbPopMarker(ctx);

    [SysAbiExport(
        Nid = "IxYiarKlXxM",
        ExportName = "sceAgcDmaDataPatchSetDstAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetDstAddressOrOffset(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var destinationAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var destinationOffset = packetLength == 7 ? 4UL : 16UL;
        return ctx.TryWriteUInt64(commandAddress + destinationOffset, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // The SRC counterpart of sceAgcDmaDataPatchSetDstAddressOrOffset. Patches
    // the source field (offset +24, matching the layout written by
    // sceAgcDcbDmaData) of a NOP/RDmaData packet. Games patch this to point a
    // GPU DMA at the data it should copy — commonly a completion/label write.
    // When it is missing the source stays 0, ApplySubmittedDmaData skips the
    // copy (copied=False), and whatever the guest waits on that label for never
    // fires (observed: Void Terrarium's first draw batch presents a black frame
    // then the render pipeline stalls with no further flips).
    [SysAbiExport(
        Nid = "cdDRpqcFGbU",
        ExportName = "sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetSrcAddressOrOffsetOrImmediate(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var sourceValue = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var sourceOffset = packetLength == 7 ? 12UL : 24UL;
        return ctx.TryWriteUInt64(commandAddress + sourceOffset, sourceValue)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "eAy8eGNsCuU",
        ExportName = "sceAgcWriteDataPatchSetCachePolicy",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetCachePolicy(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 1);

    [SysAbiExport(
        Nid = "tmy-+rBpspY",
        ExportName = "sceAgcWriteDataPatchSetDst",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetDst(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 0);

    [SysAbiExport(
        Nid = "fPSCdQxgpSw",
        ExportName = "sceAgcWriteDataPatchSetAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetAddressOrOffset(CpuContext ctx)
    {
        // SDK revisions disagree on whether the packet or destination is the
        // first argument. Astro passes (destination, packet), while older
        // captures use (packet, destination), so identify the packet by its
        // header instead of hard-coding one ordering.
        var first = ctx[CpuRegister.Rdi];
        var second = ctx[CpuRegister.Rsi];
        ulong commandAddress;
        ulong destinationAddress;
        if (TryGetPacketIdentity(ctx, first, out var firstOp, out var firstRegister) &&
            firstOp == ItNop && firstRegister == RWriteData)
        {
            commandAddress = first;
            destinationAddress = second;
        }
        else if (TryGetPacketIdentity(ctx, second, out var secondOp, out var secondRegister) &&
                 secondOp == ItNop && secondRegister == RWriteData)
        {
            commandAddress = second;
            destinationAddress = first;
        }
        else
        {
            // Astro's SDK 9 ABI passes (address-or-offset, pointer-to-field)
            // rather than the whole packet. The field is already the packet's
            // 64-bit address payload, so patch it directly.
            if (second == 0 || !ctx.TryWriteUInt64(second, first))
            {
                return SetReturn(ctx, second == 0
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.patch_write_data_field field=0x{second:X16} value=0x{first:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        TraceAgc(
            $"agc.patch_write_data_addr cmd=0x{commandAddress:X16} dst=0x{destinationAddress:X16}");
        return ctx.TryWriteUInt64(commandAddress + 8, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3KDcnM3lrcU",
        ExportName = "sceAgcWaitRegMemPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 8UL
            : op == ItNop && register is RWaitMem32 or RWaitMem64
                ? 4UL
                : 0;
        if (fieldOffset == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + fieldOffset, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "n485EBnIWmk",
        ExportName = "sceAgcWaitRegMemPatchCompareFunction",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchCompareFunction(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        if (compareFunction > 7 ||
            !TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 4UL
            : op == ItNop && register == RWaitMem32
                ? 16UL
                : op == ItNop && register == RWaitMem64
                    ? 28UL
                    : 0;
        return fieldOffset != 0 &&
               TryPatchUInt32Bits(ctx, commandAddress + fieldOffset, 0x7u, compareFunction)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, fieldOffset == 0
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "7nOoijNPvEU",
        ExportName = "sceAgcWaitRegMemPatchReference",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchReference(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var reference = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 20, (uint)reference)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 20, reference);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "hXAnLgDHCoI",
        ExportName = "sceAgcWaitRegMemPatchMask",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchMask(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 20, (uint)mask)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 12, (uint)mask)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 12, mask);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "0fWWK5uG9rQ",
        ExportName = "sceAgcQueueEndOfPipeActionPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RReleaseMem)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 12, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "J8YCgfKAMQs",
        ExportName = "sceAgcQueueEndOfPipeActionPatchGcrCntl",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchGcrCntl(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x0000_FFFFu,
                (uint)ctx[CpuRegister.Rsi] & 0xFFFFu)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "MlEw1feXcjg",
        ExportName = "sceAgcQueueEndOfPipeActionPatchData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchData(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 20, ctx[CpuRegister.Rsi])
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "T9fjQIINoeE",
        ExportName = "sceAgcQueueEndOfPipeActionPatchType",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchType(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var dataSelection = (uint)ctx[CpuRegister.Rsi];
        TraceAgc(
            $"agc.eop_patch_type cmd=0x{commandAddress:X16} value=0x{dataSelection:X8}");
        if (dataSelection > 3 || !IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x00FF_0000u,
                dataSelection << 16)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsAgcReleaseMemPacket(CpuContext ctx, ulong commandAddress) =>
        TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) &&
        op == ItNop &&
        register == RReleaseMem;

    private static bool TryPatchUInt32Bits(
        CpuContext ctx,
        ulong address,
        uint mask,
        uint value)
    {
        return TryReadUInt32(ctx, address, out var current) &&
               TryWriteUInt32(ctx, address, PatchUInt32Bits(current, mask, value));
    }

    private static uint PatchUInt32Bits(uint current, uint mask, uint value) =>
        (current & ~mask) | (value & mask);

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "MWiElSNE8j8",
        ExportName = "sceAgcDcbWaitUntilSafeForRendering",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitUntilSafeForRendering(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, displayBufferIndex) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_wait_safe buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "YUeqkyT7mEQ",
        ExportName = "sceAgcDcbSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetFlip(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (int)ctx[CpuRegister.Rdx];
        var flipMode = (uint)ctx[CpuRegister.Rcx];
        var flipArg = unchecked((ulong)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItNop, RFlip)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, flipMode) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)(flipArg >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_flip buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex} mode={flipMode} arg=0x{flipArg:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "w2rJhmD+dsE",
        ExportName = "sceAgcDriverAddEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverAddEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        var userData = ctx[CpuRegister.Rdx];
        if (!KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_add_eq_event eq=0x{equeue:X16} id=0x{eventId:X16} udata=0x{userData:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "DL2RXaXOy88",
        ExportName = "sceAgcDriverDeleteEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverDeleteEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        if (!KernelEventQueueCompatExports.DeleteRegisteredEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_delete_eq_event eq=0x{equeue:X16} id=0x{eventId:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }
        }

        if (tracePackets)
        {
            TraceAgc($"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        VulkanVideoPresenter.AttachGuestMemory(ctx.Memory);
        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            gpuState.Graphics.QueueName = "dcb.graphics";
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                gpuState.Graphics,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
                $"addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        VulkanVideoPresenter.AttachGuestMemory(ctx.Memory);
        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            queueState.QueueName = $"acb.compute[{ownerHandle}]";
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
    Nid = "uJziRsODk1c",
    ExportName = "sceAgcDriverGetResourceRegistrationMaxNameLength",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverGetResourceRegistrationMaxNameLength(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];

        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, outAddress, 256))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_resource_registration_max_name_length out=0x{outAddress:X16} value=256");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private const uint DefaultAgcOwner = 1;
    [SysAbiExport(
        Nid = "F0ZXt5q0ZTA",
        ExportName = "sceAgcDriverGetDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverGetDefaultOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];

        if (ownerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, ownerAddress, DefaultAgcOwner))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_default_owner out=0x{ownerAddress:X16} owner={DefaultAgcOwner}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
    Nid = "W5z4eZrjEas",
    ExportName = "sceAgcDriverRegisterResource",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverRegisterResource(CpuContext ctx)
    {
        var resourceAddress = ctx[CpuRegister.Rdi];
        var owner = (uint)ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        var type = (uint)ctx[CpuRegister.R8];
        var flags = (uint)ctx[CpuRegister.R9];

        TraceAgc(
            $"agc.driver_register_resource resource=0x{resourceAddress:X16} owner={owner} " +
            $"name=0x{nameAddress:X16} type={type} flags={flags}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
    Nid = "-KRzWekV120",
    ExportName = "sceAgcDriverUnknown_KRzWekV120",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverUnknownKRzWekV120(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_unknown_krz rdi=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} " +
            $"rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM006

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }
    #pragma warning restore SHEM006

    private static void EnqueueSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        ulong submissionId,
        bool tracePackets)
    {
        state.PendingSubmissions.Enqueue(new SubmittedDcbState.PendingSubmission(
            commandAddress,
            dwordCount,
            submissionId,
            tracePackets));
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void PumpSubmittedQueue(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state)
    {
        if (state.IsSuspended)
        {
            return;
        }

        while (!state.HasActiveSubmission &&
               state.PendingSubmissions.TryDequeue(out var submission))
        {
            state.HasActiveSubmission = true;
            state.ActiveSubmissionId = submission.SubmissionId;
            state.IsSuspended = ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                submission.CommandAddress,
                submission.DwordCount,
                submission.TracePackets);
            if (state.IsSuspended)
            {
                return;
            }

            state.HasActiveSubmission = false;
            NotifySubmittedDcbCompleted(gpuState, state, submission.SubmissionId);
        }
    }

    private static void NotifySubmittedDcbCompleted(
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong submissionId)
    {
        if (!ReferenceEquals(state, gpuState.Graphics) ||
            state.CompletionEventNotifiedSubmissionId == submissionId)
        {
            return;
        }

        state.CompletionEventNotifiedSubmissionId = submissionId;
        void TriggerCompletionEvents()
        {
            var triggered = KernelEventQueueCompatExports.TriggerRegisteredEvents(
                ident: 0,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                data: 0);
            if (_compatibilitySubmitCompletionEvent)
            {
                triggered += KernelEventQueueCompatExports.TriggerRegisteredEventsDistinct(
                    KernelEventQueueCompatExports.KernelEventFilterGraphics);
            }
            TraceAgc(
                $"agc.driver_submit_dcb completion submission={submissionId} " +
                $"queues={triggered}");
        }

        // A DCB is complete only after its translated Vulkan work and ordered
        // guest-memory writes have finished. Put the notification on that same
        // logical graphics queue instead of approximating completion with a
        // timer, which can wake Unity while its upload data is still stale.
        if (VulkanVideoPresenter.SubmitOrderedGuestAction(
                TriggerCompletionEvents,
                $"agc submit completion {submissionId}") == 0)
        {
            TriggerCompletionEvents();
        }
    }

    // Returns true only when parsing stopped on an unsatisfied WAIT_REG_MEM.
    // Malformed packets are dropped as completed so one bad submission cannot
    // permanently wedge all later work on the same hardware queue.
    private static bool ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return false;
        }

        using var guestQueueScope = VulkanVideoPresenter.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        var windowByteCount = checked((int)(dwordCount * sizeof(uint)));
        var rented = VulkanVideoPresenter.GuestDataPool.Rent(windowByteCount);
        try
        {
            if (ctx.Memory.TryRead(commandAddress, rented.AsSpan(0, windowByteCount)))
            {
                _dcbWindowBuffer = rented;
                _dcbWindowStart = commandAddress;
                _dcbWindowByteLength = windowByteCount;
            }

            return ParseSubmittedDcbCore(
                ctx,
                gpuState,
                state,
                commandAddress,
                dwordCount,
                tracePackets);
        }
        finally
        {
            _dcbWindowBuffer = null;
            _dcbWindowByteLength = 0;
            VulkanVideoPresenter.GuestDataPool.Return(rented);
        }
    }

    private static bool ParseSubmittedDcbCore(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                TracePacketParseFailure(state, currentAddress, offset, 0, "header-read");
                return false;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.packet dw={offset} addr=0x{currentAddress:X16} " +
                        $"header=0x{header:X8} len=1 type=2");
                }

                offset++;
                continue;
            }

            if (packetType != 3)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"packet-type-{packetType}");
                return false;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"length-{length}-remaining-{dwordCount - offset}");
                return false;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (_traceFramePackets && ReferenceEquals(state, gpuState.Graphics))
            {
                var packetKey = (op, op == ItNop ? register : uint.MaxValue);
                state.FramePacketCounts[packetKey] =
                    state.FramePacketCounts.TryGetValue(packetKey, out var packetCount)
                        ? packetCount + 1
                        : 1;
                state.FramePacketCount++;
            }
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (_traceDraws)
            {
                CountSubmittedOpcode(op, register);
            }

            if (op == ItNop &&
                register is RDrawReset or RAcbReset &&
                length >= 2)
            {
                ResetSubmittedParserState(state);
                TraceAgc(
                    $"agc.queue_reset queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"kind={(register == RDrawReset ? "draw" : "acb")} " +
                    $"packet=0x{currentAddress:X16}");
            }

            if (op == ItNop && register == RAcquireMem && length >= 8)
            {
                ApplySubmittedAcquireMem(
                    ctx,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);

            if (op == ItSetBase &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var baseSelector) &&
                baseSelector == 1 &&
                TryReadUInt64(ctx, currentAddress + 8, out var indirectArgsAddress))
            {
                state.IndirectArgsAddress = indirectArgsAddress;
            }

            if (op == ItEventWrite &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                var eventType = eventTypeRaw & 0x3Fu;
                SubmitOrderedGpuSideEffect(
                    ctx,
                    gpuState,
                    state,
                    () =>
                    {
                        var triggered = KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                            KernelEventQueueCompatExports.KernelEventFilterGraphics,
                            eventType);
                        if (tracePackets)
                        {
                            TraceAgc($"agc.dcb.event type=0x{eventType:X2} queues={triggered}");
                        }
                    },
                    $"event_write type=0x{eventType:X2}",
                    currentAddress);
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, gpuState, state, currentAddress, tracePackets);
            }

            if (op == ItReleaseMem && length >= 8)
            {
                ApplySubmittedStandardReleaseMem(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: false,
                    tracePacket: tracePackets);
            }

            if (op == ItWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: true,
                    tracePacket: tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 7)
            {
                ApplySubmittedDmaData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    compactLayout: length == 7,
                    tracePacket: tracePackets);
            }

            if (op == ItDmaData && length >= 7)
            {
                ApplySubmittedStandardDmaData(ctx, gpuState, state, currentAddress);
            }

            if (op == ItIndexBase &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBaseLo) &&
                TryReadUInt32(ctx, currentAddress + 8, out var indexBaseHi))
            {
                state.IndexBufferAddress =
                    indexBaseLo | ((ulong)indexBaseHi << 32);
            }

            if (op == ItIndexBufferSize &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBufferCount))
            {
                state.IndexBufferCount = indexBufferCount;
            }

            if (op == ItNop &&
                register == RIndexCount &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var customIndexCount))
            {
                state.IndexBufferCount = customIndexCount;
            }

            if (op == ItIndexType &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexSize))
            {
                state.IndexSize = indexSize & 0x3;
            }

            if (op == ItNumInstances &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: register == RWaitMem64, isStandard: false,
                        tracePackets))
                {
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: false, isStandard: true, tracePackets))
                {
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (TryReadSubmittedDrawCount(
                    ctx,
                    state,
                    currentAddress,
                    length,
                    op,
                    out var indexCount) &&
                indexCount != 0)
            {
                state.FrameDrawCount++;
                if (_traceAgcShader)
                {
                    lock (_submitTraceGate)
                    {
                        if (_tracedSubmittedDrawOpcodes.Add(op))
                        {
                            TraceAgcShader(
                                $"agc.draw_packet op=0x{op:X2} count={indexCount}");
                        }
                    }
                }

                var indexed = op is
                    ItDrawIndex2 or
                    ItDrawIndexOffset2 or
                    ItDrawIndexIndirect;
                state.SawIndexedDraw |= indexed;
                TryTranslateGuestDraw(ctx, gpuState, state, indexCount, indexed);
            }

            if (op == ItNop &&
                register == RDrawIndexAuto &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
                state.FrameDrawCount++;
                TryTranslateGuestDraw(
                    ctx,
                    gpuState,
                    state,
                    autoIndexCount,
                    indexed: false);
            }

            if ((op is ItDispatchDirect or ItDispatchIndirect) &&
                TryReadComputeDispatch(
                    ctx,
                    state,
                    currentAddress,
                    length,
                    op,
                out var dispatch))
            {
                state.FrameDispatchCount++;
                ObserveComputeDispatch(ctx, gpuState, state, dispatch);
            }

            if (op == ItNop &&
                register == RWaitFlipDone &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var waitVideoOutHandle) &&
                TryReadUInt32(ctx, currentAddress + 8, out var waitDisplayBufferIndex))
            {
                var waitSequence = VulkanVideoPresenter.SubmitOrderedGuestFlipWait(
                    unchecked((int)waitVideoOutHandle),
                    unchecked((int)waitDisplayBufferIndex));
                TraceAgcShader(
                    $"agc.flip_wait_safe queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"handle={waitVideoOutHandle} index={waitDisplayBufferIndex} " +
                    $"work_sequence={waitSequence}");
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                TraceFramePacketSummary(state);
                SyncCpuWrittenGuestImages(ctx);
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return false;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                var handle = unchecked((int)videoOutHandle);
                if (state.PendingTargetlessDraw is { } pendingComposite &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var pendingDisplayBuffer) &&
                    state.KnownRenderTargets.TryGetValue(
                        pendingDisplayBuffer.Address,
                        out var pendingDisplayTarget))
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        pendingComposite.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(pendingComposite);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(pendingComposite.VertexInputs);
                    ProvideRenderTargetInitialData(ctx, pendingDisplayTarget);
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        pendingComposite.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        pendingComposite.AttributeCount,
                        [new GuestRenderTarget(
                            pendingDisplayTarget.Address,
                            pendingDisplayTarget.Width,
                            pendingDisplayTarget.Height,
                            pendingDisplayTarget.Format,
                            pendingDisplayTarget.NumberType)],
                        pendingComposite.VertexShader,
                        pendingComposite.VertexCount,
                        pendingComposite.InstanceCount,
                        pendingComposite.PrimitiveType,
                        pendingComposite.IndexBuffer,
                        vertexBuffers,
                        pendingComposite.RenderState,
                        pendingComposite.DepthTarget,
                        pendingComposite.PixelShaderAddress);
                    TraceAgcShader(
                        $"agc.deferred_composite ps=0x{pendingComposite.PixelShaderAddress:X16} " +
                        $"src=0x{pendingComposite.Textures.FirstOrDefault()?.Descriptor.Address ?? 0:X16} " +
                        $"dst=0x{pendingDisplayTarget.Address:X16} " +
                        $"size={pendingDisplayTarget.Width}x{pendingDisplayTarget.Height}");
                    state.PendingTargetlessDraw = null;
                    state.TranslatedDraw = null;
                }

                if (VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var cachedDisplayBuffer) &&
                    GuestGpu.Current.TrySubmitOrderedGuestImageFlip(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer.Address,
                        cachedDisplayBuffer.Width,
                        cachedDisplayBuffer.Height,
                        cachedDisplayBuffer.PitchInPixel))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer,
                        "gpu-cache");
                }
                else if (state.SawIndexedDraw &&
                    state.TranslatedDraw is { } translatedDraw &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var translatedDisplayBuffer))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        translatedDisplayBuffer,
                        "draw-fallback");
                    var textures = CreateGuestDrawTextures(ctx, translatedDraw.Textures, out var fallbackTextureCount);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffersForPresent(ctx, translatedDraw);
                    GuestGpu.Current.SubmitTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDisplayBuffer.Width,
                        translatedDisplayBuffer.Height,
                        translatedDraw.AttributeCount);
                    TraceAgcShader(
                        $"agc.shader_present ps=0x{translatedDraw.PixelShaderAddress:X16} " +
                        $"spirv={translatedDraw.PixelShader.Payload.Length} textures={textures.Count} " +
                        $"global_buffers={globalMemoryBuffers.Count} " +
                        $"fallback={fallbackTextureCount} {translatedDisplayBuffer.Width}x{translatedDisplayBuffer.Height}");

                    for (var i = 0; i < translatedDraw.Textures.Count; i++)
                    {
                        var binding = translatedDraw.Textures[i];
                        var d = binding.Descriptor;

                        TraceAgcShader(
                            $"agc.present_desc[{i}] " +
                            $"addr=0x{d.Address:X16} " +
                            $"size={d.Width}x{d.Height} " +
                            $"fmt={d.Format} " +
                            $"num={d.NumberType} " +
                            $"type={d.Type} " +
                            $"tile={d.TileMode} " +
                            $"storage={binding.IsStorage}");
                    }
                }
                else if (state.SawIndexedDraw && state.PresenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }
                else if (state.SawIndexedDraw &&
                         state.GuestDrawKind != GuestDrawKind.None &&
                         VideoOutExports.TryGetDisplayBufferInfo(
                             handle,
                             displayBufferIndex,
                             out var displayBuffer))
                {
                    GuestGpu.Current.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                _ = VideoOutExports.SubmitFlipFromAgc(ctx, handle, displayBufferIndex, unchecked((int)flipMode), flipArg);
                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
                if (state.PendingTargetlessDraw is { } unusedPendingDraw)
                {
                    ReturnPooledDrawArrays(
                        unusedPendingDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.PendingTargetlessDraw = null;
                }
                state.TranslatedDraw = null;
            }

            offset += length;
        }

        return false;
    }

    private static void TraceFramePacketSummary(SubmittedDcbState state)
    {
        if (!_traceFramePackets)
        {
            return;
        }

        var flip = ++state.FlipCount;
        if (flip <= 8 || flip % 60 == 0 || state.FrameDrawCount == 0)
        {
            var opcodes = string.Join(
                ',',
                state.FramePacketCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key.Op)
                    .Take(32)
                    .Select(entry => entry.Key.Register == uint.MaxValue
                        ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                        : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
            Console.Error.WriteLine(
                $"[FRAMEPKT] flip={flip} submission={state.ActiveSubmissionId} " +
                $"packets={state.FramePacketCount} draws={state.FrameDrawCount} " +
                $"dispatches={state.FrameDispatchCount} opcodes=[{opcodes}]");
        }

        state.FramePacketCounts.Clear();
        state.FramePacketCount = 0;
        state.FrameDrawCount = 0;
        state.FrameDispatchCount = 0;
    }

    private static void TracePacketParseFailure(
        SubmittedDcbState state,
        ulong address,
        uint offset,
        uint header,
        string reason)
    {
        if (!_traceFramePackets ||
            Interlocked.Increment(ref _packetParseFailureTraceCount) > 128)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[FRAMEPKT] parse-failure queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} offset={offset} " +
            $"address=0x{address:X16} header=0x{header:X8} reason={reason}");
    }

    private static void TraceDisplayBuffer(
        int handle,
        int index,
        VideoOutExports.DisplayBufferInfo buffer,
        string path)
    {
        lock (_submitTraceGate)
        {
            if (!_tracedDisplayBuffers.Add((handle, index, buffer.Address, path)))
            {
                return;
            }
        }

        TraceAgcShader(
            $"agc.display_buffer handle={handle} index={index} " +
            $"addr=0x{buffer.Address:X16} fmt=0x{buffer.PixelFormat:X16} " +
            $"tile={buffer.TilingMode} size={buffer.Width}x{buffer.Height} " +
            $"pitch={buffer.PitchInPixel} path={path}");
    }

    private static void ApplySubmittedDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool compactLayout,
        bool tracePacket)
    {
        var byteCountOffset = compactLayout ? 20UL : 12UL;
        var destinationOffset = compactLayout ? 4UL : 16UL;
        var sourceOffset = compactLayout ? 12UL : 24UL;
        if (!TryReadUInt32(ctx, packetAddress + byteCountOffset, out var byteCount) ||
            !TryReadUInt64(ctx, packetAddress + destinationOffset, out var destinationAddress) ||
            !TryReadUInt64(ctx, packetAddress + sourceOffset, out var sourceAddress))
        {
            return;
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                var immediateFill =
                    compactLayout &&
                    destinationAddress >= 0x10000 &&
                    sourceAddress <= uint.MaxValue;
                var copied =
                    byteCount != 0 &&
                    byteCount <= 256u * 1024u * 1024u &&
                    destinationAddress != 0 &&
                    (immediateFill
                        ? TryFillGuestMemory(ctx, (uint)sourceAddress, destinationAddress, byteCount)
                        : sourceAddress != 0 &&
                          TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount));
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(
                        ctx,
                        destinationAddress,
                        byteCount,
                        immediateFill ? (uint)sourceAddress : null);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.dma_data dst=0x{destinationAddress:X16} " +
                        $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                        $"fill={immediateFill} copied={copied}");
                }
            },
            $"agc_dma_data dst=0x{destinationAddress:X16} bytes={byteCount}",
            packetAddress,
            destinationAddress,
            byteCount);
    }

    private static void SubmitOrderedGpuSideEffect(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        Action action,
        string debugName,
        ulong packetAddress,
        ulong producerAddress = 0,
        ulong producerLength = 0)
    {
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            producerAddress,
            producerLength,
            debugName);

        void CompleteAndWake()
        {
            CompleteLabelProducer(producer);
            if (GpuWaitRegistry.Count == 0)
            {
                return;
            }

            // Resuming a DCB can enqueue another compute dispatch and wait for
            // it. Never do that reentrantly on the Vulkan render thread.
            ThreadPool.UnsafeQueueUserWorkItem(
                static state =>
                {
                    var (resumeContext, resumeGpuState) = state;
                    lock (resumeGpuState.Gate)
                    {
                        DrainResumableDcbs(
                            resumeContext,
                            resumeGpuState,
                            tracePackets: _traceAgc);
                    }
                },
                (ctx, gpuState),
                preferLocal: false);
        }

        void ApplyAndQueueCompletion()
        {
            action();
            // DMA side effects can enqueue a Vulkan image mirror while this
            // ordered action is executing. Completing the label here would
            // wake another queue before that mirror is visible. Queue a
            // second same-queue ordered action after all immediate follow-up
            // writes; it fences those writes before publishing the producer.
            if (VulkanVideoPresenter.SubmitOrderedGuestAction(
                    CompleteAndWake,
                    $"{debugName} completion") == 0)
            {
                CompleteAndWake();
            }
        }

        if (VulkanVideoPresenter.SubmitOrderedGuestAction(
                ApplyAndQueueCompletion,
                debugName) == 0)
        {
            // Headless/startup submissions have no Vulkan queue to order
            // against, so retaining the previous immediate behavior is exact.
            ApplyAndQueueCompletion();
        }
    }

    private static LabelProducerTrace? RegisterLabelProducer(
        object memory,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong address,
        ulong length,
        string debugName)
    {
        if (address == 0 || length == 0)
        {
            return null;
        }

        var producer = new LabelProducerTrace
        {
            Sequence = Interlocked.Increment(ref _labelProducerSequence),
            Memory = memory,
            Address = address,
            Length = length,
            PacketAddress = packetAddress,
            SubmissionId = state.ActiveSubmissionId,
            QueueName = state.QueueName,
            DebugName = debugName,
        };
        lock (_labelProducerGate)
        {
            if (_labelProducers.Count >= 4096)
            {
                _labelProducers.RemoveRange(0, 1024);
            }

            _labelProducers.Add(producer);
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(memory, address, length))
            {
                TraceAgc(
                    $"agc.wait_producer_scheduled label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"packet=0x{packetAddress:X16} action='{debugName}'");
            }
        }

        return producer;
    }

    private static void CompleteLabelProducer(LabelProducerTrace? producer)
    {
        if (producer is null)
        {
            return;
        }

        lock (_labelProducerGate)
        {
            producer.Completed = true;
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(
                         producer.Memory,
                         producer.Address,
                         producer.Length))
            {
                TraceAgc(
                    $"agc.wait_producer_completed label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"action='{producer.DebugName}'");
            }
        }
    }

    private static void TraceWaitProducerState(
        object memory,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong commandAddress,
        ulong packetAddress,
        bool stale,
        ulong? currentValue = null)
    {
        LabelProducerTrace? producer = null;
        lock (_labelProducerGate)
        {
            for (var index = _labelProducers.Count - 1; index >= 0; index--)
            {
                var candidate = _labelProducers[index];
                if (!ReferenceEquals(candidate.Memory, memory) ||
                    !RangesOverlap(
                        candidate.Address,
                        candidate.Length,
                        waiter.WaitAddress,
                        waiter.Is64Bit ? (ulong)sizeof(ulong) : sizeof(uint)))
                {
                    continue;
                }

                producer = candidate;
                break;
            }

            if (_tracedProducerlessWaits.Count >= 4096)
            {
                _tracedProducerlessWaits.Clear();
            }

            if (!stale && producer is null &&
                !_tracedProducerlessWaits.Add(
                    (memory, waiter.WaitAddress, waiter.SubmissionId)))
            {
                return;
            }
        }

        // Producer-backed waits are trace-only. Keep the producer lookup above
        // because producerless waits are always warned, but do not build the
        // detailed condition strings when AGC tracing is disabled.
        if (producer is not null && !_traceAgc)
        {
            return;
        }

        var prefix = stale ? "agc.wait_stale" : "agc.wait_suspended";
        var current = currentValue.HasValue
            ? $"0x{currentValue.Value:X16}"
            : "unreadable";
        var condition =
            $"value={current} mask=0x{waiter.Mask:X16} " +
            $"ref=0x{waiter.ReferenceValue:X16} cmp={waiter.CompareFunction} " +
            $"control=0x{waiter.ControlValue:X8} bits={(waiter.Is64Bit ? 64 : 32)} " +
            $"form={(waiter.IsStandard ? "standard" : "agc-nop")}";
        if (producer is null)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {prefix} label=0x{waiter.WaitAddress:X16} " +
                $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
                $"command=0x{commandAddress:X16} packet=0x{packetAddress:X16} " +
                condition + " " +
                "producer=none-observed; remaining-suspended");
            return;
        }

        TraceAgc(
            $"{prefix} label=0x{waiter.WaitAddress:X16} " +
            $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
            condition + " " +
            $"producer_seq={producer.Sequence} producer_state=" +
            $"{(producer.Completed ? "completed" : "queued")} " +
            $"producer_queue={producer.QueueName} " +
            $"producer_submission={producer.SubmissionId} " +
            $"producer_packet=0x{producer.PacketAddress:X16} " +
            $"action='{producer.DebugName}'");
    }

    private static void ApplySubmittedAcquireMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryDecodeSubmittedAcquireMem(ctx, packetAddress, out var acquire))
        {
            TraceAgc(
                $"agc.acquire_mem_decode_failed queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16}");
            return;
        }

        var queueName = state.QueueName;
        var submissionId = state.ActiveSubmissionId;
        var debugName =
            $"acquire_mem base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
            $"gcr=0x{acquire.GcrControl:X8}";
        void ApplyAcquire()
        {
            // ExecuteOrderedGuestAction first flushes and waits for this guest
            // queue, then writes back dirty guest buffers. At that exact PM4
            // point, refresh only tracked guest images covered by the acquire
            // range. Cached sampled textures use the same dirty tracker and
            // are evicted by the presenter without throwing away clean cache
            // entries (hardware invalidation does not imply changed bytes).
            if (acquire.InvalidatesGuestResources)
            {
                SyncCpuWrittenGuestImages(
                    ctx,
                    acquire.BaseAddress,
                    acquire.CoversAllGuestMemory
                        ? ulong.MaxValue
                        : acquire.SizeBytes);
            }

            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_applied queue={queueName} " +
                    $"submission={submissionId} packet=0x{packetAddress:X16} " +
                    $"work_sequence={VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics}");
            }
        }

        var sequence = VulkanVideoPresenter.SubmitOrderedGuestAction(
            ApplyAcquire,
            debugName);
        if (sequence == 0)
        {
            // Headless startup has no host GPU queue, but the guest-memory
            // cache model still needs the same invalidation semantics.
            ApplyAcquire();
        }

        // The bulk PM4 read is itself a parser-side cache. Do not retain it
        // across a guest cache-invalidation point; subsequent packets return
        // to live guest memory while the host barrier remains ordered in the
        // logical GPU queue. Submission stays asynchronous, matching hardware
        // and avoiding a CPU stall for every ACQUIRE_MEM packet.
        _dcbWindowBuffer = null;
        _dcbWindowByteLength = 0;

        if (tracePacket)
        {
            TraceAgc(
                $"agc.acquire_mem queue={queueName} " +
                $"submission={submissionId} packet=0x{packetAddress:X16} " +
                $"engine={acquire.Engine} cbdb=0x{acquire.CbDbControl:X8} " +
                $"base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
                $"scope={(acquire.CoversAllGuestMemory ? "all" : "range")} " +
                $"poll={acquire.PollInterval} gcr=0x{acquire.GcrControl:X8} " +
                $"resource_inv={acquire.InvalidatesGuestResources} " +
                $"sequence={sequence} scheduled={(sequence != 0)}");
        }
    }

    private static bool TryDecodeSubmittedAcquireMem(
        CpuContext ctx,
        ulong packetAddress,
        out SubmittedAcquireMem acquire)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var coherControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sizeLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sizeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var baseLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var baseHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var pollInterval) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var gcrControl))
        {
            acquire = default;
            return false;
        }

        acquire = DecodeSubmittedAcquireMem(
            coherControl,
            sizeLow,
            sizeHigh,
            baseLow,
            baseHigh,
            pollInterval,
            gcrControl);
        return true;
    }

    private static SubmittedAcquireMem DecodeSubmittedAcquireMem(
        uint coherControl,
        uint sizeLow,
        uint sizeHigh,
        uint baseLow,
        uint baseHigh,
        uint pollInterval,
        uint gcrControl)
    {
        // GFX10 ACQUIRE_MEM expresses COHER_SIZE and COHER_BASE in 256-byte
        // units. SIZE_HI is 8 bits and BASE_HI is 24 bits in the packet.
        var sizeUnits = sizeLow | ((ulong)(sizeHigh & 0xFFu) << 32);
        var baseUnits = baseLow | ((ulong)(baseHigh & 0x00FF_FFFFu) << 32);
        return new SubmittedAcquireMem(
            Engine: coherControl >> 31,
            CbDbControl: coherControl & 0x7FFF_FFFFu,
            BaseAddress: baseUnits << 8,
            SizeBytes: sizeUnits << 8,
            PollInterval: pollInterval & 0xFFFFu,
            GcrControl: gcrControl & 0x7FFFFu);
    }

    private static void ResetSubmittedParserState(SubmittedDcbState state)
    {
        // Queue ownership, pending submissions and suspension bookkeeping are
        // deliberately retained. Work emitted before this packet already owns
        // immutable snapshots; clearing these fields affects only commands
        // translated after RESET at this precise packet position.
        state.CxRegisters.Clear();
        state.ShRegisters.Clear();
        state.UcRegisters.Clear();
        state.PresenterTexture = null;
        state.GuestDrawKind = GuestDrawKind.None;
        state.TranslatedDraw = null;
        state.RenderTargetWriters.Clear();
        state.IndirectArgsAddress = 0;
        state.SawIndexedDraw = false;
        state.IndexBufferAddress = 0;
        state.IndexBufferCount = 0;
        state.IndexSize = 0;
        state.InstanceCount = 1;
        state.DrawIndexOffset = 0;
    }

    private static bool RangesOverlap(
        ulong leftAddress,
        ulong leftLength,
        ulong rightAddress,
        ulong rightLength)
    {
        var leftEnd = leftAddress > ulong.MaxValue - leftLength
            ? ulong.MaxValue
            : leftAddress + leftLength;
        var rightEnd = rightAddress > ulong.MaxValue - rightLength
            ? ulong.MaxValue
            : rightAddress + rightLength;
        return leftAddress < rightEnd && rightAddress < leftEnd;
    }

    /// <summary>
    /// PS5 render targets alias guest memory, so a CP DMA fill or copy that
    /// lands on an RT is visible to later GPU reads. Our render targets live
    /// in Vulkan images, so mirror DMA writes into them: fills become
    /// vkCmdClearColorImage, copies re-upload the guest bytes. Without this,
    /// per-frame DMA clears never reach the image (the fog layer in Dreaming
    /// Sarah accumulates until it saturates, washing the scene out).
    /// </summary>
    /// <summary>
    /// PS5 render targets alias unified memory, so the game's CPU can rewrite a
    /// surface (Chowdren memsets its fog-noise layer every frame) and the GPU
    /// observes it. Our Vulkan guest images are separate storage, so re-upload
    /// CPU-authored surfaces once per flip. Surfaces only the GPU writes keep
    /// all-zero guest memory and are skipped, preserving their GPU content.
    /// </summary>
    private static long _guestImageSyncTraceCount;

    private static void SyncCpuWrittenGuestImages(
        CpuContext ctx,
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        if (!SharpEmu.HLE.GuestImageWriteTracker.Enabled || scopeByteCount == 0)
        {
            return;
        }

        foreach (var (address, width, height, byteCount) in VulkanVideoPresenter.GetGuestImageExtents())
        {
            if (scopeByteCount != ulong.MaxValue &&
                !RangesOverlap(address, byteCount, scopeAddress, scopeByteCount))
            {
                continue;
            }

            if (!SharpEmu.HLE.GuestImageWriteTracker.ConsumeDirty(address))
            {
                continue;
            }

            if (byteCount == 0 || byteCount > MaxPresentedTextureBytes)
            {
                continue;
            }

            var pixels = new byte[byteCount];
            if (ctx.Memory.TryRead(address, pixels))
            {
                VulkanVideoPresenter.SubmitGuestImageWrite(address, pixels);
                if (Interlocked.Increment(ref _guestImageSyncTraceCount) <= 64)
                {
                    Console.Error.WriteLine(
                        $"[SYNC] cpu-write addr=0x{address:X} {width}x{height}");
                }
            }

            SharpEmu.HLE.GuestImageWriteTracker.Rearm(address);
        }
    }

    private static long _dmaMirrorTraceCount;
    private static readonly Dictionary<(uint Op, uint Register), long> _submittedOpcodeCounts = new();
    private static long _submittedOpcodeTotal;

    private static void CountSubmittedOpcode(uint op, uint register)
    {
        var key = (op, op == ItNop ? register : uint.MaxValue);
        lock (_submittedOpcodeCounts)
        {
            _submittedOpcodeCounts[key] =
                _submittedOpcodeCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (++_submittedOpcodeTotal % 500_000 == 0)
            {
                var summary = string.Join(
                    ' ',
                    _submittedOpcodeCounts
                        .OrderByDescending(entry => entry.Value)
                        .Select(entry => entry.Key.Register == uint.MaxValue
                            ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                            : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
                Console.Error.WriteLine($"[PKT] total={_submittedOpcodeTotal} {summary}");
            }
        }
    }

    private static void MirrorDmaWriteToGuestImage(
        CpuContext ctx,
        ulong destinationAddress,
        ulong byteCount,
        uint? fillValue)
    {
        var hasImage = VulkanVideoPresenter.TryGetGuestImageExtent(
            destinationAddress,
            out var width,
            out var height,
            out var imageBytes);
        if (_traceDraws && Interlocked.Increment(ref _dmaMirrorTraceCount) <= 400)
        {
            Console.Error.WriteLine(
                $"[DMA] dst=0x{destinationAddress:X} bytes={byteCount} " +
                $"fill={(fillValue is { } f ? $"0x{f:X8}" : "copy")} image={hasImage}");
        }

        if (!hasImage)
        {
            return;
        }

        if (imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            VulkanVideoPresenter.SubmitGuestImageFill(destinationAddress, fill);
            return;
        }

        var pixels = new byte[imageBytes];
        if (ctx.Memory.TryRead(destinationAddress, pixels))
        {
            VulkanVideoPresenter.SubmitGuestImageWrite(destinationAddress, pixels);
        }
    }

    private static void ApplySubmittedStandardDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var destinationHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var command))
        {
            return;
        }

        var byteCount = command & 0x1F_FFFFu;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var destinationAddress = destinationLow | ((ulong)destinationHigh << 32);
        var writesGuestMemory =
            byteCount != 0 &&
            destinationSwap == 0 &&
            destinationSelect is 0 or 3 &&
            (destinationSelect == 3 || destinationAddressSpace == 0);

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () => ApplySubmittedStandardDmaDataSnapshot(
                ctx,
                control,
                sourceLow,
                sourceHigh,
                destinationLow,
                destinationHigh,
                command),
            $"dma_data dst=0x{destinationHigh:X8}{destinationLow:X8} bytes={byteCount}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? byteCount : 0);
    }

    private static void ApplySubmittedStandardDmaDataSnapshot(
        CpuContext ctx,
        uint control,
        uint sourceLow,
        uint sourceHigh,
        uint destinationLow,
        uint destinationHigh,
        uint command)
    {
        var byteCount = command & 0x1F_FFFFu;
        var sourceSelect = (control >> 29) & 0x3u;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var sourceAddressSpace = (command >> 26) & 0x1u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        if (byteCount == 0 ||
            destinationSwap != 0 ||
            destinationSelect is not (0 or 3) ||
            (destinationSelect == 0 && destinationAddressSpace != 0))
        {
            return;
        }

        var destinationAddress =
            destinationLow | ((ulong)destinationHigh << 32);
        InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
        bool copied;
        ulong sourceAddress;
        if (sourceSelect is 0 or 3 &&
            (sourceSelect == 3 || sourceAddressSpace == 0))
        {
            sourceAddress = sourceLow | ((ulong)sourceHigh << 32);
            if (sourceAddressIncrement != 0)
            {
                copied =
                    TryReadUInt32(ctx, sourceAddress, out var fillValue) &&
                    TryFillGuestMemory(
                        ctx,
                        fillValue,
                        destinationAddress,
                        byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue);
                }
            }
            else
            {
                copied = TryCopyGuestMemory(
                    ctx,
                    sourceAddress,
                    destinationAddress,
                    byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue: null);
                }
            }
        }
        else if (sourceSelect == 2)
        {
            sourceAddress = 0;
            copied = TryFillGuestMemory(
                ctx,
                sourceLow,
                destinationAddress,
                byteCount);
            if (copied)
            {
                MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, sourceLow);
            }
        }
        else
        {
            return;
        }

        if (ShouldTraceHotPath(ref _standardDmaTraceCount))
        {
            TraceAgcShader(
                $"agc.dma_packet dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                $"src_sel={sourceSelect} fill={sourceAddressIncrement != 0 || sourceSelect == 2} " +
                $"copied={copied}");
        }
    }

    private static void ApplySubmittedWriteData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool standardPacket,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var (destination, incrementAddress, writeConfirm, cachePolicy) = standardPacket
            ? DecodeStandardWriteDataControl(control)
            : DecodeAgcWriteDataControl(control);
        var dwordCount = packetLength - 4;
        var values = new uint[dwordCount];
        for (uint index = 0; index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            if (!TryReadUInt32(ctx, sourceAddress, out values[index]))
            {
                return;
            }
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(
                    destinationAddress,
                    incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint));
                var wroteData = destination is 1 or 2 or 4 or 5;
                for (uint index = 0; wroteData && index < dwordCount; index++)
                {
                    var targetAddress = destinationAddress +
                        (incrementAddress ? (ulong)index * sizeof(uint) : 0);
                    wroteData = TryWriteUInt32(ctx, targetAddress, values[index]);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.write_data dst={destination} " +
                        $"addr=0x{destinationAddress:X16} count={dwordCount} " +
                        $"increment={incrementAddress} confirm={writeConfirm} " +
                        $"cache={cachePolicy} standard={standardPacket} wrote={wroteData}");
                }
            },
            $"write_data dst=0x{destinationAddress:X16} count={dwordCount}",
            packetAddress,
            destination is 1 or 2 or 4 or 5 ? destinationAddress : 0,
            destination is 1 or 2 or 4 or 5
                ? incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint)
                : 0);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeStandardWriteDataControl(uint control)
    {
        // GFX10 PKT3_WRITE_DATA is not byte-packed like sceAgcDcbWriteData's
        // NOP wrapper: DST_SEL is 11:8, ADDR_INCR is bit 16 (0 increments),
        // WR_CONFIRM is bit 20, and CACHE_POLICY is 26:25. In particular, the
        // low byte is reserved and must never be interpreted as DST_SEL.
        return (
            Destination: (control >> 8) & 0xFu,
            IncrementAddress: (control & (1u << 16)) == 0,
            WriteConfirm: (control & (1u << 20)) != 0,
            CachePolicy: (control >> 25) & 0x3u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeAgcWriteDataControl(uint control) =>
        (
            Destination: control & 0xFFu,
            IncrementAddress: ((control >> 16) & 0xFFu) == 0,
            WriteConfirm: ((control >> 24) & 0xFFu) != 0,
            CachePolicy: (control >> 8) & 0xFFu);

#if DEBUG
    private static void ValidateWriteDataControlDecoders()
    {
        // Regression vector: reserved low-byte noise previously decoded 0xA5
        // as DST_SEL, causing a valid standard memory write to be discarded.
        const uint standardControl = 0xA5u | (5u << 8) | (1u << 16) | (1u << 20) | (2u << 25);
        var standard = DecodeStandardWriteDataControl(standardControl);
        System.Diagnostics.Debug.Assert(standard.Destination == 5u);
        System.Diagnostics.Debug.Assert(!standard.IncrementAddress);
        System.Diagnostics.Debug.Assert(standard.WriteConfirm);
        System.Diagnostics.Debug.Assert(standard.CachePolicy == 2u);

        const uint agcControl = 4u | (3u << 8) | (1u << 24);
        var agc = DecodeAgcWriteDataControl(agcControl);
        System.Diagnostics.Debug.Assert(agc.Destination == 4u);
        System.Diagnostics.Debug.Assert(agc.IncrementAddress);
        System.Diagnostics.Debug.Assert(agc.WriteConfirm);
        System.Diagnostics.Debug.Assert(agc.CachePolicy == 3u);
    }

    private static void ValidateDispatchInitiators()
    {
        const uint threadCount = 0x00F0_0100u;
        const uint localSize = 64u;
        var initiator = DirectDispatchInitiator(0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 5)) == 0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 6)) != 0);
        System.Diagnostics.Debug.Assert(threadCount * localSize == 0x3C00_4000u);
        System.Diagnostics.Debug.Assert(CeilDivide(20, 8) == 3);
        System.Diagnostics.Debug.Assert(CeilDivide(12, 8) == 2);
    }

    private static void ValidateSubmittedQueueAndReleaseMemDecoders()
    {
        var nggRegisters = new Dictionary<uint, uint>
        {
            [GsUserDataRegister - 1] = 3u << 1,
        };
        System.Diagnostics.Debug.Assert(
            SelectExportUserDataRegister(nggRegisters) == GsUserDataRegister);

        var queue = new SubmittedDcbState();
        queue.PendingSubmissions.Enqueue(new(0x1000, 8, 11, false));
        queue.PendingSubmissions.Enqueue(new(0x2000, 16, 12, true));
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 11);
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 12);

        var control = (1u << 16) | (2u << 29);
        var decoded = DecodeStandardReleaseMemControl(control);
        System.Diagnostics.Debug.Assert(decoded.Destination == 1u);
        System.Diagnostics.Debug.Assert(decoded.DataSelection == 2u);
        System.Diagnostics.Debug.Assert(
            PatchUInt32Bits(0xABCD_1234u, 0x00FF_0000u, 3u << 16) ==
            0xAB03_1234u);
    }

    private static void ValidateAcquireMemAndQueueResetDecoders()
    {
        var range = DecodeSubmittedAcquireMem(
            0x8000_7FC0u,
            0x0000_0123u,
            0x45u,
            0x89AB_CDEFu,
            0x0012_3456u,
            0x1_000Au,
            0x0001_0388u);
        System.Diagnostics.Debug.Assert(range.Engine == 1u);
        System.Diagnostics.Debug.Assert(range.CbDbControl == 0x7FC0u);
        System.Diagnostics.Debug.Assert(range.SizeBytes == 0x0000_4500_0001_2300UL);
        System.Diagnostics.Debug.Assert(range.BaseAddress == 0x1234_5689_ABCD_EF00UL);
        System.Diagnostics.Debug.Assert(range.PollInterval == 0xAu);
        System.Diagnostics.Debug.Assert(range.InvalidatesGuestResources);
        System.Diagnostics.Debug.Assert(!range.CoversAllGuestMemory);

        var all = DecodeSubmittedAcquireMem(0, 0, 0, 0, 0, 0, 0x280u);
        System.Diagnostics.Debug.Assert(all.CoversAllGuestMemory);
        System.Diagnostics.Debug.Assert(all.InvalidatesGuestResources);
        var explicitAll = DecodeSubmittedAcquireMem(0, 1, 0, 0, 0, 0, 0x103C0u);
        System.Diagnostics.Debug.Assert(explicitAll.CoversAllGuestMemory);

        var queue = new SubmittedDcbState
        {
            QueueName = "validator",
            ActiveSubmissionId = 7,
            HasActiveSubmission = true,
            IsSuspended = true,
            IndexBufferAddress = 0x1000,
            IndexBufferCount = 12,
            IndexSize = 1,
            InstanceCount = 4,
            DrawIndexOffset = 2,
            IndirectArgsAddress = 0x2000,
            SawIndexedDraw = true,
            GuestDrawKind = GuestDrawKind.FullscreenBarycentric,
        };
        queue.CxRegisters.Add(1, 2);
        queue.ShRegisters.Add(3, 4);
        queue.UcRegisters.Add(5, 6);
        queue.PendingSubmissions.Enqueue(new(0x3000, 2, 8, false));
        ResetSubmittedParserState(queue);
        System.Diagnostics.Debug.Assert(queue.CxRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.ShRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.UcRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferAddress == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferCount == 0);
        System.Diagnostics.Debug.Assert(queue.IndexSize == 0);
        System.Diagnostics.Debug.Assert(queue.InstanceCount == 1);
        System.Diagnostics.Debug.Assert(queue.DrawIndexOffset == 0);
        System.Diagnostics.Debug.Assert(queue.IndirectArgsAddress == 0);
        System.Diagnostics.Debug.Assert(!queue.SawIndexedDraw);
        System.Diagnostics.Debug.Assert(queue.GuestDrawKind == GuestDrawKind.None);
        System.Diagnostics.Debug.Assert(queue.QueueName == "validator");
        System.Diagnostics.Debug.Assert(queue.ActiveSubmissionId == 7);
        System.Diagnostics.Debug.Assert(queue.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(queue.IsSuspended);
        System.Diagnostics.Debug.Assert(queue.PendingSubmissions.Count == 1);
    }

    private static void ValidateDepthTargetDecoder()
    {
        var registers = new Dictionary<uint, uint>
        {
            [DbDepthControl] = 0x2u | 0x4u | (1u << 4),
            [DbDepthSizeXy] = 1919u | (1079u << 16),
            [DbDepthClear] = BitConverter.SingleToUInt32Bits(1f),
            [DbZInfo] = 3u | (24u << 4),
            [DbZReadBase] = 0x0123_4567u,
            [DbZWriteBase] = 0x0123_4567u,
            [DbZReadBaseHi] = 2u,
            [DbZWriteBaseHi] = 2u,
        };
        var depth = DecodeDepthTarget(registers);
        System.Diagnostics.Debug.Assert(depth is not null);
        System.Diagnostics.Debug.Assert(depth.Width == 1920 && depth.Height == 1080);
        System.Diagnostics.Debug.Assert(depth.GuestFormat == 3u);
        System.Diagnostics.Debug.Assert(depth.SwizzleMode == 24u);
        System.Diagnostics.Debug.Assert(depth.Address == 0x0000_0201_2345_6700UL);
        System.Diagnostics.Debug.Assert(depth.ClearDepth == 1f);
    }
#endif

    // SHARPEMU_GPU_WAIT_MODE=force reverts to the legacy behaviour of faking a
    // satisfying value at parse time. Default (suspend) properly suspends the
    // DCB on an unmet WAIT_REG_MEM and resumes it once the awaited completion
    // label is genuinely written by a later submit — preserving cross-submit
    // ordering so the work after a wait (e.g. the final composite) does not run
    // ahead of the compute it samples.
    private static readonly bool _gpuWaitSuspendEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_MODE"),
        "force",
        StringComparison.OrdinalIgnoreCase);

    // Optional age for one-shot missing-producer diagnostics. Stale waits are
    // never removed or force-satisfied in the default suspend mode: doing so
    // advances a queue without its real cross-queue producer and can publish
    // incomplete CPU/GPU state. Only SHARPEMU_GPU_WAIT_MODE=force retains the
    // explicit legacy mutation path above. Default 0 disables age diagnostics.
    private static readonly long _gpuWaitStaleTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_FALLBACK_MS"),
             out var fallbackMs) && fallbackMs >= 0
            ? fallbackMs
            : 0L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // Reads the WAIT_REG_MEM watched address, reference, mask, and 3-bit compare
    // function for both the AGC NOP-encapsulated (RWaitMem32/64) and the standard
    // ItWaitRegMem packet layouts.
    private static bool TryParseSubmittedWait(
        CpuContext ctx,
        ulong packetAddress,
        bool is64Bit,
        bool isStandard,
        out ulong waitAddress,
        out ulong reference,
        out ulong mask,
        out uint compareFunction,
        out uint controlValue)
    {
        waitAddress = 0;
        reference = 0;
        mask = 0;
        compareFunction = 0;
        controlValue = 0;
        if (isStandard)
        {
            if (!TryReadUInt32(ctx, packetAddress + 4, out var stdControl) ||
                !TryReadUInt64(ctx, packetAddress + 8, out waitAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out var stdRef) ||
                !TryReadUInt32(ctx, packetAddress + 20, out var stdMask))
            {
                return false;
            }

            compareFunction = stdControl & 0x7u;
            controlValue = stdControl;
            reference = stdRef;
            mask = stdMask;
            return true;
        }

        if (!TryReadUInt64(ctx, packetAddress + 4, out waitAddress) ||
            !TryReadUInt32(ctx, packetAddress + (is64Bit ? 28u : 16u), out var control))
        {
            return false;
        }

        compareFunction = control & 0x7u;
        controlValue = control;
        if (is64Bit)
        {
            return TryReadUInt64(ctx, packetAddress + 12, out mask) &&
                   TryReadUInt64(ctx, packetAddress + 20, out reference);
        }

        if (!TryReadUInt32(ctx, packetAddress + 12, out var mask32) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var reference32))
        {
            return false;
        }

        mask = mask32;
        reference = reference32;
        return true;
    }

    // Returns true when the DCB should suspend parsing at this wait (its
    // continuation was registered into GpuWaitRegistry); false to keep parsing
    // (already satisfied, unreadable, or legacy force-satisfy mode).
    private static bool HandleSubmittedWaitRegMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool is64Bit,
        bool isStandard,
        bool tracePacket)
    {
        if (!TryParseSubmittedWait(
                ctx, packetAddress, is64Bit, isStandard,
                out var waitAddress, out var reference, out var mask, out var compareFunction,
                out var controlValue))
        {
            return false;
        }

        // COMPARE_FUNC=0 is the hardware "always" condition. Reserved 7 is
        // also fail-open; neither condition may register a waiter. Validate
        // the watched memory before any read so null/malformed packets cannot
        // become permanent entries keyed by address zero.
        if (compareFunction is 0 or 7)
        {
            TraceSubmittedWait(
                waitAddress,
                0,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            return false;
        }

        var requiredAlignment = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (waitAddress == 0 ||
            mask == 0 ||
            waitAddress % (ulong)requiredAlignment != 0)
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"mask=0x{mask:X16} compare={compareFunction} bits=" +
                $"{(is64Bit ? 64 : 32)} standard={isStandard} " +
                $"packet=0x{packetAddress:X16} reason=invalid-address-or-mask");
            return false;
        }

        ulong currentValue = 0;
        bool hasCurrent;
        if (is64Bit)
        {
            hasCurrent = TryReadUInt64(ctx, waitAddress, out currentValue);
        }
        else if (TryReadUInt32(ctx, waitAddress, out var current32))
        {
            currentValue = current32;
            hasCurrent = true;
        }
        else
        {
            hasCurrent = false;
        }

        TraceSubmittedWait(
            waitAddress, currentValue, mask, reference, compareFunction,
            is64Bit ? 64 : 32, tracePacket);

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = reference,
            Mask = mask,
            CompareFunction = compareFunction,
            ControlValue = controlValue,
            Is64Bit = is64Bit,
            IsStandard = isStandard,
            WaitAddress = waitAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        if (hasCurrent && GpuWaitRegistry.Compare(waiter, currentValue))
        {
            return false; // already satisfied — keep parsing
        }

        if (!_gpuWaitSuspendEnabled)
        {
            if (hasCurrent)
            {
                ForceSatisfyGpuWait(ctx, waiter, currentValue);
            }

            return false;
        }

        if (!hasCurrent)
        {
            return false; // cannot evaluate the label — do not stall the DCB
        }

        GpuWaitRegistry.Register(waitAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            ctx.Memory,
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TraceWaitProducerState(
            ctx.Memory,
            waiter,
            commandAddress,
            packetAddress,
            stale: false,
            currentValue);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.suspended addr=0x{waitAddress:X16} ref=0x{reference:X16} " +
                $"mask=0x{mask:X16} cur=0x{currentValue:X16} cmp={compareFunction}");
        }

        return true;
    }

    /// <summary>
    /// Direct guest CPU stores can satisfy a GPU wait without crossing another
    /// AGC import. Keep one low-frequency monitor per guest memory while waits
    /// exist so those real stores wake their queues. The monitor never changes
    /// a label: it uses the same masked comparison as submission-time parsing
    /// and resumes only after the guest value genuinely satisfies the packet.
    /// </summary>
    private static void EnsureGpuWaitMonitor(
        CpuContext submitContext,
        SubmittedGpuState gpuState)
    {
        if (gpuState.WaitMonitorRunning)
        {
            return;
        }

        gpuState.WaitMonitorRunning = true;
        var monitorContext = new CpuContext(
            submitContext.Memory,
            submitContext.TargetGeneration);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => MonitorGpuWaits(state.Context, state.GpuState),
            (Context: monitorContext, GpuState: gpuState),
            preferLocal: false);
    }

    private static void MonitorGpuWaits(
        CpuContext ctx,
        SubmittedGpuState gpuState)
    {
        var delayMilliseconds = 1;
        while (true)
        {
            var madeProgress = false;
            lock (gpuState.Gate)
            {
                var before = GpuWaitRegistry.CountForMemory(ctx.Memory);
                if (before == 0)
                {
                    gpuState.WaitMonitorRunning = false;
                    return;
                }

                DrainResumableDcbs(ctx, gpuState, tracePackets: _traceAgc);
                var after = GpuWaitRegistry.CountForMemory(ctx.Memory);
                madeProgress = after < before;
                if (madeProgress)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] agc.wait_monitor_resumed count={before - after} " +
                        $"remaining={after}");
                }
                if (after == 0)
                {
                    gpuState.WaitMonitorRunning = false;
                    return;
                }
            }

            delayMilliseconds = madeProgress
                ? 1
                : Math.Min(delayMilliseconds * 2, 16);
            Thread.Sleep(delayMilliseconds);
        }
    }

    /// <summary>
    /// Writes a value that satisfies the waiter's comparison. This deliberately
    /// exists only behind SHARPEMU_GPU_WAIT_MODE=force for legacy A/B testing;
    /// normal and stale waits must never mutate their watched label.
    /// </summary>
    private static void ForceSatisfyGpuWait(
        CpuContext ctx,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong value)
    {
        var address = waiter.WaitAddress;
        var mask = waiter.Mask;
        if (address == 0 || mask == 0)
        {
            return;
        }

        var maskedRef = waiter.ReferenceValue & mask;
        ulong? satisfyMasked = waiter.CompareFunction switch
        {
            1 => maskedRef == 0 ? null : (maskedRef - 1) & mask,            // <
            2 => maskedRef,                                                 // <=
            3 => maskedRef,                                                 // ==
            4 => (~maskedRef) & mask,                                       // !=
            5 => maskedRef,                                                 // >=
            6 => maskedRef == mask ? null : (maskedRef + 1) & mask,         // >
            _ => null,
        };

        if (satisfyMasked is not { } satisfy)
        {
            return;
        }

        var newValue = (value & ~mask) | (satisfy & mask);
        if (waiter.Is64Bit)
        {
            ctx.TryWriteUInt64(address, newValue);
        }
        else
        {
            TryWriteUInt32(ctx, address, unchecked((uint)newValue));
        }
    }

    // WAIT_REG_MEM packets whose condition is not met suspend their DCB into
    // GpuWaitRegistry. Each submit re-checks every suspended DCB against current
    // guest memory (labels are advanced by ReleaseMem/WriteData/DmaData packets
    // or direct CPU writes) and resumes the ones now satisfied. A resumed DCB
    // can itself write labels that unblock others, so loop to a fixed point.
    private static void DrainResumableDcbs(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        bool tracePackets)
    {
        if (!_gpuWaitSuspendEnabled)
        {
            return;
        }

        for (var pass = 0; pass < 256; pass++)
        {
            var woken = GpuWaitRegistry.CollectSatisfied(ctx.Memory, (address, is64Bit) =>
                is64Bit
                    ? TryReadUInt64(ctx, address, out var value64) ? value64 : (ulong?)null
                    : TryReadUInt32(ctx, address, out var value32) ? value32 : (ulong?)null);

            if (woken is null)
            {
                if (_gpuWaitStaleTicks > 0 &&
                    GpuWaitRegistry.CollectUnreportedStale(
                        ctx.Memory,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _gpuWaitStaleTicks) is { } stale)
                {
                    foreach (var waiter in stale)
                    {
                        ulong? currentValue = waiter.Is64Bit
                            ? TryReadUInt64(ctx, waiter.WaitAddress, out var value64)
                                ? value64
                                : null
                            : TryReadUInt32(ctx, waiter.WaitAddress, out var value32)
                                ? value32
                                : null;
                        TraceWaitProducerState(
                            ctx.Memory,
                            waiter,
                            waiter.CommandBufferAddress,
                            waiter.ResumeAddress,
                            stale: true,
                            currentValue);
                    }
                }

                return;
            }

            foreach (var waiter in woken)
            {
                ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
            }
        }
    }

    private static void ResumeSuspendedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        in GpuWaitRegistry.WaitingDcb waiter,
        bool tracePackets)
    {
        var state = waiter.State as SubmittedDcbState ?? gpuState.Graphics;
        var remainingDwords = waiter.TotalDwords - waiter.ResumeOffset;
        var waitedMilliseconds = waiter.RegisteredTicks == 0
            ? 0.0
            : (System.Diagnostics.Stopwatch.GetTimestamp() - waiter.RegisteredTicks) *
              1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TraceAgcShader(
            $"agc.queue_resumed queue={waiter.QueueName} " +
            $"submission={waiter.SubmissionId} label=0x{waiter.WaitAddress:X16} " +
            $"resume=0x{waiter.ResumeAddress:X16} remaining_dwords={remainingDwords} " +
            $"waited_ms={waitedMilliseconds:F3}");
        if (remainingDwords == 0)
        {
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
            PumpSubmittedQueue(ctx, gpuState, state);
            return;
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.dcb.resumed addr=0x{waiter.WaitAddress:X16} " +
                $"resume=0x{waiter.ResumeAddress:X16} dwords={remainingDwords} forced=False");
        }

        System.Diagnostics.Debug.Assert(state.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(state.IsSuspended);
        state.QueueName = waiter.QueueName ?? state.QueueName;
        state.ActiveSubmissionId = waiter.SubmissionId;
        state.IsSuspended = false;
        if (ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                waiter.ResumeAddress,
                remainingDwords,
                tracePackets))
        {
            state.IsSuspended = true;
            return;
        }

        state.HasActiveSubmission = false;
        NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void TraceSubmittedWait(
        ulong address,
        ulong value,
        ulong mask,
        ulong reference,
        uint compareFunction,
        int bits,
        bool tracePacket)
    {
        var maskedValue = value & mask;
        var satisfied = compareFunction switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
        if (!tracePacket && (satisfied || !ShouldTraceHotPath(ref _unsatisfiedWaitTraceCount)))
        {
            return;
        }

        TraceAgc(
            $"agc.dcb.wait_reg_mem bits={bits} addr=0x{address:X16} " +
            $"value=0x{value:X16} mask=0x{mask:X16} ref=0x{reference:X16} " +
            $"compare={compareFunction} satisfied={satisfied}");
    }

    private static void ApplySubmittedStandardReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var (destination, dataSelection) = DecodeStandardReleaseMemControl(control);
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 or 4 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var writesGuestMemory = destination is 0 or 1 &&
                                destinationAddress != 0 &&
                                writeLength != 0;

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && (dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Hardware counter writes are timing values sampled at the
                    // release point, not the immediate payload in ordinal 6/7.
                    3 or 4 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                });

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem_standard dst_sel={destination} " +
                        $"dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                        $"data=0x{data:X16} wrote={wroteData}");
                }
            },
            $"release_mem_standard dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    private static (uint Destination, uint DataSelection)
        DecodeStandardReleaseMemControl(uint control) =>
        (
            Destination: (control >> 16) & 0x3u,
            DataSelection: (control >> 29) & 0x7u);

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var dataSelection = (control >> 16) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                var wroteData = dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Data selection 3 samples the GPU clock at the release
                    // point. The packet payload is ignored by hardware; Unity
                    // uses the nonzero timestamp as submit-completion state.
                    3 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                };

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem dst=0x{destinationAddress:X16} " +
                        $"data_sel={dataSelection} data=0x{data:X16} wrote={wroteData}");
                }
            },
            $"release_mem dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            dataSelection is 1 or 2 or 3 ? destinationAddress : 0,
            writeLength);
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op is ItSetShReg or ItSetContextReg or ItSetUconfigReg)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            var directDestination = op switch
            {
                ItSetShReg => state.ShRegisters,
                ItSetContextReg => state.CxRegisters,
                _ => state.UcRegisters,
            };
            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                directDestination[startRegister + index] = value;
            }

            return;
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            packetLength < 4 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var destination = register switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            // The indirect table has an explicit count; offset zero is a real
            // context-register index (DB_RENDER_CONTROL), not a terminator.
            // Dropping it leaves stale depth/render-control state active in
            // later passes.
            destination[registerOffset] = value;
        }
    }

    private static bool TryReadSubmittedDrawCount(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        out uint drawCount)
    {
        drawCount = 0;
        switch (op)
        {
            case ItDrawIndexAuto when packetLength >= 3:
                return TryReadUInt32(ctx, packetAddress + 4, out drawCount);
            case ItDrawIndex2 when packetLength >= 6:
                state.DrawIndexOffset = 0;
                return TryReadUInt32(ctx, packetAddress + 16, out drawCount);
            case ItDrawIndexOffset2 when packetLength >= 5:
                if (!TryReadUInt32(ctx, packetAddress + 8, out var indexOffset))
                {
                    return false;
                }

                state.DrawIndexOffset = indexOffset;
                return TryReadUInt32(ctx, packetAddress + 12, out drawCount);
            case ItDrawIndexMultiAuto when packetLength >= 4:
                if (!TryReadUInt32(ctx, packetAddress + 12, out var control))
                {
                    return false;
                }

                drawCount = (control >> 21) & 0x7FFu;
                return true;
            case ItDrawIndirect or ItDrawIndexIndirect
                when packetLength >= 5 && state.IndirectArgsAddress != 0:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var dataOffset))
                {
                    return false;
                }

                return TryReadUInt32(
                    ctx,
                    state.IndirectArgsAddress + dataOffset,
                    out drawCount);
            default:
                return false;
        }
    }

    private static void TryTranslateGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        uint vertexCount,
        bool indexed)
    {
        var hasExportShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoEs,
            SpiShaderPgmHiEs,
            out var exportShaderAddress);
        var hasPixelShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoPs,
            SpiShaderPgmHiPs,
            out var pixelShaderAddress);
        var hasPsInputEna = state.CxRegisters.TryGetValue(SpiPsInputEna, out var psInputEna);
        var hasPsInputAddr = state.CxRegisters.TryGetValue(SpiPsInputAddr, out var psInputAddr);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var renderTargets = GetRenderTargets(state.CxRegisters);
        var drawSequence = ++gpuState.WorkSequence;
        if (state.PendingTargetlessDraw is { } stalePendingDraw)
        {
            ReturnPooledDrawArrays(
                stalePendingDraw,
                globals: true,
                vertex: true,
                index: true);
            state.PendingTargetlessDraw = null;
        }
        state.TranslatedDraw = null;
        state.GuestDrawKind = GuestDrawKind.None;
        foreach (var target in renderTargets)
        {
            state.KnownRenderTargets[target.Address] = target;
            // Colour exports originate in the pixel stage.  A depth-only draw
            // can leave old CB registers bound, but it must not become the
            // advertised writer of those surfaces merely because they remain
            // in state.
            if (hasPixelShader)
            {
                state.RenderTargetWriters[target.Address] = new RenderTargetWriter(
                    drawSequence,
                    hasExportShader ? exportShaderAddress : 0,
                    pixelShaderAddress,
                    vertexCount,
                    primitiveType);
            }

            if (_traceAgcShader ||
                _tracePixelShaderAddress == pixelShaderAddress ||
                _traceRenderTargetAddress == target.Address)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.rt_writer seq={drawSequence} target=0x{target.Address:X16} " +
                    $"fmt={target.Format} tile={target.TileMode} " +
                    $"size={target.Width}x{target.Height} vertices={vertexCount} " +
                    $"prim=0x{primitiveType:X} indexed={indexed} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"color_write={(hasPixelShader ? 1 : 0)}");
            }
        }

        if (vertexCount == 0 || vertexCount > 1_048_576)
        {
            return;
        }

        var translationError = string.Empty;
        var depthState = DecodeDepthState(state.CxRegisters);
        var depthTarget = DecodeDepthTarget(state.CxRegisters);
        var hasDepthOnlyCandidate = hasExportShader &&
            !hasPixelShader &&
            depthTarget is not null &&
            (depthState.TestEnable || depthState.WriteEnable || depthState.ClearEnable);
        if (hasDepthOnlyCandidate &&
            TryCreateTranslatedDepthOnlyGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                vertexCount,
                indexed,
                depthTarget!,
                out var depthOnlyDraw,
                out translationError))
        {
            state.TranslatedDraw = depthOnlyDraw;
            var activeDepthTarget = depthOnlyDraw.DepthTarget!;
            var textures = CreateGuestDrawTextures(
                ctx,
                depthOnlyDraw.Textures,
                out _);
            var globalMemoryBuffers =
                CreateTranslatedDrawGlobalBuffers(depthOnlyDraw);
            var vertexBuffers =
                CreateGuestVertexBuffers(depthOnlyDraw.VertexInputs);
            var renderState = depthOnlyDraw.RenderState;
            if (activeDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
            {
                renderState = renderState with
                {
                    Depth = renderState.Depth with { WriteEnable = false },
                };
            }

            TraceDrawCompact(
                drawSequence,
                depthOnlyDraw,
                textures,
                vertexBuffers);
            GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                depthOnlyDraw.PixelShader,
                textures,
                globalMemoryBuffers,
                depthOnlyDraw.AttributeCount,
                activeDepthTarget,
                depthOnlyDraw.VertexShader,
                depthOnlyDraw.VertexCount,
                depthOnlyDraw.InstanceCount,
                depthOnlyDraw.PrimitiveType,
                depthOnlyDraw.IndexBuffer,
                vertexBuffers,
                renderState,
                depthOnlyDraw.PixelShaderAddress);

            if (_traceAgcShader)
            {
                TraceAgcShader(
                    $"agc.depth_only_draw seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} " +
                    $"depth=0x{activeDepthTarget.Address:X16}:" +
                    $"{activeDepthTarget.Width}x{activeDepthTarget.Height}:" +
                    $"fmt{activeDepthTarget.GuestFormat}/sw{activeDepthTarget.SwizzleMode} " +
                    $"test={(renderState.Depth.TestEnable ? 1 : 0)} " +
                    $"write={(renderState.Depth.WriteEnable ? 1 : 0)} " +
                    $"func={renderState.Depth.CompareOp} ro={(activeDepthTarget.ReadOnly ? 1 : 0)}");
            }

            return;
        }

        if (hasExportShader &&
            hasPixelShader &&
            hasPsInputEna &&
            hasPsInputAddr &&
            TryCreateTranslatedGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                psInputEna,
                psInputAddr,
                vertexCount,
                indexed,
                out var translatedDraw,
                out translationError))
        {
            state.TranslatedDraw = translatedDraw;
            if (TryGetHardwareColorResolveTargets(
                    state.CxRegisters,
                    out var resolveSource,
                    out var resolveDestination))
            {
                state.KnownRenderTargets[resolveSource.Address] = resolveSource;
                state.KnownRenderTargets[resolveDestination.Address] = resolveDestination;
                ProvideRenderTargetInitialData(ctx, resolveSource);
                if (VulkanVideoPresenter.TrySubmitGuestImageBlit(
                        resolveSource.Address,
                        resolveSource.Width,
                        resolveSource.Height,
                        resolveSource.Format,
                        resolveSource.NumberType,
                        resolveDestination.Address,
                        resolveDestination.Width,
                        resolveDestination.Height,
                        resolveDestination.Format,
                        resolveDestination.NumberType))
                {
                    state.RenderTargetWriters[resolveDestination.Address] =
                        new RenderTargetWriter(
                            drawSequence,
                            exportShaderAddress,
                            pixelShaderAddress,
                            vertexCount,
                            primitiveType);
                    TraceAgcShader(
                        $"agc.hardware_color_resolve seq={drawSequence} " +
                        $"src=0x{resolveSource.Address:X16}:" +
                        $"{resolveSource.Width}x{resolveSource.Height}:" +
                        $"fmt{resolveSource.Format}/num{resolveSource.NumberType} " +
                        $"dst=0x{resolveDestination.Address:X16}:" +
                        $"{resolveDestination.Width}x{resolveDestination.Height}:" +
                        $"fmt{resolveDestination.Format}/num{resolveDestination.NumberType}");
                    ReturnPooledDrawArrays(
                        translatedDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.TranslatedDraw = null;
                    return;
                }

                TraceAgcShader(
                    $"agc.hardware_color_resolve_unavailable seq={drawSequence} " +
                    $"src=0x{resolveSource.Address:X16} " +
                    $"dst=0x{resolveDestination.Address:X16}");
            }

            var firstTarget = translatedDraw.RenderTargets.FirstOrDefault();
            if (firstTarget.Address != 0)
            {
                // Render every bound color target. A deferred G-buffer draw
                // writes several targets in one guest pass; we render one bound
                // target per Vulkan pass, each with the pixel variant that
                // routes that target's MRT export slot to the fragment output.
                // Every pass is enqueued in order on the same guest render
                // queue. Share the immutable snapshots between those passes
                // and let only the final pass return pooled arrays after its
                // host upload. Copying the full vertex/global payload for each
                // secondary target made deferred G-buffer draws allocate
                // hundreds of MiB per second on the managed large-object heap.
                var drawRenderTargets = translatedDraw.RenderTargets;
                var lastTargetIndex = 0;
                for (var targetIndex = 1; targetIndex < drawRenderTargets.Count; targetIndex++)
                {
                    if (drawRenderTargets[targetIndex].Address != 0)
                    {
                        lastTargetIndex = targetIndex;
                    }
                }

                var sharedTextures = CreateGuestDrawTextures(
                    ctx,
                    translatedDraw.Textures,
                    out _);
                var sharedGlobalMemoryBuffers =
                    CreateTranslatedDrawGlobalBuffers(translatedDraw);
                var sharedVertexBuffers =
                    CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                TraceRectListVertices(translatedDraw, sharedVertexBuffers);
                TraceGrassDrawVertices(translatedDraw, sharedTextures, sharedVertexBuffers);
                TraceDrawCompact(
                    drawSequence,
                    translatedDraw,
                    sharedTextures,
                    sharedVertexBuffers);
                foreach (var renderTarget in drawRenderTargets)
                {
                    if (renderTarget.Address != 0)
                    {
                        ProvideRenderTargetInitialData(ctx, renderTarget);
                    }
                }

                GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                    translatedDraw.PixelShader,
                    sharedTextures,
                    sharedGlobalMemoryBuffers,
                    translatedDraw.AttributeCount,
                    translatedDraw.GuestTargets,
                    translatedDraw.VertexShader,
                    translatedDraw.VertexCount,
                    translatedDraw.InstanceCount,
                    translatedDraw.PrimitiveType,
                    translatedDraw.IndexBuffer,
                    sharedVertexBuffers,
                    translatedDraw.RenderState,
                    translatedDraw.DepthTarget,
                    translatedDraw.PixelShaderAddress);
            }
            else
            {
                if (translatedDraw.DepthTarget is { } translatedDepthTarget)
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        translatedDraw.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(translatedDraw);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                    var renderState = translatedDraw.RenderState;
                    if (translatedDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
                    {
                        renderState = renderState with
                        {
                            Depth = renderState.Depth with { WriteEnable = false },
                        };
                    }

                    TraceDrawCompact(
                        drawSequence,
                        translatedDraw,
                        textures,
                        vertexBuffers);
                    GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDepthTarget,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        vertexBuffers,
                        renderState,
                        translatedDraw.PixelShaderAddress);
                }
                else
                {
                    var storageTarget = translatedDraw.Textures
                        .FirstOrDefault(binding => binding.IsStorage);
                    if (storageTarget is not null)
                    {
                        var textures = CreateGuestDrawTextures(
                            ctx,
                            translatedDraw.Textures,
                            out _);
                        var globalMemoryBuffers =
                            CreateTranslatedDrawGlobalBuffers(translatedDraw);
                        TraceDrawCompact(drawSequence, translatedDraw, textures, []);
                        GuestGpu.Current.SubmitStorageTranslatedDraw(
                            translatedDraw.PixelShader,
                            textures,
                            globalMemoryBuffers,
                            translatedDraw.AttributeCount,
                            storageTarget.Descriptor.Width,
                            storageTarget.Descriptor.Height,
                            translatedDraw.PixelShaderAddress);
                        // The storage submit consumes the global buffers (the
                        // presenter returns them) but never the vertex/index
                        // arrays; return those here so they don't leak the pool.
                        ReturnPooledDrawArrays(
                            translatedDraw,
                            globals: false,
                            vertex: true,
                            index: true);
                    }
                    else
                    {
                        if (translatedDraw.Textures.Count != 0)
                        {
                            // Unity's PS5 final blit can omit CB registers and
                            // rely on the following AGC flip to name the scanout
                            // target. Retain that sampled draw until RFlip, then
                            // enqueue it against the known display surface before
                            // the ordered capture.
                            state.PendingTargetlessDraw = translatedDraw;
                        }
                        else
                        {
                            // No render target, storage sink or sampled source:
                            // nothing can consume this draw.
                            ReturnPooledDrawArrays(
                                translatedDraw,
                                globals: true,
                                vertex: true,
                                index: true);
                        }
                    }
                }
            }

            if (ShouldTraceHotPath(ref _translatedDrawTraceCount))
            {
                TraceAgcShader(
                    $"agc.shader_draw_seen seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
                    $"target=0x{firstTarget.Address:X16}:{firstTarget.Width}x{firstTarget.Height}:fmt{firstTarget.Format}/tile{firstTarget.TileMode} " +
                    $"textures={translatedDraw.Textures.Count}");
            }

            // Trace-only: gated on the flag so the dedup set and the dump —
            // which reads pooled buffer data the presenter may already have
            // recycled (harmless for diagnostics, garbage bytes at worst) —
            // cost nothing in normal runs.
            if (_traceAgcShader)
            {
                lock (_submitTraceGate)
                {
                    var firstTextureAddress = translatedDraw.Textures.FirstOrDefault()?.Descriptor.Address ?? 0;
                    if (_tracedShaderDraws.Add(
                            (exportShaderAddress, pixelShaderAddress, firstTarget.Address, firstTextureAddress, vertexCount)))
                    {
                        TraceTranslatedGuestDraw(
                            ctx,
                            gpuState,
                            state,
                            translatedDraw,
                            psInputEna,
                            psInputAddr);
                    }
                }
            }

            return;
        }

        TraceDrawCompactMiss(
            drawSequence,
            vertexCount,
            hasExportShader && hasPixelShader
                ? translationError
                : hasDepthOnlyCandidate && !string.IsNullOrEmpty(translationError)
                    ? $"depth-only: {translationError}"
                : $"missing-shaders es={hasExportShader} ps={hasPixelShader} ena={hasPsInputEna} addr={hasPsInputAddr}");
        TraceShaderTranslationMiss(
            ctx,
            state,
            vertexCount,
            hasExportShader,
            exportShaderAddress,
            hasPixelShader,
            pixelShaderAddress,
            hasPsInputEna,
            psInputEna,
            hasPsInputAddr,
            psInputAddr,
            hasExportShader && hasPixelShader || hasDepthOnlyCandidate
                ? translationError
                : null);
    }

    private static bool TryCreateTranslatedDepthOnlyGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        uint vertexCount,
        bool indexed,
        GuestDepthTarget depthTarget,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var depthVertexRecords)
                        ? depthVertexRecords
                        : null))
        {
            return false;
        }

        var exportFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var cacheKey = (
            exportShaderAddress,
            exportFingerprint,
            VulkanVideoPresenter.GuestStorageBufferOffsetAlignment);
        _depthOnlyVertexShaderCache.TryGetValue(cacheKey, out var vertexShader);

        if (vertexShader is null)
        {
            var guestGlobalBufferCount = exportEvaluation.GlobalMemoryBindings.Count;
            // CreateTranslatedDrawGlobalBuffers appends both stage scalar
            // blocks.  The pixel block is unused by the fixed fragment stage;
            // the vertex block remains at guestCount+1, matching this layout.
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 2;
            if (!GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out vertexShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBufferCount,
                    imageBindingBase: 0,
                    scalarRegisterBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount + 1,
                    requiredVertexOutputCount: 0,
                    storageBufferOffsetAlignment:
                        VulkanVideoPresenter.GuestStorageBufferOffsetAlignment))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return false;
            }

            DumpCompiledShader(
                "depth-vs",
                exportShaderAddress,
                exportFingerprint,
                vertexShader!,
                exportState.Program);
            VulkanVideoPresenter.CountSpirvCompilation();
            _depthOnlyVertexShaderCache.TryAdd(cacheKey, vertexShader!);
        }

        var textures = new List<TranslatedImageBinding>(
            exportEvaluation.ImageBindings.Count);
        foreach (var binding in exportEvaluation.ImageBindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                if (_strictShaderDescriptors)
                {
                    error = $"invalid export texture descriptor at pc=0x{binding.Pc:X}";
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    return false;
                }

                texture = new TextureDescriptor(
                    0,
                    1,
                    1,
                    Gen5TextureFormatR8G8B8A8Unorm,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1,
                    0xFAC);
            }

            textures.Add(new TranslatedImageBinding(
                texture,
                Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode),
                binding.MipLevel ?? 0,
                binding.SamplerDescriptor));
        }

        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var syntheticTarget = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            TileMode: 0);
        var renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
        {
            // A guest pass without a pixel shader has no colour exports.  The
            // presenter uses a private compatibility attachment, so disable
            // all writes to it and expose only the persistent DB result.
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
        if (depthTarget.Width == 1 &&
            depthTarget.Height == 1 &&
            renderState.Viewport is { } depthViewport)
        {
            var inferredWidth = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Width)),
                1f,
                16384f);
            var inferredHeight = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Height)),
                1f,
                16384f);
            if (inferredWidth > 1 || inferredHeight > 1)
            {
                depthTarget = depthTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                syntheticTarget = syntheticTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
                {
                    Blends = [GuestBlendState.Default with { WriteMask = 0 }],
                };
            }
        }
        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            PixelShaderAddress: 0,
            primitiveType,
            vertexShader!,
            GuestGpu.Current.GetDepthOnlyFragmentShader(),
            AttributeCount: 0,
            vertexCount,
            state.InstanceCount,
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            exportEvaluation.GlobalMemoryBindings,
            vertexInputs,
            RenderTargets: [],
            depthTarget,
            GuestTargets: [],
            renderState,
            PixelUserData: [],
            RawBlendControl: 0,
            RawColorInfo: 0,
            PixelInitialScalars: [],
            exportEvaluation.InitialScalarRegisters);
        return true;
    }

    private static bool TryCreateTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint psInputEna,
        uint psInputAddr,
        uint vertexCount,
        bool indexed,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        ulong pixelShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
            _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
        }

        // Sequential (not short-circuited into one condition) so a failure
        // after an evaluation succeeded can return that evaluation's pooled
        // buffer arrays to the pool instead of leaking them.
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase))
        {
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var vertexRecords)
                        ? vertexRecords
                        : null))
        {
            return false;
        }

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                pixelShaderAddress,
                pixelShaderHeader,
                state.ShRegisters,
                PsTextureUserDataRegister,
                out var pixelState,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS") == "1")
        {
            TraceAstroTitlePixelGlobals(pixelEvaluation);
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS_LIVE") == "1")
        {
            TraceAstroTitlePixelGlobalProbe(pixelEvaluation);
        }

        // Every bound color target the shader exports to. Deferred renderers
        // draw a multi-render-target G-buffer (up to eight slots) in one pass.
        // Fall back to slot 0 if we cannot match any export to a bound target.
        var pixelColorExportMasks = pixelState.Program.PixelColorExportMasks;
        var allBoundTargets = GetRenderTargets(state.CxRegisters);
        // At most 8 slots; a manual filter avoids the per-draw LINQ iterator/
        // closure allocations. Slots are distinct, so sorting by slot is stable.
        var selectedTargets = new List<RenderTargetDescriptor>(allBoundTargets.Count);
        foreach (var target in allBoundTargets)
        {
            if (GetPixelColorExportMask(pixelColorExportMasks, target.Slot) != 0)
            {
                selectedTargets.Add(target);
            }
        }

        if (selectedTargets.Count == 0)
        {
            foreach (var target in allBoundTargets)
            {
                if (target.Slot == 0)
                {
                    selectedTargets.Add(target);
                }
            }
        }

        selectedTargets.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        var renderTargets = selectedTargets.ToArray();
        if (_traceAgcShader && allBoundTargets.Count > 1)
        {
            TraceAgcShader(
                $"agc.mrt_filter ps=0x{pixelShaderAddress:X16} " +
                $"bound=[{string.Join(",", allBoundTargets.Select(t => $"s{t.Slot}:0x{t.Address:X}:exp{(GetPixelColorExportMask(pixelColorExportMasks, t.Slot) != 0 ? 1 : 0)}"))}] " +
                 $"kept={renderTargets.Length}");
        }

        var renderTargetOutputKinds = new Gen5PixelOutputKind[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            var target = renderTargets[index];
            if (!GuestGpu.Current.TryGetRenderTargetOutputKind(
                    target.Format,
                    target.NumberType,
                    out renderTargetOutputKinds[index]))
            {
                error =
                    $"unsupported color target format={target.Format} number_type={target.NumberType}";
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        // Exact packed encoding of the output layout — guest slot (6 bits, CB targets are
        // 0-7) plus output kind (2 bits) per target, host locations being the sequential
        // byte positions. Replaces a per-draw LINQ + string build that allocated on every
        // draw, cache hit or not; the target count disambiguates trailing zero bytes.
        var outputLayout = 0UL;
        for (var index = 0; index < renderTargets.Length; index++)
        {
            outputLayout |= (ulong)(((renderTargets[index].Slot & 0x3Fu) << 2) |
                (uint)renderTargetOutputKinds[index]) << (index * 8);
        }

        var attributeCount = GetInterpolatedAttributeCount(pixelState);
        var exportStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var pixelStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(pixelEvaluation)
            : ComputeShaderStructuralFingerprint(pixelEvaluation);
        var shaderKey = (
            exportShaderAddress,
            exportStateFingerprint,
            pixelShaderAddress,
            pixelStateFingerprint,
            outputLayout,
            (uint)renderTargets.Length,
            attributeCount,
            psInputEna,
            psInputAddr,
            VulkanVideoPresenter.GuestStorageBufferOffsetAlignment);

        var guestGlobalBuffers =
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count;
        // Two per-draw initial-scalar buffers ride after the guest buffers:
        // [pixel guest][vertex guest][pixel sgprs][vertex sgprs].
        var totalGlobalBuffers = _bakeScalars
            ? guestGlobalBuffers
            : guestGlobalBuffers + 2;
        _graphicsShaderCache.TryGetValue(shaderKey, out var compiled);

        if (compiled.Vertex is null || compiled.Pixel is null)
        {
            var pixelOutputs = new Gen5PixelOutputBinding[renderTargets.Length];
            for (var location = 0; location < renderTargets.Length; location++)
            {
                pixelOutputs[location] = new Gen5PixelOutputBinding(
                    renderTargets[location].Slot,
                    (uint)location,
                    renderTargetOutputKinds[location]);
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
                    pixelInputEnable: psInputEna,
                    pixelInputAddress: psInputAddr,
                    storageBufferOffsetAlignment:
                        VulkanVideoPresenter.GuestStorageBufferOffsetAlignment) ||
                !GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out var vertexShader,
                    out error,
                    globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                    totalGlobalBufferCount: totalGlobalBuffers,
                    imageBindingBase: pixelEvaluation.ImageBindings.Count,
                    scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                    requiredVertexOutputCount: (int)GetInterpolatedAttributeCount(pixelState),
                    storageBufferOffsetAlignment:
                        VulkanVideoPresenter.GuestStorageBufferOffsetAlignment))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }

            compiled = (vertexShader!, pixelShader!);
            DumpCompiledShader(
                "vs",
                exportShaderAddress,
                exportStateFingerprint,
                compiled.Vertex,
                exportState.Program);
            DumpCompiledShader(
                "ps",
                pixelShaderAddress,
                pixelStateFingerprint,
                compiled.Pixel,
                pixelState.Program);
            VulkanVideoPresenter.CountSpirvCompilation();
            _graphicsShaderCache.TryAdd(shaderKey, compiled);
        }

        var textures = new List<TranslatedImageBinding>(
            pixelEvaluation.ImageBindings.Count +
            exportEvaluation.ImageBindings.Count);
        if (!TryAppendTranslatedImageBindings(
                pixelEvaluation.ImageBindings,
                textures,
                pixelShaderAddress,
                exportShaderAddress,
                out error) ||
            !TryAppendTranslatedImageBindings(
                exportEvaluation.ImageBindings,
                textures,
                pixelShaderAddress,
                exportShaderAddress,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            ReturnPooledEvaluationArrays(pixelEvaluation);
            return false;
        }

        var globalMemoryBindings = new Gen5GlobalMemoryBinding[
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count];
        for (var index = 0; index < pixelEvaluation.GlobalMemoryBindings.Count; index++)
        {
            globalMemoryBindings[index] = pixelEvaluation.GlobalMemoryBindings[index];
        }
        for (var index = 0; index < exportEvaluation.GlobalMemoryBindings.Count; index++)
        {
            globalMemoryBindings[pixelEvaluation.GlobalMemoryBindings.Count + index] =
                exportEvaluation.GlobalMemoryBindings[index];
        }
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var guestTargets = new GuestRenderTarget[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            guestTargets[index] = new GuestRenderTarget(
                renderTargets[index].Address,
                renderTargets[index].Width,
                renderTargets[index].Height,
                renderTargets[index].Format,
                renderTargets[index].NumberType);
        }

        var pixelUserDataCount = Math.Min(pixelEvaluation.InitialScalarRegisters.Count, 8);
        var pixelUserData = new uint[pixelUserDataCount];
        for (var index = 0; index < pixelUserDataCount; index++)
        {
            pixelUserData[index] = pixelEvaluation.InitialScalarRegisters[index];
        }

        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            pixelShaderAddress,
            primitiveType,
            compiled.Vertex,
            compiled.Pixel,
            GetInterpolatedAttributeCount(pixelState),
            vertexCount,
            state.InstanceCount,
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            globalMemoryBindings,
            vertexInputs,
            renderTargets,
            DecodeDepthTarget(state.CxRegisters),
            guestTargets,
            ApplyTransparentPremultipliedFillClear(
                CreateRenderState(state.CxRegisters, renderTargets, pixelColorExportMasks),
                textures,
                vertexInputs,
                pixelEvaluation.InitialScalarRegisters),
            pixelUserData,
            state.CxRegisters.TryGetValue(CbBlend0Control, out var rawBlend) ? rawBlend : 0,
            state.CxRegisters.TryGetValue(
                CbColor0Info + renderTargets.FirstOrDefault().Slot * CbColorRegisterStride,
                out var rawInfo)
                ? rawInfo
                : 0,
            pixelEvaluation.InitialScalarRegisters,
            exportEvaluation.InitialScalarRegisters);
        return true;
    }

    private static bool TryAppendTranslatedImageBindings(
        IReadOnlyList<Gen5ImageBinding> bindings,
        List<TranslatedImageBinding> textures,
        ulong pixelShaderAddress,
        ulong exportShaderAddress,
        out string error)
    {
        foreach (var binding in bindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                // A garbage/zeroed texture descriptor (from a per-draw descriptor
                // setup race — the same root as scalar-load-failed) would drop
                // the whole draw, so deferred-lighting/composite passes that
                // produce the composite's feeder targets never run. Keep the
                // existing 1x1 fallback unless strict diagnostics are requested.
                if (_strictShaderDescriptors)
                {
                    error = $"invalid texture descriptor at pc=0x{binding.Pc:X}";
                    return false;
                }

                texture = new TextureDescriptor(
                    0, 1, 1, Gen5TextureFormatR8G8B8A8Unorm, 0, 0, 0, 0, 0, 1, 0xFAC);
            }

            var isStorage =
                Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
            if (_traceAgcShader || _tracePixelShaderAddress == pixelShaderAddress)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.texture_binding ps=0x{pixelShaderAddress:X16} es=0x{exportShaderAddress:X16} " +
                    $"pc=0x{binding.Pc:X} op={binding.Opcode} storage={(isStorage ? 1 : 0)} " +
                    $"decoded={FormatTextureDescriptor(texture)} " +
                    $"raw={FormatShaderDwords(binding.ResourceDescriptor)} sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
            }
            textures.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                    binding.SamplerDescriptor));
        }

        error = string.Empty;
        return true;
    }

    private static int _tracedAstroTitlePixelGlobals;
    private static int _tracedAstroTitlePixelGlobalProbe;

    private static void TraceAstroTitlePixelGlobalProbe(Gen5ShaderEvaluation evaluation)
    {
        const int probeOffset = 17216;
        var draw = Interlocked.Increment(ref _tracedAstroTitlePixelGlobalProbe);
        foreach (var (binding, index) in evaluation.GlobalMemoryBindings.Select((value, index) => (value, index)))
        {
            if (probeOffset + 16 > binding.DataLength)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[TITLE-GLOBALS-LIVE] draw={draw} binding={index} " +
                $"base=0x{binding.BaseAddress:X16} offset=0x{probeOffset:X} " +
                $"bytes={Convert.ToHexString(binding.Data.AsSpan(probeOffset, 16))}");
        }
    }

    private static void TraceAstroTitlePixelGlobals(Gen5ShaderEvaluation evaluation)
    {
        if (Interlocked.Exchange(ref _tracedAstroTitlePixelGlobals, 1) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[TITLE-GLOBALS] initial_s0_31=" +
            string.Join(',', evaluation.InitialScalarRegisters
                .Take(32)
                .Select((value, index) => $"s{index}={value:X8}")));

        var probeOffsets = new[]
        {
            0, 16, 24, 32, 48,
            192, 256, 400, 432,
            17100, 17104, 17136, 17168, 17184, 17200, 17216,
        };
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.Error.WriteLine(
                $"[TITLE-GLOBALS] binding s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
            foreach (var offset in probeOffsets)
            {
                if (offset < 0 || offset + 16 > binding.DataLength)
                {
                    continue;
                }

                Console.Error.WriteLine(
                    $"[TITLE-GLOBALS] s{binding.ScalarAddress}+0x{offset:X}=" +
                    Convert.ToHexString(binding.Data.AsSpan(offset, 16)));
            }
        }
    }

    private static readonly bool _fillClearHack = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Treat an untextured fill that outputs pure transparent black through
    /// premultiplied blending as an overwrite. Chowdren issues exactly this
    /// draw once per frame to reset its effect layers (fog smoke, vignette
    /// masks); under the blend factors it sets (One, OneMinusSrcAlpha) a
    /// (0,0,0,0) source is a mathematical no-op, so without this the layers
    /// accumulate until they saturate and the fog composites as a flat veil
    /// over the whole scene. The workaround applies only when every MRT
    /// attachment uses the same blend pattern. Disable with
    /// SHARPEMU_DISABLE_FILL_CLEAR=1.
    /// </summary>
    private static GuestRenderState ApplyTransparentPremultipliedFillClear(
        GuestRenderState renderState,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        IReadOnlyList<uint> pixelUserData)
    {
        if (!_fillClearHack ||
            textures.Count != 0 ||
            vertexInputs.Count != 0 ||
            pixelUserData.Count < 4 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return renderState;
        }

        for (var index = 0; index < 4; index++)
        {
            // Positive or negative zero.
            if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
            {
                return renderState;
            }
        }

        return renderState with
        {
            Blends = renderState.Blends
                .Select(blend => blend with { Enable = false })
                .ToArray(),
        };
    }

    private static bool IsTransparentPremultipliedFillBlend(GuestBlendState blend) =>
        blend is
        {
            Enable: true,
            ColorSrcFactor: 1,
            ColorDstFactor: 5,
            ColorFunc: 0,
        };

    private static GuestIndexBuffer? CreateGuestIndexBuffer(
        CpuContext ctx,
        SubmittedDcbState state,
        uint indexCount)
    {
        if (state.IndexBufferAddress == 0 || indexCount == 0)
        {
            return null;
        }

        var is32Bit = state.IndexSize != 0;
        var bytesPerIndex = is32Bit ? sizeof(uint) : sizeof(ushort);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var byteCount = checked((int)(indexCount * (uint)bytesPerIndex));
        var data = VulkanVideoPresenter.GuestDataPool.Rent(byteCount);
        var span = data.AsSpan(0, byteCount);
        var address = state.IndexBufferAddress + byteOffset;
        if (ctx.Memory.TryRead(address, span) ||
            KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
        {
            return new GuestIndexBuffer(data, byteCount, is32Bit, Pooled: true);
        }

        VulkanVideoPresenter.GuestDataPool.Return(data);
        return null;
    }

    private static bool TryGetRequiredVertexRecordCount(
        CpuContext ctx,
        SubmittedDcbState state,
        uint drawCount,
        bool indexed,
        out uint recordCount)
    {
        recordCount = Math.Max(drawCount, Math.Max(state.InstanceCount, 1u));
        if (!indexed)
        {
            return true;
        }

        if (state.IndexBufferAddress == 0 || drawCount == 0)
        {
            return false;
        }

        var is32Bit = state.IndexSize != 0;
        var bytesPerIndex = is32Bit ? sizeof(uint) : sizeof(ushort);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var address = state.IndexBufferAddress + byteOffset;
        const int chunkBytes = 64 * 1024;
        var scratch = VulkanVideoPresenter.GuestDataPool.Rent(chunkBytes);
        var remaining = drawCount;
        var maxIndex = 0u;
        var sawIndex = false;
        try
        {
            while (remaining != 0)
            {
                var chunkIndices = (int)Math.Min(
                    remaining,
                    (uint)(chunkBytes / bytesPerIndex));
                var bytes = chunkIndices * bytesPerIndex;
                var span = scratch.AsSpan(0, bytes);
                if (!ctx.Memory.TryRead(address, span) &&
                    !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
                {
                    return false;
                }

                for (var index = 0; index < chunkIndices; index++)
                {
                    var value = is32Bit
                        ? BinaryPrimitives.ReadUInt32LittleEndian(
                            span.Slice(index * sizeof(uint), sizeof(uint)))
                        : BinaryPrimitives.ReadUInt16LittleEndian(
                            span.Slice(index * sizeof(ushort), sizeof(ushort)));
                    if (value == (is32Bit ? uint.MaxValue : ushort.MaxValue))
                    {
                        // Primitive-restart markers do not address vertex data.
                        continue;
                    }

                    maxIndex = Math.Max(maxIndex, value);
                    sawIndex = true;
                }

                address += (uint)bytes;
                remaining -= (uint)chunkIndices;
            }
        }
        finally
        {
            VulkanVideoPresenter.GuestDataPool.Return(scratch);
        }

        var indexedRecords = sawIndex && maxIndex != uint.MaxValue
            ? maxIndex + 1
            : 1u;
        recordCount = Math.Max(indexedRecords, Math.Max(state.InstanceCount, 1u));
        if (_traceVertexRanges &&
            Interlocked.Increment(ref _tracedVertexRangeCount) <= 512)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.vertex_range indexed=1 draw_count={drawCount} " +
                $"max_index={(sawIndex ? maxIndex : 0)} records={recordCount} " +
                $"instances={state.InstanceCount} index_size={(is32Bit ? 32 : 16)} " +
                $"index_addr=0x{state.IndexBufferAddress:X16} offset={state.DrawIndexOffset}");
        }
        return true;
    }

    private static uint GetPixelColorExportMask(uint packedMasks, uint target) =>
        target < ColorTargetCount
            ? (packedMasks >> (int)(target * 4)) & 0xFu
            : 0;

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

    private static readonly bool _bakeScalars = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BAKE_SGPRS"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Fingerprint of everything that shapes the translated SPIR-V besides
    /// scalar register values (those arrive in a per-draw buffer): the
    /// resolved binding set with its format-shaping descriptor words, vertex
    /// input layouts, and compute system registers. Value churn in user data
    /// no longer forces a new translation and pipeline.
    /// </summary>
    private static ulong ComputeShaderStructuralFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        void Mix(ulong value) => hash = (hash ^ value) * prime;

        foreach (var binding in evaluation.ImageBindings)
        {
            Mix(binding.Pc);
            Mix((ulong)(uint)binding.Opcode.GetHashCode());
            if (binding.ResourceDescriptor.Count > 1)
            {
                // The generated image type depends only on unified format.
                // Bounds are queried from the bound view in SPIR-V; guest image
                // addresses, dimensions, swizzles and sampler state are all
                // runtime descriptor data and must not create pipeline variants.
                Mix(binding.ResourceDescriptor[1] & 0x1FF0_0000u);
            }

            Mix(binding.MipLevel ?? 0xFFFF_FFFFUL);
        }

        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Mix(binding.ScalarAddress);
            Mix((ulong)binding.InstructionPcs.Count);
            foreach (var pc in binding.InstructionPcs)
            {
                Mix(pc);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var input in vertexInputs)
            {
                Mix(input.Pc);
                Mix(input.Location);
                Mix(input.ComponentCount);
                Mix(input.DataFormat);
                Mix(input.NumberFormat);
                Mix(input.Stride);
                Mix(input.OffsetBytes);
            }
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            Mix(computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue);
        }

        return hash;
    }

    private static ulong ComputeShaderStateFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in evaluation.ScalarRegisters)
        {
            hash = (hash ^ value) * prime;
        }

        // Baked-scalar mode has no runtime state block from which the shader
        // can load descriptor-alignment biases, so the low guest address bits
        // remain part of the generated module and must participate in its key.
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            hash = (hash ^ (
                binding.BaseAddress &
                (VulkanVideoPresenter.GuestStorageBufferOffsetAlignment - 1))) * prime;
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            hash = (hash ^ (computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue)) * prime;
        }

        return hash;
    }

    private static bool TryGetHardwareColorResolveTargets(
        IReadOnlyDictionary<uint, uint> registers,
        out RenderTargetDescriptor source,
        out RenderTargetDescriptor destination)
    {
        source = default;
        destination = default;
        if (!registers.TryGetValue(CbColorControl, out var colorControl) ||
            ((colorControl >> 4) & 0x7u) != 3u)
        {
            return false;
        }

        // CB_COLOR_CONTROL.MODE=RESOLVE uses color slot 0 as the multisampled
        // source and slot 1 as the single-sample destination. CB_TARGET_MASK
        // still enables only slot 0, so treating this like a normal MRT draw
        // rewrites the source and leaves the following composite's input blank.
        var boundTargets = GetRenderTargets(registers, includeMaskedTargets: true);
        source = boundTargets.FirstOrDefault(target => target.Slot == 0);
        destination = boundTargets.FirstOrDefault(target => target.Slot == 1);
        return source.Address != 0 &&
            destination.Address != 0 &&
            source.Width == destination.Width &&
            source.Height == destination.Height &&
            source.Format == destination.Format;
    }

    private static IReadOnlyList<RenderTargetDescriptor> GetRenderTargets(
        IReadOnlyDictionary<uint, uint> registers,
        bool includeMaskedTargets = false)
    {
        var hasTargetMask = registers.TryGetValue(CbTargetMask, out var targetMask);
        var targets = new List<RenderTargetDescriptor>(ColorTargetCount);
        for (uint slot = 0; slot < ColorTargetCount; slot++)
        {
            var baseRegister = CbColor0Base + slot * CbColorRegisterStride;
            if (!registers.TryGetValue(baseRegister, out var baseLow) ||
                !registers.TryGetValue(CbColor0BaseExt + slot, out var baseHigh) ||
                !registers.TryGetValue(CbColor0Attrib2 + slot, out var attrib2) ||
                !registers.TryGetValue(CbColor0Attrib3 + slot, out var attrib3) ||
                !registers.TryGetValue(CbColor0Info + slot * CbColorRegisterStride, out var info))
            {
                continue;
            }

            var address = ((ulong)(baseHigh & 0xFFu) << 40) | ((ulong)baseLow << 8);
            var writeMask = (targetMask >> ((int)slot * 4)) & 0xFu;
            if (address == 0 ||
                (!includeMaskedTargets && hasTargetMask && writeMask == 0))
            {
                continue;
            }

            targets.Add(new RenderTargetDescriptor(
                slot,
                address,
                ((attrib2 >> 14) & 0x3FFFu) + 1,
                (attrib2 & 0x3FFFu) + 1,
                (info >> 2) & 0x1Fu,
                (info >> 8) & 0x7u,
                (attrib3 >> 14) & 0x1Fu));
        }

        return targets;
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        RenderTargetDescriptor target)
    {
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        return new GuestRenderState(
            [DecodeBlendState(registers, target.Slot)],
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers));
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> targets,
        uint pixelColorExportMasks)
    {
        if (targets.Count == 0)
        {
            return GuestRenderState.Default;
        }

        var target = targets[0];
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var blend = DecodeBlendState(registers, targets[index].Slot);
            blends[index] = blend with
            {
                WriteMask = blend.WriteMask &
                    GetPixelColorExportMask(
                        pixelColorExportMasks,
                        targets[index].Slot),
            };
        }

        return new GuestRenderState(
            blends,
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers));
    }

    // DB_DEPTH_CONTROL (context register 0x200): Z_ENABLE bit1, Z_WRITE_ENABLE
    // bit2, ZFUNC bits[6:4] (GCN compare, matches Vulkan CompareOp ordering).
    // DB_RENDER_CONTROL (context register 0x000): DEPTH_CLEAR_ENABLE bit0.
    private const uint DbDepthControl = 0x200;

    internal static GuestDepthState DecodeDepthState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var hasDepthControl = registers.TryGetValue(DbDepthControl, out var control);
        registers.TryGetValue(DbRenderControl, out var renderControl);
        var testEnable = (control & 0x2u) != 0;
        var writeEnable = (control & 0x4u) != 0;
        var compareOp = hasDepthControl
            ? (control >> 4) & 0x7u
            : GuestDepthState.Default.CompareOp;
        var clearEnable = (renderControl & 0x1u) != 0;
        return new GuestDepthState(testEnable, writeEnable, compareOp, clearEnable);
    }

    private static GuestDepthTarget? DecodeDepthTarget(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var depthState = DecodeDepthState(registers);
        if (!depthState.TestEnable &&
            !depthState.WriteEnable &&
            !depthState.ClearEnable)
        {
            return null;
        }

        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            !registers.TryGetValue(DbDepthSizeXy, out var sizeXy))
        {
            return null;
        }

        var guestFormat = zInfo & 0x3u;
        if (guestFormat == 0)
        {
            return null;
        }

        registers.TryGetValue(DbZReadBase, out var readBase);
        registers.TryGetValue(DbZWriteBase, out var writeBase);
        registers.TryGetValue(DbZReadBaseHi, out var readBaseHi);
        registers.TryGetValue(DbZWriteBaseHi, out var writeBaseHi);
        var readAddress = ((ulong)(readBaseHi & 0xFFu) << 40) | ((ulong)readBase << 8);
        var writeAddress = ((ulong)(writeBaseHi & 0xFFu) << 40) | ((ulong)writeBase << 8);
        if (readAddress == 0 && writeAddress == 0)
        {
            return null;
        }

        var width = (sizeXy & 0x3FFFu) + 1;
        var height = ((sizeXy >> 16) & 0x3FFFu) + 1;
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        registers.TryGetValue(DbDepthView, out var depthView);
        var clearDepth = registers.TryGetValue(DbDepthClear, out var clearBits)
            ? BitConverter.UInt32BitsToSingle(clearBits)
            : 1f;
        if (!float.IsFinite(clearDepth) || clearDepth < 0f || clearDepth > 1f)
        {
            clearDepth = 1f;
        }

        return new GuestDepthTarget(
            readAddress,
            writeAddress,
            width,
            height,
            guestFormat,
            (zInfo >> 4) & 0x1Fu,
            clearDepth,
            ReadOnly: (depthView & (1u << 24)) != 0 || writeAddress == 0);
    }

    // PA_SU_SC_MODE_CNTL (context register 0x205) carries face culling, the
    // front-face winding and polygon (wireframe) mode.
    private const uint PaSuScModeCntl = 0x205;

    private static GuestRasterState DecodeRasterState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (!registers.TryGetValue(PaSuScModeCntl, out var mode))
        {
            return GuestRasterState.Default;
        }

        var cullFront = (mode & 0x1u) != 0;
        var cullBack = (mode & 0x2u) != 0;
        var frontFaceClockwise = (mode & 0x4u) != 0;
        var polyMode = (mode >> 3) & 0x3u;
        var frontPtype = (mode >> 5) & 0x7u;
        // POLY_MODE != 0 with a line front primitive type renders wireframe.
        var wireframe = polyMode != 0 && frontPtype == 1;
        return new GuestRasterState(cullFront, cullBack, frontFaceClockwise, wireframe);
    }

    private static GuestBlendState DecodeBlendState(
        IReadOnlyDictionary<uint, uint> registers,
        uint slot)
    {
        var writeMask = 0xFu;
        if (registers.TryGetValue(CbTargetMask, out var targetMask))
        {
            writeMask = (targetMask >> checked((int)(slot * 4))) & 0xFu;
        }

        registers.TryGetValue(CbBlend0Control + slot, out var control);
        return new GuestBlendState(
            ((control >> 30) & 1u) != 0,
            control & 0x1Fu,
            (control >> 8) & 0x1Fu,
            (control >> 5) & 0x7u,
            (control >> 16) & 0x1Fu,
            (control >> 24) & 0x1Fu,
            (control >> 21) & 0x7u,
            ((control >> 29) & 1u) != 0,
            writeMask);
    }

    private static GuestRect? DecodeScissor(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestRect(0, 0, 0, 0);
        }

        var left = 0;
        var top = 0;
        var right = checked((int)Math.Min(targetWidth, int.MaxValue));
        var bottom = checked((int)Math.Min(targetHeight, int.MaxValue));

        var windowOffsetX = 0;
        var windowOffsetY = 0;
        var enableWindowOffset = true;
        if (registers.TryGetValue(PaScWindowScissorTl, out var windowScissorTl))
        {
            enableWindowOffset = (windowScissorTl & 0x80000000u) == 0;
        }

        if (enableWindowOffset &&
            registers.TryGetValue(PaScWindowOffset, out var windowOffset))
        {
            windowOffsetX = (short)(windowOffset & 0xFFFFu);
            windowOffsetY = (short)(windowOffset >> 16);
        }

        // AGC reset-state blocks can carry an all-zero screen-scissor pair as
        // an unpatched placeholder while the generic/viewport scissors hold
        // the active bounds. Treat only that exact reset value as absent. A
        // nonzero empty rectangle remains meaningful and still clips the draw.
        IntersectScissorPair(
            registers,
            PaScScreenScissorTl,
            PaScScreenScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ignoreAllZeroPair: true);
        IntersectScissorPair(
            registers,
            PaScWindowScissorTl,
            PaScWindowScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        IntersectScissorPair(
            registers,
            PaScGenericScissorTl,
            PaScGenericScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        var vportScissorEnabled =
            !registers.TryGetValue(PaScModeCntl0, out var modeControl) ||
            ((modeControl >> 1) & 1u) != 0;
        if (vportScissorEnabled)
        {
            IntersectScissorPair(registers, PaScVportScissor0Tl, PaScVportScissor0Br, ref left, ref top, ref right, ref bottom);
        }

        left = Math.Clamp(left, 0, checked((int)targetWidth));
        top = Math.Clamp(top, 0, checked((int)targetHeight));
        right = Math.Clamp(right, left, checked((int)targetWidth));
        bottom = Math.Clamp(bottom, top, checked((int)targetHeight));

        if (left == 0 &&
            top == 0 &&
            right == (int)targetWidth &&
            bottom == (int)targetHeight)
        {
            return null;
        }

        return new GuestRect(
            left,
            top,
            checked((uint)(right - left)),
            checked((uint)(bottom - top)));
    }

    private static GuestViewport? DecodeViewport(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight,
        GuestRect? scissor)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestViewport(0, 0, 0, 0, 0, 1);
        }

        var minDepth = 0f;
        var maxDepth = 1f;
        if (registers.TryGetValue(PaScVportZMin0, out var zMinBits) &&
            registers.TryGetValue(PaScVportZMax0, out var zMaxBits))
        {
            var decodedMin = BitConverter.UInt32BitsToSingle(zMinBits);
            var decodedMax = BitConverter.UInt32BitsToSingle(zMaxBits);
            if (float.IsFinite(decodedMin) &&
                float.IsFinite(decodedMax) &&
                decodedMax > decodedMin)
            {
                minDepth = decodedMin;
                maxDepth = decodedMax;
            }
        }

        if (TryDecodeFiniteFloat(registers, PaClVportXScale, out var xScale) &&
            TryDecodeFiniteFloat(registers, PaClVportXOffset, out var xOffset) &&
            TryDecodeFiniteFloat(registers, PaClVportYScale, out var yScale) &&
            TryDecodeFiniteFloat(registers, PaClVportYOffset, out var yOffset) &&
            xScale > 0f &&
            yScale != 0f)
        {
            return new GuestViewport(
                xOffset - xScale,
                yOffset - yScale,
                xScale * 2f,
                yScale * 2f,
                minDepth,
                maxDepth);
        }

        if (scissor is not { } rect)
        {
            return minDepth == 0f && maxDepth == 1f
                ? null
                : new GuestViewport(0, 0, targetWidth, targetHeight, minDepth, maxDepth);
        }

        return new GuestViewport(
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            minDepth,
            maxDepth);
    }

    private static bool TryDecodeFiniteFloat(
        IReadOnlyDictionary<uint, uint> registers,
        uint register,
        out float value)
    {
        value = 0;
        if (!registers.TryGetValue(register, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsFinite(value);
    }

    private static void IntersectScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        ref int left,
        ref int top,
        ref int right,
        ref int bottom,
        int offsetX = 0,
        int offsetY = 0,
        bool ignoreAllZeroPair = false)
    {
        if (!TryDecodeScissorPair(
                registers,
                tlRegister,
                brRegister,
                out var pairLeft,
                out var pairTop,
                out var pairRight,
                out var pairBottom,
                out var allZero) ||
            (ignoreAllZeroPair && allZero))
        {
            return;
        }

        pairLeft += offsetX;
        pairTop += offsetY;
        pairRight += offsetX;
        pairBottom += offsetY;

        left = Math.Max(left, pairLeft);
        top = Math.Max(top, pairTop);
        right = Math.Min(right, pairRight);
        bottom = Math.Min(bottom, pairBottom);
    }

    private static bool TryDecodeScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        out int left,
        out int top,
        out int right,
        out int bottom,
        out bool allZero)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        allZero = false;
        if (!registers.TryGetValue(tlRegister, out var tl) ||
            !registers.TryGetValue(brRegister, out var br))
        {
            return false;
        }

        allZero = tl == 0 && br == 0;
        left = (int)(tl & 0x7FFFu);
        top = (int)((tl >> 16) & 0x7FFFu);
        right = (int)(br & 0x7FFFu);
        bottom = (int)((br >> 16) & 0x7FFFu);
        return true;
    }

    private static void TraceTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        TranslatedGuestDraw draw,
        uint psInputEna,
        uint psInputAddr)
    {
        var targets = draw.RenderTargets.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.RenderTargets.Select(target =>
                    $"{target.Slot}:0x{target.Address:X16}:{target.Width}x{target.Height}:" +
                    $"fmt{target.Format}/num{target.NumberType}/tile{target.TileMode}"));
        var depthTarget = draw.DepthTarget is { } depth
            ? $"0x{depth.Address:X16}:{depth.Width}x{depth.Height}:" +
              $"fmt{depth.GuestFormat}/sw{depth.SwizzleMode}:" +
              $"read=0x{depth.ReadAddress:X16}/write=0x{depth.WriteAddress:X16}:" +
              $"clear={depth.ClearDepth:0.######}/ro={(depth.ReadOnly ? 1 : 0)}"
            : "none";
        var probes = new Dictionary<ulong, string>();
        var textures = string.Join(
            ',',
            draw.Textures.Select(binding =>
            {
                var texture = binding.Descriptor;
                var targetSlot = draw.RenderTargets
                    .FirstOrDefault(target => target.Address == texture.Address)
                    .Slot;
                var target = draw.RenderTargets.Any(candidate => candidate.Address == texture.Address)
                    ? $"/rt{targetSlot}"
                    : string.Empty;
                if (!probes.TryGetValue(texture.Address, out var probe))
                {
                    probe = ProbeTexture(ctx, texture);
                    probes.Add(texture.Address, probe);
                }

                state.RenderTargetWriters.TryGetValue(texture.Address, out var sourceWriter);
                gpuState.ComputeImageWriters.TryGetValue(texture.Address, out var computeWriter);
                var writer = sourceWriter.Sequence >= computeWriter.Sequence && sourceWriter.Sequence != 0
                    ? $"/writer={sourceWriter.Sequence}:" +
                      $"es0x{sourceWriter.ExportShaderAddress:X}:" +
                      $"ps0x{sourceWriter.PixelShaderAddress:X}:" +
                      $"v{sourceWriter.VertexCount}:prim0x{sourceWriter.PrimitiveType:X}"
                    : computeWriter.Sequence != 0
                        ? $"/compute={computeWriter.Sequence}:" +
                          $"cs0x{computeWriter.ShaderAddress:X}:{computeWriter.Opcode}"
                        : "/writer=none";
                return
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"/storage={binding.IsStorage}{target}/{probe}{writer}";
            }));
        var buffers = string.Join(
            ',',
            draw.GlobalMemoryBindings.Select((binding, index) =>
                $"{index}:0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                Convert.ToHexString(binding.Data.AsSpan(0, Math.Min(binding.DataLength, 256)))));
        var indices = draw.IndexBuffer is { } indexBuffer
            ? $"{(indexBuffer.Is32Bit ? 32 : 16)}:" +
              Convert.ToHexString(indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Length, 32)))
            : "none";
        var vertexInputs = draw.VertexInputs.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.VertexInputs.Select(input =>
                    $"{input.Location}:pc=0x{input.Pc:X}:0x{input.BaseAddress:X16}" +
                    $":stride{input.Stride}:off{input.OffsetBytes}:c{input.ComponentCount}" +
                    $":fmt{input.DataFormat}/num{input.NumberFormat}"));
        var scissor = draw.RenderState.Scissor is { } drawScissor
            ? $"{drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height}"
            : "full";
        var viewport = draw.RenderState.Viewport is { } drawViewport
            ? $"{drawViewport.X:0.###},{drawViewport.Y:0.###}," +
              $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###}:" +
              $"{drawViewport.MinDepth:0.###}-{drawViewport.MaxDepth:0.###}"
            : "full";
        var rasterRegisters = new (string Name, uint Offset)[]
        {
            ("screen_tl", PaScScreenScissorTl),
            ("screen_br", PaScScreenScissorBr),
            ("window_off", PaScWindowOffset),
            ("window_tl", PaScWindowScissorTl),
            ("window_br", PaScWindowScissorBr),
            ("generic_tl", PaScGenericScissorTl),
            ("generic_br", PaScGenericScissorBr),
            ("vport_tl", PaScVportScissor0Tl),
            ("vport_br", PaScVportScissor0Br),
            ("mode", PaScModeCntl0),
            ("xscale", PaClVportXScale),
            ("xoffset", PaClVportXOffset),
            ("yscale", PaClVportYScale),
            ("yoffset", PaClVportYOffset),
        };
        var raster = string.Join(
            ',',
            rasterRegisters.Select(entry =>
                state.CxRegisters.TryGetValue(entry.Offset, out var value)
                    ? $"{entry.Name}=0x{value:X8}"
                    : $"{entry.Name}=missing"));
        var blend = draw.RenderState.Blend;
        TraceAgcShader(
            $"agc.shader_draw es=0x{draw.ExportShaderAddress:X16} " +
            $"ps=0x{draw.PixelShaderAddress:X16} spirv={draw.PixelShader.Payload.Length} " +
            $"primitive=0x{draw.PrimitiveType:X} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} scissor={scissor} viewport={viewport} " +
            $"raster=[{raster}] " +
            $"ps_ena=0x{psInputEna:X8} ps_addr=0x{psInputAddr:X8} " +
            $"targets=[{targets}] depth=[{depthTarget}] textures=[{textures}] " +
            $"buffers=[{buffers}] vertex=[{vertexInputs}] indices=[{indices}]");
    }

    private static IReadOnlyList<GuestDrawTexture> CreateGuestDrawTextures(
        CpuContext ctx,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<GuestDrawTexture>(bindings.Count);
        fallbackTextureCount = 0;
        foreach (var binding in bindings)
        {
            if (TryCreateGuestDrawTexture(
                    ctx,
                    binding.Descriptor,
                    binding.IsStorage,
                    binding.MipLevel,
                    binding.SamplerDescriptor,
                    out var texture))
            {
                textures.Add(texture);
                if (texture.IsFallback)
                {
                    fallbackTextureCount++;
                }
            }
        }

        return textures;
    }

    /// <summary>
    /// Guest storage buffers for a translated draw, followed by the per-draw
    /// initial scalar registers of each stage (pixel then vertex), matching
    /// the binding layout the shaders were compiled against.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffers(
        TranslatedGuestDraw translatedDraw)
    {
        var buffers = CreateGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 2);
        combined.AddRange(buffers);
        var runtimeStateLength = GetRuntimeScalarBufferLength(
            translatedDraw.GlobalMemoryBindings.Count);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.PixelInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.VertexInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        return combined;
    }

    private static IReadOnlyList<GuestMemoryBuffer>
        CreateGlobalBufferOwnershipView(
            IReadOnlyList<GuestMemoryBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestMemoryBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    /// <summary>
    /// Present-time variant: the flip path can reuse the same translated
    /// draw across several flips and swapchain retries, so it must not wrap
    /// the (pooled, single-consumption) binding arrays. Buffer contents are
    /// re-read from guest memory instead, which also presents current data.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffersForPresent(
        CpuContext ctx,
        TranslatedGuestDraw translatedDraw)
    {
        var bindings = translatedDraw.GlobalMemoryBindings;
        var combined = new List<GuestMemoryBuffer>(bindings.Count + 2);
        foreach (var binding in bindings)
        {
            var data = new byte[Math.Max(binding.DataLength, sizeof(uint))];
            var guestMemoryBacked = binding.BaseAddress != 0 &&
                (ctx.Memory.TryRead(binding.BaseAddress, data) ||
                 KernelMemoryCompatExports.TryReadTrackedLibcHeap(binding.BaseAddress, data));
            if (!guestMemoryBacked)
            {
                // Keep the zero-filled buffer; layout must match the shader.
            }

            combined.Add(new GuestMemoryBuffer(
                binding.BaseAddress,
                data,
                data.Length,
                Pooled: false,
                Writable: binding.Writable,
                WriteBackToGuest: binding.WriteBackToGuest && guestMemoryBacked));
        }

        if (!_bakeScalars)
        {
            var runtimeStateLength = GetRuntimeScalarBufferLength(bindings.Count);
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.PixelInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.VertexInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
        }

        return combined;
    }

    private static int GetRuntimeScalarBufferLength(int bindingCount) =>
        checked((256 + bindingCount) * sizeof(uint));

    private static byte[] PackRuntimeScalarState(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = VulkanVideoPresenter.GuestDataPool.Rent(
            GetRuntimeScalarBufferLength(bindings.Count));
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    private static byte[] PackRuntimeScalarStateUnpooled(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = new byte[GetRuntimeScalarBufferLength(bindings.Count)];
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    private static void PackRuntimeScalarStateInto(
        byte[] bytes,
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        PackScalarRegistersInto(bytes, registers);
        var biasOffset = 256 * sizeof(uint);
        for (var index = 0; index < bindings.Count; index++)
        {
            var byteBias = checked((uint)(
                bindings[index].BaseAddress &
                (VulkanVideoPresenter.GuestStorageBufferOffsetAlignment - 1)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(biasOffset + index * sizeof(uint), sizeof(uint)),
                byteBias);
        }
    }

    private static void PackScalarRegistersInto(byte[] bytes, IReadOnlyList<uint> registers)
    {
        if (registers is uint[] { Length: >= 256 } array)
        {
            // Guest scalar registers are little-endian dwords and the host
            // is x86-64, so a bulk copy replaces 256 per-element writes.
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(array.AsSpan(0, 256))
                .CopyTo(bytes);
            return;
        }

        // Rented arrays carry stale bytes; clear the packed window first.
        Array.Clear(bytes, 0, 256 * sizeof(uint));
        var count = Math.Min(registers.Count, 256);
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                registers[index]);
        }
    }

    /// <summary>
    /// Returns the pooled buffer arrays an evaluation produced. Called only
    /// on translation-failure paths, where no <see cref="TranslatedGuestDraw"/>
    /// is built to take ownership; on success the draw's consumers return them.
    /// </summary>
    private static void ReturnPooledEvaluationArrays(Gen5ShaderEvaluation evaluation)
    {
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            if (binding.DataPooled)
            {
                VulkanVideoPresenter.GuestDataPool.Return(binding.Data);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var binding in vertexInputs)
            {
                if (binding.DataPooled)
                {
                    VulkanVideoPresenter.GuestDataPool.Return(binding.Data);
                }
            }
        }
    }

    /// <summary>
    /// Returns pooled data arrays a translated draw owns but did not hand to
    /// a presenter consumer. The offscreen path hands globals, vertex and
    /// index buffers to the presenter (which returns them), so it passes all
    /// three false; other draw sinks pass true for whatever they dropped.
    /// </summary>
    private static void ReturnPooledDrawArrays(
        TranslatedGuestDraw draw,
        bool globals,
        bool vertex,
        bool index)
    {
        if (globals)
        {
            foreach (var binding in draw.GlobalMemoryBindings)
            {
                if (binding.DataPooled)
                {
                    VulkanVideoPresenter.GuestDataPool.Return(binding.Data);
                }
            }
        }

        if (vertex)
        {
            foreach (var binding in draw.VertexInputs)
            {
                if (binding.DataPooled)
                {
                    VulkanVideoPresenter.GuestDataPool.Return(binding.Data);
                }
            }
        }

        if (index && draw.IndexBuffer is { Pooled: true } indexBuffer)
        {
            VulkanVideoPresenter.GuestDataPool.Return(indexBuffer.Data);
        }
    }

    private static IReadOnlyList<GuestMemoryBuffer> CreateGuestMemoryBuffers(
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var buffers = new GuestMemoryBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            buffers[index] = new GuestMemoryBuffer(
                bindings[index].BaseAddress,
                bindings[index].Data,
                bindings[index].DataLength,
                bindings[index].DataPooled,
                bindings[index].Writable,
                bindings[index].WriteBackToGuest);
        }

        return buffers;
    }

    /// <summary>
    /// Guest storage buffers for a compute dispatch followed by its initial
    /// scalar registers. Dispatch-specific SGPR values remain runtime data so
    /// one translated pipeline serves every matching shader/resource shape.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedComputeGlobalBuffers(
        Gen5ShaderEvaluation evaluation)
    {
        var buffers = CreateGuestMemoryBuffers(evaluation.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 1);
        combined.AddRange(buffers);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                evaluation.InitialScalarRegisters,
                evaluation.GlobalMemoryBindings),
            GetRuntimeScalarBufferLength(evaluation.GlobalMemoryBindings.Count),
            Pooled: true));
        return combined;
    }

    private static IReadOnlyList<GuestVertexBuffer> CreateGuestVertexBuffers(
        IReadOnlyList<Gen5VertexInputBinding> bindings)
    {
        var buffers = new GuestVertexBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            buffers[index] = new GuestVertexBuffer(
                binding.Location,
                binding.ComponentCount,
                binding.DataFormat,
                binding.NumberFormat,
                binding.BaseAddress,
                binding.Stride,
                binding.OffsetBytes,
                binding.Data,
                binding.DataLength,
                binding.DataPooled);
        }

        return buffers;
    }

    private static IReadOnlyList<GuestVertexBuffer>
        CreateVertexBufferOwnershipView(
            IReadOnlyList<GuestVertexBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestVertexBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    private static GuestIndexBuffer? CreateIndexBufferOwnershipView(
        GuestIndexBuffer? buffer,
        bool ownsPooledData) =>
        buffer is null
            ? null
            : buffer with { Pooled = ownsPooledData && buffer.Pooled };

    // BCn block-compressed guest formats and the bytes per 4x4 block.
    private static int GetBlockCompressedBlockBytes(uint format) => format switch
    {
        169 or 170 or 175 or 176 => 8,
        171 or 172 or 173 or 174 or 177 or 178 or 179 or 180 or 181 or 182 => 16,
        _ => 0,
    };

    /// <summary>
    /// Deswizzles a tiled texture source into linear layout when tiling is
    /// enabled and the format is understood; returns null to keep the raw
    /// bytes (linear surfaces, unknown modes, or non-power-of-two elements).
    /// </summary>
    private static bool TryGetTextureElementLayout(
        TextureDescriptor descriptor,
        uint sourceWidth,
        out int elementsWide,
        out int elementsHigh,
        out int bytesPerElement)
    {
        var blockBytes = GetBlockCompressedBlockBytes(descriptor.Format);
        if (blockBytes != 0)
        {
            bytesPerElement = blockBytes;
            elementsWide = (int)((sourceWidth + 3) / 4);
            elementsHigh = (int)((descriptor.Height + 3) / 4);
        }
        else
        {
            bytesPerElement = (int)GetTextureBytesPerTexel(descriptor.Format);
            if (bytesPerElement == 0)
            {
                elementsWide = 0;
                elementsHigh = 0;
                return false;
            }

            elementsWide = (int)sourceWidth;
            elementsHigh = (int)descriptor.Height;
        }

        return true;
    }

    private static byte[]? TryDetileTextureSource(
        TextureDescriptor descriptor,
        uint sourceWidth,
        int logicalByteCount,
        byte[] source)
    {
        if (!GnmTiling.NeedsDetile(descriptor.TileMode) ||
            !TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out var elementsWide,
                out var elementsHigh,
                out var bytesPerElement))
        {
            return null;
        }

        var linear = new byte[logicalByteCount];
        return GnmTiling.TryDetile(
            source,
            linear,
            descriptor.TileMode,
            elementsWide,
            elementsHigh,
            bytesPerElement)
            ? linear
            : null;
    }

    private static void TraceTextureFallback(TextureDescriptor descriptor, string reason)
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        if ((!string.Equals(mode, "1", StringComparison.Ordinal) &&
             !string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)) ||
            Interlocked.Increment(ref _textureFallbackTraceCount) > 64)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.texture_fallback reason={reason} " +
            $"addr=0x{descriptor.Address:X16} type={descriptor.Type} " +
            $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
            $"fmt={descriptor.Format} num={descriptor.NumberType} " +
            $"tile={descriptor.TileMode} mip={descriptor.MipLevels} " +
            $"dst=0x{descriptor.DstSelect:X3}");
    }

    private static bool TryCreateGuestDrawTexture(
        CpuContext ctx,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        out GuestDrawTexture texture)
    {
        texture = default!;
        if ((descriptor.Type != Gen5TextureType1D &&
             descriptor.Type != Gen5TextureType2D) ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Width > 8192 ||
            descriptor.Height > 8192)
        {
            TraceTextureFallback(descriptor, "invalid-descriptor");
            texture = CreateFallbackGuestDrawTexture(isStorage, descriptor.Format, descriptor.NumberType);
            return true;
        }

        var sourceWidth = descriptor.TileMode == 0
            ? GetLinearTexturePitch(
                Math.Max(descriptor.Width, descriptor.Pitch),
                descriptor.Height,
                descriptor.Format)
            : descriptor.Width;
        var sourceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height);
        if (sourceByteCount == 0 ||
            sourceByteCount > MaxPresentedTextureBytes ||
            sourceByteCount > int.MaxValue)
        {
            TraceTextureFallback(
                descriptor,
                $"invalid-byte-count:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(isStorage, descriptor.Format, descriptor.NumberType);
            return true;
        }

        var physicalSourceByteCount = sourceByteCount;
        if (GnmTiling.NeedsDetile(descriptor.TileMode) &&
            TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out var elementsWide,
                out var elementsHigh,
                out var bytesPerElement) &&
            GnmTiling.TryGetTiledByteCount(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                out var tiledByteCount))
        {
            physicalSourceByteCount = tiledByteCount;
        }

        if (physicalSourceByteCount > MaxPresentedTextureBytes ||
            physicalSourceByteCount > int.MaxValue)
        {
            texture = CreateFallbackGuestDrawTexture(isStorage, descriptor.Format, descriptor.NumberType);
            return true;
        }

        if (!isStorage &&
            descriptor.Address != 0 &&
            VulkanVideoPresenter.IsGuestImageAvailable(
                descriptor.Address,
                descriptor.Format,
                descriptor.NumberType))
        {
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor));
            return true;
        }

        if (isStorage)
        {
            var initialPixels = Array.Empty<byte>();
            var uploadKnown = descriptor.Address != 0 &&
                VulkanVideoPresenter.IsGuestImageUploadKnown(
                    descriptor.Address,
                    descriptor.Format,
                    descriptor.NumberType);
            var readSucceeded = false;
            var linearNonzero = false;
            if (descriptor.Address != 0 && !uploadKnown)
            {
                // Storage images can be pre-populated in tiled guest memory
                // just like sampled images. Reading only the logical linear
                // byte count both truncates 64 KiB swizzle blocks and uploads
                // tiled bytes as scanlines. Read the full physical footprint
                // and run the same AddrLib-derived detile path used below for
                // sampled textures before seeding the Vulkan image.
                var storageSource = new byte[(int)physicalSourceByteCount];
                if (ctx.Memory.TryRead(descriptor.Address, storageSource))
                {
                    readSucceeded = true;
                    var linearStorage = TryDetileTextureSource(
                        descriptor,
                        sourceWidth,
                        checked((int)sourceByteCount),
                        storageSource) ?? storageSource
                            .AsSpan(0, checked((int)sourceByteCount))
                            .ToArray();
                    if (linearStorage.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    {
                        linearNonzero = true;
                        initialPixels = linearStorage;
                    }
                }
            }

            if (ParseOptionalHexAddress(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS")) ==
                descriptor.Address)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.storage_initial_data " +
                    $"addr=0x{descriptor.Address:X16} op_storage={isStorage} " +
                    $"upload_known={uploadKnown} read={readSucceeded} " +
                    $"nonzero={linearNonzero} initial_bytes={initialPixels.Length} " +
                    $"logical_bytes={sourceByteCount} physical_bytes={physicalSourceByteCount} " +
                    $"size={descriptor.Width}x{descriptor.Height} pitch={sourceWidth} " +
                    $"fmt={descriptor.Format} num={descriptor.NumberType} " +
                    $"tile={descriptor.TileMode} mip={mipLevel}");
            }

            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                initialPixels,
                IsFallback: descriptor.Address == 0,
                IsStorage: true,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor));
            return true;
        }

        // When the presenter already holds this exact texture identity in
        // its cache, the texel copy below would be discarded on arrival; for
        // scenes that sample large textures every draw this copy dominated
        // CPU time. The dirty peek closes the race with eviction: a texture
        // the guest rewrote must ship fresh texels with this draw, because
        // the render thread evicts the stale cache entry before executing it
        // (skipping would leave the draw with no pixels and a fallback
        // texture for the frame — visible flicker on animated textures).
        var sampler = ToGuestSampler(samplerDescriptor);
        if (!_textureCopySkipDisabled &&
            descriptor.Address != 0 &&
            !SharpEmu.HLE.GuestImageWriteTracker.PeekDirty(descriptor.Address) &&
            VulkanVideoPresenter.IsTextureContentCached(
                new VulkanVideoPresenter.TextureContentIdentity(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    descriptor.DstSelect,
                    descriptor.TileMode,
                    sourceWidth,
                    sampler)))
        {
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: sampler);
            return true;
        }

        var source = new byte[(int)physicalSourceByteCount];
        if (!ctx.Memory.TryRead(descriptor.Address, source))
        {
            TraceTextureFallback(
                descriptor,
                $"guest-read-failed:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(isStorage, descriptor.Format, descriptor.NumberType);
            return true;
        }

        if (_traceAgcShader)
        {
            var nonZero = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != 0)
                {
                    nonZero++;
                    if (nonZero >= 64)
                    {
                        break;
                    }
                }
            }

            TraceAgcShader(
                $"agc.texture_source addr=0x{descriptor.Address:X16} " +
                $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
                $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
                $"dst=0x{descriptor.DstSelect:X3} " +
                $"bytes={source.Length} logical_bytes={sourceByteCount} nonzero64={nonZero}");
        }
        DumpTextureSourceIfRequested(descriptor, sourceWidth, source);

        var rgba = TryDetileTextureSource(
            descriptor,
            sourceWidth,
            checked((int)sourceByteCount),
            source) ?? source.AsSpan(0, checked((int)sourceByteCount)).ToArray();
        DumpLinearTextureIfRequested(descriptor, sourceWidth, rgba);
        texture = new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            rgba,
            IsFallback: false,
            IsStorage: isStorage,
            MipLevels: descriptor.MipLevels,
            MipLevel: mipLevel,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: sourceWidth,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: ToGuestSampler(samplerDescriptor));
        return true;
    }



    /// <summary>
    /// On PS5 render targets alias guest memory, so pixels the game wrote with
    /// the CPU are visible before the first GPU draw (Chowdren pre-fills its
    /// fog/overlay layers that way). Seed newly created Vulkan guest images
    /// with the current guest memory contents to preserve that base layer.
    /// </summary>
    private static void ProvideRenderTargetInitialData(
        CpuContext ctx,
        RenderTargetDescriptor target)
    {
        if (!VulkanVideoPresenter.GuestImageWantsInitialData(target.Address))
        {
            return;
        }

        var byteCount = (ulong)target.Width * target.Height * 4;
        if (byteCount == 0 || byteCount > MaxPresentedTextureBytes)
        {
            return;
        }

        var initialData = new byte[byteCount];
        var readOk = ctx.Memory.TryRead(target.Address, initialData);
        var nonZero = readOk && initialData.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (_traceDraws && _rtSeedTraced.Add(target.Address))
        {
            Console.Error.WriteLine(
                $"[RTSEED] addr=0x{target.Address:X} {target.Width}x{target.Height} " +
                $"read={readOk} nonZero={nonZero}");
        }

        if (nonZero)
        {
            VulkanVideoPresenter.ProvideGuestImageInitialData(target.Address, initialData);
        }
    }

    private static readonly HashSet<ulong> _rtSeedTraced = new();

    private static void TraceDrawCompact(
        ulong sequence,
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (!_traceDraws)
        {
            return;
        }

        var target = draw.RenderTargets.FirstOrDefault();
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport is { } vp
            ? $"{vp.X:0.#},{vp.Y:0.#},{vp.Width:0.#}x{vp.Height:0.#}"
            : "none";
        var textureList = string.Join(
            '|',
            textures.Select(texture =>
                $"0x{texture.Address:X}:{texture.Width}x{texture.Height}" +
                $":f{texture.Format}/n{texture.NumberType}/d{texture.DstSelect:X3}" +
                (texture.IsFallback ? ":FALLBACK" : string.Empty)));
        var positions = string.Empty;
        var positionBuffer = vertexBuffers.FirstOrDefault(buffer => buffer.Location == 0);
        if (positionBuffer is { Length: >= 8 })
        {
            var stride = Math.Max(positionBuffer.Stride, 4u);
            var vertexTotal = (int)((positionBuffer.Length - positionBuffer.OffsetBytes) / stride);
            var sampled = new List<string>();
            foreach (var vertex in new[] { 0, 1, vertexTotal - 1 })
            {
                var baseOffset = (int)(positionBuffer.OffsetBytes + vertex * stride);
                if (vertex < 0 || baseOffset + 8 > positionBuffer.Length)
                {
                    continue;
                }

                sampled.Add(
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset):0.##}," +
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset + 4):0.##}");
            }

            positions = string.Join(';', sampled);
        }

        Console.Error.WriteLine(
            $"[DRAW] seq={sequence} es=0x{draw.ExportShaderAddress:X} ps=0x{draw.PixelShaderAddress:X} " +
            $"target=0x{target.Address:X}:{target.Width}x{target.Height}:f{target.Format}/n{target.NumberType} " +
            $"prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} indexed={draw.IndexBuffer is not null} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc}" +
            $":a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc}/s{(blend.SeparateAlphaBlend ? 1 : 0)} " +
            $"mask=0x{blend.WriteMask:X} viewport={viewport} textures={textureList} pos={positions} " +
            $"ps_s0..3={string.Join(',', draw.PixelUserData.Take(4).Select(value => BitConverter.UInt32BitsToSingle(value).ToString("0.###")))} " +
            $"rawblend=0x{draw.RawBlendControl:X8} info=0x{draw.RawColorInfo:X8}");
    }

    private static void TraceDrawCompactMiss(ulong sequence, uint vertexCount, string error)
    {
        if (!_traceDraws)
        {
            return;
        }

        Console.Error.WriteLine($"[DRAW] seq={sequence} MISS verts={vertexCount} error={error}");
    }

    private static int _grassTraceCount;

    private static void TraceGrassDrawVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (_grassTraceCount >= 6 ||
            !textures.Any(texture => texture.Width == 288 && texture.Height == 160) ||
            vertexBuffers.Count == 0 ||
            Interlocked.Increment(ref _grassTraceCount) > 6)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.Append($"agc.grassdraw prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} ");
        text.Append($"indexed={draw.IndexBuffer is not null} buffers={vertexBuffers.Count}");
        foreach (var buffer in vertexBuffers)
        {
            text.Append(
                $"\n  loc={buffer.Location} fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount} " +
                $"stride={buffer.Stride} offset={buffer.OffsetBytes} bytes={buffer.Length}");
            var stride = Math.Max(buffer.Stride, 4u);
            var maxVerts = Math.Min(6, (int)((buffer.Length - buffer.OffsetBytes) / stride));
            for (var vertex = 0; vertex < maxVerts; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                var components = Math.Min(4, (int)((buffer.Length - baseOffset) / 4));
                text.Append($"\n    v{vertex}:");
                for (var c = 0; c < components; c++)
                {
                    text.Append($" {BitConverter.ToSingle(buffer.Data, baseOffset + c * 4):0.#####}");
                }
            }
        }

        TraceAgcShader(text.ToString());
    }

    private static int _rectListTraceCount;

    private static void TraceRectListVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (draw.PrimitiveType != 0x11 ||
            draw.IndexBuffer is not null ||
            vertexBuffers.Count == 0 ||
            _rectListTraceCount >= 8 ||
            Interlocked.Increment(ref _rectListTraceCount) > 8)
        {
            return;
        }

        var buffer = vertexBuffers[0];
        var stride = Math.Max(buffer.Stride, 4u);
        var text = new System.Text.StringBuilder();
        for (var vertex = 0; vertex < 3; vertex++)
        {
            var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
            if (baseOffset + 16 > buffer.Length)
            {
                break;
            }

            var x = BitConverter.ToSingle(buffer.Data, baseOffset);
            var y = BitConverter.ToSingle(buffer.Data, baseOffset + 4);
            var z = BitConverter.ToSingle(buffer.Data, baseOffset + 8);
            var w = BitConverter.ToSingle(buffer.Data, baseOffset + 12);
            text.Append($" v{vertex}=({x:0.###},{y:0.###},{z:0.###},{w:0.###})");
        }

        TraceAgcShader(
            $"agc.rectlist verts={draw.VertexCount} stride={buffer.Stride} " +
            $"fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount}{text}");
    }

    private static int _textureDumpCount;
    private static readonly ConcurrentDictionary<string, int> _textureDumpKeys = new();

    /// <summary>
    /// Writes raw sampled-texture bytes (as read from guest memory) when
    /// SHARPEMU_TEXTURE_DUMP_DIR is set, so upload-time content can be
    /// inspected offline. File name records size and effective pitch.
    /// </summary>
    private static void DumpTextureSourceIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        // First uses plus periodic later snapshots (the game reuses the same
        // allocation for successive full-screen images).
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes the bytes after detiling when SHARPEMU_TEXTURE_LINEAR_DUMP_DIR is
    /// set. Keeping this separate from the raw-source dump makes AddrLib
    /// equation changes directly inspectable with ordinary image tools.
    /// </summary>
    private static void DumpLinearTextureIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_LINEAR_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"linear-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.linear.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    private static GuestDrawTexture CreateFallbackGuestDrawTexture(
        bool isStorage,
        uint format,
        uint numberType)
    {
        var fallbackFormat = format == 0 ? 10u : format;
        var fallbackNumberType = numberType;
        return new(
            0,
            1,
            1,
            fallbackFormat,
            fallbackNumberType,
            [0, 0, 0, 255],
            IsFallback: true,
            IsStorage: isStorage,
            MipLevels: 1,
            MipLevel: 0);
    }

    private static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new GuestSampler(
                descriptor[0],
                descriptor[1],
                descriptor[2],
                descriptor[3])
            : default;

    private static byte[] ConvertRgba16FloatToRgba8(ReadOnlySpan<byte> source, uint width, uint height)
    {
        var destination = new byte[checked((int)((ulong)width * height * 4))];
        var pixelCount = destination.Length / 4;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var sourceOffset = pixel * 8;
            var destinationOffset = pixel * 4;
            destination[destinationOffset + 0] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[sourceOffset..]));
            destination[destinationOffset + 1] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 2)..]));
            destination[destinationOffset + 2] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 4)..]));
            destination[destinationOffset + 3] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 6)..]));
        }

        return destination;
    }

    private static byte HalfToByte(ushort bits)
    {
        var value = (float)BitConverter.UInt16BitsToHalf(bits);
        if (!float.IsFinite(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private static bool TryReadComputeDispatch(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint opcode,
        out ComputeDispatch dispatch)
    {
        dispatch = default;
        ulong dimensionsAddress;
        uint initiator;
        string dispatchSource;
        if (opcode == ItDispatchDirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt32(ctx, packetAddress + 16, out initiator))
            {
                return false;
            }

            dimensionsAddress = packetAddress + 4;
            dispatchSource = "direct";
        }
        else if (packetLength >= 4)
        {
            if (!TryReadUInt64(ctx, packetAddress + 4, out dimensionsAddress) ||
                !TryReadUInt32(ctx, packetAddress + 12, out initiator))
            {
                return false;
            }

            dispatchSource = "absolute-indirect";
        }
        else
        {
            if (packetLength < 3 ||
                state.IndirectArgsAddress == 0 ||
                !TryReadUInt32(ctx, packetAddress + 4, out var dataOffset) ||
                !TryReadUInt32(ctx, packetAddress + 8, out initiator))
            {
                return false;
            }

            dimensionsAddress = state.IndirectArgsAddress + dataOffset;
            dispatchSource = "base-indirect";
        }

        if ((initiator & 1) == 0 ||
            !TryReadUInt32(ctx, dimensionsAddress, out var dispatchEndX) ||
            !TryReadUInt32(ctx, dimensionsAddress + 4, out var dispatchEndY) ||
            !TryReadUInt32(ctx, dimensionsAddress + 8, out var dispatchEndZ))
        {
            return false;
        }

        if (dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0)
        {
            return RejectComputeDispatch(
                dimensionsAddress,
                initiator,
                dispatchSource,
                dispatchEndX,
                dispatchEndY,
                dispatchEndZ,
                "zero-dimension");
        }

        // When FORCE_START_AT_000 is clear, RDNA2 interprets the three packet
        // values as end coordinates, not group counts. Vulkan expresses the
        // same operation as vkCmdDispatchBase(base, end - base). Ignoring the
        // COMPUTE_START registers turned small high-base clears into apparent
        // multi-million/billion-group dispatches and forced an unsafe cap.
        const uint forceStartAtZero = 1u << 2;
        const uint partialThreadGroupEnabled = 1u << 1;
        const uint useThreadDimensions = 1u << 5;
        uint baseGroupX = 0;
        uint baseGroupY = 0;
        uint baseGroupZ = 0;
        if ((initiator & forceStartAtZero) == 0)
        {
            state.ShRegisters.TryGetValue(ComputeStartX, out baseGroupX);
            state.ShRegisters.TryGetValue(ComputeStartY, out baseGroupY);
            state.ShRegisters.TryGetValue(ComputeStartZ, out baseGroupZ);
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        uint groupCountX;
        uint groupCountY;
        uint groupCountZ;
        var threadCountX = uint.MaxValue;
        var threadCountY = uint.MaxValue;
        var threadCountZ = uint.MaxValue;
        if ((initiator & useThreadDimensions) != 0)
        {
            // In thread-dimension mode the packet contains thread counts, not
            // group end coordinates. Vulkan still dispatches whole workgroups,
            // so round up and pass the exact exclusive thread bounds to the
            // translated shader. Its entry guard disables invocations in the
            // partially populated final group before any guest instruction.
            var startThreadX = (ulong)baseGroupX * localSizeX;
            var startThreadY = (ulong)baseGroupY * localSizeY;
            var startThreadZ = (ulong)baseGroupZ * localSizeZ;
            if ((ulong)dispatchEndX <= startThreadX ||
                (ulong)dispatchEndY <= startThreadY ||
                (ulong)dispatchEndZ <= startThreadZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"thread-end-not-after-base(" +
                    $"{startThreadX}x{startThreadY}x{startThreadZ})");
            }

            groupCountX = CeilDivide((ulong)dispatchEndX - startThreadX, localSizeX);
            groupCountY = CeilDivide((ulong)dispatchEndY - startThreadY, localSizeY);
            groupCountZ = CeilDivide((ulong)dispatchEndZ - startThreadZ, localSizeZ);
            threadCountX = dispatchEndX;
            threadCountY = dispatchEndY;
            threadCountZ = dispatchEndZ;
        }
        else
        {
            if (dispatchEndX <= baseGroupX ||
                dispatchEndY <= baseGroupY ||
                dispatchEndZ <= baseGroupZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"end-not-after-base({baseGroupX}x{baseGroupY}x{baseGroupZ})");
            }

            groupCountX = dispatchEndX - baseGroupX;
            groupCountY = dispatchEndY - baseGroupY;
            groupCountZ = dispatchEndZ - baseGroupZ;
        }

        if ((initiator & partialThreadGroupEnabled) != 0)
        {
            var partialSizeX = GetComputePartialSize(state.ShRegisters, ComputeNumThreadX);
            var partialSizeY = GetComputePartialSize(state.ShRegisters, ComputeNumThreadY);
            var partialSizeZ = GetComputePartialSize(state.ShRegisters, ComputeNumThreadZ);
            if (partialSizeX == 0 || partialSizeX > localSizeX ||
                partialSizeY == 0 || partialSizeY > localSizeY ||
                partialSizeZ == 0 || partialSizeZ > localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"invalid-partial-size({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }

            if (partialSizeX != localSizeX ||
                partialSizeY != localSizeY ||
                partialSizeZ != localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"unrepresentable-partial-group({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }
        }

        var waveLaneCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;

        if (_traceAgcShader &&
            ((ulong)groupCountX * groupCountY * groupCountZ >= 1_000_000UL ||
             groupCountX >= 1_000_000u))
        {
            lock (_submitTraceGate)
            {
                if (_tracedDispatchArguments.Add(
                        (dimensionsAddress, groupCountX, groupCountY, groupCountZ)))
                {
                    TraceAgcShader(
                        $"agc.dispatch_args source={dispatchSource} op=0x{opcode:X2} " +
                        $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                        $"packet=0x{packetAddress:X16} len={packetLength} " +
                        $"dims=0x{dimensionsAddress:X16} " +
                        $"raw={dispatchEndX:X8}/{dispatchEndY:X8}/{dispatchEndZ:X8} " +
                        $"base={baseGroupX:X8}/{baseGroupY:X8}/{baseGroupZ:X8} " +
                        $"count={groupCountX:X8}/{groupCountY:X8}/{groupCountZ:X8} " +
                        $"wave={waveLaneCount} " +
                        $"initiator=0x{initiator:X8} " +
                        $"indirect_base=0x{state.IndirectArgsAddress:X16}");
                }
            }
        }

        dispatch = new ComputeDispatch(
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            waveLaneCount,
            IsIndirect: opcode == ItDispatchIndirect,
            threadCountX,
            threadCountY,
            threadCountZ);
        return true;
    }

    private static uint CeilDivide(ulong value, uint divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static bool RejectComputeDispatch(
        ulong dimensionsAddress,
        uint initiator,
        string source,
        uint rawX,
        uint rawY,
        uint rawZ,
        string reason)
    {
        lock (_submitTraceGate)
        {
            if (_rejectedDispatchArguments.Count < 256 &&
                _rejectedDispatchArguments.Add((dimensionsAddress, initiator, reason)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dispatch_reject source={source} " +
                    $"dims=0x{dimensionsAddress:X16} raw={rawX:X8}/{rawY:X8}/{rawZ:X8} " +
                    $"initiator=0x{initiator:X8} reason={reason}");
            }
        }

        return false;
    }

    private static void ObserveComputeDispatch(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ComputeDispatch dispatch)
    {
        if (!TryGetShaderAddress(
                state.ShRegisters,
                ComputePgmLo,
                ComputePgmHi,
                out var shaderAddress))
        {
            return;
        }

        var sequence = ++gpuState.WorkSequence;
        ulong shaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(shaderAddress, out shaderHeader);
        }

        var computeSystemRegisters = DecodeComputeSystemRegisters(state.ShRegisters);
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                shaderAddress,
                shaderHeader,
                state.ShRegisters,
                ComputeUserDataRegister,
                out var shaderState,
                out var error,
                computeSystemRegisters) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                shaderState,
                out var evaluation,
                out error))
        {
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    TraceAgcShader(
                        $"agc.compute_shader cs=0x{shaderAddress:X16} error={error}");
                }
            }

            return;
        }

        var bindings = evaluation.ImageBindings;
        var descriptions = new List<string>(bindings.Count);
        var translatedBindings = new List<TranslatedImageBinding>(bindings.Count);
        var hasStorageBinding = false;
        foreach (var binding in bindings)
        {
            var isStorage = Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
            var descriptorValid = TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture);
            if (!descriptorValid)
            {
                texture = CreateFallbackTextureDescriptor(binding.ResourceDescriptor);
            }

            translatedBindings.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                    binding.SamplerDescriptor));
            hasStorageBinding |= isStorage;

            var descriptorState = descriptorValid ? string.Empty : "/invalid-desc";
            descriptions.Add(
                $"{binding.Opcode}@0x{binding.Pc:X}:" +
                $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                $"{descriptorState}/{ProbeTexture(ctx, texture)}");
            if (isStorage && descriptorValid && texture.Address != 0)
            {
                gpuState.ComputeImageWriters[texture.Address] = new ComputeImageWriter(
                    sequence,
                    shaderAddress,
                    binding.Opcode);

                TraceAgcShader(
                    $"agc.compute_writer addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} tile={texture.TileMode} " +
                    $"size={texture.Width}x{texture.Height} " +
                    $"cs=0x{shaderAddress:X16} op={binding.Opcode}");
            }
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        if (_traceComputeShaderAddress == shaderAddress)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.compute_dispatch_trace seq={sequence} " +
                $"cs=0x{shaderAddress:X16} " +
                $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                $"bindings=[{string.Join(',', descriptions)}]");
        }

        var writesGlobalMemory = evaluation.GlobalMemoryBindings.Any(static binding =>
            binding.Writable);
        var gpuDispatch = false;
        var evaluationHandledByCpu = false;
        var computeError = string.Empty;
        if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitMaskedDwordCopyKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticCopySequence,
                out var copyDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                copyDescription);
            // The scalar evaluator snapshots guest buffers while parsing the
            // command stream.  Do not let another submission (or the CPU)
            // observe that snapshot until the semantic replacement has
            // reached the same CPU-visible completion point as a translated
            // writable-buffer dispatch below.  Returning early here allowed
            // the guest to reuse a transient heap while its delayed clear was
            // still queued, so the clear could erase newly constructed CPU
            // objects.  Waiting on the work sequence also retires preceding
            // Vulkan writes before the next evaluator snapshot is captured.
            if (!VulkanVideoPresenter.WaitForGuestWork(semanticCopySequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticCopySequence}";
            }
        }
        else if ((hasStorageBinding || writesGlobalMemory) &&
            (ulong)localSizeX * localSizeY * localSizeZ <= 1024)
        {
            var shaderKey = (
                shaderAddress,
                _bakeScalars
                    ? ComputeShaderStateFingerprint(evaluation)
                    : ComputeShaderStructuralFingerprint(evaluation),
                localSizeX,
                localSizeY,
                localSizeZ,
                dispatch.WaveLaneCount,
                VulkanVideoPresenter.GuestStorageBufferOffsetAlignment);
            var guestGlobalBufferCount = evaluation.GlobalMemoryBindings.Count;
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 1;
            _computeShaderCache.TryGetValue(shaderKey, out var computeShader);

            if (computeShader is null &&
                GuestGpu.Current.TryCompileComputeShader(
                    shaderState,
                    evaluation,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    out computeShader,
                    out computeError,
                    totalGlobalBufferCount,
                    initialScalarBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount,
                    waveLaneCount: dispatch.WaveLaneCount,
                    storageBufferOffsetAlignment:
                        VulkanVideoPresenter.GuestStorageBufferOffsetAlignment))
            {
                DumpCompiledShader(
                    "cs",
                    shaderAddress,
                    shaderKey.Item2,
                    computeShader!,
                    shaderState.Program);
            }

            if (computeShader is not null)
            {
                _computeShaderCache.TryAdd(shaderKey, computeShader);

                var textures = CreateGuestDrawTextures(
                    ctx,
                    translatedBindings,
                    out _);
                var globalMemoryBuffers =
                    CreateTranslatedComputeGlobalBuffers(evaluation);
                var workSequence = GuestGpu.Current.SubmitComputeDispatch(
                    shaderAddress,
                    computeShader,
                    textures,
                    globalMemoryBuffers,
                    dispatch.GroupCountX,
                    dispatch.GroupCountY,
                    dispatch.GroupCountZ,
                    dispatch.BaseGroupX,
                    dispatch.BaseGroupY,
                    dispatch.BaseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    dispatch.IsIndirect,
                    writesGlobalMemory,
                    dispatch.ThreadCountX,
                    dispatch.ThreadCountY,
                    dispatch.ThreadCountZ);
                gpuDispatch = true;
                if (writesGlobalMemory &&
                    !VulkanVideoPresenter.WaitForGuestWork(workSequence))
                {
                    computeError = $"global-write-sync-timeout sequence={workSequence}";
                }
            }
        }

        const int blitCount = 0;

        lock (_submitTraceGate)
        {
            if (_tracedComputeShaders.Add(shaderAddress))
            {
                var globalBuffers = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_buffers=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding => $"0x{binding.BaseAddress:X16}:{binding.DataLength}"))}]";
                var scalarProbe = string.Join(
                    ',',
                    evaluation.InitialScalarRegisters
                        .Take(16)
                        .Select((value, index) => $"s{index}={value:X8}"));
                var globalProbes = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"0x{binding.BaseAddress:X16}:" +
                            Convert.ToHexString(binding.Data.AsSpan(
                                0,
                                Math.Min(binding.DataLength, 16)))))}]";
                var globalDescriptors = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_descriptors=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"s{binding.ScalarAddress}=" +
                            string.Join(':', evaluation.ScalarRegisters
                                .Skip(checked((int)binding.ScalarAddress))
                                .Take(4)
                                .Select(value => $"{value:X8}"))))}]";
                var opcodes = string.Join(
                    ',',
                    shaderState.Program.Instructions
                        .Select(instruction => instruction.Opcode)
                        .Distinct()
                        .Take(48));
                TraceAgcShader(
                    $"agc.compute_shader cs=0x{shaderAddress:X16} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"wave={dispatch.WaveLaneCount} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                    $"sys={DescribeComputeSystemRegisters(computeSystemRegisters)} " +
                    $"gpu={gpuDispatch} blits={blitCount} globals={evaluation.GlobalMemoryBindings.Count} " +
                    $"global_writes={writesGlobalMemory}" +
                    (computeError.Length == 0 ? string.Empty : $" error={computeError}") +
                    $" sgprs=[{scalarProbe}]" +
                    globalBuffers +
                    globalProbes +
                    globalDescriptors +
                    $" opcodes=[{opcodes}]" +
                    $" bindings=[{string.Join(',', descriptions)}]");
            }
        }

        if (evaluationHandledByCpu)
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    /// <summary>
    /// Recognizes the SDK's masked-dword resource initialization kernel and
    /// executes its exact semantics over the guest-memory window that the
    /// emulator can map. The guest dispatches this kernel over multi-gigabyte
    /// virtual heaps (up to ~67 million 64-lane workgroups); translating every
    /// out-of-window invocation to Vulkan dominated startup despite those
    /// stores being bounds-discarded. This is a semantic kernel replacement,
    /// not a generic dispatch cap: the complete instruction shape and SGPR
    /// bindings must match before the ordered CPU action is used.
    /// </summary>
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

        var destinationAddress = destination.BaseAddress;
        workSequence = VulkanVideoPresenter.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC masked-copy fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                GuestImageWriteTracker.Track(
                    destinationAddress,
                    (ulong)output.Length,
                    VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.masked-dword-copy");
            },
            $"masked_dword_copy dst=0x{destinationAddress:X16} bytes={output.Length}");
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

        // This replacement depends on the operands as much as the opcode
        // sequence. Reversing V_CMPX_GT or enabling offen on either MUBUF
        // operation changes the set or address of written lanes.
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

        // RDNA2 tables 35 and 37: OOB_SELECT=0 is structured indexing, so
        // NUM_RECORDS counts stride-sized records. FORMAT=20 is 32_UINT and
        // dst_sel_x=4 selects its R component. ADD_TID and either swizzle bit
        // alter addressing and are therefore outside this replacement.
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

    private static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(ComputePgmRsrc2, out var rsrc2);
        var nextRegister = (rsrc2 >> 1) & 0x1Fu;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;

        if ((rsrc2 & (1u << 7)) != 0)
        {
            workGroupX = nextRegister++;
        }

        if ((rsrc2 & (1u << 8)) != 0)
        {
            workGroupY = nextRegister++;
        }

        if ((rsrc2 & (1u << 9)) != 0)
        {
            workGroupZ = nextRegister++;
        }

        if ((rsrc2 & (1u << 10)) != 0)
        {
            threadGroupSize = nextRegister++;
        }

        return new Gen5ComputeSystemRegisters(
            workGroupX,
            workGroupY,
            workGroupZ,
            threadGroupSize);
    }

    private static string DescribeComputeSystemRegisters(Gen5ComputeSystemRegisters registers) =>
        $"x={DescribeRegister(registers.WorkGroupXRegister)}," +
        $"y={DescribeRegister(registers.WorkGroupYRegister)}," +
        $"z={DescribeRegister(registers.WorkGroupZRegister)}," +
        $"size={DescribeRegister(registers.ThreadGroupSizeRegister)}";

    private static string DescribeRegister(uint? register) =>
        register.HasValue ? $"s{register.Value}" : "-";

    private static uint SelectExportUserDataRegister(
        IReadOnlyDictionary<uint, uint> registers)
    {
        // RSRC2 is the authoritative stage selector: its USER_SGPR field
        // describes the hardware SGPR window even when the shader has zero
        // user-data dwords and therefore no USER_DATA register was written.
        // GFX10 NGG export shaders use the GS user-data bank (RSRC2 at 0x8B),
        // while their program address is carried in the ES/NGG registers.
        // Looking only for a populated USER_DATA range made those shaders
        // fall through to ES (0xCC) and reject every graphics draw because
        // the unrelated ES RSRC2 register at 0xCB was legitimately absent.
        if (HasShaderResource2(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasShaderResource2(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasShaderResource2(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        if (HasUserDataRange(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasUserDataRange(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasUserDataRange(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        var esValues = CountUserDataValues(registers, EsUserDataRegister);
        var vsValues = CountUserDataValues(registers, VsUserDataRegister);
        return esValues == 0 && vsValues != 0
            ? VsUserDataRegister
            : EsUserDataRegister;
    }

    private static bool HasShaderResource2(
        IReadOnlyDictionary<uint, uint> registers,
        uint userDataBaseRegister) =>
        registers.ContainsKey(userDataBaseRegister - 1);

    private static bool HasUserDataRange(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        for (var index = 0u; index < 16; index++)
        {
            if (registers.ContainsKey(startRegister + index))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountUserDataValues(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        var count = 0;
        for (var index = 0u; index < 16; index++)
        {
            count += registers.TryGetValue(startRegister + index, out var value) &&
                     value != 0
                ? 1
                : 0;
        }

        return count;
    }

    private static uint GetComputeLocalSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register)
    {
        return registers.TryGetValue(register, out var value)
            ? Math.Max(value & 0xFFFFu, 1u)
            : 1u;
    }

    private static uint GetComputePartialSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register) =>
        registers.TryGetValue(register, out var value)
            ? value >> 16
            : 0u;

    private static int TryApplySoftwareComputeBlits(
        CpuContext ctx,
        ulong shaderAddress,
        IReadOnlyList<(Gen5ImageBinding Binding, TextureDescriptor Texture)> bindings)
    {
        var blits = 0;
        TextureDescriptor? source = null;
        foreach (var (binding, texture) in bindings)
        {
            if (binding.Opcode.StartsWith("ImageStore", StringComparison.Ordinal))
            {
                if (source is { } sourceTexture &&
                    TrySoftwareTextureBlit(ctx, sourceTexture, texture, out var fingerprint))
                {
                    blits++;
                    var key = (shaderAddress, sourceTexture.Address, texture.Address);
                    lock (_softwarePresenterGate)
                    {
                        if (!_softwareComputeBlitFingerprints.TryGetValue(key, out var previous) ||
                            previous != fingerprint)
                        {
                            _softwareComputeBlitFingerprints[key] = fingerprint;
                            TraceAgcShader(
                                $"agc.compute_blit cs=0x{shaderAddress:X16} " +
                                $"src=0x{sourceTexture.Address:X16}:{sourceTexture.Width}x{sourceTexture.Height}:fmt{sourceTexture.Format}/num{sourceTexture.NumberType}/tile{sourceTexture.TileMode} " +
                                $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode} " +
                                $"fingerprint=0x{fingerprint:X16}");
                        }
                    }
                }
                else if (source is { } cachedSourceTexture &&
                    GuestGpu.Current.TrySubmitGuestImageBlit(
                        cachedSourceTexture.Address,
                        cachedSourceTexture.Width,
                        cachedSourceTexture.Height,
                        cachedSourceTexture.Format,
                        cachedSourceTexture.NumberType,
                        texture.Address,
                        texture.Width,
                        texture.Height,
                        texture.Format,
                        texture.NumberType))
                {
                    blits++;
                    TraceAgcShader(
                        $"agc.compute_gpu_blit cs=0x{shaderAddress:X16} " +
                        $"src=0x{cachedSourceTexture.Address:X16}:{cachedSourceTexture.Width}x{cachedSourceTexture.Height}:fmt{cachedSourceTexture.Format}/num{cachedSourceTexture.NumberType}/tile{cachedSourceTexture.TileMode} " +
                        $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}");
                }

                continue;
            }

            if (binding.Opcode.StartsWith("Image", StringComparison.Ordinal))
            {
                source = texture;
            }
        }

        return blits;
    }

    private static bool TrySoftwareTextureBlit(
        CpuContext ctx,
        TextureDescriptor source,
        TextureDescriptor destination,
        out ulong fingerprint)
    {
        fingerprint = 0;
        var bytesPerTexel = GetTextureBytesPerTexel(source.Format);
        if (bytesPerTexel == 0 ||
            bytesPerTexel != GetTextureBytesPerTexel(destination.Format) ||
            source.Type != Gen5TextureType2D ||
            destination.Type != Gen5TextureType2D ||
            source.Width == 0 ||
            source.Height == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            destination.Width > 8192 ||
            destination.Height > 8192)
        {
            return false;
        }

        var sourceBytes = checked((ulong)source.Width * source.Height * bytesPerTexel);
        var destinationBytes = checked((ulong)destination.Width * destination.Height * bytesPerTexel);
        if (sourceBytes == 0 ||
            destinationBytes == 0 ||
            sourceBytes > MaxPresentedTextureBytes ||
            destinationBytes > MaxPresentedTextureBytes ||
            sourceBytes > int.MaxValue ||
            destinationBytes > int.MaxValue)
        {
            return false;
        }

        var sourceData = new byte[(int)sourceBytes];
        if (!ctx.Memory.TryRead(source.Address, sourceData))
        {
            return false;
        }

        var nonzero = 0;
        foreach (var value in sourceData)
        {
            if (value != 0)
            {
                nonzero++;
                break;
            }
        }

        if (nonzero == 0)
        {
            return false;
        }

        var destinationData = new byte[(int)destinationBytes];
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * bytesPerTexel));
                var destinationOffset = checked((int)(((ulong)y * destination.Width + x) * bytesPerTexel));
                sourceData.AsSpan(sourceOffset, (int)bytesPerTexel)
                    .CopyTo(destinationData.AsSpan(destinationOffset, (int)bytesPerTexel));
            }
        }

        if (!ctx.Memory.TryWrite(destination.Address, destinationData))
        {
            return false;
        }

        fingerprint = ComputeFingerprint(destinationData);
        return true;
    }

    private static string ProbeTexture(CpuContext ctx, TextureDescriptor texture)
    {
        if (texture.Width == 0 ||
            texture.Height == 0)
        {
            return "probe=unsupported";
        }

        var totalBytes = GetTextureByteCount(
            texture.Format,
            texture.Width,
            texture.Height);
        if (totalBytes == 0)
        {
            return "probe=unsupported";
        }

        const int sampleCount = 32;
        const int sampleSize = 256;
        var sample = new byte[sampleSize];
        var reads = 0;
        var nonzero = 0;
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        for (var index = 0; index < sampleCount; index++)
        {
            var maxOffset = totalBytes > sampleSize ? totalBytes - sampleSize : 0;
            var offset = sampleCount == 1
                ? 0
                : maxOffset * (ulong)index / (sampleCount - 1);
            if (!ctx.Memory.TryRead(texture.Address + offset, sample))
            {
                continue;
            }

            reads++;
            foreach (var value in sample)
            {
                if (value != 0)
                {
                    nonzero++;
                }

                hash = (hash ^ value) * prime;
            }
        }

        var bytesPerTexel = GetTextureBytesPerTexel(texture.Format);
        var texels = bytesPerTexel is > 0 and <= 16
            ? string.Join(
                '/',
                ProbeTextureTexel(ctx, texture.Address, (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address +
                    (((ulong)(texture.Height / 2) * texture.Width) + (texture.Width / 2)) *
                    bytesPerTexel,
                    (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address + totalBytes - bytesPerTexel,
                    (int)bytesPerTexel))
            : "unsupported";
        return $"probe={reads}/{sampleCount}:{nonzero}:0x{hash:X16}:texels={texels}";
    }

    private static string ProbeTextureTexel(CpuContext ctx, ulong address, int size)
    {
        var texel = new byte[size];
        return ctx.Memory.TryRead(address, texel)
            ? Convert.ToHexString(texel)
            : "unreadable";
    }

    private static ulong GetTextureBytesPerTexel(uint format) =>
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

    private static ulong GetTextureByteCount(uint format, uint width, uint height)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked((ulong)width * height * bytesPerTexel);
        }

        var blockBytes = (ulong)GetBlockCompressedBlockBytes(format);
        return blockBytes == 0
            ? 0
            : checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
    }

    private static uint GetLinearTexturePitch(uint pitch, uint height, uint format)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 || height == 0)
        {
            return pitch;
        }

        // GNM linear surfaces align the row pitch to 256 bytes, so a 32px
        // RGBA8 texture is stored with a 64px (256-byte) pitch and a 288px
        // one with 320px. Reading at the unpadded width made every padded
        // tail land on the next row, which showed as transparent gaps every
        // other row on small tiles and diagonal dashes on wider surfaces.
        var pitchBytes = AlignUp((ulong)pitch * bytesPerTexel, 256UL);
        return checked((uint)(pitchBytes / bytesPerTexel));
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static void TraceShaderTranslationMiss(
        CpuContext ctx,
        SubmittedDcbState state,
        uint vertexCount,
        bool hasExportShader,
        ulong exportShaderAddress,
        bool hasPixelShader,
        ulong pixelShaderAddress,
        bool hasPsInputEna,
        uint psInputEna,
        bool hasPsInputAddr,
        uint psInputAddr,
        string? translationError = null)
    {
        var firstFailure = false;
        if (!string.IsNullOrEmpty(translationError))
        {
            lock (_submitTraceGate)
            {
                firstFailure = _tracedShaderFailures.Add(
                    (pixelShaderAddress, translationError));
            }
        }

        if (!firstFailure &&
            !ShouldTraceHotPath(ref _shaderTranslationMissTraceCount))
        {
            return;
        }

        // Translation failures are compatibility issues, not merely verbose
        // shader diagnostics. Report each distinct failure once even when AGC
        // tracing is disabled so normal runs preserve the missing opcode or
        // unsupported translation reason needed to fix the game.
        if (firstFailure)
        {
            Console.Error.WriteLine(
                $"[COMPAT][SHADER] ps=0x{pixelShaderAddress:X16} " +
                $"es=0x{exportShaderAddress:X16} error={translationError}");
        }

        if ((!hasPixelShader || !hasPsInputEna || !hasPsInputAddr) &&
            TryMarkMissingPixelShaderBindingsTrace())
        {
            TraceAgcShader(
                $"agc.shader_register_candidates " +
                DescribeShaderRegisterCandidates(ctx, state.ShRegisters));
        }

        if (!hasPixelShader)
        {
            state.CxRegisters.TryGetValue(DbDepthControl, out var rawDepthControl);
            state.CxRegisters.TryGetValue(DbZInfo, out var rawZInfo);
            state.CxRegisters.TryGetValue(DbDepthSizeXy, out var rawDepthSize);
            state.CxRegisters.TryGetValue(DbDepthView, out var rawDepthView);
            var depthState = DecodeDepthState(state.CxRegisters);
            var depthTarget = DecodeDepthTarget(state.CxRegisters);
            TraceAgcShader(
                $"agc.shader_depth_state control=0x{rawDepthControl:X8} " +
                $"zinfo=0x{rawZInfo:X8} size=0x{rawDepthSize:X8} " +
                $"view=0x{rawDepthView:X8} " +
                $"test={(depthState.TestEnable ? 1 : 0)} " +
                $"write={(depthState.WriteEnable ? 1 : 0)} " +
                $"func={depthState.CompareOp} " +
                (depthTarget is null
                    ? "target=none"
                    : $"target=0x{depthTarget.Address:X16}:" +
                      $"{depthTarget.Width}x{depthTarget.Height}:" +
                      $"fmt{depthTarget.GuestFormat}/sw{depthTarget.SwizzleMode}:" +
                      $"ro={(depthTarget.ReadOnly ? 1 : 0)}"));
        }

        var shaderDecode = string.Empty;
        if (hasExportShader && hasPixelShader)
        {
            var shouldDescribe = false;
            ulong exportShaderHeader;
            ulong pixelShaderHeader;
            lock (_submitTraceGate)
            {
                shouldDescribe = _tracedShaderDecodePairs.Add((exportShaderAddress, pixelShaderAddress));
                _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
                _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
            }

            if (shouldDescribe)
            {
                shaderDecode = $" decode={Gen5ShaderTranslator.Describe(ctx, exportShaderAddress, pixelShaderAddress)}";
                TraceAgcShader(
                    $"agc.shader_words es=0x{exportShaderAddress:X16} " +
                    Gen5ShaderTranslator.DescribeWords(ctx, exportShaderAddress));
                if (Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        exportShaderAddress,
                        exportShaderHeader,
                        state.ShRegisters,
                        SelectExportUserDataRegister(state.ShRegisters),
                        out var exportState,
                        out _,
                        userDataScalarRegisterBase: NggUserDataScalarRegisterBase) &&
                    Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        pixelShaderAddress,
                        pixelShaderHeader,
                        state.ShRegisters,
                        PsTextureUserDataRegister,
                        out var pixelState,
                        out _))
                {
                    TraceAgcShader(
                        $"agc.shader_state es=0x{exportShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(exportState));
                    TraceAgcShader(
                        $"agc.shader_state ps=0x{pixelShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(pixelState));
                    if (Gen5ShaderScalarEvaluator.TryEvaluate(
                            ctx,
                            pixelState,
                            out var evaluation,
                            out var bindingError))
                    {
                        foreach (var binding in evaluation.ImageBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_binding ps=0x{pixelShaderAddress:X16} " +
                                $"pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in evaluation.GlobalMemoryBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_global_binding ps=0x{pixelShaderAddress:X16} " +
                                $"saddr=s{binding.ScalarAddress} " +
                                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
                        }

                        if (GuestGpu.Current.TryCompilePixelShader(
                                 pixelState,
                                 evaluation,
                                 [new(0, 0, Gen5PixelOutputKind.Float)],
                                 out var compiledPixel,
                                 out var compileError,
                                 pixelInputEnable: psInputEna,
                                 pixelInputAddress: psInputAddr,
                                 storageBufferOffsetAlignment:
                                     VulkanVideoPresenter.GuestStorageBufferOffsetAlignment))
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv ps=0x{pixelShaderAddress:X16} " +
                                $"bytes={compiledPixel!.Payload.Length} bindings={evaluation.ImageBindings.Count} " +
                                $"global_buffers={evaluation.GlobalMemoryBindings.Count}");
                        }
                        else
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv_error ps=0x{pixelShaderAddress:X16} " +
                                compileError.ReplaceLineEndings(" "));
                        }
                    }
                    else
                    {
                        TraceAgcShader(
                            $"agc.shader_binding_error ps=0x{pixelShaderAddress:X16} " +
                            bindingError);
                    }
                }
            }
        }

        TraceAgcShader(
            $"agc.shader_translate_miss vertices={vertexCount} " +
            $"es={(hasExportShader ? $"0x{exportShaderAddress:X16}" : "missing")} " +
            $"ps={(hasPixelShader ? $"0x{pixelShaderAddress:X16}" : "missing")} " +
            $"ps_ena={(hasPsInputEna ? $"0x{psInputEna:X8}" : "missing")} " +
            $"ps_addr={(hasPsInputAddr ? $"0x{psInputAddr:X8}" : "missing")}" +
            (string.IsNullOrEmpty(translationError) ? string.Empty : $" error={translationError}") +
            shaderDecode);
    }

    private static bool TryMarkMissingPixelShaderBindingsTrace()
    {
        lock (_submitTraceGate)
        {
            if (_tracedMissingPixelShaderBindings)
            {
                return false;
            }

            _tracedMissingPixelShaderBindings = true;
            return true;
        }
    }

    private static string DescribeShaderRegisterCandidates(
        CpuContext ctx,
        IReadOnlyDictionary<uint, uint> registers)
    {
        var candidates = new List<(uint Register, ulong Address, ulong Header)>();
        lock (_submitTraceGate)
        {
            foreach (var (register, lo) in registers)
            {
                if (!registers.TryGetValue(register + 1, out var hi))
                {
                    continue;
                }

                var address = ((ulong)hi << 40) | ((ulong)lo << 8);
                if (address != 0 &&
                    _shaderHeadersByCode.TryGetValue(address, out var header))
                {
                    candidates.Add((register, address, header));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ',',
            candidates
                .OrderBy(candidate => candidate.Register)
                .Take(16)
                .Select(candidate =>
                {
                    var type = TryReadByte(
                        ctx,
                        candidate.Header + ShaderTypeOffset,
                        out var shaderType)
                        ? shaderType.ToString()
                        : "?";
                    return
                        $"sh[0x{candidate.Register:X}/0x{candidate.Register + 1:X}]=" +
                        $"0x{candidate.Address:X16}:type{type}";
                }));
    }

    private static bool TryGetShaderAddress(
        IReadOnlyDictionary<uint, uint> registers,
        uint loRegister,
        uint hiRegister,
        out ulong address)
    {
        address = 0;
        if (!registers.TryGetValue(loRegister, out var lo) ||
            !registers.TryGetValue(hiRegister, out var hi))
        {
            return false;
        }

        address = ((ulong)hi << 40) | ((ulong)lo << 8);
        return address != 0;
    }

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[8];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        return TryDecodeTextureDescriptor(fields.ToArray(), out descriptor);
    }

    private static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (fields.Count < 4)
        {
            return false;
        }

        // RDNA2 ISA table 45: BASE_ADDRESS is addr[47:8], WIDTH is the full
        // 16-bit field split across word1/word2, and HEIGHT is word2[29:14].
        // Keeping the high base byte is required for legal guest VAs above
        // 1 TiB; it is not descriptor metadata.
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
        // In a 256-bit 1D/2D/2D-MSAA descriptor word4[13:0] is
        // (pitch-1). A zeroed upper half denotes the common 128-bit resource,
        // where pitch is implicit; use width rather than inventing pitch=1.
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
        if (address == 0 || width == 0 || height == 0 || type is >= 1 and <= 7)
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

    private static TextureDescriptor CreateFallbackTextureDescriptor(IReadOnlyList<uint> fields)
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
            Type: Gen5TextureType2D,
            BaseLevel: 0,
            LastLevel: 0,
            Pitch: 1,
            DstSelect: 0xFAC);
    }

    private static bool TrySoftwarePresent(
        CpuContext ctx,
        TextureDescriptor source,
        int videoOutHandle,
        int displayBufferIndex)
    {
        if (source.Format != Gen5TextureFormatR8G8B8A8Unorm ||
            source.TileMode != 0 ||
            source.Type != Gen5TextureType2D ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            !VideoOutExports.TryGetDisplayBufferInfo(videoOutHandle, displayBufferIndex, out var destination) ||
            destination.Address == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            destination.Width > 8192 ||
            destination.Height > 8192 ||
            destination.TilingMode != 0 ||
            destination.PixelFormat is not (
                VideoOutPixelFormatA8R8G8B8Srgb or
                VideoOutPixelFormatA8B8G8R8Srgb or
                VideoOutPixelFormat2R8G8B8A8Srgb or
                VideoOutPixelFormat2B8G8R8A8Srgb or
                VideoOutPixelFormat2R10G10B10A2 or
                VideoOutPixelFormat2B10G10R10A2 or
                VideoOutPixelFormat2R10G10B10A2Srgb or
                VideoOutPixelFormat2B10G10R10A2Srgb or
                VideoOutPixelFormat2R10G10B10A2Bt2100Pq or
                VideoOutPixelFormat2B10G10R10A2Bt2100Pq))
        {
            return false;
        }

        var sourceByteCount = checked((ulong)source.Width * source.Height * 4);
        if (sourceByteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var sourceBytes = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(source.Address, sourceBytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(sourceBytes);
        var fingerprintKey = (source.Address, destination.Address);
        lock (_softwarePresenterGate)
        {
            if (_softwarePresenterFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return true;
            }
        }

        var destinationPitch = destination.PitchInPixel == 0
            ? destination.Width
            : destination.PitchInPixel;
        if (destinationPitch < destination.Width)
        {
            return false;
        }

        var destinationRow = new byte[checked((int)destinationPitch * 4)];
        var rgbaDestination = destination.PixelFormat is
            VideoOutPixelFormatA8B8G8R8Srgb or
            VideoOutPixelFormat2R8G8B8A8Srgb;
        var packed10Destination =
            VideoOutExports.IsPacked10BitPixelFormat(destination.PixelFormat);
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (packed10Destination)
                {
                    if (!VideoOutExports.TryPackRgba8Pixel(
                            destination.PixelFormat,
                            sourceBytes[sourceOffset + 0],
                            sourceBytes[sourceOffset + 1],
                            sourceBytes[sourceOffset + 2],
                            sourceBytes[sourceOffset + 3],
                            out var packed))
                    {
                        return false;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destinationRow.AsSpan(destinationOffset, sizeof(uint)),
                        packed);
                }
                else if (rgbaDestination)
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 0];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 2];
                }
                else
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 0];
                }

                if (!packed10Destination)
                {
                    destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
                }
            }

            var destinationAddress = destination.Address + ((ulong)y * destinationPitch * 4);
            if (!ctx.Memory.TryWrite(destinationAddress, destinationRow))
            {
                return false;
            }
        }

        lock (_softwarePresenterGate)
        {
            _softwarePresenterFingerprints[fingerprintKey] = fingerprint;
        }

        VideoOutExports.SubmitHostRgbaFrame(sourceBytes, source.Width, source.Height);
        TraceAgc(
            $"agc.software_presenter src=0x{source.Address:X16} {source.Width}x{source.Height} fmt={source.Format}/num{source.NumberType} " +
            $"dst=0x{destination.Address:X16} {destination.Width}x{destination.Height} fingerprint=0x{fingerprint:X16}");
        return true;
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }

    private static bool PatchShaderProgramRegisters(CpuContext ctx, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType) ||
            !TryReadByte(ctx, headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return false;
        }

        if (shRegistersAddress == 0 || registerCount < 2)
        {
            return false;
        }

        if (!TryReadUInt32(ctx, shRegistersAddress, out var loRegister) ||
            !TryReadUInt32(ctx, shRegistersAddress + 8, out var hiRegister))
        {
            return false;
        }

        var expectedLo = shaderType switch
        {
            0 => ComputePgmLo,
            1 => SpiShaderPgmLoPs,
            2 or 6 => SpiShaderPgmLoEs,
            4 => SpiShaderPgmLoGs,
            7 => SpiShaderPgmLoLs,
            _ => 0u,
        };
        var expectedHi = shaderType switch
        {
            0 => ComputePgmHi,
            1 => SpiShaderPgmHiPs,
            2 or 6 => SpiShaderPgmHiEs,
            4 => SpiShaderPgmHiGs,
            7 => SpiShaderPgmHiLs,
            _ => 0u,
        };
        if (expectedLo == 0 || loRegister != expectedLo || hiRegister != expectedHi)
        {
            TraceCreateShader(0, headerAddress, codeAddress, $"unexpected-registers type={shaderType} lo=0x{loRegister:X8} hi=0x{hiRegister:X8}");
            return false;
        }

        var loValue = (uint)((codeAddress >> 8) & 0xFFFF_FFFFUL);
        var hiValue = (uint)((codeAddress >> 40) & 0xFFUL);
        return TryWriteUInt32(ctx, shRegistersAddress + sizeof(uint), loValue) &&
               TryWriteUInt32(ctx, shRegistersAddress + 8 + sizeof(uint), hiValue);
    }

    private static bool IsEsGeometryShaderType(byte shaderType) =>
        shaderType is 2 or 6;

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PatchWriteDataControlByte(CpuContext ctx, int byteIndex)
    {
        if (!TryResolveWriteDataPatchArguments(
                ctx,
                ctx[CpuRegister.Rdi],
                ctx[CpuRegister.Rsi],
                out var commandAddress,
                out var value) ||
            !TryReadUInt32(ctx, commandAddress + 4, out var control))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var shift = byteIndex * 8;
        var patchedControl = (control & ~(0xFFu << shift)) | (((uint)value & 0xFFu) << shift);
        return TryWriteUInt32(ctx, commandAddress + 4, patchedControl)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryResolveWriteDataPatchArguments(
        CpuContext ctx,
        ulong first,
        ulong second,
        out ulong commandAddress,
        out ulong value)
    {
        if (IsWriteDataPacket(ctx, first))
        {
            commandAddress = first;
            value = second;
            return true;
        }

        if (IsWriteDataPacket(ctx, second))
        {
            commandAddress = second;
            value = first;
            return true;
        }

        commandAddress = 0;
        value = 0;
        return false;
    }

    private static bool IsWriteDataPacket(CpuContext ctx, ulong commandAddress)
    {
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return false;
        }

        return op == ItWriteData || (op == ItNop && register == RWriteData);
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static bool TryAllocateCommandDwords(CpuContext ctx, ulong commandBufferAddress, uint sizeDwords, out ulong commandAddress)
    {
        commandAddress = 0;
        if (sizeDwords == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferUserDataOffset, out var userData) ||
            !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var remainingDwords = GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords);
        if (sizeDwords > remainingDwords)
        {
            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            var scheduler = GuestThreadExecution.Scheduler;
            ulong callbackResult = 0;
            string? callbackError = null;
            if (callback == 0 ||
                scheduler is null ||
                !scheduler.TryCallGuestFunction(
                    ctx,
                    callback,
                    commandBufferAddress,
                    (ulong)sizeDwords + reservedDwords,
                    userData,
                    0,
                    0,
                    "agc_command_buffer_full",
                    out callbackResult,
                    out callbackError))
            {
                TraceAgc(
                    $"agc.cmd_alloc_callback_failed buf=0x{commandBufferAddress:X16} " +
                    $"callback=0x{callback:X16} result=0x{callbackResult:X16} " +
                    $"error={callbackError ?? "none"}");
                return false;
            }

            TraceAgc(
                $"agc.cmd_alloc_callback_complete buf=0x{commandBufferAddress:X16} " +
                $"callback=0x{callback:X16} result=0x{callbackResult:X16}");

            if (!TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out cursorUp) ||
                !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out cursorDown) ||
                !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out reservedDwords) ||
                sizeDwords > GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords))
            {
                TraceAgc($"agc.cmd_alloc_callback_no_space buf=0x{commandBufferAddress:X16} need={sizeDwords}");
                return false;
            }
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!ctx.TryWriteUInt64(commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static uint GetRemainingCommandDwords(
        ulong cursorUp,
        ulong cursorDown,
        uint reservedDwords)
    {
        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        return availableDwords > reservedDwords
            ? (uint)availableDwords - reservedDwords
            : 0;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!TryReadUInt32(ctx, sourceAddress, out var offset) ||
            !TryReadUInt32(ctx, sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return TryWriteUInt32(ctx, destinationAddress, offset) &&
               TryWriteUInt32(ctx, destinationAddress + sizeof(uint), value);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!TryReadUInt64(ctx, fieldAddress, out var relativeAddress))
        {
            return false;
        }

        if (relativeAddress == 0)
        {
            return true;
        }

        return ctx.TryWriteUInt64(fieldAddress, fieldAddress + relativeAddress);
    }

    private static int ReturnRegisterDefaults(CpuContext ctx, bool internalDefaults)
    {
        var version = (uint)ctx[CpuRegister.Rdi];
        if (!IsSupportedRegisterDefaultsVersion(version))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryGetRegisterDefaultsAllocation(ctx, out var allocation))
        {
            return ReturnPointer(ctx, 0);
        }

        var address = internalDefaults ? allocation.Internal : allocation.Primary;
        TraceAgc($"agc.get_register_defaults internal={internalDefaults} version={version} address=0x{address:X16}");
        return ReturnPointer(ctx, address);
    }

    private static bool IsSupportedRegisterDefaultsVersion(uint version)
    {
        return version is
            RegisterDefaultsVersion7 or
            RegisterDefaultsVersion8 or
            RegisterDefaultsVersion10 or
            RegisterDefaultsVersion13;
    }

    private static bool TryGetRegisterDefaultsAllocation(
        CpuContext ctx,
        out RegisterDefaultsAllocation allocation)
    {
        lock (_registerDefaultsGate)
        {
            if (_registerDefaultsAllocations.TryGetValue(ctx.Memory, out allocation!))
            {
                return true;
            }

            if (!TryBuildRegisterDefaults(
                    ctx,
                    PrimaryRegisterDefaults,
                    cxTableLength: 78,
                    shTableLength: 29,
                    ucTableLength: 20,
                    out var primaryAddress) ||
                !TryBuildRegisterDefaults(
                    ctx,
                    InternalRegisterDefaults,
                    cxTableLength: 4,
                    shTableLength: 15,
                    ucTableLength: 3,
                    out var internalAddress))
            {
                allocation = null!;
                return false;
            }

            allocation = new RegisterDefaultsAllocation(primaryAddress, internalAddress);
            _registerDefaultsAllocations.Add(ctx.Memory, allocation);
            return true;
        }
    }

    private static bool TryBuildRegisterDefaults(
        CpuContext ctx,
        RegisterDefaultGroup[] groups,
        int cxTableLength,
        int shTableLength,
        int ucTableLength,
        out ulong address)
    {
        var cxTableOffset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var shTableOffset = cxTableOffset + (cxTableLength * sizeof(ulong));
        var ucTableOffset = shTableOffset + (shTableLength * sizeof(ulong));
        var typesOffset = AlignUp(ucTableOffset + (ucTableLength * sizeof(ulong)), sizeof(uint));
        var registerBlocksOffset = AlignUp(typesOffset + (groups.Length * 3 * sizeof(uint)), sizeof(ulong));
        var blobLength = registerBlocksOffset + (groups.Length * RegisterDefaultBlockSize);

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteBlobUInt64(blob, 0x00, address + (ulong)cxTableOffset);
        WriteBlobUInt64(blob, 0x08, address + (ulong)shTableOffset);
        WriteBlobUInt64(blob, 0x10, address + (ulong)ucTableOffset);
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)groups.Length);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Registers.Length > 16)
            {
                return false;
            }

            var tableOffset = group.Space switch
            {
                0 => cxTableOffset,
                1 => shTableOffset,
                2 => ucTableOffset,
                _ => -1,
            };
            var tableLength = group.Space switch
            {
                0 => cxTableLength,
                1 => shTableLength,
                2 => ucTableLength,
                _ => 0,
            };
            if (tableOffset < 0 || group.Index >= tableLength)
            {
                return false;
            }

            var registerBlockOffset = registerBlocksOffset + (groupIndex * RegisterDefaultBlockSize);
            WriteBlobUInt64(
                blob,
                tableOffset + ((int)group.Index * sizeof(ulong)),
                address + (ulong)registerBlockOffset);

            var typeEntryOffset = typesOffset + (groupIndex * 3 * sizeof(uint));
            WriteBlobUInt32(blob, typeEntryOffset, group.Type);
            WriteBlobUInt32(blob, typeEntryOffset + sizeof(uint), (group.Index * 4) + group.Space);

            for (var registerIndex = 0; registerIndex < group.Registers.Length; registerIndex++)
            {
                var register = group.Registers[registerIndex];
                var registerOffset = registerBlockOffset + (registerIndex * 2 * sizeof(uint));
                WriteBlobUInt32(blob, registerOffset, register.Offset);
                WriteBlobUInt32(blob, registerOffset + sizeof(uint), register.Value);
            }
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private static void WriteBlobUInt32(Span<byte> blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob[offset..], value);

    private static void WriteBlobUInt64(Span<byte> blob, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(blob[offset..], value);

    private static int ReturnPointer(CpuContext ctx, ulong pointer)
    {
        ctx[CpuRegister.Rax] = pointer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private static uint Pm4Length(uint header) =>
        ((header >> 16) & 0x3FFFu) + 2u;

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    // A submitted command buffer is bulk-copied once per submit and served
    // from this thread-local window: the previous per-dword reads each took
    // the guest-memory reader lock and ran a region binary search, which
    // dominated submit parsing (thousands of locked 4-byte reads per DCB).
    [ThreadStatic]
    private static byte[]? _dcbWindowBuffer;
    [ThreadStatic]
    private static ulong _dcbWindowStart;
    [ThreadStatic]
    private static int _dcbWindowByteLength;

    /// <summary>
    /// Drops the bulk-read window when a self-patching command buffer writes
    /// into its own bytes during parse, so subsequent reads see live guest
    /// memory instead of the pre-write snapshot. Self-patching is rare, so
    /// paying live-read cost for the rest of that one submit is acceptable.
    /// </summary>
    private static void InvalidateDcbWindowIfOverlaps(ulong address, ulong length)
    {
        if (_dcbWindowBuffer is null || length == 0)
        {
            return;
        }

        var windowEnd = _dcbWindowStart + (ulong)_dcbWindowByteLength;
        if (address < windowEnd && address + length > _dcbWindowStart)
        {
            _dcbWindowBuffer = null;
            _dcbWindowByteLength = 0;
        }
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        if (_dcbWindowBuffer is { } window &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(uint) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            return true;
        }

        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        if (_dcbWindowBuffer is { } window &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(ulong) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            return true;
        }

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadGuestCString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out byte[] bytes)
    {
        if (address == 0)
        {
            bytes = [];
            return true;
        }

        var values = new List<byte>(Math.Min(maximumLength, 128));
        for (var index = 0; index < maximumLength; index++)
        {
            if (!TryReadByte(ctx, address + (ulong)index, out var value))
            {
                bytes = [];
                return false;
            }

            if (value == 0)
            {
                bytes = [.. values];
                return true;
            }

            values.Add(value);
        }

        bytes = [];
        return false;
    }

    private static bool TryGetPacketIdentity(
        CpuContext ctx,
        ulong commandAddress,
        out uint op,
        out uint register)
    {
        op = 0;
        register = 0;
        if (commandAddress == 0 || !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return false;
        }

        op = (header >> 8) & 0xFFu;
        register = (header >> 2) & 0x3Fu;
        return true;
    }

    private static bool TryCopyGuestMemory(
        CpuContext ctx,
        ulong sourceAddress,
        ulong destinationAddress,
        uint byteCount)
    {
        if (sourceAddress == destinationAddress)
        {
            return true;
        }

        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        ulong offset = 0;
        while (offset < byteCount)
        {
            var chunkLength = (int)Math.Min((ulong)buffer.Length, byteCount - offset);
            var chunk = buffer.AsSpan(0, chunkLength);
            if (!ctx.Memory.TryRead(sourceAddress + offset, chunk) ||
                !ctx.Memory.TryWrite(destinationAddress + offset, chunk))
            {
                return false;
            }

            offset += (uint)chunkLength;
        }

        return true;
    }

    private static bool TryFillGuestMemory(
        CpuContext ctx,
        uint value,
        ulong destinationAddress,
        uint byteCount)
    {
        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            var remaining = Math.Min(sizeof(uint), buffer.Length - offset);
            encoded[..remaining].CopyTo(buffer.AsSpan(offset, remaining));
        }

        ulong destinationOffset = 0;
        while (destinationOffset < byteCount)
        {
            var chunkLength = (int)Math.Min(
                (ulong)buffer.Length,
                byteCount - destinationOffset);
            if (!ctx.Memory.TryWrite(
                    destinationAddress + destinationOffset,
                    buffer.AsSpan(0, chunkLength)))
            {
                return false;
            }

            destinationOffset += (uint)chunkLength;
        }

        return true;
    }

    private static bool ShouldTraceHotPath(ref long counter)
    {
        var count = Interlocked.Increment(ref counter);
        return count <= 8 || count % 100_000 == 0;
    }

    // Interpolated-string handlers gated on the trace flags: when tracing is
    // off (the normal case) the compiler skips every AppendFormatted call, so
    // the interpolation never runs. These functions are on the hottest guest
    // paths — e.g. AddIndirectPatchRegisters fires tens of thousands of times
    // per second — and previously formatted a discarded string every call.
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgc;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcShaderTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcShaderTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgcShader;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    private static void TraceAgc(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcTraceHandler message)
    {
        if (_traceAgc)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgc(string message)
    {
        if (!_traceAgc)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }

    private static void TraceAgcShader(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcShaderTraceHandler message)
    {
        if (_traceAgcShader)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgcShader(string message)
    {
        if (!_traceAgcShader)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
    }

    private static string FormatShaderDwords(IReadOnlyList<uint> values) =>
        values.Count == 0
            ? "none"
            : string.Join(',', values.Select(static value => $"{value:X8}"));

    private static string FormatTextureDescriptor(TextureDescriptor descriptor) =>
        $"addr=0x{descriptor.Address:X16} {descriptor.Width}x{descriptor.Height} " +
        $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
        $"type={descriptor.Type} depth={descriptor.Depth} base_array={descriptor.BaseArray} " +
        $"levels={descriptor.BaseLevel}-{descriptor.LastLevel}/max{descriptor.MaxMip} " +
        $"pitch={descriptor.Pitch} array_pitch={descriptor.ArrayPitch} " +
        $"lod={descriptor.MinLod:X3}/{descriptor.MinLodWarn:X3} " +
        $"bc={descriptor.BcSwizzle} meta=0x{descriptor.MetadataAddress:X16} " +
        $"flags=0x{descriptor.DescriptorFlags:X6} dst=0x{descriptor.DstSelect:X3}";

    private static ulong? ParseOptionalHexAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(
            span,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var address)
            ? address
            : null;
    }

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

        var directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
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

    private static void TraceCreateShader(ulong destinationAddress, ulong headerAddress, ulong codeAddress, string detail)
    {
        var isOk = string.Equals(detail, "ok", StringComparison.Ordinal);
        if (isOk &&
            (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) ||
             !ShouldTraceHotPath(ref _createShaderTraceCount)))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.create_shader dst=0x{destinationAddress:X16} header=0x{headerAddress:X16} code=0x{codeAddress:X16} {detail}");
    }

    [SysAbiExport(
        Nid = "xSAR0LTcRKM",
        ExportName = "sceAgcDcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJump(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var target = ctx[CpuRegister.Rsi];
        var sizeDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItIndirectBuffer, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 12, sizeDwords & 0xFFFFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "bbFueFP+J4k",
        ExportName = "sceAgcDcbSetPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetPredication(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 3, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(3, ItNop, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(address & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "w6Dj1VJt5qY",
        ExportName = "sceAgcSetPacketPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetPacketPredication(CpuContext ctx)
    {
        // Global predication toggle on a packet; a no-op is safe for rendering.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ABI (reversed from Quake): rdi = array of DCB base addresses (u64 each),
    // rsi = array of DCB sizes in dwords (u32 each), rdx = buffer count.
    [SysAbiExport(
        Nid = "6UzEidRZwkg",
        ExportName = "sceAgcDriverSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitMultiDcbs(CpuContext ctx)
    {
        var addressArray = ctx[CpuRegister.Rdi];
        var sizeArray = ctx[CpuRegister.Rsi];
        var bufferCount = (uint)ctx[CpuRegister.Rdx];
        if (addressArray == 0 || sizeArray == 0 || bufferCount == 0 || bufferCount > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal);

        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            Gen5ShaderScalarEvaluator.BeginGlobalMemoryReadScope();
            try
            {
                for (uint i = 0; i < bufferCount; i++)
                {
                    if (!ctx.TryReadUInt64(addressArray + i * 8, out var commandAddress) ||
                        commandAddress == 0 ||
                        !ctx.TryReadUInt32(sizeArray + i * 4, out var dwordCount) ||
                        dwordCount == 0)
                    {
                        continue;
                    }

                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.driver_submit_multi_dcbs index={i}/{bufferCount} " +
                            $"addr=0x{commandAddress:X16} dwords={dwordCount}");
                    }

                    ParseSubmittedDcb(ctx, gpuState, gpuState.Graphics, commandAddress, dwordCount, tracePackets);
                }

                DrainResumableDcbs(ctx, gpuState, tracePackets);
            }
            finally
            {
                Gen5ShaderScalarEvaluator.EndGlobalMemoryReadScope();
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AOLcoIkQDgM",
        ExportName = "sceAgcDriverQueryResourceRegistrationUserMemoryRequirements",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverQueryResourceRegistrationUserMemoryRequirements(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rdi];
        var resourceCount = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (sizeAddress == 0 || resourceCount == 0 || ownerCount == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong requiredSize;
        try
        {
            requiredSize = checked(
                resourceCount * ResourceRegistrationBytesPerResource +
                ownerCount * ResourceRegistrationBytesPerOwner);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(sizeAddress, requiredSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_query_resource_registration_memory resources={resourceCount} " +
            $"owners={ownerCount} bytes=0x{requiredSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "F0Y42t-3e18",
        ExportName = "sceAgcDriverInitResourceRegistration",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverInitResourceRegistration(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rdi];
        var memorySize = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (memoryAddress == 0 || memorySize == 0 || ownerCount == 0 || ownerCount > uint.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.ResourceRegistrationInitialized = true;
            state.ResourceRegistrationMemory = memoryAddress;
            state.ResourceRegistrationMemorySize = memorySize;
            state.ResourceRegistrationMaxOwners = (uint)ownerCount;
            state.ResourceOwners.Clear();
            state.RegisteredResources.Clear();
            state.DefaultOwner = DefaultAgcOwner;
            state.NextOwner = 1;
            state.NextResource = 1;
        }

        TraceAgc(
            $"agc.driver_init_resource_registration memory=0x{memoryAddress:X16} " +
            $"bytes=0x{memorySize:X} owners={ownerCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "U9ueyEhSkF4",
        ExportName = "sceAgcDriverRegisterDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterDefaultOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.DefaultOwner = owner;
        }

        TraceAgc($"agc.driver_register_default_owner owner={owner}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "X-Nm5KLREeg",
        ExportName = "sceAgcDriverRegisterOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (ownerAddress == 0 || nameAddress == 0 ||
            !TryReadGuestCString(
                ctx,
                nameAddress,
                ResourceRegistrationMaxNameLength,
                out var nameBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        uint owner;
        lock (state.Gate)
        {
            if (!state.ResourceRegistrationInitialized ||
                state.ResourceRegistrationMaxOwners != 0 &&
                state.ResourceOwners.Count >= state.ResourceRegistrationMaxOwners)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            owner = state.NextOwner;
            while (owner == state.DefaultOwner || state.ResourceOwners.ContainsKey(owner))
            {
                owner++;
                if (owner == 0)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                }
            }

            state.NextOwner = owner + 1;
            state.ResourceOwners.Add(owner, System.Text.Encoding.UTF8.GetString(nameBytes));
        }

        if (!ctx.TryWriteUInt32(ownerAddress, owner))
        {
            lock (state.Gate)
            {
                state.ResourceOwners.Remove(owner);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_register_owner out=0x{ownerAddress:X16} owner={owner} " +
            $"name={System.Text.Encoding.UTF8.GetString(nameBytes)}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pWLG7WOpVcw",
        ExportName = "sceAgcDriverUnregisterResource",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterResource(CpuContext ctx)
    {
        var resourceHandle = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            if (!state.RegisteredResources.Remove(resourceHandle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        TraceAgc($"agc.driver_unregister_resource handle={resourceHandle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
