// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// AGC embedded vertex metadata. Locates
/// PtrVertexBufferTable / PtrVertexAttribDescTable and builds authoritative
/// attribute layouts that draw translation merges onto IR-discovered fetches.
/// </summary>
internal static class AgcVertexMetadata
{
    private const ushort IllegalDirectOffset = 0xFFFF;
    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private static readonly ConcurrentDictionary<uint, byte> _reportedUnknownFormats = new();

    internal enum AgcDirectResourceType : uint
    {
        PtrVertexBufferTable = 8,
        PtrVertexAttribDescTable = 10,
        Last = PtrVertexAttribDescTable,
    }

    internal readonly record struct VertexTableRegisters(
        int VertexBufferReg,
        int VertexAttribReg,
        uint InputSemanticsCount,
        ulong InputSemanticsAddress);

    /// <summary>
    /// AGC direct-resource offsets are relative to the stage user-data block.
    /// Convert them to the logical SGPR indices used by the evaluator.
    /// </summary>
    internal static VertexTableRegisters AddUserDataScalarRegisterBase(
        VertexTableRegisters registers,
        uint userDataScalarRegisterBase) =>
        registers with
        {
            VertexBufferReg = checked(
                registers.VertexBufferReg + (int)userDataScalarRegisterBase),
            VertexAttribReg = checked(
                registers.VertexAttribReg + (int)userDataScalarRegisterBase),
        };

    /// <summary>
    /// One AGC attrib-table resource.
    /// Representation: <see cref="SharpBase"/> is the V# base; attribute byte
    /// offset is applied as <see cref="OffsetBytes"/> (Vulkan bind offset),
    /// not folded into the base — avoids double-counting when the IR prolog
    /// already bumped the sharp address.
    /// </summary>
    internal readonly record struct MetadataVertexResource(
        uint Location,
        uint Semantic,
        uint HardwareMapping,
        uint SizeInElements,
        ulong SharpBase,
        uint Stride,
        uint OffsetBytes,
        uint DataFormat,
        uint NumberFormat,
        uint ComponentCount,
        bool PerInstance,
        MetadataFormatState FormatState = MetadataFormatState.Known);

    internal enum MetadataFormatState
    {
        NoOverride,
        Known,
        Unknown,
    }

    /// <summary>
    /// Reads AGC user-data direct-resource offsets for the ES header mapped to
    /// <paramref name="shaderCodeAddress"/>. Returns false when the header is
    /// unknown or the tables are absent (attribute-less clears).
    /// </summary>
    internal static bool TryGetVertexTableRegisters(
        CpuContext ctx,
        ulong shaderCodeAddress,
        ulong shaderHeaderAddress,
        out VertexTableRegisters registers)
    {
        registers = new VertexTableRegisters(-1, -1, 0, 0);
        if (shaderHeaderAddress == 0 ||
            !TryReadUInt64(ctx, shaderHeaderAddress + ShaderUserDataOffset, out var userDataAddress) ||
            userDataAddress == 0)
        {
            return false;
        }

        // ShaderUserData layout:
        //   0x00: uint16_t* direct_resource_offset
        //   0x08: sharp_resource_offset[4]
        //   0x28: eud_size_dw, srt_size_dw
        //   0x2C: direct_resource_count
        if (!TryReadUInt64(ctx, userDataAddress, out var directResourceOffset) ||
            !TryReadUInt16(ctx, userDataAddress + 0x2C, out var directResourceCount))
        {
            return false;
        }

        var maxTypes = (uint)AgcDirectResourceType.Last + 1u;
        if (directResourceCount > maxTypes || directResourceOffset == 0)
        {
            return false;
        }

        var vertexBufferReg = -1;
        var vertexAttribReg = -1;
        for (uint type = 0; type < directResourceCount; type++)
        {
            if (!TryReadUInt16(
                    ctx,
                    directResourceOffset + (type * sizeof(ushort)),
                    out var reg) ||
                reg == IllegalDirectOffset)
            {
                continue;
            }

            switch ((AgcDirectResourceType)type)
            {
                case AgcDirectResourceType.PtrVertexBufferTable:
                    vertexBufferReg = reg;
                    break;
                case AgcDirectResourceType.PtrVertexAttribDescTable:
                    vertexAttribReg = reg;
                    break;
            }
        }

        if (vertexBufferReg < 0 || vertexAttribReg < 0)
        {
            return false;
        }

        if (!TryReadUInt64(
                ctx,
                shaderHeaderAddress + ShaderInputSemanticsOffset,
                out var inputSemanticsAddress) ||
            !TryReadUInt32(
                ctx,
                shaderHeaderAddress + ShaderNumInputSemanticsOffset,
                out var inputSemanticsCount) ||
            inputSemanticsCount == 0 ||
            inputSemanticsAddress == 0)
        {
            return false;
        }

        registers = new VertexTableRegisters(
            vertexBufferReg,
            vertexAttribReg,
            inputSemanticsCount,
            inputSemanticsAddress);
        return true;
    }

