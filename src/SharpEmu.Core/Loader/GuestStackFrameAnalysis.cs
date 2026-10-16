// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;

namespace SharpEmu.Core.Loader;

internal static class GuestStackFrameAnalysis
{
    private readonly record struct StackState(long? Stack, long? Frame);

    internal static bool UsesFrameRedZone(IReadOnlyList<Instruction> instructions)
    {
        if (instructions.Count == 0 || !instructions.Any(static instruction =>
                instruction.MemoryBase == Register.RBP && unchecked((long)instruction.MemoryDisplacement64) < 0))
            return false;

        var indices = new Dictionary<ulong, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++) indices[instructions[index].IP] = index;
        var states = new StackState?[instructions.Count];
        var pending = new Queue<int>();
        var queued = new bool[instructions.Count];
        var factory = new InstructionInfoFactory();
        states[0] = new StackState(0, null);
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.TryDequeue(out var index))
        {
            queued[index] = false;
            var instruction = instructions[index];
            var next = Transfer(states[index]!.Value, instruction, factory);
            switch (instruction.FlowControl)
            {
                case FlowControl.Next:
                case FlowControl.Call:
                case FlowControl.IndirectCall:
                    Merge(index + 1, next);
                    break;
                case FlowControl.ConditionalBranch:
                    Merge(index + 1, next);
                    if (indices.TryGetValue(instruction.NearBranchTarget, out var conditionalTarget)) Merge(conditionalTarget, next);
                    break;
                case FlowControl.UnconditionalBranch:
                    if (indices.TryGetValue(instruction.NearBranchTarget, out var target)) Merge(target, next);
                    break;
                case FlowControl.IndirectBranch:
                    // Unknown targets can enter any instruction with unknown stack state.
                    for (var targetIndex = 0; targetIndex < instructions.Count; targetIndex++) Merge(targetIndex, new(null, null));
                    break;
            }
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (states[index] is not { Stack: long stack, Frame: long frame } ||
                instruction.MemoryBase != Register.RBP || instruction.MemoryIndex != Register.None ||
                instruction.Mnemonic is Mnemonic.Lea or Mnemonic.Nop)
                continue;
            var displacement = unchecked((long)instruction.MemoryDisplacement64);
            if (displacement < -0x100000 || displacement > 0x100000) continue;
            var relative = frame + displacement - stack;
            if (relative < 0 && relative >= -128) return true;
        }
        return false;

        void Merge(int index, StackState incoming)
        {
            if (index >= instructions.Count) return;
            var merged = states[index] is { } previous
                ? new StackState(previous.Stack == incoming.Stack ? previous.Stack : null,
                    previous.Frame == incoming.Frame ? previous.Frame : null)
                : incoming;
            if (states[index] == merged) return;
            states[index] = merged;
            if (!queued[index])
            {
                queued[index] = true;
                pending.Enqueue(index);
            }
        }
    }

    private static StackState Transfer(StackState state, in Instruction instruction, InstructionInfoFactory factory)
    {
        var stack = state.Stack;
        var frame = state.Frame;
        var information = factory.GetInfo(instruction);
        foreach (var register in information.GetUsedRegisters())
        {
            if (register.Access is not (OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite)) continue;
            if (register.Register is Register.RSP or Register.ESP or Register.SP or Register.SPL) stack = null;
            if (register.Register is Register.RBP or Register.EBP or Register.BP or Register.BPL) frame = null;
        }

        if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            stack = state.Stack;
        else if (instruction.Mnemonic is Mnemonic.Push or Mnemonic.Pushfq or Mnemonic.Pop or Mnemonic.Popfq)
        {
            if (!(instruction.Mnemonic == Mnemonic.Pop && instruction.Op0Register == Register.RSP))
                stack = Add(state.Stack, instruction.StackPointerIncrement);
        }
        else if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op1Kind == OpKind.Register)
        {
            if (instruction.Op0Register == Register.RBP && instruction.Op1Register == Register.RSP) frame = state.Stack;
            if (instruction.Op0Register == Register.RSP && instruction.Op1Register == Register.RBP) stack = state.Frame;
        }
        else if (instruction.Op0Register == Register.RSP && instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub &&
                 instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64)
        {
            var adjustment = instruction.Op1Kind == OpKind.Immediate8to64 ? instruction.Immediate8to64 : instruction.Immediate32to64;
            stack = Add(state.Stack, instruction.Mnemonic == Mnemonic.Sub ? -adjustment : adjustment);
        }
        else if (instruction.Mnemonic == Mnemonic.Leave)
        {
            stack = Add(state.Frame, 8);
            frame = null;
        }
        return new(stack, frame);
    }

    private static long? Add(long? value, long adjustment)
    {
        if (value is not long offset || adjustment < -0x100000 || adjustment > 0x100000) return null;
        var result = offset + adjustment;
        return result is >= -0x100000 and <= 0x100000 ? result : null;
    }
}
