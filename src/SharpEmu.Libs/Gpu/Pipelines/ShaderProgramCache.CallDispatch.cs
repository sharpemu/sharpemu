// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderProgramCache
{
    // A ready fallback continues from the caller's current preparation (CallDispatchPreparation) instead of preparing it
    // again. SHARPEMU_CALL_DISPATCH_REUSE=0 turns it off.
    internal bool ReuseCallDispatch { get; set; } =
        Environment.GetEnvironmentVariable("SHARPEMU_CALL_DISPATCH_REUSE") != "0";

    internal long CallDispatchReuses { get; private set; }

    // A continuation of this resolution only, never a cache or an asynchronous payload. The library is captured after
    // its current reads and union update, before selecting the newest tier. A ready fallback still evaluates its own
    // linked resources and record ranges; it need not restart the caller's already completed preparation.
    private readonly record struct CallDispatchPreparation(
        ProgramKey Key, ProgramSourceEntry Entry, ResourceRuntimeInputs Inputs,
        ResourceSnapshot Snapshot, ResourceSpecialization Specialization, ShaderCallLibrary Library);
}