    /// <summary>
    /// Builds attrib resources from AGC input_semantics + tables.
    /// ShaderSemantic packing:
    ///   bits [7:0]   semantic          → attrib table index
    ///   bits [15:8]  hardware_mapping  → VGPR destination
    ///   bits [19:16] size_in_elements
    /// </summary>
    internal static bool TryBuildVertexResourcesFromMetadata(
        CpuContext ctx,
        IReadOnlyList<uint> scalarRegisters,
        VertexTableRegisters tables,
        out IReadOnlyList<MetadataVertexResource> resources)
    {
        resources = Array.Empty<MetadataVertexResource>();
        if (tables.VertexAttribReg < 0 ||
            tables.VertexBufferReg < 0 ||
            tables.VertexAttribReg + 1 >= scalarRegisters.Count ||
            tables.VertexBufferReg + 1 >= scalarRegisters.Count ||
            tables.InputSemanticsCount == 0)
        {
            return false;
        }

        var attribTable =
            ((ulong)scalarRegisters[tables.VertexAttribReg + 1] << 32) |
            scalarRegisters[tables.VertexAttribReg];
        var bufferTable =
            ((ulong)scalarRegisters[tables.VertexBufferReg + 1] << 32) |
            scalarRegisters[tables.VertexBufferReg];
        if (attribTable == 0 || bufferTable == 0)
        {
            return false;
        }

        var built = new List<MetadataVertexResource>((int)tables.InputSemanticsCount);
        for (uint i = 0; i < tables.InputSemanticsCount; i++)
        {
            if (!TryReadUInt32(
                    ctx,
                    tables.InputSemanticsAddress + (i * sizeof(uint)),
                    out var semanticWord))
            {
                return false;
            }

            // Attrib index is semantic bits [7:0], not hardware_mapping.
            var semantic = semanticWord & 0xFFu;
            var hardwareMapping = (semanticWord >> 8) & 0xFFu;
            var sizeInElements = (semanticWord >> 16) & 0xFu;
            if (!TryReadUInt32(ctx, attribTable + (semantic * sizeof(uint)), out var attribWord))
            {
                return false;
            }

            // Attrib dword: buffer index [4:0], format [13:5], offset [25:14], fetch [26].
            var bufferIndex = attribWord & 0x1Fu;
            var format = (attribWord >> 5) & 0x1FFu;
            var offset = (attribWord >> 14) & 0xFFFu;
            var fetchIndex = (attribWord >> 26) & 0x1u;
            var sharpAddress = bufferTable + (bufferIndex * 16u);
            if (!TryReadUInt32(ctx, sharpAddress, out var sharp0) ||
                !TryReadUInt32(ctx, sharpAddress + 4, out var sharp1))
            {
                return false;
            }

            var sharpBase = sharp0 | ((ulong)(sharp1 & 0xFFFFu) << 32);
            var stride = (sharp1 >> 16) & 0x3FFFu;
            if (sharpBase == 0 || stride == 0)
            {
                continue;
            }

            var fallbackComponents = sizeInElements != 0 ? sizeInElements : 4u;
            var formatState = TryMapAttribFormat(
                format,
                fallbackComponents,
                out var dataFormat,
                out var numberFormat,
                out var components);
            if (formatState == MetadataFormatState.Unknown &&
                _reportedUnknownFormats.TryAdd(format, 0))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] agc.vertex_metadata_format_unknown " +
                    $"semantic={semantic} hardware_mapping={hardwareMapping} " +
                    $"attrib_word=0x{attribWord:X8} format={format} " +
                    "— preserving the format discovered from the shader.");
            }

            built.Add(new MetadataVertexResource(
                Location: i,
                Semantic: semantic,
                HardwareMapping: hardwareMapping,
                SizeInElements: sizeInElements,
                SharpBase: sharpBase,
                Stride: stride,
                OffsetBytes: offset,
                DataFormat: dataFormat,
                NumberFormat: numberFormat,
                ComponentCount: components,
                PerInstance: fetchIndex != 0,
                FormatState: formatState));
        }

        if (built.Count == 0)
        {
            return false;
        }

        resources = built;
        return true;
    }

    /// <summary>
    /// Patch IR-discovered fetches from the attrib table onto the V# layout.
    /// Prefer 1:1 Location pairing when counts match on one interleaved stream
    /// (GTA UI glyphs). Otherwise match by the effective captured byte offset.
    /// Never rebases BaseAddress/Data/Location/Pc or overwrites a discovered
    /// offset: metadata may refine the format, stride and instance rate only
    /// after both address keys independently resolve to the same attribute.
    /// </summary>
    internal static IReadOnlyList<Gen5VertexInputBinding> MergeVertexInputsFromMetadata(
        CpuContext ctx,
        IReadOnlyList<uint> scalarRegisters,
        VertexTableRegisters tables,
        Gen5ShaderProgram program,
        IReadOnlyList<Gen5VertexInputBinding> discovered) =>
        MergeVertexInputsFromMetadata(
            ctx,
            scalarRegisters,
            tables,
            discovered,
            program);

    internal static IReadOnlyList<Gen5VertexInputBinding> MergeVertexInputsFromMetadata(
        CpuContext ctx,
        IReadOnlyList<uint> scalarRegisters,
        VertexTableRegisters tables,
        IReadOnlyList<Gen5VertexInputBinding> discovered)
        => MergeVertexInputsFromMetadata(
            ctx,
            scalarRegisters,
            tables,
            discovered,
            program: null);

    private static IReadOnlyList<Gen5VertexInputBinding> MergeVertexInputsFromMetadata(
        CpuContext ctx,
        IReadOnlyList<uint> scalarRegisters,
        VertexTableRegisters tables,
        IReadOnlyList<Gen5VertexInputBinding> discovered,
        Gen5ShaderProgram? program)
    {
        if (discovered.Count == 0 ||
            !TryBuildVertexResourcesFromMetadata(
                ctx,
                scalarRegisters,
                tables,
                out var resources))
        {
            return discovered;
        }

        // Metadata may only refine a binding when the association is validated.
        // A known format can replace the discovered format. The kInvalid
        // sentinel preserves the discovered format but can still refine the
        // offset and input rate. An unknown nonzero format preserves the complete
        // discovered binding.
        //
        // The association key is hardware_mapping matched against the fetch
        // destination VGPR, which is the same key Kyty uses. The resolved byte
        // offset is a second, independent key. When the two disagree there is
        // no basis for preferring either, so the pair is ambiguous and the
        // discovered binding stands: a wrong overlay silently rewrites the
        // attribute's format and offset, which is worse than leaving the
        // shader-derived values alone.
        var hardwareAssignments = program is null
            ? null
            : BuildHardwareMappingAssignments(
                program,
                discovered,
                resources);
        if (hardwareAssignments is null)
        {
            // Without the program there is no destination VGPR to associate
            // against, so no assignment can be validated.
            return discovered;
        }

        var locationAssignments = TryBuildCompleteNoOverrideAddressAssignments(
            discovered,
            resources,
            out var completeLocationAssignments)
            ? completeLocationAssignments
            : null;

        var merged = new List<Gen5VertexInputBinding>(discovered.Count);
        var changed = false;
        for (var inputIndex = 0; inputIndex < discovered.Count; inputIndex++)
        {
            var input = discovered[inputIndex];
            var refined = input;
            var resourceIndex = hardwareAssignments[inputIndex];
            if (resourceIndex >= 0)
            {
                var resource = resources[resourceIndex];
                if (resource.FormatState != MetadataFormatState.Unknown &&
                    TryValidateMetadataOffset(input, resource))
                {
                    refined = ApplyMetadata(input, resource);
                }
            }

            if (locationAssignments is not null)
            {
                var locationResource = resources[locationAssignments[inputIndex]];
                refined = refined with
                {
                    Location = locationResource.Location,
                    PerInstance = locationResource.PerInstance,
                };
            }

            changed |= refined != input;
            merged.Add(refined);
        }

        return changed ? merged : discovered;
    }

    /// <summary>
    /// Matches AGC hardware_mapping to the VGPR written by each discovered
    /// fetch. A binding can represent more than one fetch PC, so aliases take
    /// part in the match. Stream identity still has to agree. An assignment is
    /// accepted only when it is unique in both directions.
    /// </summary>
    private static int[] BuildHardwareMappingAssignments(
        Gen5ShaderProgram program,
        IReadOnlyList<Gen5VertexInputBinding> discovered,
        IReadOnlyList<MetadataVertexResource> resources)
    {
        var fetchVectorDataByPc = new Dictionary<uint, uint>();
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5BufferMemoryControl { IndexEnabled: true } control &&
                (instruction.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
                 instruction.Opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal)))
            {
                fetchVectorDataByPc[instruction.Pc] = control.VectorData;
            }
        }

        var assignments = new int[discovered.Count];
        Array.Fill(assignments, -1);
        var resourceUseCounts = new int[resources.Count];
        for (var inputIndex = 0; inputIndex < discovered.Count; inputIndex++)
        {
            var input = discovered[inputIndex];
            var vectorDestinations = new HashSet<uint>();
            if (fetchVectorDataByPc.TryGetValue(input.Pc, out var vectorData))
            {
                vectorDestinations.Add(vectorData);
            }

            foreach (var aliasPc in input.AliasPcs ?? [])
            {
                if (fetchVectorDataByPc.TryGetValue(aliasPc, out vectorData))
                {
                    vectorDestinations.Add(vectorData);
                }
            }

            if (vectorDestinations.Count == 0)
            {
                continue;
            }

            var candidateIndex = -1;
            for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
            {
                var candidate = resources[resourceIndex];
                if (!vectorDestinations.Contains(candidate.HardwareMapping) ||
                    !IsCompatibleVertexStream(input, candidate))
                {
                    continue;
                }

                if (candidateIndex >= 0)
                {
                    candidateIndex = -1;
                    break;
                }

                candidateIndex = resourceIndex;
            }

            if (candidateIndex >= 0)
            {
                assignments[inputIndex] = candidateIndex;
                resourceUseCounts[candidateIndex]++;
            }
        }

        for (var inputIndex = 0; inputIndex < assignments.Length; inputIndex++)
        {
            var resourceIndex = assignments[inputIndex];
            if (resourceIndex >= 0 && resourceUseCounts[resourceIndex] != 1)
            {
                assignments[inputIndex] = -1;
            }
        }

        return assignments;
    }

    /// <summary>
    /// Associates a complete format-0 table by exact byte address. Format 0
    /// does not override the evaluated format. The locations can be used only
    /// when every input and every output location is unique.
    /// </summary>
    private static bool TryBuildCompleteNoOverrideAddressAssignments(
        IReadOnlyList<Gen5VertexInputBinding> discovered,
        IReadOnlyList<MetadataVertexResource> resources,
        out int[] assignments)
    {
        assignments = new int[discovered.Count];
        Array.Fill(assignments, -1);
        var resourceUseCounts = new int[resources.Count];
        for (var inputIndex = 0; inputIndex < discovered.Count; inputIndex++)
        {
            var candidateIndex = -1;
            for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
            {
                var candidate = resources[resourceIndex];
                if (candidate.FormatState != MetadataFormatState.NoOverride ||
                    !IsCompatibleVertexStream(discovered[inputIndex], candidate) ||
                    !TryValidateMetadataOffset(discovered[inputIndex], candidate))
                {
                    continue;
                }

                if (candidateIndex >= 0)
                {
                    return false;
                }

                candidateIndex = resourceIndex;
            }

            if (candidateIndex < 0)
            {
                return false;
            }

            assignments[inputIndex] = candidateIndex;
            resourceUseCounts[candidateIndex]++;
        }

        var locations = new HashSet<uint>();
        for (var inputIndex = 0; inputIndex < assignments.Length; inputIndex++)
        {
            var resourceIndex = assignments[inputIndex];
            if (resourceUseCounts[resourceIndex] != 1 ||
                !locations.Add(resources[resourceIndex].Location))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCompatibleVertexStream(
        Gen5VertexInputBinding input,
        MetadataVertexResource resource) =>
        (resource.Stride == 0 || resource.OffsetBytes < resource.Stride) &&
        IsSameVertexStream(input, resource);

    private static bool IsSameVertexStream(
        Gen5VertexInputBinding input,
        MetadataVertexResource resource)
    {
        if (input.BaseAddress == resource.SharpBase ||
            input.BaseAddress == resource.SharpBase + resource.OffsetBytes)
        {
            return true;
        }

        return IsAddressInsideCapturedSpan(input, resource.SharpBase);
    }

    /// <summary>
    /// Second, independent check on a hardware-mapping association: the byte
    /// offset the metadata resolves to must agree with what discovery found.
    /// </summary>
    private static bool TryValidateMetadataOffset(
        Gen5VertexInputBinding input,
        MetadataVertexResource resource)
    {
        if (!TryGetMetadataOffset(input, resource, out var resolved))
        {
            return false;
        }

        return resolved == input.OffsetBytes;
    }

    private static Gen5VertexInputBinding ApplyMetadata(
        Gen5VertexInputBinding input,
        MetadataVertexResource resource)
    {
        if (resource.FormatState == MetadataFormatState.NoOverride)
        {
            return input with
            {
                Stride = resource.Stride,
                PerInstance = resource.PerInstance,
            };
        }

        var components = input.ComponentCount != 0 &&
                         input.ComponentCount < resource.ComponentCount
            ? input.ComponentCount
            : resource.ComponentCount;

        return input with
        {
            DataFormat = resource.DataFormat,
            NumberFormat = resource.NumberFormat,
            ComponentCount = components,
            Stride = resource.Stride,
            PerInstance = resource.PerInstance,
        };
    }

    /// <summary>
    /// Legacy entry point — forwards to <see cref="MergeVertexInputsFromMetadata"/>.
    /// </summary>
    internal static IReadOnlyList<Gen5VertexInputBinding> RefineVertexInputs(
        CpuContext ctx,
        IReadOnlyList<uint> scalarRegisters,
        VertexTableRegisters tables,
        IReadOnlyList<Gen5VertexInputBinding> discovered) =>
        MergeVertexInputsFromMetadata(ctx, scalarRegisters, tables, discovered);

    /// <summary>
    /// Collects SBufferLoad / SLoad PCs that read the AGC attrib or buffer
    /// tables (embedded-fetch prolog). Those loads are executed on the
    /// CPU during scalar evaluation; once vertex inputs are bound they must
    /// not run again as live SSBOs on the GPU.
    /// </summary>
    internal static HashSet<uint> CollectFetchPrologPcs(
        Gen5ShaderProgram program,
        VertexTableRegisters tables)
    {
        var pcs = new HashSet<uint>();
        if (tables.VertexAttribReg < 0 || tables.VertexBufferReg < 0)
        {
            return pcs;
        }

        var tableRegs = new HashSet<uint>
        {
            (uint)tables.VertexAttribReg,
            (uint)tables.VertexAttribReg + 1u,
            (uint)tables.VertexBufferReg,
            (uint)tables.VertexBufferReg + 1u,
        };

        foreach (var instruction in program.Instructions)
        {
            var isScalarLoad =
                instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SLoad", StringComparison.Ordinal);
            if (!isScalarLoad)
            {
                continue;
            }

            // SMEM loads encode the scalar base pointer in Sources[0].
            if (instruction.Sources.Count > 0 &&
                instruction.Sources[0] is
                {
                    Kind: Gen5OperandKind.ScalarRegister,
                    Value: var scalarBase,
                } &&
                tableRegs.Contains(scalarBase))
            {
                pcs.Add(instruction.Pc);
                continue;
            }

            if (instruction.Control is Gen5BufferMemoryControl buffer &&
                tableRegs.Contains(buffer.ScalarResource))
            {
                pcs.Add(instruction.Pc);
            }
        }

        return pcs;
    }

    private static bool TryGetMetadataOffset(
        Gen5VertexInputBinding input,
        MetadataVertexResource resource,
        out uint offsetBytes)
    {
        offsetBytes = input.OffsetBytes;
        if (resource.SharpBase < input.BaseAddress ||
            (!IsAddressInsideCapturedSpan(input, resource.SharpBase) &&
             resource.SharpBase != input.BaseAddress))
        {
            return false;
        }

        var relativeBase = resource.SharpBase - input.BaseAddress;
        var resolvedOffset = relativeBase + resource.OffsetBytes;
        if (resolvedOffset > uint.MaxValue)
        {
            return false;
        }

        offsetBytes = (uint)resolvedOffset;
        return true;
    }

    private static bool IsAddressInsideCapturedSpan(
        Gen5VertexInputBinding input,
        ulong address) =>
        input.DataLength > 0 &&
        address >= input.BaseAddress &&
        address < input.BaseAddress + (ulong)input.DataLength;

    /// <summary>
    /// Attrib-table format
    /// fields are VertexAttribFormat; V# / Vulkan paths need BufferFormat.
    /// Unknown values pass through (already BufferFormat).
    /// </summary>
    private static uint VertexAttribFormatToBufferFormat(uint format) =>
        format switch
        {
            0 => 0,     // Invalid
            4 => 1,     // k8UNorm
            8 => 2,     // k8SNorm
            12 => 3,    // k8UScaled
            16 => 4,    // k8SScaled
            20 => 5,    // k8UInt
            24 => 6,    // k8SInt
            28 => 7,    // k16UNorm
            32 => 8,    // k16SNorm
            36 => 9,    // k16UScaled
            40 => 10,   // k16SScaled
            44 => 11,   // k16UInt
            48 => 12,   // k16SInt
            52 => 13,   // k16Float
            57 => 14,   // k8_8UNorm
            61 => 15,   // k8_8SNorm
            65 => 16,   // k8_8UScaled
            69 => 17,   // k8_8SScaled
            73 => 18,   // k8_8UInt
            77 => 19,   // k8_8SInt
            80 => 20,   // k32UInt
            84 => 21,   // k32SInt
            88 => 22,   // k32Float
            93 => 23,   // k16_16UNorm
            97 => 24,   // k16_16SNorm
            101 => 25,  // k16_16UScaled
            105 => 26,  // k16_16SScaled
            109 => 27,  // k16_16UInt
            113 => 28,  // k16_16SInt
            117 => 29,  // k16_16Float
            122 => 30,  // k11_11_10UNorm
            126 => 31,
            130 => 32,
            134 => 33,
            138 => 34,
            142 => 35,
            146 => 36,
            150 => 37,  // k10_11_11UNorm
            154 => 38,
            158 => 39,
            162 => 40,
            166 => 41,
            170 => 42,
            174 => 43,
            179 => 44,  // k2_10_10_10UNorm
            183 => 45,
            187 => 46,
            191 => 47,
            195 => 48,
            199 => 49,
            203 => 50,  // k10_10_10_2UNorm
            207 => 51,
            211 => 52,
            215 => 53,
            219 => 54,
            223 => 55,
            227 => 56,  // k8_8_8_8UNorm
            231 => 57,
            235 => 58,
            239 => 59,
            243 => 60,
            247 => 61,
            249 => 62,  // k32_32UInt
            253 => 63,
            257 => 64,  // k32_32Float
            263 => 65,  // k16_16_16_16UNorm
            267 => 66,
            271 => 67,
            275 => 68,
            279 => 69,
            283 => 70,
            287 => 71,  // k16_16_16_16Float
            290 => 72,  // k32_32_32UInt
            294 => 73,
            298 => 74,
            303 => 75,  // k32_32_32_32UInt
            307 => 76,
            311 => 77,  // k32_32_32_32Float
            _ => format,
        };

    /// <summary>
    /// Maps an attrib-table format onto GNM (DataFormat, NumberFormat,
    /// Components). Returns false when the value is neither a
    /// VertexAttribFormat nor a BufferFormat, in which case the caller must
    /// keep the format discovered from the shader. Synthesizing a format here
    /// is never safe: an unrecognised value used to fall through to
    /// R32G32B32A32_SFLOAT, which silently widened float3 attributes to float4
    /// and read past the end of every one of them.
    /// </summary>
    private static MetadataFormatState TryMapAttribFormat(
        uint attribFormat,
        uint fallbackComponents,
        out uint dataFormat,
        out uint numberFormat,
        out uint components)
    {
        // kInvalid means this metadata entry does not override the format.
        // Association, offset, and input-rate data remain valid.
        if (attribFormat == 0)
        {
            dataFormat = 0;
            numberFormat = 0;
            components = Math.Clamp(fallbackComponents, 1u, 4u);
            return MetadataFormatState.NoOverride;
        }

        // No VertexAttribFormat overrides live here. The two that used to
        // (113, 121) contradicted the SDK: sce::Agc::Core::VertexAttribute
        // defines k16_16SInt = 113 and k16_16Float = 117, and both are already
        // handled correctly by the conversion table below.
        var bufferFormat = VertexAttribFormatToBufferFormat(attribFormat);

        // Prospero::BufferFormat numeric values (gpu_defs.h).
        (uint DataFormat, uint NumberFormat, uint Components)? mapped = bufferFormat switch
        {
            1 => (1, 0, 1),   // k8UNorm
            2 => (1, 1, 1),   // k8SNorm
            3 => (1, 2, 1),   // k8UScaled
            4 => (1, 3, 1),   // k8SScaled
            5 => (1, 4, 1),   // k8UInt
            6 => (1, 5, 1),   // k8SInt
            7 => (2, 0, 1),   // k16UNorm
            8 => (2, 1, 1),   // k16SNorm
            9 => (2, 2, 1),   // k16UScaled
            10 => (2, 3, 1),  // k16SScaled
            11 => (2, 4, 1),  // k16UInt
            12 => (2, 5, 1),  // k16SInt
            13 => (2, 7, 1),  // k16Float
            14 => (3, 0, 2),  // k8_8UNorm
            15 => (3, 1, 2),  // k8_8SNorm
            16 => (3, 2, 2),  // k8_8UScaled
            17 => (3, 3, 2),  // k8_8SScaled
            18 => (3, 4, 2),  // k8_8UInt
            19 => (3, 5, 2),  // k8_8SInt
            20 => (4, 4, 1),  // k32UInt
            21 => (4, 5, 1),  // k32SInt
            22 => (4, 7, 1),  // k32Float
            23 => (5, 0, 2),  // k16_16UNorm
            24 => (5, 1, 2),  // k16_16SNorm
            25 => (5, 2, 2),  // k16_16UScaled
            26 => (5, 3, 2),  // k16_16SScaled
            27 => (5, 4, 2),  // k16_16UInt
            28 => (5, 5, 2),  // k16_16SInt
            29 => (5, 7, 2),  // k16_16Float
            50 => (9, 0, 4),  // k10_10_10_2UNorm
            51 => (9, 1, 4),  // k10_10_10_2SNorm
            56 => (10, 0, 4), // k8_8_8_8UNorm
            57 => (10, 1, 4), // k8_8_8_8SNorm
            58 => (10, 2, 4), // k8_8_8_8UScaled
            59 => (10, 3, 4), // k8_8_8_8SScaled
            60 => (10, 4, 4), // k8_8_8_8UInt
            61 => (10, 5, 4), // k8_8_8_8SInt
            62 => (11, 4, 2), // k32_32UInt
            63 => (11, 5, 2), // k32_32SInt
            64 => (11, 7, 2), // k32_32Float
            65 => (12, 0, 4), // k16_16_16_16UNorm
            66 => (12, 1, 4), // k16_16_16_16SNorm
            67 => (12, 2, 4), // k16_16_16_16UScaled
            68 => (12, 3, 4), // k16_16_16_16SScaled
            69 => (12, 4, 4), // k16_16_16_16UInt
            70 => (12, 5, 4), // k16_16_16_16SInt
            71 => (12, 7, 4), // k16_16_16_16Float
            72 => (13, 4, 3), // k32_32_32UInt
            73 => (13, 5, 3), // k32_32_32SInt
            74 => (13, 7, 3), // k32_32_32Float
            75 => (14, 4, 4), // k32_32_32_32UInt
            76 => (14, 5, 4), // k32_32_32_32SInt
            77 => (14, 7, 4), // k32_32_32_32Float
            _ => null,
        };

        if (mapped is not { } resolved)
        {
            dataFormat = 0;
            numberFormat = 0;
            // m_sizeInElements is a separate field from the format and is
            // corroborated by the contiguous hardware_mapping VGPR allocation,
            // so it stays usable even when the format does not resolve.
            components = Math.Clamp(fallbackComponents, 1u, 4u);
            return MetadataFormatState.Unknown;
        }

        (dataFormat, numberFormat, components) = resolved;
        return MetadataFormatState.Known;
    }

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }
}
