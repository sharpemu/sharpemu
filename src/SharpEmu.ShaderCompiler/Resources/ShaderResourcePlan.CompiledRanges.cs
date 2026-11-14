// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ShaderResourcePlan
{
    // The compiled device-address ranges (CompiledDeviceAddressRanges), published with the compiled evaluator.
    private CompiledDeviceAddressRanges? _compiledRanges;

    internal CompiledDeviceAddressRanges? CompiledRanges
    {
        get
        {
            _ = CompiledEvaluator;
            return Volatile.Read(ref _compiledEvaluatorState) == 2 ? _compiledRanges : null;
        }
    }

    // Runs under the compile gate before the compiled state is published; it never throws.
    private void CompileRangesNow() => _compiledRanges = CompiledDeviceAddressRanges.TryBuild(this);
}
