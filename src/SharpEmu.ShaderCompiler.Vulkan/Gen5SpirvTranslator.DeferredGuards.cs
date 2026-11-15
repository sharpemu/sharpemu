// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // A vector register write under a partial EXEC keeps the old value in the lanes EXEC leaves out:
        // select(active, new, old). While EXEC and the SPIR-V block stay the same, those lanes of a register are
        // read only by instructions that read other lanes or address memory through VGPR values, so one select
        // per register and region is enough. A write in the region stores the new value in every lane; the first
        // one remembers the value the register had when the region began. The select is emitted when EXEC
        // changes, before the block ends or calls, and before an instruction that reads other lanes.
        // SHARPEMU_DEFERRED_EXEC_GUARDS=0 guards every write again.
        private static readonly bool DeferredExecGuards = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_EXEC_GUARDS"), "0", StringComparison.Ordinal);

        private bool _deferGuards;
        private readonly List<(uint Register, uint Old)> _deferredGuards = [];
        private readonly HashSet<uint> _deferredGuardRegisters = [];

        // A half wave keeps one guest lane per invocation, as a native wave does.
        private bool CanDeferGuards =>
            DeferredExecGuards && !_pairedLanes && !_pairedMeshLanes && !_hasIndirectControlFlow &&
            _stage != Gen5SpirvStage.Mesh && _waveLowering is NativeWave32Lowering or NativeWave64Lowering or HalfWave64Lowering;

        // A guarded write inside the current region: the new value goes to every lane.
        private bool TryDeferGuard(uint register, uint value)
        {
            if (!_deferGuards)
            {
                return false;
            }

            if (_deferredGuardRegisters.Add(register))
            {
                _deferredGuards.Add((register, LoadV(register)));
            }

            Store(VectorPointer(register), value);
            return true;
        }

        // Ends the region: the lanes EXEC left out get back the values their registers had when it began.
        private void FlushDeferredGuards()
        {
            if (_deferredGuards.Count == 0)
            {
                return;
            }

            var active = Load(_boolType, _exec);
            foreach (var (register, old) in _deferredGuards)
            {
                Store(VectorPointer(register), _module.AddInstruction(SpirvOp.Select, _uintType, active, LoadV(register), old));
            }

            _deferredGuards.Clear();
            _deferredGuardRegisters.Clear();
        }

        // Instructions that can read the vector registers of other lanes (DPP, lane reads and writes, permutes,
        // image derivatives, exports), or that address memory through VGPR values outside bounds-checked bindings.
        private static bool ReadsOtherLanes(Gen5ShaderInstruction instruction) =>
            instruction.Control is Gen5DppControl or Gen5Dpp8Control or Gen5DataShareControl { Gds: true } ||
            instruction.Encoding is Gen5ShaderEncoding.Mimg or Gen5ShaderEncoding.Flat or Gen5ShaderEncoding.Exp ||
            instruction.Opcode is "VReadlaneB32" or "VReadfirstlaneB32" or "VWritelaneB32" or "VPermlane16B32" or
                "VPermlanex16B32" or "DsSwizzleB32" or "DsBpermuteB32" or "DsPermuteB32" ||
            instruction.Opcode.StartsWith("VMovrel", StringComparison.Ordinal);
    }
}
