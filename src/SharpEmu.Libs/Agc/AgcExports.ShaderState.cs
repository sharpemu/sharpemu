// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial creates AGC shader-pipeline state objects.

    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const int ShaderStructBytes = 0x60;
    private const uint MaximumDeclaredShaderSizeBytes = 1024 * 1024;
    // Embedded GS/HS front/back descriptors can sit beyond the first 64 KiB of
    // the upload. Scan the full shader object size accepted by AGC so the
    // continuation is registered before resource planning sees the front half.
    private const int MaximumEmbeddedFusedScanBytes = 1024 * 1024;
    private const ulong FusedShaderImageAlignment = 4;
    private const byte ComputeShaderType = 0;
    private const byte PsShaderType = 1;
    private const byte GsShaderType = 2;
    private const byte HsShaderType = 3;
    private const byte GsFrontShaderType = 4;
    private const byte HsFrontShaderType = 5;
    private const byte GsBackShaderType = 6;
    private const byte HsBackShaderType = 7;
    private const byte FunctionShaderType = 8;
    private const OrbisGen2Result IncompleteShaderRegistersResult = unchecked((OrbisGen2Result)0x8A6C0005);

    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const uint VgtShaderStagesHsW32EnBit = 1u << 21;
    private const uint VgtShaderStagesGsW32EnBit = 1u << 22;
    private const uint VgtShaderStagesGsEnableBit = 1u << 5;
    private const uint VgtGsOutPrimType = 0x29B;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;

    private static readonly ConditionalWeakTable<
        object,
        ConcurrentDictionary<(ulong Code, ulong Header), byte>>
        _embeddedFusedScanAttempts = new();

    private static long _createShaderTraceCount;

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

        var programRegisterResult = PatchShaderProgramRegisters(ctx, headerAddress, codeAddress);
        if (programRegisterResult != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return SetReturn(ctx, programRegisterResult);
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

        TryRegisterEmbeddedFusedProgram(ctx, codeAddress, headerAddress);

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Registers a pre-combined shader whose continuation descriptor is in the
    /// same AGC upload. Some titles create this object without a fuse API call.
    /// </summary>
    internal static bool TryRegisterEmbeddedFusedProgram(
        CpuContext ctx,
        ulong entryCodeAddress,
        ulong entryHeaderAddress)
    {
        if (!TryReadByte(ctx, entryHeaderAddress + ShaderTypeOffset, out var entryType) ||
            entryType is not (GsFrontShaderType or HsFrontShaderType))
        {
            return false;
        }

        var attempts = _embeddedFusedScanAttempts.GetValue(
            ctx.Memory,
            static _ => new ConcurrentDictionary<(ulong Code, ulong Header), byte>());
        if (!attempts.TryAdd((entryCodeAddress, entryHeaderAddress), 0))
        {
            return false;
        }

        if (!TryReadUInt32(ctx, entryHeaderAddress + ShaderSizeOffset, out var entrySize) ||
            !IsValidDeclaredShaderSize(entrySize))
        {
            return false;
        }

        var requiredContinuationType = entryType == GsFrontShaderType
            ? GsBackShaderType
            : HsBackShaderType;
        var waveSizeBit = entryType == GsFrontShaderType
            ? VgtShaderStagesGsW32EnBit
            : VgtShaderStagesHsW32EnBit;
        TryReadUInt64(
            ctx,
            entryHeaderAddress + ShaderSpecialsOffset,
            out var entrySpecialsAddress);

        ulong bestCodeAddress = 0;
        ulong bestHeaderAddress = 0;
        var bestDistance = ulong.MaxValue;

        bool ScanForContinuation(ulong scanAddress)
        {
            var upload = new byte[MaximumEmbeddedFusedScanBytes];
            var bytesRead = 0;
            const int readChunkBytes = 4 * 1024;
            while (bytesRead < upload.Length)
            {
                var chunkLength = Math.Min(readChunkBytes, upload.Length - bytesRead);
                if (!ctx.Memory.TryRead(
                        scanAddress + (ulong)bytesRead,
                        upload.AsSpan(bytesRead, chunkLength)))
                {
                    break;
                }

                bytesRead += chunkLength;
            }

            if (bytesRead < ShaderStructBytes)
            {
                return false;
            }

            var found = false;
            for (var offset = 0;
                 offset <= bytesRead - ShaderStructBytes;
                 offset += sizeof(uint))
            {
                var descriptor = upload.AsSpan(offset, ShaderStructBytes);
                if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != ShaderFileHeader ||
                    BinaryPrimitives.ReadUInt32LittleEndian(descriptor[sizeof(uint)..]) != ShaderVersion ||
                    descriptor[(int)ShaderTypeOffset] != requiredContinuationType)
                {
                    continue;
                }

                var continuationCodeAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                    descriptor[(int)ShaderCodeOffset..]);
                var continuationSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[(int)ShaderSizeOffset..]);
                if (continuationCodeAddress <= entryCodeAddress ||
                    continuationCodeAddress - entryCodeAddress > uint.MaxValue ||
                    !IsValidDeclaredShaderSize(continuationSize) ||
                    !CanReadShaderRange(ctx, continuationCodeAddress, continuationSize))
                {
                    continue;
                }

                var continuationSpecialsAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                    descriptor[(int)ShaderSpecialsOffset..]);
                if (entrySpecialsAddress != 0 && continuationSpecialsAddress != 0)
                {
                    if (!TryReadUInt32(
                            ctx,
                            entrySpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                            out var entryStages) ||
                        !TryReadUInt32(
                            ctx,
                            continuationSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                            out var continuationStages) ||
                        ((entryStages ^ continuationStages) & waveSizeBit) != 0)
                    {
                        continue;
                    }
                }

                var distance = continuationCodeAddress - entryCodeAddress;
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestCodeAddress = continuationCodeAddress;
                bestHeaderAddress = scanAddress + (ulong)offset;
                found = true;
            }

            return found;
        }

        // Most uploads place the second descriptor beside the code. Some
        // titles keep both descriptors in the header allocation instead, so
        // inspect that region as a fallback.
        ScanForContinuation(entryCodeAddress);
        if (bestHeaderAddress == 0)
        {
            ScanForContinuation(entryHeaderAddress);
        }

        if (bestHeaderAddress == 0)
        {
            return false;
        }

        Gen5ShaderTranslator.RegisterFusedProgram(
            ctx,
            entryCodeAddress,
            entryHeaderAddress,
            bestCodeAddress,
            bestHeaderAddress);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.fused_shader_discovered " +
            $"entry=0x{entryCodeAddress:X16} type={entryType} size=0x{entrySize:X} " +
            $"continuation=0x{bestCodeAddress:X16} type={requiredContinuationType} " +
            $"header=0x{bestHeaderAddress:X16}");
        return true;
    }

    private static bool IsValidDeclaredShaderSize(uint size) =>
        size != 0 &&
        (size & (sizeof(uint) - 1)) == 0 &&
        size <= MaximumDeclaredShaderSizeBytes;

    private static bool CanReadShaderRange(CpuContext ctx, ulong address, uint size)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        return ctx.Memory.TryRead(address, word) &&
               ctx.Memory.TryRead(address + size - sizeof(uint), word);
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "dolOmWH+huQ",
        ExportName = "sceAgcGetFusedShaderSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetFusedShaderSize(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        if (destinationAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(destinationAddress, registerCount * 8UL) ||
            !ctx.TryWriteUInt64(destinationAddress + 8, FusedShaderImageAlignment))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.get_fused_shader_size front=0x{frontAddress:X16} back=0x{backAddress:X16} " +
            $"types={frontType}/{backType} registers={registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "fd5Bp5tGTgo",
        ExportName = "sceAgcFuseShaderHalves",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int FuseShaderHalves(CpuContext ctx)
    {
        var fusedAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        var scratchAddress = ctx[CpuRegister.Rcx];
        if (fusedAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, frontAddress + ShaderSpecialsOffset, out var frontSpecialsAddress) ||
            !TryReadUInt64(ctx, backAddress + ShaderSpecialsOffset, out var backSpecialsAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var isGeometryPair = frontType == GsFrontShaderType;
        if (frontSpecialsAddress != 0 && backSpecialsAddress != 0)
        {
            if (!TryReadUInt32(ctx, frontSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var frontStages) ||
                !TryReadUInt32(ctx, backSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var backStages))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var waveSizeBit = isGeometryPair ? VgtShaderStagesGsW32EnBit : VgtShaderStagesHsW32EnBit;
            if (((frontStages ^ backStages) & waveSizeBit) != 0)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        if (!TryReadUInt64(ctx, backAddress + ShaderShRegistersOffset, out var backRegistersAddress) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount) ||
            !TryReadUInt64(ctx, frontAddress + ShaderCodeOffset, out var frontCodeAddress) ||
            !TryReadUInt64(ctx, backAddress + ShaderCodeOffset, out var backCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> header = stackalloc byte[ShaderStructBytes];
        if (!ctx.Memory.TryRead(backAddress, header) ||
            !ctx.Memory.TryWrite(fusedAddress, header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var fusedRegistersAddress = backRegistersAddress;
        if (scratchAddress != 0 && backRegistersAddress != 0 && registerCount != 0)
        {
            Span<byte> registers = stackalloc byte[registerCount * 8];
            if (!ctx.Memory.TryRead(backRegistersAddress, registers) ||
                !ctx.Memory.TryWrite(scratchAddress, registers))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            fusedRegistersAddress = scratchAddress;
        }

        if (!TryWriteByte(ctx, fusedAddress + ShaderTypeOffset, isGeometryPair ? GsShaderType : HsShaderType) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderUserDataOffset, 0) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderShRegistersOffset, fusedRegistersAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (isGeometryPair)
        {
            if (!TryReadUInt64(ctx, frontAddress + ShaderShRegistersOffset, out var frontRegistersAddress) ||
                !TryReadByte(ctx, frontAddress + ShaderNumShRegistersOffset, out var frontRegisterCount))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            for (var occurrence = 0; occurrence < 2; occurrence++)
            {
                if (!TryFindShaderRegister(ctx, fusedRegistersAddress, registerCount, SpiShaderPgmChksumGs, occurrence, out var fusedEntry) ||
                    !TryFindShaderRegister(ctx, frontRegistersAddress, frontRegisterCount, SpiShaderPgmChksumGs, occurrence, out var frontEntry))
                {
                    continue;
                }

                if (!TryReadUInt32(ctx, frontEntry + sizeof(uint), out var checksum) ||
                    !TryWriteUInt32(ctx, fusedEntry + sizeof(uint), checksum))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        if (!PatchFusedProgramAddress(
                ctx,
                fusedRegistersAddress,
                registerCount,
                isGeometryPair ? SpiShaderPgmLoEs : SpiShaderPgmLoLs,
                frontCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Gen5ShaderTranslator.RegisterFusedProgram(
            ctx,
            frontCodeAddress,
            frontAddress,
            backCodeAddress,
            backAddress);

        TraceAgc(
            $"agc.fuse_shader_halves fused=0x{fusedAddress:X16} front=0x{frontAddress:X16} " +
            $"back=0x{backAddress:X16} scratch=0x{scratchAddress:X16} types={frontType}/{backType} " +
            $"registers={registerCount} entry=0x{frontCodeAddress:X16} " +
            $"continuation=0x{backCodeAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

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

        if (cxRegistersAddress == 0 && ucRegistersAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, geometryShaderAddress + ShaderTypeOffset, out var shaderType) || !IsEsGeometryShaderType(shaderType) ||
            !TryReadUInt64(ctx, geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong hullSpecialsAddress = 0;
        if (hullShaderAddress != 0 &&
            (!TryReadByte(ctx, hullShaderAddress + ShaderTypeOffset, out var hullShaderType) ||
             hullShaderType != HsShaderType ||
             !TryReadUInt64(ctx, hullShaderAddress + ShaderSpecialsOffset, out hullSpecialsAddress) ||
             hullSpecialsAddress == 0))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (cxRegistersAddress != 0 &&
            !TryCreatePrimCxState(
                ctx,
                cxRegistersAddress,
                specialsAddress,
                hullSpecialsAddress,
                primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ucRegistersAddress != 0 &&
            (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
             !CopyShaderRegister(
                 ctx,
                 (hullSpecialsAddress != 0 ? hullSpecialsAddress : specialsAddress) +
                     ShaderSpecialGeUserVgprEnOffset,
                 ucRegistersAddress + 8) ||
             !TryWriteUInt32(ctx, ucRegistersAddress + 16, VgtPrimitiveType) ||
             !TryWriteUInt32(ctx, ucRegistersAddress + 20, primitiveType)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} " +
            $"hull=0x{hullShaderAddress:X16} gs=0x{geometryShaderAddress:X16} type={shaderType} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "dbOlWdppb4o",
        ExportName = "sceAgcCreateInterpolantMapping2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping2(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint inputSemanticsCount = 0;
        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0 &&
            (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
             !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out inputSemanticsCount)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (inputSemanticsCount == 0)
        {
            if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.create_interpolant_mapping2 regs=0x{registersAddress:X16} " +
                $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} inputs=0");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (inputSemanticsAddress == 0 || geometryShaderAddress == 0 || inputSemanticsCount > 32)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt16(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (outputSemanticsCount != 0 && outputSemanticsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (uint inputIndex = 0; inputIndex < inputSemanticsCount; inputIndex++)
        {
            if (!TryReadUInt32(
                    ctx,
                    inputSemanticsAddress + (inputIndex * sizeof(uint)),
                    out var source))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var hasMask = false;
            var mask = 0u;
            for (uint outputIndex = 0; outputIndex < outputSemanticsCount; outputIndex++)
            {
                if (!TryReadUInt32(
                        ctx,
                        outputSemanticsAddress + (outputIndex * sizeof(uint)),
                        out var candidate))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if ((byte)candidate == (byte)source)
                {
                    hasMask = true;
                    mask = candidate;
                    break;
                }
            }

            var mode = (source >> 20) & 0x3u;
            uint flags;
            if (mode == 0)
            {
                flags = (((source >> 24) & 0x1u) | (hasMask ? 0u : 1u)) << 5;
                flags = ApplyInterpolantTwoBitField(flags, source >> 28, 8);
            }
            else
            {
                flags = ((source << 4) & 0x0300_0000u) + 0x0008_0000u;
                if (mode == 2)
                {
                    flags &= 0xFFEF_FFDFu;
                    flags |= hasMask ? ((~(mask & source) >> 16) & 0x20u) : 0x20u;
                    flags = ApplyInterpolantTwoBitField(flags, source >> 30, 8);
                    flags = ApplyInterpolantTwoBitField(flags, source >> 30, 21);
                }
                else
                {
                    if (hasMask)
                    {
                        var masked = mask & source;
                        flags = (flags & 0xFFFF_FFDFu) | ((masked >> 15) & 0x20u);
                        flags ^= 0x20u;
                        flags = (flags & 0xFFEF_FFFFu) | ((~masked >> 1) & 0x0010_0000u);
                    }
                    else
                    {
                        flags |= 0x0010_0020u;
                    }

                    flags = ApplyInterpolantTwoBitField(flags, source >> 28, 8);
                    flags = ApplyInterpolantTwoBitField(flags, source >> 30, 21);
                }
            }

            flags = hasMask
                ? ApplyInterpolantFinalMask(flags, source, mask)
                : flags & 0xFFFF_FBE0u;
            if (!TryWriteInterpolantRegister(ctx, registersAddress, inputIndex, flags))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, inputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.create_interpolant_mapping2 regs=0x{registersAddress:X16} " +
            $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
            $"inputs={inputSemanticsCount} outputs={outputSemanticsCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryCreatePrimCxState(
        CpuContext ctx,
        ulong destinationAddress,
        ulong geometrySpecialsAddress,
        ulong hullSpecialsAddress,
        uint primitiveType)
    {
        var stagesAddress = geometrySpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset;
        if (!TryReadUInt32(ctx, stagesAddress, out var stagesOffset) ||
            !TryReadUInt32(ctx, stagesAddress + sizeof(uint), out var stagesValue) ||
            !TryWriteUInt32(ctx, destinationAddress, stagesOffset))
        {
            return false;
        }

        uint outputPrimitiveOffset;
        uint outputPrimitiveValue;
        if ((stagesValue & VgtShaderStagesGsEnableBit) != 0)
        {
            var outputAddress = geometrySpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset;
            if (!TryReadUInt32(ctx, outputAddress, out outputPrimitiveOffset) ||
                !TryReadUInt32(ctx, outputAddress + sizeof(uint), out outputPrimitiveValue))
            {
                return false;
            }
        }
        else
        {
            outputPrimitiveOffset = VgtGsOutPrimType;
            outputPrimitiveValue = AgcPrimitiveHelpers.PrimitiveTypeToGsOut(primitiveType);
        }

        if (hullSpecialsAddress != 0)
        {
            var hullStagesAddress = hullSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset;
            if (!TryReadUInt32(ctx, hullStagesAddress + sizeof(uint), out var hullStagesValue))
            {
                return false;
            }

            stagesValue |= hullStagesValue;
            if ((stagesValue & VgtShaderStagesGsEnableBit) == 0)
            {
                var hullOutputAddress = hullSpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset;
                if (!TryReadUInt32(ctx, hullOutputAddress, out outputPrimitiveOffset) ||
                    !TryReadUInt32(ctx, hullOutputAddress + sizeof(uint), out outputPrimitiveValue))
                {
                    return false;
                }
            }
        }

        return TryWriteUInt32(ctx, destinationAddress + sizeof(uint), stagesValue) &&
               TryWriteUInt32(ctx, destinationAddress + 8, outputPrimitiveOffset) &&
               TryWriteUInt32(ctx, destinationAddress + 12, outputPrimitiveValue);
    }
    private static uint ApplyInterpolantTwoBitField(uint value, uint field, int shift)
    {
        var mask = 0x3u << shift;
        return (value & ~mask) | ((field & 0x3u) << shift);
    }

    private static uint ApplyInterpolantFinalMask(uint flags, uint source, uint mask)
    {
        flags = (flags & 0xFFFF_FFE0u) | ((mask >> 8) & 0x1Fu);
        flags = (flags & 0xFFFF_FBFFu) |
                ((source & 0x0040_0000u) != 0 ? 0x400u : (source >> 14) & 0x400u);
        return flags;
    }
    #pragma warning restore SHEM006

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

        if (registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SPI_PS_INPUT_CNTL maps each PS VINTRP ATTR slot to a VS/GS param export.
        // Walk PS input semantics, find the GS output with the same semantic id,
        // and pack the hardware CNTL word (location in bits [4:0], Flat at 0x400).
        uint inputSemanticsCount = 0;
        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0)
        {
            if (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
                !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out inputSemanticsCount))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (inputSemanticsCount == 0 || inputSemanticsAddress == 0)
        {
            if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.create_interpolant_mapping regs=0x{registersAddress:X16} " +
                $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} inputs=0");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // NumOutputSemantics is a u16 at header +0x56.
        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt16(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        inputSemanticsCount = Math.Min(inputSemanticsCount, 32u);
        for (uint psIndex = 0; psIndex < inputSemanticsCount; psIndex++)
        {
            if (!TryReadUInt32(
                    ctx,
                    inputSemanticsAddress + (psIndex * sizeof(uint)),
                    out var psWord))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var psSemantic = psWord & 0xFFu;
            uint? gsWord = null;
            if (outputSemanticsAddress != 0)
            {
                for (uint gsIndex = 0; gsIndex < outputSemanticsCount; gsIndex++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            outputSemanticsAddress + (gsIndex * sizeof(uint)),
                            out var candidate))
                    {
                        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if ((candidate & 0xFFu) == psSemantic)
                    {
                        gsWord = candidate;
                        break;
                    }
                }
            }

            var value = (psWord & 0x0030_0000u) != 0
                ? CreateInterpolantF16Value(psWord, gsWord)
                : CreateInterpolantNonF16Value(psWord, gsWord.HasValue);
            value = gsWord is { } matched
                ? CreateInterpolantMappingValue(value, psWord, matched)
                : CreateInterpolantDefaultParamValue(value, psWord);

            if (!TryWriteInterpolantRegister(ctx, registersAddress, psIndex, value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, inputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.create_interpolant_mapping regs=0x{registersAddress:X16} " +
            $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
            $"inputs={inputSemanticsCount} outputs={outputSemanticsCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    private static uint ApplyInterpolantDefaultValue(uint value, uint psWord)
    {
        value &= ~0x0000_0300u;
        value |= ((psWord >> 28) & 0x3u) << 8;
        return value;
    }

    private static uint ApplyInterpolantDefaultValueHi(uint value, uint psWord)
    {
        value &= ~0x0060_0000u;
        value |= ((psWord >> 30) & 0x3u) << 21;
        return value;
    }

    private static uint CreateInterpolantMappingValue(uint value, uint psWord, uint gsWord)
    {
        var flatShade =
            (psWord & 0x0040_0000u) != 0 || (psWord & 0x0100_0000u) != 0
                ? 0x0000_0400u
                : 0u;
        value &= ~0x0000_001Fu;
        value |= (gsWord >> 8) & 0x1Fu;
        value &= ~0x0000_0400u;
        value |= flatShade;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantDefaultParamValue(uint value, uint psWord)
    {
        value &= ~0x0000_001Fu;
        value &= ~0x0000_0400u;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantF16Value(uint psWord, uint? gsWord)
    {
        var value = (psWord << 4) & 0x0300_0000u;
        if (gsWord is null)
        {
            value |= 0x0018_0020u;
        }
        else
        {
            var commonWord = psWord & gsWord.Value;
            value &= 0xFFF7_FFDFu;
            value |= (commonWord >> 15) & 0x20u;
            value ^= 0x0008_0020u;
            value &= ~0x0010_0000u;
            value |= (~commonWord >> 1) & 0x0010_0000u;
        }

        return ApplyInterpolantDefaultValueHi(value, psWord);
    }

    private static uint CreateInterpolantNonF16Value(uint psWord, bool hasGsSemantic)
    {
        uint value = 0;
        if ((psWord & 0x0100_0000u) != 0 || !hasGsSemantic)
        {
            value |= 0x20u;
        }

        return value;
    }

    private static bool TryWriteInterpolantRegister(
        CpuContext ctx,
        ulong registersAddress,
        uint index,
        uint value)
    {
        var destination = registersAddress + (index * 8);
        return TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + index) &&
               TryWriteUInt32(ctx, destination + sizeof(uint), value);
    }

    private static bool TryWriteIdentityInterpolantRegisters(
        CpuContext ctx,
        ulong registersAddress,
        uint firstIndex)
    {
        for (uint i = firstIndex; i < 32u; i++)
        {
            if (!TryWriteInterpolantRegister(ctx, registersAddress, i, i))
            {
                return false;
            }
        }

        return true;
    }

    private static OrbisGen2Result PatchShaderProgramRegisters(CpuContext context, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadUInt64(context, headerAddress + ShaderShRegistersOffset, out var shaderRegistersAddress) ||
            !TryReadByte(context, headerAddress + ShaderTypeOffset, out var shaderType) ||
            !TryReadByte(context, headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (shaderType > FunctionShaderType)
        {
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var permitsMissingProgramRegisters = shaderType is GsFrontShaderType or HsFrontShaderType or FunctionShaderType;
        if (registerCount == 0)
        {
            return permitsMissingProgramRegisters ? OrbisGen2Result.ORBIS_GEN2_OK : IncompleteShaderRegistersResult;
        }

        if (shaderRegistersAddress == 0)
        {
            return IncompleteShaderRegistersResult;
        }

        // Read each declared entry before deciding whether the address pair can be absent.
        Span<byte> registerTable = stackalloc byte[registerCount * 2 * sizeof(uint)];
        if (shaderRegistersAddress > ulong.MaxValue - (ulong)registerTable.Length ||
            !context.Memory.TryRead(shaderRegistersAddress, registerTable))
        {
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (shaderType == FunctionShaderType)
        {
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Select the address registers for the shader stage.
        var expectedLowRegister = shaderType switch
        {
            ComputeShaderType => ComputePgmLo,
            PsShaderType => SpiShaderPgmLoPs,
            GsShaderType or GsBackShaderType => SpiShaderPgmLoEs,
            HsShaderType => SpiShaderPgmLoVs,
            GsFrontShaderType => SpiShaderPgmLoGs,
            HsFrontShaderType => SpiShaderPgmLoHs,
            HsBackShaderType => SpiShaderPgmLoLs,
            _ => 0u,
        };
        var expectedHighRegister = shaderType switch
        {
            ComputeShaderType => ComputePgmHi,
            PsShaderType => SpiShaderPgmHiPs,
            GsShaderType or GsBackShaderType => SpiShaderPgmHiEs,
            HsShaderType => SpiShaderPgmHiVs,
            GsFrontShaderType => SpiShaderPgmHiGs,
            HsFrontShaderType => SpiShaderPgmHiHs,
            HsBackShaderType => SpiShaderPgmHiLs,
            _ => 0u,
        };

        if (!TryFindShaderProgramRegisterPair(
                registerTable,
                shaderRegistersAddress,
                registerCount,
                expectedLowRegister,
                expectedHighRegister,
                out var lowEntryAddress,
                out var highEntryAddress,
                out var foundLowRegister,
                out var foundHighRegister,
                out var hasProgramAddressEntries))
        {
            var firstRegisterOffset = BinaryPrimitives.ReadUInt32LittleEndian(registerTable);
            if (permitsMissingProgramRegisters && !hasProgramAddressEntries)
            {
                TraceCreateShader(
                    0,
                    headerAddress,
                    codeAddress,
                    $"skip-pgm-patch type={shaderType} first_lo=0x{firstRegisterOffset:X8}");
                return OrbisGen2Result.ORBIS_GEN2_OK;
            }

            TraceCreateShader(
                0,
                headerAddress,
                codeAddress,
                $"unexpected-registers type={shaderType} expected_lo=0x{expectedLowRegister:X8} first_lo=0x{firstRegisterOffset:X8}");
            return IncompleteShaderRegistersResult;
        }

        var lowValue = (uint)((codeAddress >> 8) & 0xFFFF_FFFFUL);
        var highValue = (uint)((codeAddress >> 40) & 0xFFUL);
        if (!TryWriteUInt32(context, lowEntryAddress + sizeof(uint), lowValue) ||
            !TryWriteUInt32(context, highEntryAddress + sizeof(uint), highValue))
        {
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (foundLowRegister != expectedLowRegister || foundHighRegister != expectedHighRegister)
        {
            TraceCreateShader(
                0,
                headerAddress,
                codeAddress,
                $"patched-alt-registers type={shaderType} lo=0x{foundLowRegister:X8} hi=0x{foundHighRegister:X8}");
        }

        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static readonly (uint Low, uint High)[] ShaderProgramRegisterPairs =
    [
        (ComputePgmLo, ComputePgmHi),
        (SpiShaderPgmLoPs, SpiShaderPgmHiPs),
        (SpiShaderPgmLoVs, SpiShaderPgmHiVs),
        (SpiShaderPgmLoEs, SpiShaderPgmHiEs),
        (SpiShaderPgmLoGs, SpiShaderPgmHiGs),
        (SpiShaderPgmLoHs, SpiShaderPgmHiHs),
        (SpiShaderPgmLoLs, SpiShaderPgmHiLs),
    ];

    private static bool TryFindShaderProgramRegisterPair(
        ReadOnlySpan<byte> registerTable,
        ulong shaderRegistersAddress,
        byte registerCount,
        uint preferredLowRegister,
        uint preferredHighRegister,
        out ulong lowEntryAddress,
        out ulong highEntryAddress,
        out uint foundLowRegister,
        out uint foundHighRegister,
        out bool hasProgramAddressEntries)
    {
        lowEntryAddress = 0;
        highEntryAddress = 0;
        foundLowRegister = 0;
        foundHighRegister = 0;
        hasProgramAddressEntries = false;

        ulong preferredLowAddress = 0;
        ulong preferredHighAddress = 0;
        ulong fallbackLowAddress = 0;
        ulong fallbackHighAddress = 0;
        uint fallbackLowRegister = 0;
        uint fallbackHighRegister = 0;

        for (uint registerIndex = 0; registerIndex < registerCount; registerIndex++)
        {
            var entryAddress = shaderRegistersAddress + ((ulong)registerIndex * 8);
            var registerOffset = BinaryPrimitives.ReadUInt32LittleEndian(registerTable[(int)(registerIndex * 8)..]);
            foreach (var registerPair in ShaderProgramRegisterPairs)
            {
                hasProgramAddressEntries |= registerOffset == registerPair.Low || registerOffset == registerPair.High;
            }

            if (preferredLowRegister != 0 && registerOffset == preferredLowRegister)
            {
                preferredLowAddress = entryAddress;
            }
            else if (preferredHighRegister != 0 && registerOffset == preferredHighRegister)
            {
                preferredHighAddress = entryAddress;
            }

            if (fallbackLowAddress != 0)
            {
                continue;
            }

            foreach (var registerPair in ShaderProgramRegisterPairs)
            {
                if (registerOffset != registerPair.Low)
                {
                    continue;
                }

                // Prefer a contiguous LO/HI pair when present.
                if (registerIndex + 1 < registerCount &&
                    BinaryPrimitives.ReadUInt32LittleEndian(registerTable[(int)((registerIndex + 1) * 8)..]) == registerPair.High)
                {
                    fallbackLowAddress = entryAddress;
                    fallbackHighAddress = entryAddress + 8;
                    fallbackLowRegister = registerPair.Low;
                    fallbackHighRegister = registerPair.High;
                    break;
                }

                for (uint highRegisterIndex = 0; highRegisterIndex < registerCount; highRegisterIndex++)
                {
                    if (highRegisterIndex == registerIndex)
                    {
                        continue;
                    }

                    var highAddress = shaderRegistersAddress + ((ulong)highRegisterIndex * 8);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(registerTable[(int)(highRegisterIndex * 8)..]) != registerPair.High)
                    {
                        continue;
                    }

                    fallbackLowAddress = entryAddress;
                    fallbackHighAddress = highAddress;
                    fallbackLowRegister = registerPair.Low;
                    fallbackHighRegister = registerPair.High;
                    break;
                }

                break;
            }
        }

        if (preferredLowAddress != 0 && preferredHighAddress != 0)
        {
            lowEntryAddress = preferredLowAddress;
            highEntryAddress = preferredHighAddress;
            foundLowRegister = preferredLowRegister;
            foundHighRegister = preferredHighRegister;
            return true;
        }

        if (fallbackLowAddress != 0 && fallbackHighAddress != 0)
        {
            lowEntryAddress = fallbackLowAddress;
            highEntryAddress = fallbackHighAddress;
            foundLowRegister = fallbackLowRegister;
            foundHighRegister = fallbackHighRegister;
            return true;
        }

        return false;
    }

    private static bool IsEsGeometryShaderType(byte shaderType) =>
        shaderType is GsShaderType or GsBackShaderType;

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

    private static bool IsFusedShaderHalfPair(byte frontType, byte backType) =>
        (frontType == GsFrontShaderType && backType == GsBackShaderType) ||
        (frontType == HsFrontShaderType && backType == HsBackShaderType);

    private static bool TryFindShaderRegister(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint registerOffset,
        int occurrence,
        out ulong entryAddress)
    {
        if (registersAddress != 0)
        {
            for (var index = 0; index < registerCount; index++)
            {
                var address = registersAddress + (ulong)index * 8;
                if (!TryReadUInt32(ctx, address, out var current) || current != registerOffset)
                {
                    continue;
                }

                if (occurrence == 0)
                {
                    entryAddress = address;
                    return true;
                }

                occurrence--;
            }
        }

        entryAddress = 0;
        return false;
    }

    // A missing or unpaired lo/hi register is not an error: the retail library
    // leaves absent registers untouched, unlike the create-time patch which
    // requires them.
    private static bool PatchFusedProgramAddress(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint loRegisterOffset,
        ulong codeAddress)
    {
        if (!TryFindShaderRegister(ctx, registersAddress, registerCount, loRegisterOffset, 0, out var loEntry))
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_absent lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        var hiEntry = loEntry + 8;
        if (hiEntry >= registersAddress + (ulong)registerCount * 8 ||
            !TryReadUInt32(ctx, hiEntry, out var hiOffset) ||
            hiOffset != loRegisterOffset + 1)
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_unpaired lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        if (!TryReadUInt32(ctx, hiEntry + sizeof(uint), out var hiValue))
        {
            return false;
        }

        return TryWriteUInt32(ctx, loEntry + sizeof(uint), (uint)(codeAddress >> 8)) &&
               TryWriteUInt32(ctx, hiEntry + sizeof(uint), (hiValue & 0xFFFF_FF00u) | (uint)((codeAddress >> 40) & 0xFFUL));
    }

    private static bool TryWriteByte(CpuContext ctx, ulong address, byte value)
    {
        Span<byte> buffer = [value];
        return ctx.Memory.TryWrite(address, buffer);
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
