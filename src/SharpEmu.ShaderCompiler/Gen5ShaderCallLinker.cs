// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler;

public sealed record ShaderCallTarget(ulong Address, ulong Argument, Gen5ShaderProgram Program);
public sealed record ShaderCallSite(uint Pc, uint AddressRegister, uint ArgumentRegister, uint ReturnRegister,
    ulong ReturnAddress, IReadOnlyList<ShaderCallTarget> Targets);
// With RuntimeArgument, the test of the callee alone: every record of it matches.
public sealed record ShaderCallMatchControl(uint TargetPc, uint AddressRegister, uint ArgumentRegister,
    ulong Address, ulong Argument) : Gen5InstructionControl
{
    public bool RuntimeArgument { get; init; }
}

// With RuntimeArgument, the entry of Callee's copy for every record of it: the argument registers keep the caller's
// value, through which the copy reads its record's descriptors (CallRecordSelector).
public sealed record ShaderCallEntryControl(uint ReturnRegister, ulong ReturnAddress,
    uint ArgumentRegister, ulong Argument) : Gen5InstructionControl
{
    public bool RuntimeArgument { get; init; }

    public ulong Callee { get; init; }
}
// With ReturnPc (a program linked with run-time arguments), a branch there: a record of a callee the program does not hold
// returns from the call at once, as a newer program would have run it; otherwise the end of the wave.
public sealed record ShaderCallFaultControl(uint CallPc, uint AddressRegister) : Gen5InstructionControl
{
    public uint? ReturnPc { get; init; }
}
// An S_GETPC_B64 copied from the function at FunctionAddress, FunctionOffset bytes into it.
public sealed record ShaderFunctionPcControl(ulong FunctionAddress, ulong FunctionOffset) : Gen5InstructionControl;
public sealed record ShaderLinkedBranchControl(uint TargetPc) : Gen5InstructionControl;
// A call site's test of one callee: it branches to TargetPc unless the record the site found for the caller's target
// and argument belongs to Group.
public sealed record ShaderCallGroupControl(uint TargetPc, uint Group) : Gen5InstructionControl;

