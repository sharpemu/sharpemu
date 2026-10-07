// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler;

public sealed record ShaderCallTarget(ulong Address, ulong Argument, Gen5ShaderProgram Program);
public sealed record ShaderCallSite(uint Pc, uint AddressRegister, uint ArgumentRegister, uint ReturnRegister,
    ulong ReturnAddress, IReadOnlyList<ShaderCallTarget> Targets);
public sealed record ShaderCallMatchControl(uint TargetPc, uint AddressRegister, uint ArgumentRegister,
    ulong Address, ulong Argument) : Gen5InstructionControl;
public sealed record ShaderCallEntryControl(uint ReturnRegister, ulong ReturnAddress,
    uint ArgumentRegister, ulong Argument) : Gen5InstructionControl;
public sealed record ShaderCallFaultControl(uint CallPc, uint AddressRegister) : Gen5InstructionControl;
public sealed record ShaderLinkedBranchControl(uint TargetPc) : Gen5InstructionControl;

public static class Gen5ShaderCallLinker
{
    public static Gen5ShaderProgram Link(Gen5ShaderProgram caller, IReadOnlyList<ShaderCallSite> calls)
    {
        if (caller.IsFusedProgram || caller.Instructions.Any(instruction => instruction.Opcode is "SGetpcB64" or "SSetpcB64"))
            throw new InvalidOperationException("The caller uses an unsupported program-address operation.");
        var instructions = new List<Gen5ShaderInstruction>();
        var functionBuffers = new HashSet<uint>();
        var labels = new Dictionary<(int Body, uint Pc), uint>();
        var branches = new List<(int Index, int Body, uint Target)>();
        var nextBody = 1;
        uint pc = 0;
        var sites = calls.ToDictionary(call => call.Pc);

        void Append(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc = checked(pc + (uint)Math.Max(instruction.Words.Count, 1) * 4);
        }
        void Branch(int body, uint target)
        {
            branches.Add((instructions.Count, body, target));
            Append(new(0, Gen5ShaderEncoding.Sopp, "SBranch", [0xBF820000], [], [], null));
        }
        foreach (var instruction in caller.Instructions)
        {
            labels.Add((0, instruction.Pc), pc);
            if (instruction.Opcode != "SSwappcB64")
            {
                if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target))
                    branches.Add((instructions.Count, 0, target));
                Append(instruction);
                continue;
            }
            if (!sites.TryGetValue(instruction.Pc, out var call) || call.Targets.Count == 0)
                throw new InvalidOperationException($"The shader call has no resolved targets: pc=0x{instruction.Pc:X}.");
            if (call.ReturnRegister > 104 || call.AddressRegister > 104 || call.ArgumentRegister > 104 ||
                call.ReturnRegister < call.ArgumentRegister + 2 && call.ArgumentRegister < call.ReturnRegister + 2)
                throw new InvalidOperationException("The shader call uses overlapping or unsupported register pairs.");
            var returnPc = checked(instruction.Pc + (uint)instruction.Words.Count * 4);
            foreach (var target in call.Targets)
            {
                var body = nextBody++;
                var testIndex = instructions.Count;
                Append(new(0, Gen5ShaderEncoding.Sopp, "SCbranchCallMismatch", [0], [], [],
                    new ShaderCallMatchControl(0, call.AddressRegister, call.ArgumentRegister, target.Address, target.Argument)));
                Append(new(0, Gen5ShaderEncoding.Sop1, "ShaderCallEntry", [0], [], [],
                    new ShaderCallEntryControl(call.ReturnRegister, call.ReturnAddress, call.ArgumentRegister, target.Argument)));
                foreach (var operation in target.Program.Instructions)
                {
                    labels.Add((body, operation.Pc), pc);
                    if (operation.Opcode == "SSetpcB64")
                    {
                        if (operation.Sources.Count != 1 || operation.Sources[0] != Gen5Operand.Scalar(call.ReturnRegister))
                            throw new InvalidOperationException("The shader function has an unresolved return target.");
                        Branch(0, returnPc);
                        continue;
                    }
                    if (operation.Opcode is "SSwappcB64" or "SCallB64" or "SRfeB64" or
                        "SMovreldB32" or "SMovreldB64" or "SMovrelsd2B32" ||
                        operation.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister &&
                            destination.Value < call.ReturnRegister + 2 && destination.Value + operation.DestinationWidth > call.ReturnRegister))
                        throw new InvalidOperationException("The shader function changes its return address or calls another function.");
                    if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(operation, out var destinationPc))
                        branches.Add((instructions.Count, body, destinationPc));
                    if (operation.Opcode == "SGetpcB64")
                        throw new InvalidOperationException("The shader function reads its program address.");
                    if (operation.Control is Gen5BufferMemoryControl || operation.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal))
                        functionBuffers.Add(pc);
                    Append(operation);
                }
                if (target.Program.Instructions.LastOrDefault()?.Opcode is not ("SSetpcB64" or "SEndpgm"))
                    throw new InvalidOperationException("The shader function has no terminal return.");
                var test = instructions[testIndex];
                instructions[testIndex] = test with { Control = ((ShaderCallMatchControl)test.Control!) with { TargetPc = pc } };
            }
            Append(new(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [],
                new ShaderCallFaultControl(call.Pc, call.AddressRegister)));
        }
        foreach (var branch in branches)
        {
            if (!labels.TryGetValue((branch.Body, branch.Target), out var target))
                throw new InvalidOperationException("The linked shader branch has no target.");
            var instruction = instructions[branch.Index];
            instructions[branch.Index] = instruction with { Control = new ShaderLinkedBranchControl(target) };
        }
        return new(caller.Address, instructions) { FunctionBufferAccesses = functionBuffers };
    }
}
