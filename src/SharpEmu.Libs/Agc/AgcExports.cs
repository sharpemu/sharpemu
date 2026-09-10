// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // The backend is a process-fixed singleton, so its offset-alignment
    // requirement is snapshot once: several per-draw paths (shader-key
    // hashing, buffer-offset alignment) read it in loops.
    private static readonly ulong _storageBufferOffsetAlignment =
        GuestGpu.Current.GuestStorageBufferOffsetAlignment;

#if DEBUG
    static AgcExports()
    {
        ValidateDispatchInitiators();
        ValidateDepthTargetDecoder();
    }
#endif

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
    private const uint ItDrawIndexIndirectMulti = 0x38;
    private const uint DrawIndexedIndirectArgsSize = 20;
    private const uint DrawIndexedIndirectMaxScan = 1024;
    private const uint ItWriteData = 0x37;
    private const uint ItDispatchDirect = 0x15;
    private const uint ItDispatchIndirect = 0x16;
    private const uint ItSetPredication = 0x20;
    private const uint ItCondExec = 0x22;
    private const uint ItWaitRegMem = 0x3C;
    private const uint ItIndirectBuffer = 0x3F;
    private const uint ItCondWrite = 0x45;
    private const uint ItEventWrite = 0x46;
    private const uint ItRewind = 0x59;
    private const uint ItSetShRegIndirect = 0x63;
    private const uint ItSetUconfigRegIndirect = 0x64;
    private const uint ItSetContextReg = 0x69;
    private const uint ItSetShReg = 0x76;
    private const uint ItSetUconfigReg = 0x79;
    private const uint ItSetUconfigRegIndex = 0x7A;
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindOffloadEnableBit = 1u << 24;
    private const uint ItGetLodStats = 0x8E;
    private const uint ItSetContextRegIndirect = 0x9F;

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

    // release_mem here raises an EOP interrupt; above this range it's
    // GPU-internal queue sync with no interrupt.
    private const ulong GpuLabelPoolBase = 0x2000000000UL;
    private const ulong GpuLabelPoolSize = 0x10000UL;

    private static bool IsCpuVisibleLabel(ulong address) =>
        address >= GpuLabelPoolBase &&
        address < GpuLabelPoolBase + GpuLabelPoolSize;

    private const uint RIndexCount = 0x1C;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoVs = 0x48;
    private const uint SpiShaderPgmHiVs = 0x49;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoHs = 0x108;
    private const uint SpiShaderPgmHiHs = 0x109;
    private const uint SpiShaderPgmRsrc1Hs = 0x10A;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    // Not 0x8A/0x8B - those are SPI_SHADER_PGM_RSRC1/RSRC2_GS, and reading them
    // as an address yields a nonsensical 58-bit value.
    private const uint SpiShaderPgmLoGs = 0x88;
    private const uint SpiShaderPgmHiGs = 0x89;
    private const uint SpiShaderPgmRsrc1Gs = 0x8A;
    private const uint SpiShaderPgmChksumGs = 0x80;
    private const uint SpiShaderPgmChksumPs = 0x06;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint SpiPsInControl = 0x1B6;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint ComputeShaderChksum = 0x22A;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint VgtIndexType = 0x243;
    // GE_INDX_OFFSET — base vertex for DrawIndexed / firstVertex for
    // DrawIndexAuto. Glyph meshes and UI icon batches rely on this.
    private const uint GeIndxOffset = 0x24A;
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
    private const uint CbColor0View = 0x31B;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0Attrib = 0x31D;
    private const uint CbColor0DccControl = 0x31E;
    private const uint CbColor0Cmask = 0x31F;
    private const uint CbColor0Fmask = 0x321;
    private const uint CbColor0ClearWord0 = 0x323;
    private const uint CbColor0ClearWord1 = 0x324;
    private const uint CbColor0DccBase = 0x325;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0CmaskBaseExt = 0x398;
    private const uint CbColor0FmaskBaseExt = 0x3A0;
    private const uint CbColor0DccBaseExt = 0x3A8;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    // CB_COLORn_INFO.DCC_ENABLE (gc_10_1_0_sh_mask.h). On GFX10 the legacy
    // FAST_CLEAR and COMPRESSION bits stay clear because DCC, not CMASK,
    // carries the compression.
    private const uint CbColorInfoDccEnableMask = 1u << 28;
    private const uint CbColorInfoFastClearEnableMask = 1u << 12;
    private const uint CbBlend0Control = 0x1E0;
    private const uint PaScModeCntl0 = 0x292;
    // GFX10 DB context registers (register byte address minus 0x28000, / 4).
    private const uint DbRenderControl = 0x000;
    private const uint DbDepthView = 0x002;
    private const uint DbHtileDataBase = 0x005;
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbStencilClear = 0x00A;
    private const uint DbDepthClear = 0x00B;
    private const uint DbZInfo = 0x010;
    private const uint DbStencilInfo = 0x011;
    private const uint DbZReadBase = 0x012;
    private const uint DbStencilReadBase = 0x013;
    private const uint DbZWriteBase = 0x014;
    private const uint DbStencilWriteBase = 0x015;
    private const uint DbZReadBaseHi = 0x01A;
    private const uint DbStencilReadBaseHi = 0x01B;
    private const uint DbZWriteBaseHi = 0x01C;
    private const uint DbStencilWriteBaseHi = 0x01D;
    private const uint DbHtileDataBaseHi = 0x01E;
    private const uint DbHtileSurface = 0x2AF;
    private const uint DbStencilControl = 0x10B;
    private const uint DbStencilRefMask = 0x10C;
    private const uint DbStencilRefMaskBack = 0x10D;
    private const int ColorTargetCount = 8;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint NggUserDataScalarRegisterBase = 8;
    internal const uint Gen5TextureFormatR8G8B8A8Unorm = 10;
    internal const uint Gen5TextureFormatR16G16B16A16Float = 12;
    private const uint Gen5TextureType1D = 8;
    private const uint Gen5TextureType2D = 9;
    private const uint Gen5TextureType3D = 10;
    private const uint Gen5TextureTypeCube = 11;
    private const uint Gen5TextureType1DArray = 12;
    private const uint Gen5TextureType2DArray = 13;
    private const ulong MaxPresentedTextureBytes = 128UL * 1024UL * 1024UL;

    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferUserDataOffset = 0x28;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private static readonly object _submitTraceGate = new();
    // Every PM4 opcode with no handler, logged once with its first two
    // payload dwords as a possible target address.
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
    private static readonly bool _traceDraws = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceDrawOracle = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAW_ORACLE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVideoDrawChain = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VIDEO_DRAW_CHAIN"),
        "1",
        StringComparison.Ordinal);
    private static readonly object _softwarePresenterGate = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    // Unwraps decorator chains so all threads resolve to one shared root —
    // ctx.Memory identity otherwise differs per native worker thread.
    private static object CanonicalMemory(object memory)
    {
        while (memory is SharpEmu.HLE.ICpuMemoryWrapper wrapper)
        {
            memory = wrapper.Inner;
        }

        return memory;
    }

    private sealed record TranslatedGuestDraw(
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint PrimitiveType,
        IGuestCompiledShader VertexShader,
        IGuestCompiledShader PixelShader,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        int BaseVertex,
        int VertexBufferBaseVertex,
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
        IReadOnlyList<uint> VertexInitialScalars,
        bool IsFullscreenColorClear = false,
        float ClearRed = 0f,
        float ClearGreen = 0f,
        float ClearBlue = 0f,
        float ClearAlpha = 1f,
        bool IsDccFastClear = false);

    private sealed class SubmittedDcbState
    {
        private uint? _compositeDepthSizeXy;
        private CommandRegisterBanks? _interpreterBanks;

        public Dictionary<uint, uint> CxRegisters { get; private set; } = new();
        public Dictionary<uint, uint> ShRegisters { get; private set; } = new();
        public Dictionary<uint, uint> UcRegisters { get; private set; } = new();

        // With an interpreter attached the banks and the composite extent are its own.
        public uint? CompositeDepthSizeXy
        {
            get => _interpreterBanks is { } banks ? banks.CompositeDepthSizeXy : _compositeDepthSizeXy;
            set
            {
                if (_interpreterBanks is { } banks)
                {
                    banks.CompositeDepthSizeXy = value;
                }
                else
                {
                    _compositeDepthSizeXy = value;
                }
            }
        }

        public void AttachInterpreter(GpuCommandInterpreter interpreter)
        {
            if (ReferenceEquals(_interpreterBanks, interpreter.Registers))
            {
                return;
            }

            _interpreterBanks = interpreter.Registers;
            CxRegisters = interpreter.Registers.Context;
            ShRegisters = interpreter.Registers.Shader;
            UcRegisters = interpreter.Registers.UserConfig;
        }
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
        // Ident this queue's end-of-pipe completion interrupt is published under.
        // The graphics queue keeps 0; a compute queue takes the owner handle it
        // was submitted with, which is the same value the guest registers through
        // sceAgcDriverAddEqEvent.
        public ulong CompletionEventId { get; set; }
        public ulong ActiveSubmissionId { get; set; }
        public Dictionary<ulong, SubmittedIndexSnapshot>? ActiveIndexSnapshots { get; set; }
        public SubmittedIndexSnapshot? CurrentIndexSnapshot { get; set; }
        public Dictionary<ulong, SubmittedVertexSnapshot>? ActiveVertexSnapshots { get; set; }
        public SubmittedVertexSnapshot? CurrentVertexSnapshot { get; set; }
        public uint FrameDrawCount { get; set; }
        public uint FrameDispatchCount { get; set; }
        public ulong FlipCount { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public object CommandSubmissionGate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        // The submit-time geometry prepass shadow; only the prepass reads or writes it.
        public SubmittedDcbState GeometryCapture { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
        public Dictionary<ulong, ComputeImageWriter> ComputeImageWriters { get; } = new();
        public AgcHtileMetadataTracker HtileMetadata { get; } = new();
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
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    internal static uint GetPsInputCount(
        IReadOnlyDictionary<uint, uint> cxRegisters,
        uint fallbackCount)
    {
        var count = cxRegisters.TryGetValue(SpiPsInControl, out var control)
            ? control & 0x3Fu
            : fallbackCount;
        return Math.Min(count, 32u);
    }

    internal static uint[] ReadPsInputCntlRegisters(
        IReadOnlyDictionary<uint, uint> cxRegisters,
        uint inputCount)
    {
        var boundedCount = Math.Min(inputCount, 32u);
        var cntl = new uint[boundedCount];
        for (uint i = 0; i < boundedCount; i++)
        {
            // Unprogrammed slots default to identity (ATTR i → param i).
            cntl[i] = cxRegisters.TryGetValue(SpiPsInputCntl0 + i, out var value)
                ? value
                : i;
        }

        return cntl;
    }

    private static uint GetPixelColorExportMask(uint packedMasks, uint target) =>
        target < ColorTargetCount
            ? (packedMasks >> (int)(target * 4)) & 0xFu
            : 0;

    internal static ulong PackPixelOutputMappings(
        IReadOnlyList<Gen5ColorComponentMapping> mappings)
    {
        if (mappings.Count > ColorTargetCount)
        {
            throw new ArgumentOutOfRangeException(nameof(mappings));
        }

        var packed = 0UL;
        for (var index = 0; index < mappings.Count; index++)
        {
            packed |= (ulong)mappings[index].Packed << (index * 8);
        }

        return packed;
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
    internal static ulong ComputeShaderStructuralFingerprint(Gen5ShaderEvaluation evaluation)
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
                // The unified format selects the generated image type.
                Mix(binding.ResourceDescriptor[1] & 0x1FF0_0000u);
            }

            if (binding.ResourceDescriptor.Count > 3 &&
                binding.Opcode.StartsWith("ImageStore", StringComparison.Ordinal))
            {
                // A storage write applies this value in generated shader code.
                Mix(Gen5ShaderTranslator.GetImageDescriptorDstSelect(
                    binding.ResourceDescriptor));
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
                Mix(input.PerInstance ? 1u : 0u);
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
                (_storageBufferOffsetAlignment - 1))) * prime;
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

    private static int GetRuntimeScalarBufferLength(int bindingCount) =>
        checked((256 + bindingCount) * sizeof(uint));

    private static byte[] PackRuntimeScalarState(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = GuestDataPool.Shared.Rent(
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
                (_storageBufferOffsetAlignment - 1)));
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
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            if (binding.DataPooled && returned.Add(binding.Data))
            {
                GuestDataPool.Shared.Return(binding.Data);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var binding in vertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
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
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        if (globals)
        {
            foreach (var binding in draw.GlobalMemoryBindings)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (vertex)
        {
            foreach (var binding in draw.VertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (index && draw.IndexBuffer is { Pooled: true } indexBuffer &&
            returned.Add(indexBuffer.Data))
        {
            indexBuffer.TryReturnPooledData();
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
                bindings[index].Size,
                bindings[index].DataPooled,
                bindings[index].Writable,
                bindings[index].WriteBackToGuest);
        }

        return buffers;
    }

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

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadLiveUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadLiveUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
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

    // Monotonic seconds since process start, prefixed on every AGC trace
    // line — the frame pipeline's dependency chains span tens of seconds.
    private static readonly long _traceStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    private static string TraceSeconds() =>
        ((System.Diagnostics.Stopwatch.GetTimestamp() - _traceStartTicks) /
         (double)System.Diagnostics.Stopwatch.Frequency).ToString(
            "F3", System.Globalization.CultureInfo.InvariantCulture);

    private static void TraceAgc(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcTraceHandler message)
    {
        if (_traceAgc)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgc(string message)
    {
        if (!_traceAgc)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

    private static void TraceAgcShader(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcShaderTraceHandler message)
    {
        if (_traceAgcShader)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgcShader(string message)
    {
        if (!_traceAgcShader)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

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

}
