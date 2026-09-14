// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler;

public static partial class Gen5ShaderTranslator
{
    private const uint VertexIndexVectorRegister = 5;
    private static readonly ConditionalWeakTable<Gen5ShaderProgram, VertexOffsetAnalysis> _vertexOffsetAnalyses = new();

    private sealed record VertexOffsetAnalysis(int ScalarRegister, uint[] FetchAddresses);

    // Only a fetch-only index can move to the draw without changing other shader results.
    public static bool TryGetEmbeddedVertexOffsetRegister(
        Gen5ShaderState state,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        out int scalarRegister)
    {
        scalarRegister = -1;
        if (state.UserDataScalarRegisterBase != 8 || vertexInputs.Count == 0)
        {
            return false;
        }

        var analysis = _vertexOffsetAnalyses.GetValue(state.Program, AnalyzeVertexOffset);
        if (analysis.ScalarRegister < state.UserDataScalarRegisterBase ||
            analysis.ScalarRegister - state.UserDataScalarRegisterBase >= state.UserData.Count)
        {
            return false;
        }

        foreach (var fetchAddress in analysis.FetchAddresses)
        {
            var resolved = false;
            for (var inputIndex = 0; inputIndex < vertexInputs.Count; inputIndex++)
            {
                var input = vertexInputs[inputIndex];
                if (!input.PerInstance && (input.Pc == fetchAddress || input.AliasPcs?.Contains(fetchAddress) == true))
                {
                    resolved = true;
                    break;
                }
            }

            if (!resolved)
            {
                return false;
            }
        }

        for (var inputIndex = 0; inputIndex < vertexInputs.Count; inputIndex++)
        {
            var input = vertexInputs[inputIndex];
            if (input.PerInstance)
            {
                continue;
            }

            if (!analysis.FetchAddresses.Contains(input.Pc))
            {
                return false;
            }

            if (input.AliasPcs is { } aliasAddresses)
            {
                for (var aliasIndex = 0; aliasIndex < aliasAddresses.Count; aliasIndex++)
                {
                    if (!analysis.FetchAddresses.Contains(aliasAddresses[aliasIndex]))
                    {
                        return false;
                    }
                }
            }
        }

        scalarRegister = analysis.ScalarRegister;
        return true;
    }

    private static VertexOffsetAnalysis AnalyzeVertexOffset(Gen5ShaderProgram program)
    {
        var rejected = new VertexOffsetAnalysis(-1, []);
        var scalarRegister = -1;
        var fetchAddresses = new List<uint>();
        var writtenScalars = new HashSet<uint>();

        foreach (var instruction in program.Instructions)
        {
            // A branch can skip or repeat the addition. Keep those programs unchanged.
            if (instruction.Opcode.Contains("Branch", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" ||
                instruction.Opcode.Contains("Exec", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode.Contains("Movrel", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode.StartsWith("VCmpX", StringComparison.OrdinalIgnoreCase) ||
                instruction.Control is Gen5Vop3Control { ScalarDestination: >= 126 } ||
                instruction.Destinations.Any(destination =>
                    destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value >= 126))
            {
                return rejected;
            }

            if (instruction.Opcode == "VSadU32" && instruction.Destinations.Count == 1 &&
                instruction.Destinations[0] == Gen5Operand.Vector(VertexIndexVectorRegister) &&
                instruction.Sources.Count == 3 &&
                instruction.Sources[0] is { Kind: Gen5OperandKind.ScalarRegister } offset &&
                instruction.Sources[2] == Gen5Operand.Vector(VertexIndexVectorRegister) &&
                IsZeroVertexOffsetOperand(instruction.Sources[1]) &&
                (instruction.Control is null or Gen5Vop3Control(0, 0, 0, false, 0, null)))
            {
                if (scalarRegister >= 0 || fetchAddresses.Count != 0 || writtenScalars.Contains(offset.Value))
                {
                    return rejected;
                }

                scalarRegister = checked((int)offset.Value);
                continue;
            }

            if (instruction.Control is Gen5BufferMemoryControl buffer)
            {
                if (buffer.OffsetEnabled && buffer.VectorAddress + 1 == VertexIndexVectorRegister)
                {
                    return rejected;
                }

                if (buffer.VectorData <= VertexIndexVectorRegister &&
                    VertexIndexVectorRegister - buffer.VectorData < buffer.DwordCount)
                {
                    return rejected;
                }

                if (buffer.VectorAddress == VertexIndexVectorRegister)
                {
                    if (scalarRegister < 0 || !buffer.IndexEnabled || buffer.OffsetEnabled ||
                        !instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal))
                    {
                        return rejected;
                    }

                    fetchAddresses.Add(instruction.Pc);
                    continue;
                }
            }
            else if (instruction.Control is not (null or Gen5Vop3Control or Gen5ScalarMemoryControl or Gen5ExportControl))
            {
                return rejected;
            }

            var vectorWidth = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
            if (instruction.Sources.Concat(instruction.Destinations).Any(operand =>
                operand.Kind == Gen5OperandKind.VectorRegister && operand.Value <= VertexIndexVectorRegister &&
                VertexIndexVectorRegister - operand.Value < vectorWidth))
            {
                return rejected;
            }

            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination })
            {
                writtenScalars.Add(scalarDestination);
                writtenScalars.Add(scalarDestination + 1);
            }

            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    continue;
                }

                var count = instruction.Control is Gen5ScalarMemoryControl memory
                    ? memory.DestinationCount
                    : instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ? 2u : 1u;
                for (var component = 0u; component < count; component++)
                {
                    writtenScalars.Add(destination.Value + component);
                }
            }
        }

        return scalarRegister >= 0 && fetchAddresses.Count != 0
            ? new VertexOffsetAnalysis(scalarRegister, fetchAddresses.ToArray())
            : rejected;
    }

    private static bool IsZeroVertexOffsetOperand(Gen5Operand operand) =>
        operand is { Kind: Gen5OperandKind.LiteralConstant, Value: 0 } ||
        operand.Kind == Gen5OperandKind.EncodedConstant &&
        Gen5InlineConstants.TryDecode(operand.Value, out var value) && value == 0;
}
