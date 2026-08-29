// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial implements CPU semantic replacements for recognized compute kernels.
public static partial class AgcExports
{
    private static readonly Dictionary<(ulong Shader, ulong Source, ulong Destination), ulong> _softwareComputeBlitFingerprints = new();

    private static bool IsHtileMetadataWriteOnlyAccess(
        Gen5GlobalMemoryBinding binding,
        IReadOnlyDictionary<uint, string> instructionsByPc)
    {
        if (!binding.Writable || binding.InstructionPcs.Count == 0)
        {
            return false;
        }

        foreach (var pc in binding.InstructionPcs)
        {
            if (!instructionsByPc.TryGetValue(pc, out var opcode) ||
                !IsHtileMetadataWriteOnlyOpcode(opcode))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsHtileMetadataWriteOnlyOpcode(string opcode) =>
        opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("TBufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("GlobalStore", StringComparison.Ordinal) ||
        opcode.StartsWith("FlatStore", StringComparison.Ordinal);

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
        workSequence = GuestGpu.Current.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC masked-copy fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                GuestImageWriteTracker.TrackManagedWriter(
                    destinationAddress,
                    (ulong)output.Length,
                    GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics,
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

    /// <summary>
    /// Semantic CPU replacement for Yotei's constant-fill kernel (v4 =
    /// wgid*64 + tid; BufferStoreFormatXyzw writes s4..s7 at record v4). The
    /// translated Vulkan form measured ~2.2s per dispatch against
    /// microseconds for the CPU fill. Same discipline as the masked-dword-copy
    /// replacement above: full instruction shape and descriptor must match.
    /// </summary>
    private static bool TrySubmitConstantFillKernel(
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
        if (instructions.Count != ConstantFillExpectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(ConstantFillExpectedOpcodes) ||
            !IsExactConstantFillInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 8)
        {
            return false;
        }

        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        var scalars = evaluation.InitialScalarRegisters;
        if (destination is null ||
            destination.BaseAddress == 0 ||
            destination.DataLength < FillRecordBytes ||
            scalars.Count < 8 ||
            !IsExactConstantFillDescriptor(scalars, destination.BaseAddress))
        {
            return false;
        }

        var numRecords = scalars[2];
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableRecords = (uint)(destination.DataLength / FillRecordBytes);
        var outputRecords = (uint)Math.Min(
            Math.Min((ulong)numRecords, dispatchedThreads),
            writableRecords);
        if (outputRecords == 0)
        {
            return false;
        }

        var pattern = new byte[FillRecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(0), scalars[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(4), scalars[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(8), scalars[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(12), scalars[7]);
        var output = new byte[checked((int)outputRecords * FillRecordBytes)];
        var outputWindow = output.AsSpan();
        for (var offset = 0; offset < outputWindow.Length; offset += FillRecordBytes)
        {
            pattern.CopyTo(outputWindow[offset..]);
        }

        var destinationAddress = destination.BaseAddress;
        var isFullFill =
            outputRecords == numRecords &&
            dispatchedThreads == numRecords;
        var isFullUniformFill =
            isFullFill &&
            scalars[4] == scalars[5] &&
            scalars[4] == scalars[6] &&
            scalars[4] == scalars[7];
        var isRepeatedPairFill =
            isFullFill &&
            scalars[4] == scalars[6] &&
            scalars[5] == scalars[7];
        var descriptorByteCount = checked((ulong)numRecords * FillRecordBytes);
        workSequence = VulkanVideoPresenter.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC constant-fill fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                // TryWrite reports the complete write through
                // NotifyManagedWrite before it copies the bytes. Keep this
                // range on the managed-writer path so each constant fill does
                // not protect and immediately fault the same pages again.
                GuestImageWriteTracker.TrackManagedWriter(
                    destinationAddress,
                    (ulong)output.Length,
                    VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.constant-fill");

                if (isFullUniformFill)
                {
                    RecordDccFill(
                        destinationAddress,
                        descriptorByteCount,
                        scalars[4]);
                }
            },
            $"constant_fill dst=0x{destinationAddress:X16} bytes={output.Length}");
        if (workSequence > 0 && isRepeatedPairFill)
        {
            var clearSequence = VulkanVideoPresenter.SubmitGuestImagePatternFromBuffer(
                destinationAddress,
                descriptorByteCount,
                scalars[4],
                scalars[5],
                scalars[6],
                scalars[7]);
            workSequence = Math.Max(workSequence, clearSequence);
        }

        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"records={outputRecords} pattern=0x{scalars[7]:X8}{scalars[6]:X8}{scalars[5]:X8}{scalars[4]:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private const int FillRecordBytes = 4 * sizeof(uint);

    private static readonly string[] ConstantFillExpectedOpcodes =
    [
        "VLshlAddU32",
        "VMovB32",
        "VMovB32",
        "VMovB32",
        "VMovB32",
        "BufferStoreFormatXyzw",
        "SEndpgm",
    ];

    private static bool HasConstantFillOpcodeSequence(Gen5ShaderProgram program) =>
        program.Instructions.Count == ConstantFillExpectedOpcodes.Length &&
        program.Instructions.Select(static instruction => instruction.Opcode)
            .SequenceEqual(ConstantFillExpectedOpcodes);

    private static string GetConstantFillDiagnosticReason(
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ)
    {
        var instructions = program.Instructions;
        if (instructions.Count != ConstantFillExpectedOpcodes.Length)
        {
            return $"instruction-count:{instructions.Count}";
        }

        if (!instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(ConstantFillExpectedOpcodes))
        {
            return "opcode-sequence";
        }

        if (!IsExactConstantFillInstructionShape(instructions))
        {
            return "instruction-shape";
        }

        if (dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0)
        {
            return "base-group";
        }

        if (dispatch.GroupCountY != 1 || dispatch.GroupCountZ != 1)
        {
            return "group-dimensions";
        }

        if (localSizeX != 64 || localSizeY != 1 || localSizeZ != 1)
        {
            return "local-size";
        }

        if (evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 8)
        {
            return "workgroup-register";
        }

        var destinations = evaluation.GlobalMemoryBindings
            .Where(static binding =>
                binding.ScalarAddress == 0 &&
                binding.Writable &&
                binding.WriteBackToGuest)
            .ToArray();
        if (destinations.Length == 0)
        {
            return "destination-missing";
        }

        if (destinations.Length != 1)
        {
            return $"destination-count:{destinations.Length}";
        }

        var destination = destinations[0];
        if (destination.BaseAddress == 0)
        {
            return "destination-base-zero";
        }

        if (destination.DataLength < FillRecordBytes)
        {
            return $"destination-data-short:{destination.DataLength}";
        }

        var scalars = evaluation.InitialScalarRegisters;
        if (scalars.Count < 8)
        {
            return $"scalar-count:{scalars.Count}";
        }

        if (!IsExactConstantFillDescriptor(scalars, destination.BaseAddress))
        {
            return "descriptor-mismatch";
        }

        var numRecords = scalars[2];
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableRecords = (uint)(destination.DataLength / FillRecordBytes);
        var outputRecords = (uint)Math.Min(
            Math.Min((ulong)numRecords, dispatchedThreads),
            writableRecords);
        return outputRecords == 0 ? "output-empty" : "eligible";
    }

    private static bool IsExactConstantFillInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        var globalId = instructions[0];
        var store = instructions[5];
        if (globalId.Destinations.Count != 1 ||
            !IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 4) ||
            globalId.Sources.Count != 3 ||
            !IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 8) ||
            !IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) ||
            !IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0))
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            var move = instructions[1 + index];
            if (move.Destinations.Count != 1 ||
                !IsOperand(
                    move.Destinations[0],
                    Gen5OperandKind.VectorRegister,
                    (uint)index) ||
                move.Sources.Count != 1 ||
                !IsOperand(
                    move.Sources[0],
                    Gen5OperandKind.ScalarRegister,
                    (uint)(4 + index)))
            {
                return false;
            }
        }

        return store.Control is Gen5BufferMemoryControl
        {
            DwordCount: 4,
            OffsetBytes: 0,
            IndexEnabled: true,
            OffsetEnabled: false,
            Glc: false,
            Slc: false,
        } control &&
            control.VectorAddress == 4 &&
            control.VectorData == 0 &&
            control.ScalarResource == 0;
    }

    private static bool IsExactConstantFillDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        ulong expectedBaseAddress)
    {
        var word0 = scalarRegisters[0];
        var word1 = scalarRegisters[1];
        var word3 = scalarRegisters[3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        var matches = baseAddress == expectedBaseAddress &&
            stride == FillRecordBytes &&
            !cacheSwizzle &&
            !swizzleEnabled &&
            unifiedFormat == BufFmt32323232Uint &&
            !addTidEnabled &&
            outOfBoundsSelect == 0 &&
            type == 0 &&
            dstSelectX == 4;
        if (!matches && baseAddress == expectedBaseAddress && _traceAgcShader)
        {
            // Shape matched but the descriptor didn't: dump the raw V# so the
            // constants above can be corrected from evidence, not guessed.
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.constant_fill_descriptor_mismatch " +
                $"dst=0x{expectedBaseAddress:X16} " +
                $"word1=0x{word1:X8} word3=0x{word3:X8} stride={stride} " +
                $"format={unifiedFormat} oob={outOfBoundsSelect} type={type} " +
                $"dst_sel_x={dstSelectX}");
        }

        return matches;
    }

    // RDNA2 table 37: BUF_FMT_32_32_32_32_FLOAT. Bit-preserving, so the raw
    // dword copy has identical semantics to the UINT variant.
    private const uint BufFmt32323232Uint = 75;

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
            texture.Height,
            GetTextureVolumeDepth(texture.Type, texture.Depth));
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
}
