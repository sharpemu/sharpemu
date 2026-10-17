// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The draw and dispatch recognisers kept beside the ported cache: solid clears, fills and one copy kernel.
internal sealed partial class ShaderPipelineCache
{
    // A float32x3 vertex position stream.
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;
    private const uint PositionExportTarget = 12;
    private const uint ColorExportTarget = 0;
    private const int WorkGroupRegisterOfCopyKernel = 12;
    private const uint MaxCopyKernelSourceBytes = 16 * 1024 * 1024;

    private static readonly bool _premultipliedFillClearEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"), "1", StringComparison.Ordinal);

    // A procedural fullscreen vertex program paired with a constant-color pixel program.
    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderProgram vertexProgram,
        VertexInputInfo vertexInfo,
        ShaderStageResources vertexStage,
        Gen5ShaderProgram pixelProgram,
        ShaderStageResources pixelStage)
    {
        var vertexResources = vertexStage.Program?.Resources?.Info;
        var pixelResources = pixelStage.Program?.Resources?.Info;
        if (vertexResources is null || pixelResources is null || vertexInfo.Attributes.Length != 0 ||
            vertexResources.Images.Count != 0 || pixelResources.Images.Count != 0 ||
            vertexResources.Buffers.Count != 0 || pixelResources.Buffers.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(vertexProgram, PositionExportTarget) || !HasExportTarget(pixelProgram, ColorExportTarget))
        {
            return false;
        }

        if (pixelProgram.Instructions.Count is 0 or > 8 || vertexProgram.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelProgram.Instructions.All(IsBenignClearPixelInstruction) &&
               vertexProgram.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderProgram program, uint target) =>
        program.Instructions.Any(instruction => instruction.Control is Gen5ExportControl export && export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is "SNop" or "SWaitcnt" or "SInstPrefetch" or "SEndpgm" or "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: ColorExportTarget };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or Gen5ImageControl or Gen5GlobalMemoryControl or Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position plus the primitive and parameter exports.
            return export.Target is PositionExportTarget or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or "SWaitcnt" or "SInstPrefetch" or "SEndpgm" or "SSendmsg" or
            "VMovB32" or "VAndB32" or "VAddI32" or "VLshlrevB32" or "VCvtF32I32" or "VCvtF32U32" ||
            instruction.Encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc or Gen5ShaderEncoding.Sopk or Gen5ShaderEncoding.Sopp;
    }

    // Opaque white unless the first user register carries a finite color in [0, 4].
    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(IReadOnlyList<uint> pixelUserData)
    {
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelUserData.Count > 0)
        {
            var bits = pixelUserData[0];
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

    // An untextured transparent fill through premultiplied blending overwrites its targets.
    private static bool IsTransparentPremultipliedFill(
        ContextRegisters context,
        Gen5PixelOutputBinding[] outputs,
        VertexInputInfo vertexInfo,
        ShaderStageResources vertexStage,
        ShaderStageResources pixelStage)
    {
        var vertexResources = vertexStage.Program?.Resources?.Info;
        var pixelResources = pixelStage.Program?.Resources?.Info;
        var pixelUserData = pixelStage.Resources.UserData;
        if (!_premultipliedFillClearEnabled || vertexResources is null || pixelResources is null ||
            vertexResources.Images.Count != 0 || pixelResources.Images.Count != 0 ||
            vertexInfo.Attributes.Length != 0 || pixelUserData.Length < 4 || outputs.Length == 0)
        {
            return false;
        }

        foreach (var output in outputs)
        {
            var blend = context.BlendControls[output.GuestSlot];
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

    // Replaces only the exact masked-copy kernel shape; its writes land in command order over the guest range.
    private bool TrySubmitMaskedDwordCopyKernel(
        Gen5ShaderProgram program,
        ShaderSource source,
        Gen5ComputeSystemRegisters systemRegisters,
        ComputeInputInfo input,
        out string description)
    {
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "SMovB32", "STtraceData", "SInstPrefetch", "VLshlAddU32", "SBufferLoadDword", "SWaitcnt", "VCmpxGtU32", "SCbranchExecz",
            "SBufferLoadDword", "SWaitcnt", "VAndB32", "BufferLoadFormatX", "SWaitcnt", "BufferStoreFormatX", "SEndpgm",
        ];
        var groupsX = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsX, input.ThreadsX) : input.DispatchThreadsX;
        var groupsY = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsY, input.ThreadsY) : input.DispatchThreadsY;
        var groupsZ = input.DispatchThreadDimensions ? RenderExecutor.GroupsFromThreads(input.DispatchThreadsZ, input.ThreadsZ) : input.DispatchThreadsZ;
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode).SequenceEqual(expectedOpcodes) ||
            !IsExactMaskedDwordCopyInstructionShape(instructions) ||
            groupsY != 1 || groupsZ != 1 ||
            input.ThreadsX != 64 || input.ThreadsY != 1 || input.ThreadsZ != 1 ||
            systemRegisters.WorkGroupXRegister != WorkGroupRegisterOfCopyKernel)
        {
            return false;
        }

        var userData = source.UserData;
        if (userData.Length < 12 ||
            !IsExactMaskedDwordCopyDescriptor(userData, 0, out var sourceWords) ||
            !IsExactMaskedDwordCopyDescriptor(userData, 4, out var destinationWords) ||
            !IsExactMaskedDwordCopyDescriptor(userData, 8, out var controlWords))
        {
            return false;
        }

        var controlAddress = controlWords.Address;
        var sourceAddress = sourceWords.Address;
        var destinationAddress = destinationWords.Address;
        var destinationBytes = destinationWords.Footprint() ?? 0;
        var sourceBytes = sourceWords.Footprint() ?? 0;
        var controlBytes = controlWords.Footprint() ?? 0;
        if (controlAddress == 0 || sourceAddress == 0 || destinationAddress == 0 ||
            controlBytes < 2 * sizeof(uint) || sourceBytes < sizeof(uint) || destinationBytes < sizeof(uint))
        {
            return false;
        }

        Span<byte> control = stackalloc byte[2 * sizeof(uint)];
        if (!_context.Memory.TryRead(controlAddress, control))
        {
            return false;
        }

        var elementCount = BinaryPrimitives.ReadUInt32LittleEndian(control);
        var sourceMask = BinaryPrimitives.ReadUInt32LittleEndian(control[sizeof(uint)..]);
        var dispatchedThreads = Math.Min((ulong)uint.MaxValue, (ulong)groupsX * input.ThreadsX);
        var writableDwords = (uint)Math.Min(destinationBytes / sizeof(uint), uint.MaxValue);
        var outputDwords = (uint)Math.Min(Math.Min((ulong)elementCount, dispatchedThreads), writableDwords);
        if (outputDwords == 0)
        {
            return false;
        }

        var neededSourceDwords = sourceMask == 0 ? 1UL : Math.Min((ulong)sourceMask + 1, outputDwords);
        var readableSourceBytes = Math.Min(Math.Min(sourceBytes, MaxCopyKernelSourceBytes), neededSourceDwords * sizeof(uint));
        var sourceData = new byte[readableSourceBytes - readableSourceBytes % sizeof(uint)];
        if (sourceData.Length < sizeof(uint) || !_context.Memory.TryRead(sourceAddress, sourceData))
        {
            return false;
        }

        var output = new byte[checked((int)outputDwords * sizeof(uint))];
        var outputWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(output.AsSpan());
        var sourceWordsSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(sourceData.AsSpan());
        if (sourceMask == 0)
        {
            outputWords.Fill(sourceWordsSpan[0]);
        }
        else
        {
            for (uint index = 0; index < outputDwords; index++)
            {
                var sourceIndex = index & sourceMask;
                outputWords[(int)index] = sourceIndex < (uint)sourceWordsSpan.Length ? sourceWordsSpan[(int)sourceIndex] : 0;
            }
        }

        // The dispatch runs in stream order on the worker, so the replacement writes at once.
        if (!_context.Memory.TryWrite(destinationAddress, output))
        {
            Console.Error.WriteLine($"[LOADER][ERROR] AGC masked-copy fast path failed dst=0x{destinationAddress:X16} bytes={output.Length}");
            return false;
        }

        description = $"dst=0x{destinationAddress:X16} bytes={output.Length} elements={elementCount} mask=0x{sourceMask:X8} dispatch={groupsX}x{input.ThreadsX}";
        return true;
    }

    private static bool IsExactMaskedDwordCopyInstructionShape(IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(Gen5Operand operand, Gen5OperandKind kind, uint value) => operand.Kind == kind && operand.Value == value;

        static bool IsBufferControl(Gen5ShaderInstruction instruction, uint vectorAddress, uint vectorData, uint scalarResource) =>
            instruction.Control is Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false } control &&
            control.VectorAddress == vectorAddress && control.VectorData == vectorData && control.ScalarResource == scalarResource;

        static bool IsScalarLoad(Gen5ShaderInstruction instruction, int offsetBytes) =>
            instruction.Control is Gen5ScalarMemoryControl { DestinationCount: 1, DynamicOffsetRegister: null } control &&
            control.ImmediateOffsetBytes == offsetBytes &&
            instruction.Destinations.Count == 1 && IsOperand(instruction.Destinations[0], Gen5OperandKind.ScalarRegister, 106) &&
            instruction.Sources.Count >= 1 && IsOperand(instruction.Sources[0], Gen5OperandKind.ScalarRegister, 8);

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

    // Structured 32-bit records without thread-index or swizzle address changes.
    private static bool IsExactMaskedDwordCopyDescriptor(uint[] userData, int scalarBase, out BufferDescriptorWords descriptor)
    {
        descriptor = BufferDescriptorWords.From(userData.AsSpan(scalarBase, 4));
        var cacheSwizzle = (descriptor.Word1 & (1u << 30)) != 0;
        return descriptor.Stride == sizeof(uint) &&
               !cacheSwizzle &&
               !descriptor.SwizzleEnabled &&
               descriptor.Format == 20 &&
               !descriptor.AddThreadId &&
               descriptor.OutOfBounds == 0 &&
               descriptor.Type == 0 &&
               descriptor.DestinationSelectX == 4;
    }
}
