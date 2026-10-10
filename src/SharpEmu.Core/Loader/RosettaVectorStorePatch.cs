// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Iced.Intel;

namespace SharpEmu.Core.Loader;

// Use two 128-bit stores to let Rosetta report a memory fault.
// Keep the source register, general registers, and flags unchanged.
internal static class RosettaVectorStorePatch
{
    internal static bool IsRequired { get; } = IsRosettaProcess();

    private static unsafe bool IsRosettaProcess()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }

        int translationStatus = 0;
        nuint valueByteCount = sizeof(int);
        return ReadSystemControlValue("sysctl.proc_translated", &translationStatus, &valueByteCount, null, 0) == 0 && translationStatus == 1;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname")]
    private static extern unsafe int ReadSystemControlValue(string controlName, void* valueBuffer, nuint* valueByteCount, void* newValueBuffer, nuint newValueByteCount);

    internal static bool RequiresStoreSplit(in Instruction instruction) =>
        (instruction.Code is Code.VEX_Vmovups_ymmm256_ymm or Code.VEX_Vmovupd_ymmm256_ymm or Code.VEX_Vmovdqu_ymmm256_ymm &&
         instruction.Op0Kind == OpKind.Memory && instruction.Op1Register is >= Register.YMM0 and <= Register.YMM15) ||
        IsHalfConvertStore(instruction);

    // Rosetta aborts ("unexpectedly need to EmulateForward on a synchronous exception") when
    // the memory form of VCVTPS2PH faults, e.g. on a page SharpEmu write-protects for GPU
    // tracking. Convert into a scratch register and store that instead.
    private static bool IsHalfConvertStore(in Instruction instruction) =>
        instruction.Code is Code.VEX_Vcvtps2ph_xmmm128_ymm_imm8 or Code.VEX_Vcvtps2ph_xmmm64_xmm_imm8 &&
        instruction.Op0Kind == OpKind.Memory;

    // Stays below the 128-byte red zone and keeps RSP 16-byte aligned relative to entry.
    private const int ScratchFrameBytes = 0xA0;

    private static void AddHalfConvertStore(List<Instruction> output, in Instruction instruction)
    {
        var source = instruction.Op1Register;
        var sourceIndex = source >= Register.YMM0 ? source - Register.YMM0 : source - Register.XMM0;
        var scratchIndex = sourceIndex == 15 ? 14 : 15;
        var scratchXmm = Register.XMM0 + scratchIndex;
        var scratchYmm = Register.YMM0 + scratchIndex;
        var ip = instruction.IP;

        void Add(Instruction added)
        {
            // Keep branch targets at the first instruction and give the others distinct addresses.
            added.IP = ip++;
            output.Add(added);
        }

        Add(Instruction.Create(Code.Lea_r64_m, Register.RSP, new MemoryOperand(Register.RSP, -ScratchFrameBytes)));
        Add(Instruction.Create(Code.VEX_Vmovdqu_ymmm256_ymm, new MemoryOperand(Register.RSP), scratchYmm));
        Add(instruction.Code == Code.VEX_Vcvtps2ph_xmmm128_ymm_imm8
            ? Instruction.Create(Code.VEX_Vcvtps2ph_xmmm128_ymm_imm8, scratchXmm, source, instruction.Immediate8)
            : Instruction.Create(Code.VEX_Vcvtps2ph_xmmm64_xmm_imm8, scratchXmm, source, instruction.Immediate8));

        var store = instruction;
        store.Code = instruction.Code == Code.VEX_Vcvtps2ph_xmmm128_ymm_imm8
            ? Code.VEX_Vmovdqu_xmmm128_xmm
            : Code.VEX_Vmovq_xmmm64_xmm;
        store.Op1Register = scratchXmm;
        if (instruction.MemoryBase == Register.RSP)
        {
            store.MemoryDisplacement64 = unchecked(instruction.MemoryDisplacement64 + ScratchFrameBytes);
            store.MemoryDisplSize = 8;
        }
        Add(store);

        Add(Instruction.Create(Code.VEX_Vmovdqu_ymm_ymmm256, scratchYmm, new MemoryOperand(Register.RSP)));
        Add(Instruction.Create(Code.Lea_r64_m, Register.RSP, new MemoryOperand(Register.RSP, ScratchFrameBytes)));
    }

    internal static IList<Instruction> SplitVectorStores(IList<Instruction> instructions)
    {
        var splitInstructions = new List<Instruction>(instructions.Count * 2);
        foreach (var instruction in instructions)
        {
            if (!RequiresStoreSplit(instruction))
            {
                splitInstructions.Add(instruction);
                continue;
            }

            if (IsHalfConvertStore(instruction))
            {
                AddHalfConvertStore(splitInstructions, instruction);
                continue;
            }

            var lowerStore = instruction;
            lowerStore.Code = Code.VEX_Vmovups_xmmm128_xmm;
            lowerStore.Op1Register = Register.XMM0 + (instruction.Op1Register - Register.YMM0);
            splitInstructions.Add(lowerStore);

            var upperStore = instruction;
            upperStore.Code = Code.VEX_Vextractf128_xmmm128_ymm_imm8;
            upperStore.Op2Kind = OpKind.Immediate8;
            upperStore.Immediate8 = 1;
            upperStore.MemoryDisplacement64 = unchecked(instruction.MemoryDisplacement64 + 16);
            // Use the address size for the displacement size that Iced requires.
            upperStore.MemoryDisplSize = instruction.MemoryBase is >= Register.EAX and <= Register.R15D ||
                                        instruction.MemoryIndex is >= Register.EAX and <= Register.R15D ? 4 : 8;
            // Give the added instruction a separate address for the block encoder.
            // Keep branch targets at the first store.
            upperStore.IP = instruction.IP + 1;
            splitInstructions.Add(upperStore);
        }

        return splitInstructions;
    }
}
