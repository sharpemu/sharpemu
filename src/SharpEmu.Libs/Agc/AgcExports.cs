// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static class AgcExports
{
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
    private const uint ItEventWrite = 0x46;
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
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint ComputePgmRsrc2 = 0x213;
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
    private const uint CbBlendRed = 0x105;
    private const uint CbBlendGreen = 0x106;
    private const uint CbBlendBlue = 0x107;
    private const uint CbBlendAlpha = 0x108;
    private const uint CbColorControl = 0x202;
    private const uint CbColor0Base = 0x318;
    private const uint CbColorRegisterStride = 15;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    private const uint CbBlend0Control = 0x1E0;
    private const uint PaScModeCntl0 = 0x292;
    private const int ColorTargetCount = 8;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint NggUserDataScalarRegisterBase = 8;
    private const uint Gen5TextureFormatR8G8B8A8Unorm = 56;
    private const uint Gen5TextureFormatR16G16B16A16Float = 71;
    private const uint Gen5TextureType2D = 9;
    private const ulong MaxPresentedTextureBytes = 128UL * 1024UL * 1024UL;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong VideoOutPixelFormatB8G8R8A8Unorm = 0x8100000000000000;
    private const ulong VideoOutPixelFormatR8G8B8A8Unorm = 0x8100000022000000;
    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion10 = 10;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
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
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();
    private static readonly Dictionary<(ulong Ps, ulong State, Gen5PixelOutputKind Output), byte[]> _pixelSpirvCache = new();
    private static readonly Dictionary<
        (ulong Es, ulong EsState, ulong Ps, ulong PsState, Gen5PixelOutputKind Output),
        (byte[] Vertex, byte[] Pixel)> _graphicsSpirvCache = new();
    private static readonly Dictionary<
        (ulong Cs, ulong State, uint LocalX, uint LocalY, uint LocalZ),
        byte[]> _computeSpirvCache = new();
    private static readonly Dictionary<ulong, ulong> _shaderHeadersByCode = new();
    private static readonly bool _traceAgc = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcShader =
        _traceAgc ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
            "1",
            StringComparison.Ordinal);
    private static long _dcbWriteDataTraceCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _createShaderTraceCount;
    private static long _packetPayloadTraceCount;
    private static bool _tracedMissingPixelShaderBindings;
    private static long _unsatisfiedWaitTraceCount;
    private static long _shaderTranslationMissTraceCount;
    private static long _translatedDrawTraceCount;
    private static long _standardDmaTraceCount;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static readonly Dictionary<(ulong Shader, ulong Source, ulong Destination), ulong> _softwareComputeBlitFingerprints = new();
    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, RegisterDefaultsAllocation> _registerDefaultsAllocations = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
    [
        new(0, 0, 0xE24F806D, [new(CbColorControl, 0x00CC0010)]),
        new(0, 3, 0x0BC65DA4, [new(0x08F, 0)]),
        new(0, 4, 0x9E5AD592, [new(0x08E, 0)]),
        new(0, 12, 0x6DE4C312, [new(0x203, 0)]),
        new(0, 28, 0x1EB8D73A, [new(PaScModeCntl0, 0x00000002)]),
        new(0, 31, 0xA20EFC70, [new(PaScWindowOffset, 0)]),
        new(0, 58, 0x43FBD769,
        [
            new(CbBlendRed, 0),
            new(CbBlendBlue, 0),
            new(CbBlendGreen, 0),
            new(CbBlendAlpha, 0),
        ]),
        new(0, 59, 0xEF550356, [new(CbBlend0Control, 0x20010001)]),
        new(0, 67, 0x918106BB,
        [
            new(PaScGenericScissorTl, 0x80000000),
            new(PaScGenericScissorBr, 0x40004000),
        ]),
        new(0, 72, 0x38E92C91,
        [
            new(0x318, 0),
            new(0x31B, 0),
            new(0x31C, 0),
            new(0x31D, 0),
            new(0x31E, 0x48),
            new(0x31F, 0),
            new(0x321, 0),
            new(0x323, 0),
            new(0x324, 0),
            new(0x325, 0),
            new(0x390, 0),
            new(0x398, 0),
            new(0x3A0, 0),
            new(0x3A8, 0),
            new(0x3B0, 0),
            new(0x3B8, 0x0006C000),
        ]),
        new(0, 73, 0x0B177B43, [new(0x00C, 0), new(0x00D, 0x40004000)]),
        new(0, 74, 0x48531062, [new(0x191, 0)]),
        new(0, 76, 0x7690AF6F,
        [
            new(0x10F, 0x4E7E0000),
            new(0x111, 0x4E7E0000),
            new(0x113, 0x4E7E0000),
            new(0x110, 0),
            new(0x112, 0),
            new(0x114, 0),
            new(0x094, 0x80000000),
            new(0x095, 0x40004000),
            new(0x0B4, 0),
            new(0x0B5, 0),
        ]),
        new(0, 77, 0x078D7060,
        [
            new(PaScWindowScissorTl, 0x80000000),
            new(PaScWindowScissorBr, 0x40004000),
        ]),
        new(1, 13, 0xC918DF3E, [new(0x20C, 0), new(0x20D, 0)]),
        new(1, 14, 0xC9751C9C, [new(0x0C8, 0), new(0x0C9, 0)]),
        new(1, 18, 0xC9E01B31, [new(0x008, 0), new(0x009, 0)]),
        new(2, 3, 0x105971C2, [new(0x25B, 0)]),
        new(2, 7, 0x40D49AD1, [new(0x262, 0)]),
        new(2, 12, 0x9EBFAB10, [new(0x242, 0)]),
    ];

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
        uint DstSelect)
    {
        public uint MipLevels
        {
            get
            {
                var largestDimension = Math.Max(Width, Height);
                uint maximumMipLevels = 1;
                while (largestDimension > 1)
                {
                    largestDimension >>= 1;
                    maximumMipLevels++;
                }

                var descriptorMipLevels = LastLevel >= BaseLevel
                    ? LastLevel - BaseLevel + 1
                    : 1;
                return Math.Min(descriptorMipLevels, maximumMipLevels);
            }
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
        byte[] VertexSpirv,
        byte[] PixelSpirv,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        VulkanGuestIndexBuffer? IndexBuffer,
        IReadOnlyList<TranslatedImageBinding> Textures,
        IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
        IReadOnlyList<Gen5VertexInputBinding> VertexInputs,
        IReadOnlyList<RenderTargetDescriptor> RenderTargets,
        VulkanGuestRenderState RenderState);

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
        uint GroupCountZ);

    private sealed class SubmittedDcbState
    {
        public Dictionary<uint, uint> CxRegisters { get; } = new();
        public Dictionary<uint, uint> ShRegisters { get; } = new();
        public Dictionary<uint, uint> UcRegisters { get; } = new();
        public TextureDescriptor? PresenterTexture { get; set; }
        public GuestDrawKind GuestDrawKind { get; set; }
        public TranslatedGuestDraw? TranslatedDraw { get; set; }
        public Dictionary<ulong, RenderTargetWriter> RenderTargetWriters { get; } = new();
        public ulong IndirectArgsAddress { get; set; }
        public bool SawIndexedDraw { get; set; }
        public ulong IndexBufferAddress { get; set; }
        public uint IndexBufferCount { get; set; }
        public uint IndexSize { get; set; }
        public uint InstanceCount { get; set; } = 1;
        public uint DrawIndexOffset { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
        public Dictionary<ulong, ComputeImageWriter> ComputeImageWriters { get; } = new();
        public ulong WorkSequence { get; set; }
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

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

        if (!ctx.TryReadUInt32(headerAddress, out var fileHeader) ||
            !ctx.TryReadUInt32(headerAddress + sizeof(uint), out var version))
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

        if (!ctx.TryReadUInt64(headerAddress + ShaderUserDataOffset, out var userDataAddress))
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

        if (!ctx.TryReadByte(geometryShaderAddress + ShaderTypeOffset, out var shaderType) || !IsEsGeometryShaderType(shaderType) ||
            !ctx.TryReadUInt64(geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtShaderStagesEnOffset, cxRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset, cxRegistersAddress + 8) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeUserVgprEnOffset, ucRegistersAddress + 8) ||
            !ctx.TryWriteUInt32(ucRegistersAddress + 16, VgtPrimitiveType) ||
            !ctx.TryWriteUInt32(ucRegistersAddress + 20, primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} gs=0x{geometryShaderAddress:X16} type={shaderType} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

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

        if (!ctx.TryReadUInt64(geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !ctx.TryReadUInt32(geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0 &&
            (!ctx.TryReadUInt64(pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
             !ctx.TryReadUInt32(pixelShaderAddress + ShaderNumInputSemanticsOffset, out _)))
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
                    ctx.TryReadUInt32(inputSemanticsAddress + (i * sizeof(uint)), out var inputSemantic))
                {
                    flat = ((inputSemantic >> 22) & 0x1) != 0;
                }

                value = i | (flat ? 0x400u : 0u);
            }

            var destination = registersAddress + (i * 8);
            if (!ctx.TryWriteUInt32(destination, SpiPsInputCntl0 + i) ||
                !ctx.TryWriteUInt32(destination + sizeof(uint), value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAgc($"agc.create_interpolant_mapping regs=0x{registersAddress:X16} gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

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
            if (!ctx.TryReadUInt32(commandAddress, out var header))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!ctx.TryWriteUInt32(commandAddress + ((ulong)index * sizeof(uint)), 0))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, groupCountX) ||
            !ctx.TryWriteUInt32(commandAddress + 8, groupCountY) ||
            !ctx.TryWriteUInt32(commandAddress + 12, groupCountZ) ||
            !ctx.TryWriteUInt32(commandAddress + 16, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

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
            if (!ctx.TryReadUInt32(entryAddress, out var offset) ||
                !ctx.TryReadUInt32(entryAddress + sizeof(uint), out var value))
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
                !ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !ctx.TryWriteUInt32(commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!ctx.TryWriteUInt32(
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, 0))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItEventWrite, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, hasAddress ? eventType | 0x100u : eventType & 0x3Fu))
        {
            return ReturnPointer(ctx, 0);
        }

        if (hasAddress &&
            (!ctx.TryWriteUInt32(commandAddress + 8, (uint)eventAddress & ~7u) ||
             !ctx.TryWriteUInt32(commandAddress + 12, (uint)(eventAddress >> 32))))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, 0x8000_0000u) ||
            !ctx.TryWriteUInt32(commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !ctx.TryWriteUInt32(commandAddress + 20, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 24, pollCycles / 40) ||
            !ctx.TryWriteUInt32(commandAddress + 28, gcrControl))
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
        if (!ctx.TryReadUInt64(stackAddress + sizeof(ulong), out var mask) ||
            !ctx.TryReadUInt32(stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, (uint)address) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)(address >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!ctx.TryWriteUInt32(commandAddress + 16, compareFunction) ||
                !ctx.TryWriteUInt32(commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!ctx.TryWriteUInt32(commandAddress + 16, (uint)(mask >> 32)) ||
                 !ctx.TryWriteUInt32(commandAddress + 20, (uint)reference) ||
                 !ctx.TryWriteUInt32(commandAddress + 24, (uint)(reference >> 32)) ||
                 !ctx.TryWriteUInt32(commandAddress + 28, compareFunction) ||
                 !ctx.TryWriteUInt32(commandAddress + 32, pollCycles / 40))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, (uint)argumentsAddress) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (modifier & 0xA038u) | 0x41u))
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
            !ctx.TryWriteUInt32(markerAddress, Pm4(2, ItNop, RZero)) ||
            !ctx.TryWriteUInt32(markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, valueCount + 2, out var commandAddress) ||
            !ctx.TryWriteUInt32(commandAddress, Pm4(valueCount + 2, ItSetShReg, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !ctx.TryReadUInt32(valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!ctx.TryWriteUInt32(commandAddress + 8 + (i * sizeof(uint)), value))
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
        if (!ctx.TryReadUInt64(stackAddress + 8, out var dataSelectionRaw) ||
            !ctx.TryReadUInt64(stackAddress + 16, out var data) ||
            !ctx.TryReadUInt64(stackAddress + 24, out var gdsOffsetRaw) ||
            !ctx.TryReadUInt64(stackAddress + 32, out var gdsSizeRaw) ||
            !ctx.TryReadUInt64(stackAddress + 40, out var interruptRaw) ||
            !ctx.TryReadUInt64(stackAddress + 48, out var interruptContextIdRaw))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, action | (cachePolicy << 8)) ||
            !ctx.TryWriteUInt32(
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)destinationAddress) ||
            !ctx.TryWriteUInt32(commandAddress + 16, (uint)(destinationAddress >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 20, (uint)data) ||
            !ctx.TryWriteUInt32(commandAddress + 24, (uint)(data >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 28, interruptContextId))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, 0))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItIndexType, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, instanceCount))
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
            !ctx.TryWriteUInt32(baseCommand, Pm4(3, ItIndexBase, 0)) ||
            !ctx.TryWriteUInt32(baseCommand + 4, (uint)indexAddress) ||
            !ctx.TryWriteUInt32(baseCommand + 8, (uint)(indexAddress >> 32)) ||
            !ctx.TryWriteUInt32(baseCommand + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !ctx.TryWriteUInt32(baseCommand + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var drawCommand) ||
            !ctx.TryWriteUInt32(drawCommand, Pm4(5, ItDrawIndex2, 0)) ||
            !ctx.TryWriteUInt32(drawCommand + 4, indexCount) ||
            !ctx.TryWriteUInt32(drawCommand + 8, 0) ||
            !ctx.TryWriteUInt32(drawCommand + 12, 0) ||
            !ctx.TryWriteUInt32(drawCommand + 16, 0))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, indexCount) ||
            !ctx.TryWriteUInt32(commandAddress + 8, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 12, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 16, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 20, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
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
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, control) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 16, packetControl))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItEventWrite, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, eventType))
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
        if (!ctx.TryReadUInt32(ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, (engine << 31) | cbDbOp) ||
            !ctx.TryWriteUInt32(commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !ctx.TryWriteUInt32(commandAddress + 20, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 24, pollCycles / 40) ||
            !ctx.TryWriteUInt32(commandAddress + 28, gcrControl))
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
        if (!ctx.TryReadUInt64(stackAddress + sizeof(ulong), out var incrementRaw) ||
            !ctx.TryReadUInt64(stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !ctx.TryWriteUInt32(
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)destinationAddress) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!ctx.TryReadUInt32(dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !ctx.TryWriteUInt32(commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
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
        if (!ctx.TryReadUInt64(stackAddress + sizeof(ulong), out var reference) ||
            !ctx.TryReadUInt64(stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !ctx.TryReadUInt32(stackAddress + (3 * sizeof(ulong)), out var pollCycles))
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
            if (!ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItWaitRegMem, 0)) ||
                !ctx.TryWriteUInt32(commandAddress + 4, compareFunction | ((operation & 1) << 8)) ||
                !ctx.TryWriteUInt32(commandAddress + 8, (uint)address) ||
                !ctx.TryWriteUInt32(commandAddress + 12, (uint)(address >> 32)) ||
                !ctx.TryWriteUInt32(commandAddress + 16, (uint)reference) ||
                !ctx.TryWriteUInt32(commandAddress + 20, (uint)mask) ||
                !ctx.TryWriteUInt32(commandAddress + 24, pollCycles / 40))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !ctx.TryWriteUInt32(commandAddress + 4, (uint)address) ||
                 !ctx.TryWriteUInt32(commandAddress + 8, (uint)(address >> 32)) ||
                 !ctx.TryWriteUInt32(commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!ctx.TryWriteUInt32(commandAddress + 16, compareFunction | (operation << 8)) ||
                !ctx.TryWriteUInt32(commandAddress + 20, (uint)reference))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!ctx.TryWriteUInt32(commandAddress + 16, (uint)(mask >> 32)) ||
                 !ctx.TryWriteUInt32(commandAddress + 20, (uint)reference) ||
                 !ctx.TryWriteUInt32(commandAddress + 24, (uint)(reference >> 32)) ||
                 !ctx.TryWriteUInt32(commandAddress + 28, compareFunction | (operation << 8)) ||
                 !ctx.TryWriteUInt32(commandAddress + 32, pollCycles / 40))
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
        if (!ctx.TryReadUInt64(stackAddress + sizeof(ulong), out var control4Raw) ||
            !ctx.TryReadUInt64(stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !ctx.TryReadUInt32(stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !ctx.TryReadUInt64(stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !ctx.TryReadUInt64(stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !ctx.TryReadUInt64(stackAddress + (6 * sizeof(ulong)), out var control9Raw))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !ctx.TryWriteUInt32(
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !ctx.TryWriteUInt32(
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, byteCount) ||
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, 1) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)address & ~7u) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)(address >> 32)))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, dataOffset) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (modifier & 0xA038u) | 0x41u))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
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

            if (!ctx.TryWriteUInt32(commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

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
            !ctx.TryWriteUInt32(commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

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
            register != RDmaData)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 16, destinationAddress)
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 16, indexCount))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, indexCount) ||
            !ctx.TryWriteUInt32(commandAddress + 8, indexOffset) ||
            !ctx.TryWriteUInt32(commandAddress + 12, indexCount) ||
            !ctx.TryWriteUInt32(commandAddress + 16, flags & 0xE000_0001u))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, videoOutHandle) ||
            !ctx.TryWriteUInt32(commandAddress + 8, displayBufferIndex) ||
            !ctx.TryWriteUInt32(commandAddress + 12, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 16, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 20, 0) ||
            !ctx.TryWriteUInt32(commandAddress + 24, 0))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(6, ItNop, RFlip)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, videoOutHandle) ||
            !ctx.TryWriteUInt32(commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, flipMode) ||
            !ctx.TryWriteUInt32(commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(commandAddress + 20, (uint)(flipArg >> 32)))
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
            !ctx.TryReadUInt64(packetAddress, out var commandAddress) ||
            !ctx.TryReadUInt32(packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
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

        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            ParseSubmittedDcb(ctx, gpuState, gpuState.Graphics, commandAddress, dwordCount, tracePackets);
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
            !ctx.TryReadUInt64(packetAddress, out var commandAddress) ||
            !ctx.TryReadUInt32(packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal))
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

        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            ParseSubmittedDcb(ctx, gpuState, queueState, commandAddress, dwordCount, tracePackets);
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

        if (!ctx.TryWriteUInt32(outAddress, 256))
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

        if (!ctx.TryWriteUInt32(ownerAddress, DefaultAgcOwner))
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
            !ctx.TryWriteUInt32(commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static void ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return;
        }

        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!ctx.TryReadUInt32(currentAddress, out var header))
            {
                return;
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
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);

            if (op == ItSetBase &&
                length >= 4 &&
                ctx.TryReadUInt32(currentAddress + 4, out var baseSelector) &&
                baseSelector == 1 &&
                ctx.TryReadUInt64(currentAddress + 8, out var indirectArgsAddress))
            {
                state.IndirectArgsAddress = indirectArgsAddress;
            }

            if (op == ItEventWrite &&
                length >= 2 &&
                ctx.TryReadUInt32(currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                var eventType = eventTypeRaw & 0x3Fu;
                var triggered = KernelEventQueueCompatExports.TriggerRegisteredEvents(
                    eventType,
                    KernelEventQueueCompatExports.KernelEventFilterGraphics,
                    eventType);
                if (tracePackets)
                {
                    TraceAgc($"agc.dcb.event type=0x{eventType:X2} queues={triggered}");
                }
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, currentAddress, tracePackets);
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(ctx, currentAddress, length, tracePackets);
            }

            if (op == ItWriteData && length >= 4)
            {
                ApplySubmittedWriteData(ctx, currentAddress, length, tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 8)
            {
                ApplySubmittedDmaData(ctx, currentAddress, tracePackets);
            }

            if (op == ItDmaData && length >= 7)
            {
                ApplySubmittedStandardDmaData(ctx, currentAddress);
            }

            if (op == ItIndexBase &&
                length >= 3 &&
                ctx.TryReadUInt32(currentAddress + 4, out var indexBaseLo) &&
                ctx.TryReadUInt32(currentAddress + 8, out var indexBaseHi))
            {
                state.IndexBufferAddress =
                    indexBaseLo | ((ulong)indexBaseHi << 32);
            }

            if (op == ItIndexBufferSize &&
                length >= 2 &&
                ctx.TryReadUInt32(currentAddress + 4, out var indexBufferCount))
            {
                state.IndexBufferCount = indexBufferCount;
            }

            if (op == ItIndexType &&
                length >= 2 &&
                ctx.TryReadUInt32(currentAddress + 4, out var indexSize))
            {
                state.IndexSize = indexSize & 0x3;
            }

            if (op == ItNumInstances &&
                length >= 2 &&
                ctx.TryReadUInt32(currentAddress + 4, out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                ObserveSubmittedWaitRegMem(ctx, currentAddress, register == RWaitMem64, tracePackets);
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                ObserveSubmittedStandardWaitRegMem(ctx, currentAddress, tracePackets);
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
                lock (_submitTraceGate)
                {
                    if (_tracedSubmittedDrawOpcodes.Add(op))
                    {
                        TraceAgcShader(
                            $"agc.draw_packet op=0x{op:X2} count={indexCount}");
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
                ctx.TryReadUInt32(currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
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
                ObserveComputeDispatch(ctx, gpuState, state, dispatch);
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                if (!ctx.TryReadUInt32(currentAddress + 4, out var videoOutHandle) ||
                    !ctx.TryReadUInt32(currentAddress + 8, out var displayBufferIndexRaw) ||
                    !ctx.TryReadUInt32(currentAddress + 12, out var flipMode) ||
                    !ctx.TryReadUInt32(currentAddress + 16, out var flipArgLo) ||
                    !ctx.TryReadUInt32(currentAddress + 20, out var flipArgHi))
                {
                    return;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                var handle = unchecked((int)videoOutHandle);
                if (VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var cachedDisplayBuffer) &&
                    VulkanVideoPresenter.TrySubmitGuestImage(
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
                    var textures = CreateVulkanGuestDrawTextures(ctx, translatedDraw.Textures, out var fallbackTextureCount);
                    var globalMemoryBuffers =
                        CreateVulkanGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
                    VulkanVideoPresenter.SubmitTranslatedDraw(
                        translatedDraw.PixelSpirv,
                        textures,
                        globalMemoryBuffers,
                        translatedDisplayBuffer.Width,
                        translatedDisplayBuffer.Height,
                        translatedDraw.AttributeCount);
                    TraceAgcShader(
                        $"agc.shader_present ps=0x{translatedDraw.PixelShaderAddress:X16} " +
                        $"spirv={translatedDraw.PixelSpirv.Length} textures={textures.Count} " +
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
                    VulkanVideoPresenter.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                _ = VideoOutExports.SubmitFlipFromAgc(ctx, handle, displayBufferIndex, unchecked((int)flipMode), flipArg);
                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
                state.TranslatedDraw = null;
            }

            offset += length;
        }
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
        ulong packetAddress,
        bool tracePacket)
    {
        if (!ctx.TryReadUInt32(packetAddress + 12, out var byteCount) ||
            !ctx.TryReadUInt64(packetAddress + 16, out var destinationAddress) ||
            !ctx.TryReadUInt64(packetAddress + 24, out var sourceAddress))
        {
            return;
        }

        var copied =
            byteCount != 0 &&
            byteCount <= 256u * 1024u * 1024u &&
            destinationAddress != 0 &&
            sourceAddress != 0 &&
            TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.dma_data dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} " +
                $"bytes={byteCount} copied={copied}");
        }
    }

    private static void ApplySubmittedStandardDmaData(
        CpuContext ctx,
        ulong packetAddress)
    {
        if (!ctx.TryReadUInt32(packetAddress + 4, out var control) ||
            !ctx.TryReadUInt32(packetAddress + 8, out var sourceLow) ||
            !ctx.TryReadUInt32(packetAddress + 12, out var sourceHigh) ||
            !ctx.TryReadUInt32(packetAddress + 16, out var destinationLow) ||
            !ctx.TryReadUInt32(packetAddress + 20, out var destinationHigh) ||
            !ctx.TryReadUInt32(packetAddress + 24, out var command))
        {
            return;
        }

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
        bool copied;
        ulong sourceAddress;
        if (sourceSelect is 0 or 3 &&
            (sourceSelect == 3 || sourceAddressSpace == 0))
        {
            sourceAddress = sourceLow | ((ulong)sourceHigh << 32);
            if (sourceAddressIncrement != 0)
            {
                copied =
                    ctx.TryReadUInt32(sourceAddress, out var fillValue) &&
                    TryFillGuestMemory(
                        ctx,
                        fillValue,
                        destinationAddress,
                        byteCount);
            }
            else
            {
                copied = TryCopyGuestMemory(
                    ctx,
                    sourceAddress,
                    destinationAddress,
                    byteCount);
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
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (!ctx.TryReadUInt32(packetAddress + 4, out var control) ||
            !ctx.TryReadUInt64(packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var destination = control & 0xFFu;
        var increment = (control >> 16) & 0xFFu;
        var dwordCount = packetLength - 4;
        var wroteData = destination is 1 or 2 or 4 or 5;
        for (uint index = 0; wroteData && index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            var targetAddress = destinationAddress +
                (increment == 0 ? (ulong)index * sizeof(uint) : 0);
            wroteData =
                ctx.TryReadUInt32(sourceAddress, out var value) &&
                ctx.TryWriteUInt32(targetAddress, value);
        }

        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.write_data dst={destination} addr=0x{destinationAddress:X16} " +
                $"count={dwordCount} increment={increment} wrote={wroteData}");
        }
    }

    private static void ObserveSubmittedWaitRegMem(
        CpuContext ctx,
        ulong packetAddress,
        bool is64Bit,
        bool tracePacket)
    {
        if (!ctx.TryReadUInt64(packetAddress + 4, out var address) ||
            !ctx.TryReadUInt32(packetAddress + (is64Bit ? 28u : 16u), out var control))
        {
            return;
        }

        ulong mask;
        ulong reference;
        ulong value;
        if (is64Bit)
        {
            if (!ctx.TryReadUInt64(packetAddress + 12, out mask) ||
                !ctx.TryReadUInt64(packetAddress + 20, out reference) ||
                !ctx.TryReadUInt64(address, out value))
            {
                return;
            }
        }
        else
        {
            if (!ctx.TryReadUInt32(packetAddress + 12, out var mask32) ||
                !ctx.TryReadUInt32(packetAddress + 20, out var reference32) ||
                !ctx.TryReadUInt32(address, out var value32))
            {
                return;
            }

            mask = mask32;
            reference = reference32;
            value = value32;
        }

        var compareFunction = control & 0xFFu;
        TraceSubmittedWait(
            address,
            value,
            mask,
            reference,
            compareFunction,
            is64Bit ? 64 : 32,
            tracePacket);
    }

    private static void ObserveSubmittedStandardWaitRegMem(
        CpuContext ctx,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!ctx.TryReadUInt32(packetAddress + 4, out var control) ||
            !ctx.TryReadUInt64(packetAddress + 8, out var address) ||
            !ctx.TryReadUInt32(packetAddress + 16, out var reference) ||
            !ctx.TryReadUInt32(packetAddress + 20, out var mask) ||
            !ctx.TryReadUInt32(address, out var value))
        {
            return;
        }

        TraceSubmittedWait(address, value, mask, reference, control & 0x7u, 32, tracePacket);
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
            0 => false,
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

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!ctx.TryReadUInt32(packetAddress + 8, out var control) ||
            !ctx.TryReadUInt32(packetAddress + 12, out var destinationLo) ||
            !ctx.TryReadUInt32(packetAddress + 16, out var destinationHi) ||
            !ctx.TryReadUInt32(packetAddress + 20, out var dataLo) ||
            !ctx.TryReadUInt32(packetAddress + 24, out var dataHi))
        {
            return;
        }

        var dataSelection = (control >> 16) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var wroteData = dataSelection switch
        {
            1 or 2 => ctx.TryWriteUInt32(destinationAddress, dataLo),
            3 => ctx.TryWriteUInt64(destinationAddress, data),
            _ => false,
        };

        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.release_mem dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                $"data=0x{data:X16} wrote={wroteData}");
        }
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
                !ctx.TryReadUInt32(packetAddress + sizeof(uint), out var startRegister))
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
                if (!ctx.TryReadUInt32(
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
            !ctx.TryReadUInt32(packetAddress + sizeof(uint), out var registerCount) ||
            !ctx.TryReadUInt64(packetAddress + 8, out var registersAddress))
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
            if (!ctx.TryReadUInt32(entryAddress, out var registerOffset) ||
                !ctx.TryReadUInt32(entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            if (registerOffset != 0)
            {
                destination[registerOffset] = value;
            }
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
                return ctx.TryReadUInt32(packetAddress + 4, out drawCount);
            case ItDrawIndex2 when packetLength >= 6:
                state.DrawIndexOffset = 0;
                return ctx.TryReadUInt32(packetAddress + 16, out drawCount);
            case ItDrawIndexOffset2 when packetLength >= 5:
                if (!ctx.TryReadUInt32(packetAddress + 8, out var indexOffset))
                {
                    return false;
                }

                state.DrawIndexOffset = indexOffset;
                return ctx.TryReadUInt32(packetAddress + 12, out drawCount);
            case ItDrawIndexMultiAuto when packetLength >= 4:
                if (!ctx.TryReadUInt32(packetAddress + 12, out var control))
                {
                    return false;
                }

                drawCount = (control >> 21) & 0x7FFu;
                return true;
            case ItDrawIndirect or ItDrawIndexIndirect
                when packetLength >= 5 && state.IndirectArgsAddress != 0:
                if (!ctx.TryReadUInt32(packetAddress + 4, out var dataOffset))
                {
                    return false;
                }

                return ctx.TryReadUInt32(
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
        state.TranslatedDraw = null;
        state.GuestDrawKind = GuestDrawKind.None;
        foreach (var target in renderTargets)
        {
            state.RenderTargetWriters[target.Address] = new RenderTargetWriter(
                drawSequence,
                hasExportShader ? exportShaderAddress : 0,
                hasPixelShader ? pixelShaderAddress : 0,
                vertexCount,
                primitiveType);

            TraceAgcShader(
                $"agc.rt_writer seq={drawSequence} target=0x{target.Address:X16} " +
                $"fmt={target.Format} tile={target.TileMode} " +
                $"size={target.Width}x{target.Height} vertices={vertexCount} " +
                $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16}");
        }

        if (vertexCount == 0 || vertexCount > 1_048_576)
        {
            return;
        }

        var translationError = string.Empty;
        if (hasExportShader &&
            hasPixelShader &&
            hasPsInputEna &&
            hasPsInputAddr &&
            TryCreateTranslatedGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                vertexCount,
                indexed,
                out var translatedDraw,
                out translationError))
        {
            state.TranslatedDraw = translatedDraw;
            var firstTarget = translatedDraw.RenderTargets.FirstOrDefault();
            if (firstTarget.Address != 0)
            {
                var textures = CreateVulkanGuestDrawTextures(
                    ctx,
                    translatedDraw.Textures,
                    out _);
                var globalMemoryBuffers =
                    CreateVulkanGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
                var vertexBuffers =
                    CreateVulkanGuestVertexBuffers(translatedDraw.VertexInputs);
                VulkanVideoPresenter.SubmitOffscreenTranslatedDraw(
                    translatedDraw.PixelSpirv,
                    textures,
                    globalMemoryBuffers,
                    translatedDraw.AttributeCount,
                    new VulkanGuestRenderTarget(
                        firstTarget.Address,
                        firstTarget.Width,
                            firstTarget.Height,
                            firstTarget.Format,
                            firstTarget.NumberType),
                        translatedDraw.VertexSpirv,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        vertexBuffers,
                        translatedDraw.RenderState);
            }
            else
            {
                var storageTarget = translatedDraw.Textures
                    .FirstOrDefault(binding => binding.IsStorage);
                if (storageTarget is not null)
                {
                    var textures = CreateVulkanGuestDrawTextures(
                        ctx,
                        translatedDraw.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateVulkanGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
                    VulkanVideoPresenter.SubmitStorageTranslatedDraw(
                        translatedDraw.PixelSpirv,
                        textures,
                        globalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        storageTarget.Descriptor.Width,
                        storageTarget.Descriptor.Height);
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

            return;
        }

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
            hasExportShader && hasPixelShader ? translationError : null);
    }

    private static bool TryCreateTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
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
                resolveVertexInputs: true) ||
            !Gen5ShaderTranslator.TryCreateState(
                ctx,
                pixelShaderAddress,
                pixelShaderHeader,
                state.ShRegisters,
                PsTextureUserDataRegister,
                out var pixelState,
                out error) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            return false;
        }

        var renderTargets = GetRenderTargets(state.CxRegisters)
            .Where(target =>
                target.Slot == 0 &&
                HasPixelColorExport(pixelState, target.Slot))
            .ToArray();
        var outputKind = GetPixelOutputKind(renderTargets.FirstOrDefault().NumberType);
        var exportStateFingerprint = ComputeShaderStateFingerprint(exportEvaluation);
        var pixelStateFingerprint = ComputeShaderStateFingerprint(pixelEvaluation);
        var shaderKey = (
            exportShaderAddress,
            exportStateFingerprint,
            pixelShaderAddress,
            pixelStateFingerprint,
            outputKind);
        (byte[] Vertex, byte[] Pixel) compiled;
        lock (_submitTraceGate)
        {
            _graphicsSpirvCache.TryGetValue(shaderKey, out compiled);
        }

        if (compiled.Vertex is null || compiled.Pixel is null)
        {
            var totalGlobalBuffers =
                pixelEvaluation.GlobalMemoryBindings.Count +
                exportEvaluation.GlobalMemoryBindings.Count;
            if (!Gen5SpirvTranslator.TryCompilePixelShader(
                    pixelState,
                    pixelEvaluation,
                    outputKind,
                    out var pixelShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBuffers,
                    imageBindingBase: 0) ||
                !Gen5SpirvTranslator.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out var vertexShader,
                    out error,
                    globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                    totalGlobalBufferCount: totalGlobalBuffers,
                    imageBindingBase: pixelEvaluation.ImageBindings.Count))
            {
                return false;
            }

            compiled = (vertexShader.Spirv, pixelShader.Spirv);
            DumpSpirv(
                "vs",
                exportShaderAddress,
                exportStateFingerprint,
                compiled.Vertex,
                exportState.Program);
            DumpSpirv(
                "ps",
                pixelShaderAddress,
                pixelStateFingerprint,
                compiled.Pixel,
                pixelState.Program);
            lock (_submitTraceGate)
            {
                _graphicsSpirvCache.TryAdd(shaderKey, compiled);
            }
        }

        var imageBindings = pixelEvaluation.ImageBindings
            .Concat(exportEvaluation.ImageBindings);
        var textures = new List<TranslatedImageBinding>(
            pixelEvaluation.ImageBindings.Count +
            exportEvaluation.ImageBindings.Count);
        foreach (var binding in imageBindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                error = $"invalid texture descriptor at pc=0x{binding.Pc:X}";
                return false;
            }

            TraceAgcShader(
                $"agc.texture_binding ps=0x{pixelShaderAddress:X16} es=0x{exportShaderAddress:X16} " +
                $"pc=0x{binding.Pc:X} op={binding.Opcode} storage={(Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode) ? 1 : 0)} " +
                $"decoded={FormatTextureDescriptor(texture)} " +
                $"raw={FormatShaderDwords(binding.ResourceDescriptor)} sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
            textures.Add(
                new TranslatedImageBinding(
                    texture,
                    Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode),
                    binding.MipLevel ?? 0,
                    binding.SamplerDescriptor));
        }

        var globalMemoryBindings = pixelEvaluation.GlobalMemoryBindings
            .Concat(exportEvaluation.GlobalMemoryBindings)
            .ToArray();
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            pixelShaderAddress,
            primitiveType,
            compiled.Vertex,
            compiled.Pixel,
            GetInterpolatedAttributeCount(pixelState),
            vertexCount,
            state.InstanceCount,
            indexed ? CreateVulkanIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            globalMemoryBindings,
            vertexInputs,
            renderTargets,
            CreateRenderState(state.CxRegisters, renderTargets.FirstOrDefault()));
        return true;
    }

    private static VulkanGuestIndexBuffer? CreateVulkanIndexBuffer(
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
        var data = new byte[byteCount];
        var address = state.IndexBufferAddress + byteOffset;
        return (ctx.Memory.TryRead(address, data) ||
                KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, data))
            ? new VulkanGuestIndexBuffer(data, is32Bit)
            : null;
    }

    private static Gen5PixelOutputKind GetPixelOutputKind(uint numberType) =>
        numberType switch
        {
            4 => Gen5PixelOutputKind.Uint,
            5 => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };

    private static bool HasPixelColorExport(Gen5ShaderState state, uint target) =>
        state.Program.Instructions.Any(instruction =>
            instruction.Control is Gen5ExportControl export &&
            export.Target == target &&
            export.EnableMask != 0);

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

    private static ulong ComputeShaderStateFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in evaluation.ScalarRegisters)
        {
            hash = (hash ^ value) * prime;
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

    private static IReadOnlyList<RenderTargetDescriptor> GetRenderTargets(
        IReadOnlyDictionary<uint, uint> registers)
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
            if (address == 0 || (hasTargetMask && writeMask == 0))
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

    private static VulkanGuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        RenderTargetDescriptor target)
    {
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        return new VulkanGuestRenderState(
            DecodeBlendState(registers, target.Slot),
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor));
    }

    private static VulkanGuestBlendState DecodeBlendState(
        IReadOnlyDictionary<uint, uint> registers,
        uint slot)
    {
        var writeMask = 0xFu;
        if (registers.TryGetValue(CbTargetMask, out var targetMask))
        {
            writeMask = (targetMask >> checked((int)(slot * 4))) & 0xFu;
        }

        registers.TryGetValue(CbBlend0Control + slot, out var control);
        return new VulkanGuestBlendState(
            ((control >> 30) & 1u) != 0,
            control & 0x1Fu,
            (control >> 8) & 0x1Fu,
            (control >> 5) & 0x7u,
            (control >> 16) & 0x1Fu,
            (control >> 24) & 0x1Fu,
            (control >> 21) & 0x7u,
            ((control >> 29) & 1u) != 0,
            writeMask == 0 ? 0xFu : writeMask);
    }

    private static VulkanGuestRect? DecodeScissor(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new VulkanGuestRect(0, 0, 0, 0);
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

        IntersectScissorPair(registers, PaScScreenScissorTl, PaScScreenScissorBr, ref left, ref top, ref right, ref bottom);
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

        return new VulkanGuestRect(
            left,
            top,
            checked((uint)(right - left)),
            checked((uint)(bottom - top)));
    }

    private static VulkanGuestViewport? DecodeViewport(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight,
        VulkanGuestRect? scissor)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new VulkanGuestViewport(0, 0, 0, 0, 0, 1);
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
            return new VulkanGuestViewport(
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
                : new VulkanGuestViewport(0, 0, targetWidth, targetHeight, minDepth, maxDepth);
        }

        return new VulkanGuestViewport(
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
        int offsetY = 0)
    {
        if (!TryDecodeScissorPair(registers, tlRegister, brRegister, out var pairLeft, out var pairTop, out var pairRight, out var pairBottom))
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
        out int bottom)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        if (!registers.TryGetValue(tlRegister, out var tl) ||
            !registers.TryGetValue(brRegister, out var br))
        {
            return false;
        }

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
                $"{index}:0x{binding.BaseAddress:X16}:{binding.Data.Length}:" +
                Convert.ToHexString(binding.Data.AsSpan(0, Math.Min(binding.Data.Length, 256)))));
        var indices = draw.IndexBuffer is { } indexBuffer
            ? $"{(indexBuffer.Is32Bit ? 32 : 16)}:" +
              Convert.ToHexString(indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Data.Length, 32)))
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
            $"ps=0x{draw.PixelShaderAddress:X16} spirv={draw.PixelSpirv.Length} " +
            $"primitive=0x{draw.PrimitiveType:X} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} scissor={scissor} viewport={viewport} " +
            $"raster=[{raster}] " +
            $"ps_ena=0x{psInputEna:X8} ps_addr=0x{psInputAddr:X8} " +
            $"targets=[{targets}] textures=[{textures}] " +
            $"buffers=[{buffers}] vertex=[{vertexInputs}] indices=[{indices}]");
    }

    private static IReadOnlyList<VulkanGuestDrawTexture> CreateVulkanGuestDrawTextures(
        CpuContext ctx,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<VulkanGuestDrawTexture>(bindings.Count);
        fallbackTextureCount = 0;
        foreach (var binding in bindings)
        {
            if (TryCreateVulkanGuestDrawTexture(
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

    private static IReadOnlyList<VulkanGuestMemoryBuffer> CreateVulkanGuestMemoryBuffers(
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var buffers = new VulkanGuestMemoryBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            buffers[index] = new VulkanGuestMemoryBuffer(
                bindings[index].BaseAddress,
                bindings[index].Data);
        }

        return buffers;
    }

    private static IReadOnlyList<VulkanGuestVertexBuffer> CreateVulkanGuestVertexBuffers(
        IReadOnlyList<Gen5VertexInputBinding> bindings)
    {
        var buffers = new VulkanGuestVertexBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            buffers[index] = new VulkanGuestVertexBuffer(
                binding.Location,
                binding.ComponentCount,
                binding.DataFormat,
                binding.NumberFormat,
                binding.BaseAddress,
                binding.Stride,
                binding.OffsetBytes,
                binding.Data);
        }

        return buffers;
    }

    private static bool TryCreateVulkanGuestDrawTexture(
        CpuContext ctx,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        out VulkanGuestDrawTexture texture)
    {
        texture = default!;
        if (descriptor.Type != Gen5TextureType2D ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Width > 8192 ||
            descriptor.Height > 8192)
        {
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
            texture = new VulkanGuestDrawTexture(
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
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToVulkanSampler(samplerDescriptor));
            return true;
        }

        if (isStorage)
        {
            var initialPixels = Array.Empty<byte>();
            if (descriptor.Address != 0)
            {
                var storageSource = new byte[(int)sourceByteCount];
                if (ctx.Memory.TryRead(descriptor.Address, storageSource) &&
                    storageSource.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    initialPixels = storageSource;
                }
            }

            texture = new VulkanGuestDrawTexture(
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
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToVulkanSampler(samplerDescriptor));
            return true;
        }

        var source = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(descriptor.Address, source))
        {
            texture = CreateFallbackGuestDrawTexture(isStorage, descriptor.Format, descriptor.NumberType);
            return true;
        }

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
            $"bytes={source.Length} nonzero64={nonZero}");

        var rgba = source;
        texture = new VulkanGuestDrawTexture(
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
            Pitch: sourceWidth,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: ToVulkanSampler(samplerDescriptor));
        return true;
    }

    private static VulkanGuestDrawTexture CreateFallbackGuestDrawTexture(
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

    private static VulkanGuestSampler ToVulkanSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new VulkanGuestSampler(
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
        if (opcode == ItDispatchDirect)
        {
            if (packetLength < 5 ||
                !ctx.TryReadUInt32(packetAddress + 16, out initiator))
            {
                return false;
            }

            dimensionsAddress = packetAddress + 4;
        }
        else if (packetLength >= 4)
        {
            if (!ctx.TryReadUInt64(packetAddress + 4, out dimensionsAddress) ||
                !ctx.TryReadUInt32(packetAddress + 12, out initiator))
            {
                return false;
            }
        }
        else
        {
            if (packetLength < 3 ||
                state.IndirectArgsAddress == 0 ||
                !ctx.TryReadUInt32(packetAddress + 4, out var dataOffset) ||
                !ctx.TryReadUInt32(packetAddress + 8, out initiator))
            {
                return false;
            }

            dimensionsAddress = state.IndirectArgsAddress + dataOffset;
        }

        if ((initiator & 1) == 0 ||
            !ctx.TryReadUInt32(dimensionsAddress, out var groupCountX) ||
            !ctx.TryReadUInt32(dimensionsAddress + 4, out var groupCountY) ||
            !ctx.TryReadUInt32(dimensionsAddress + 8, out var groupCountZ) ||
            groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0)
        {
            return false;
        }

        dispatch = new ComputeDispatch(groupCountX, groupCountY, groupCountZ);
        return true;
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
        var gpuDispatch = false;
        var computeError = string.Empty;
        if (hasStorageBinding &&
            (ulong)localSizeX * localSizeY * localSizeZ <= 1024)
        {
            var shaderKey = (
                shaderAddress,
                ComputeShaderStateFingerprint(evaluation),
                localSizeX,
                localSizeY,
                localSizeZ);
            byte[] computeSpirv;
            lock (_submitTraceGate)
            {
                _computeSpirvCache.TryGetValue(shaderKey, out computeSpirv!);
            }

            if (computeSpirv is null &&
                Gen5SpirvTranslator.TryCompileComputeShader(
                    shaderState,
                    evaluation,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    out var compiledCompute,
                    out computeError))
            {
                computeSpirv = compiledCompute.Spirv;
                DumpSpirv(
                    "cs",
                    shaderAddress,
                    shaderKey.Item2,
                    computeSpirv,
                    shaderState.Program);
            }

            if (computeSpirv is not null)
            {
                lock (_submitTraceGate)
                {
                    _computeSpirvCache.TryAdd(shaderKey, computeSpirv);
                }

                var textures = CreateVulkanGuestDrawTextures(
                    ctx,
                    translatedBindings,
                    out _);
                var globalMemoryBuffers =
                    CreateVulkanGuestMemoryBuffers(evaluation.GlobalMemoryBindings);
                VulkanVideoPresenter.SubmitComputeDispatch(
                    shaderAddress,
                    computeSpirv,
                    textures,
                    globalMemoryBuffers,
                    dispatch.GroupCountX,
                    dispatch.GroupCountY,
                    dispatch.GroupCountZ);
                gpuDispatch = true;
            }
        }

        const int blitCount = 0;

        lock (_submitTraceGate)
        {
            if (_tracedComputeShaders.Add(shaderAddress))
            {
                TraceAgcShader(
                    $"agc.compute_shader cs=0x{shaderAddress:X16} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                    $"sys={DescribeComputeSystemRegisters(computeSystemRegisters)} " +
                    $"gpu={gpuDispatch} blits={blitCount}" +
                    (computeError.Length == 0 ? string.Empty : $" error={computeError}") +
                    $" bindings=[{string.Join(',', descriptions)}]");
            }
        }
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
            ? Math.Max(value & 0x3FFu, 1u)
            : 1u;
    }

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
                    VulkanVideoPresenter.TrySubmitGuestImageBlit(
                        cachedSourceTexture.Address,
                        cachedSourceTexture.Width,
                        cachedSourceTexture.Height,
                        cachedSourceTexture.Format,
                        texture.Address,
                        texture.Width,
                        texture.Height,
                        texture.Format))
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
            20 => 4UL,
            22 => 8UL,
            29 => 4UL,
            36 => 1UL,
            49 => 1UL,
            Gen5TextureFormatR8G8B8A8Unorm => 4UL,
            62 => 4UL,
            64 => 4UL,
            Gen5TextureFormatR16G16B16A16Float => 8UL,
            75 => 8UL,
            _ => 0UL,
        };

    private static ulong GetTextureByteCount(uint format, uint width, uint height)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked((ulong)width * height * bytesPerTexel);
        }

        var blockBytes = format switch
        {
            169 or 170 => 8UL,
            171 or 172 or 173 or 174 or 175 or 176 or
            177 or 178 or 179 or 180 or 181 or 182 => 16UL,
            _ => 0UL,
        };
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

        var pitchAlignment = Math.Max(8UL, 64UL / bytesPerTexel);
        var alignedPitch = AlignUp(pitch, pitchAlignment);
        var sliceAlignment = Math.Max(64UL, 256UL / bytesPerTexel);
        while ((alignedPitch * height) % sliceAlignment != 0)
        {
            alignedPitch += pitchAlignment;
        }

        return checked((uint)alignedPitch);
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

        if ((!hasPixelShader || !hasPsInputEna || !hasPsInputAddr) &&
            TryMarkMissingPixelShaderBindingsTrace())
        {
            TraceAgcShader(
                $"agc.shader_register_candidates " +
                DescribeShaderRegisterCandidates(ctx, state.ShRegisters));
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
                                $"base=0x{binding.BaseAddress:X16} bytes={binding.Data.Length} " +
                                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
                        }

                        if (Gen5SpirvTranslator.TryCompilePixelShader(
                                 pixelState,
                                 evaluation,
                                 Gen5PixelOutputKind.Float,
                                 out var compiledPixel,
                                 out var compileError))
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv ps=0x{pixelShaderAddress:X16} " +
                                $"bytes={compiledPixel.Spirv.Length} bindings={evaluation.ImageBindings.Count} " +
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
                    var type = ctx.TryReadByte(
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
            !ctx.TryReadUInt32(packetAddress + 4, out var startRegister))
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
        Span<uint> fields = stackalloc uint[4];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!ctx.TryReadUInt32(descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
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

        // GFX10/RDNA2 T# layout: WIDTH is split across word1[31:30] (lo 2 bits)
        // and word2[11:0] (hi 12 bits); FORMAT is the combined 9-bit field at
        // word1[28:20]. Verified against Kyty's decode of the same game
        // descriptors (fmt=56=8_8_8_8_UNORM, extent 1280x720, sw_mode 27).
        // GNM T# exposes a 38-bit baseaddr256 field, but RPCSX and the
        // Demon's Souls descriptors both show that only the low 32 bits are
        // part of the guest GPU VA. The upper baseaddr bits carry resource
        // metadata and produce bogus addresses such as 0x00003804... if used.
        var address = ((ulong)(uint)((((ulong)fields[1] << 32) | fields[0]) & 0x3F_FFFF_FFFFUL)) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0xFFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0x3FFFu) + 1;
        var format = (fields[1] >> 20) & 0x1FFu;
        var numberType = (fields[1] >> 26) & 0xFu;
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        var baseLevel = (fields[3] >> 12) & 0xFu;
        var lastLevel = (fields[3] >> 16) & 0xFu;
        var pitch = fields.Count >= 5
            ? ((fields[4] >> 13) & 0x3FFFu) + 1
            : width;
        var dstSelect = fields[3] & 0xFFFu;
        if (address == 0 || width == 0 || height == 0)
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
            dstSelect);
        return true;
    }

    private static TextureDescriptor CreateFallbackTextureDescriptor(IReadOnlyList<uint> fields)
    {
        var format = Gen5TextureFormatR8G8B8A8Unorm;
        var numberType = 0u;
        var tileMode = 0u;
        if (fields.Count >= 4)
        {
            format = (fields[1] >> 20) & 0x1FFu;
            numberType = (fields[1] >> 26) & 0xFu;
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
                VideoOutPixelFormatB8G8R8A8Unorm or
                VideoOutPixelFormatR8G8B8A8Unorm))
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
            VideoOutPixelFormatR8G8B8A8Unorm;
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (rgbaDestination)
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

                destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
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
            if (!ctx.TryReadUInt32(packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !ctx.TryReadUInt32(packetAddress + 4, out var registerCount) ||
            !ctx.TryReadUInt64(packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!ctx.TryReadUInt32(entryAddress, out var registerOffset) ||
                !ctx.TryReadUInt32(entryAddress + 4, out var value))
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
        if (!ctx.TryReadUInt64(headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !ctx.TryReadByte(headerAddress + ShaderTypeOffset, out var shaderType) ||
            !ctx.TryReadByte(headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return false;
        }

        if (shRegistersAddress == 0 || registerCount < 2)
        {
            return false;
        }

        if (!ctx.TryReadUInt32(shRegistersAddress, out var loRegister) ||
            !ctx.TryReadUInt32(shRegistersAddress + 8, out var hiRegister))
        {
            return false;
        }

        var expectedLo = shaderType switch
        {
            0 => ComputePgmLo,
            1 => SpiShaderPgmLoPs,
            2 or 6 => SpiShaderPgmLoEs,
            7 => SpiShaderPgmLoLs,
            _ => 0u,
        };
        var expectedHi = shaderType switch
        {
            0 => ComputePgmHi,
            1 => SpiShaderPgmHiPs,
            2 or 6 => SpiShaderPgmHiEs,
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
        return ctx.TryWriteUInt32(shRegistersAddress + sizeof(uint), loValue) &&
               ctx.TryWriteUInt32(shRegistersAddress + 8 + sizeof(uint), hiValue);
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

        if (!ctx.TryWriteUInt32(commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt32(commandAddress + 4, out var currentCount) ||
            !ctx.TryWriteUInt32(commandAddress + 4, currentCount + registerCount))
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
            !ctx.TryWriteUInt32(commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !ctx.TryWriteUInt32(commandAddress + 4, registerCount) ||
            !ctx.TryWriteUInt32(commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(commandAddress + 12, (uint)(registersAddress >> 32)))
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
            !ctx.TryReadUInt64(commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !ctx.TryReadUInt64(commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !ctx.TryReadUInt64(commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !ctx.TryReadUInt32(commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        var remainingDwords = (uint)Math.Max(availableDwords, reservedDwords) - reservedDwords;
        if (sizeDwords > remainingDwords)
        {
            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            return false;
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!ctx.TryWriteUInt64(commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!ctx.TryReadUInt32(sourceAddress, out var offset) ||
            !ctx.TryReadUInt32(sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return ctx.TryWriteUInt32(destinationAddress, offset) &&
               ctx.TryWriteUInt32(destinationAddress + sizeof(uint), value);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!ctx.TryReadUInt64(fieldAddress, out var relativeAddress))
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
            RegisterDefaultsVersion10;
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
            if (!ctx.TryReadByte(address + (ulong)index, out var value))
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
        if (commandAddress == 0 || !ctx.TryReadUInt32(commandAddress, out var header))
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

    private static void TraceAgc(string message)
    {
        if (!_traceAgc)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] {message}");
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
        $"type={descriptor.Type} levels={descriptor.BaseLevel}-{descriptor.LastLevel} " +
        $"pitch={descriptor.Pitch} dst=0x{descriptor.DstSelect:X3}";

    private static void DumpSpirv(
        string stage,
        ulong shaderAddress,
        ulong stateFingerprint,
        byte[] spirv,
        Gen5ShaderProgram program)
    {
        if (spirv.Length == 0 ||
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        Directory.CreateDirectory(directory);
        var name = $"{shaderAddress:X16}-{stateFingerprint:X16}.{stage}";
        File.WriteAllBytes(Path.Combine(directory, $"{name}.spv"), spirv);

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
}
