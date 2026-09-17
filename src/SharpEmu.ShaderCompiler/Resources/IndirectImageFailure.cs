// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Captures the rejected table without reading guest memory a second time.
public sealed record IndirectImageFailure(
    uint InstructionAddress,
    uint RootResource,
    uint ExemplarResource,
    uint IncompatibleResource,
    uint SelectorStride,
    uint SelectorOffset,
    uint[] MaterialDescriptor,
    uint[] HeapDescriptor,
    uint[] Keys,
    uint[] CandidateIndices,
    uint[][] TableDescriptors,
    uint[][] ImageDescriptors,
    ImageSpecialization[] ImageSpecializations,
    uint[] UserData)
{
    public IndirectSelectorDiagnostic? SelectorDiagnostic { get; init; }
    public MaterializedScalarRead[] ScalarReads { get; init; } = [];
}

public sealed record MaterializedScalarRead(uint InstructionAddress, uint ComponentIndex, uint TableOffset, uint Value);

// Records existing reads only; diagnostic collection must not synchronize guest memory.
public sealed class IndirectSelectorDiagnostic
{
    public string SelectionMode { get; internal set; } = "full_domain_no_proof";
    public string? EvaluationFailure { get; internal set; }
    public uint[] SelectorIndices { get; internal set; } = [];
    public uint[] ProvenOffsets { get; internal set; } = [];
    public ulong? FailedReadAddress { get; internal set; }
    public List<SelectorMemoryRead> MemoryReads { get; } = [];
    public int OmittedMemoryReads { get; internal set; }
    public List<SelectorKeyProbe> KeyProbes { get; } = [];
}

public sealed record SelectorMemoryRead(ulong Address, bool Succeeded, uint? Value);
public sealed record SelectorKeyProbe(uint Offset, uint Key);