public static class Gen5ShaderCallLinker
{
    // With runtimeArguments, a site links one copy of each callee for all of its records, so the program does not depend on
    // the records; otherwise one copy per record, whose argument the plan reads.
    public static Gen5ShaderProgram Link(Gen5ShaderProgram caller, IReadOnlyList<ShaderCallSite> calls, bool runtimeArguments = false)
    {
        if (caller.IsFusedProgram || caller.Instructions.Any(instruction => instruction.Opcode is "SGetpcB64" or "SSetpcB64"))
            throw new InvalidOperationException("The caller uses an unsupported program-address operation.");
        var instructions = new List<Gen5ShaderInstruction>();
        var functionBuffers = new HashSet<uint>();
        var labels = new Dictionary<(int Body, uint Pc), uint>();
        var branches = new List<(int Index, int Body, uint Target)>();
        var faultReturns = new List<(int Index, uint Target)>();
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
            // A site whose table holds only null records has no target: it links to its fault, which the
            // caller's own test of the target keeps it from reaching.
            if (!sites.TryGetValue(instruction.Pc, out var call))
                throw new InvalidOperationException($"The shader call has no resolved targets: pc=0x{instruction.Pc:X}.");
            if (call.ReturnRegister > 104 || call.AddressRegister > 104 || call.ArgumentRegister > 104 ||
                call.ReturnRegister < call.ArgumentRegister + 2 && call.ArgumentRegister < call.ReturnRegister + 2)
                throw new InvalidOperationException("The shader call uses overlapping or unsupported register pairs.");
            var returnPc = checked(instruction.Pc + (uint)instruction.Words.Count * 4);
            foreach (var target in runtimeArguments ? call.Targets.DistinctBy(static target => target.Address) : call.Targets)
            {
                var body = nextBody++;
                var testIndex = instructions.Count;
                var argument = runtimeArguments ? 0 : target.Argument;
                Append(new(0, Gen5ShaderEncoding.Sopp, "SCbranchCallMismatch", [0], [], [],
                    new ShaderCallMatchControl(0, call.AddressRegister, call.ArgumentRegister, target.Address, argument)
                    {
                        RuntimeArgument = runtimeArguments,
                    }));
                Append(new(0, Gen5ShaderEncoding.Sop1, "ShaderCallEntry", [0], [], [],
                    new ShaderCallEntryControl(call.ReturnRegister, call.ReturnAddress, call.ArgumentRegister, argument)
                    {
                        RuntimeArgument = runtimeArguments,
                        Callee = target.Address,
                    }));
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
                    if (operation.Control is Gen5BufferMemoryControl || operation.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal))
                        functionBuffers.Add(pc);
                    // S_GETPC_B64 reads the dispatch's shader base, the caller's address, plus the instruction's offset:
                    // the function's copy keeps its distance from the caller, so the function reads its own address. The
                    // control names the function, so a module can read its address with the call targets instead.
                    Append(operation.Opcode == "SGetpcB64"
                        ? operation with
                        {
                            AddressOffset = unchecked(target.Address - caller.Address + operation.ProgramOffset),
                            Control = new ShaderFunctionPcControl(target.Address, operation.ProgramOffset),
                        }
                        : operation);
                }
                if (target.Program.Instructions.LastOrDefault()?.Opcode is not ("SSetpcB64" or "SEndpgm"))
                    throw new InvalidOperationException("The shader function has no terminal return.");
                var test = instructions[testIndex];
                instructions[testIndex] = test with { Control = ((ShaderCallMatchControl)test.Control!) with { TargetPc = pc } };
            }
            if (runtimeArguments)
            {
                faultReturns.Add((instructions.Count, returnPc));
                Append(new(0, Gen5ShaderEncoding.Sopp, "SBranch", [0xBF820000], [], [],
                    new ShaderCallFaultControl(call.Pc, call.AddressRegister)));
            }
            else
            {
                Append(new(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [],
                    new ShaderCallFaultControl(call.Pc, call.AddressRegister)));
            }
        }
        foreach (var branch in branches)
        {
            if (!labels.TryGetValue((branch.Body, branch.Target), out var target))
                throw new InvalidOperationException("The linked shader branch has no target.");
            var instruction = instructions[branch.Index];
            instructions[branch.Index] = instruction with { Control = new ShaderLinkedBranchControl(target) };
        }
        foreach (var (index, returnPc) in faultReturns)
        {
            if (!labels.TryGetValue((0, returnPc), out var target))
                throw new InvalidOperationException("The shader call has no return point.");
            var fault = instructions[index];
            instructions[index] = fault with { Control = ((ShaderCallFaultControl)fault.Control!) with { ReturnPc = target } };
        }
        return new(caller.Address, instructions) { FunctionBufferAccesses = functionBuffers };
    }

    // The callees of a linked program, each once, in the order its sites test them. The module compares a record's target
    // with these addresses, and a function reads its own address from them; the host pushes them with each dispatch
    // (BindingLayout.CallTargetsDword), so the module's code does not depend on where the game placed its functions.
    public static ulong[] CallTargets(Gen5ShaderProgram program) =>
        [.. program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallMatchControl>()
            .Select(match => match.Address).Distinct()];

    // The same addresses from the call sites a program is linked from.
    public static ulong[] CallTargets(IReadOnlyList<ShaderCallSite> calls) =>
        [.. calls.OrderBy(call => call.Pc).SelectMany(call => call.Targets).Select(target => target.Address).Distinct()];

    // The distinct nonzero arguments a linked program's call entries write, in the order the entries first name them:
    // the order of the user-data registers the value graph reads them from (ScalarValueGraph.CallArgumentRegisterBase),
    // and of the arguments the host pushes for the module to test records against (BindingLayout.CallArgumentsDword).
    // Entries that share an argument share its registers, so their descriptors stay one resource.
    public static ulong[] CallEntryArguments(Gen5ShaderProgram program) =>
        [.. program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()
            .Select(entry => entry.Argument).Where(argument => argument != 0).Distinct()];

    // Whether a program was linked with its shader calls: every site of a linked program holds call entries or a fault.
    public static bool IsLinked(Gen5ShaderProgram program) =>
        program.Instructions.Any(static instruction => instruction.Control is ShaderCallEntryControl or ShaderCallFaultControl);
}
