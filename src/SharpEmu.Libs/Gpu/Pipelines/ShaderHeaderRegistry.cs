// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The header data of one registered shader, and its continuation when two objects were joined.
public sealed record RegisteredShader(
    ulong CodeAddress,
    ulong HeaderAddress,
    uint CodeSizeBytes,
    uint ScratchDwords,
    ulong UserDataAddress,
    ulong InputSemanticsAddress,
    uint InputSemanticsCount,
    ulong ContinuationAddress,
    uint ContinuationSizeBytes)
{
    public bool IsFused => ContinuationAddress != 0;

    // The code ranges the identity covers, in program order.
    public (ulong Address, uint SizeBytes)[] CodeRanges =>
        IsFused ? [(CodeAddress, CodeSizeBytes), (ContinuationAddress, ContinuationSizeBytes)] : [(CodeAddress, CodeSizeBytes)];

    public uint TotalCodeSizeBytes => CodeSizeBytes + ContinuationSizeBytes;
}

// The two code objects the guest joined into one program; the entry ends in a jump to the continuation.
public readonly record struct FusedProgramParts(ulong ContinuationAddress, ulong ContinuationHeaderAddress);

// Resolves the header the guest registered for a code address; a missing header stops the draw.
public sealed class ShaderHeaderRegistry
{
    private const ulong UserDataOffset = 0x08;
    private const ulong InputSemanticsOffset = 0x30;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong InputSemanticsCountOffset = 0x50;
    private const ulong ScratchDwordsPerThreadOffset = 0x54;

    private readonly CpuContext _context;
    private readonly Func<ulong, ulong> _headerOf;
    private readonly Func<ulong, FusedProgramParts?> _fusedPartsOf;

    public ShaderHeaderRegistry(CpuContext context, Func<ulong, ulong> headerOf, Func<ulong, FusedProgramParts?> fusedPartsOf)
    {
        _context = context;
        _headerOf = headerOf;
        _fusedPartsOf = fusedPartsOf;
    }

    public RegisteredShader Require(ulong codeAddress, string label)
    {
        var headerAddress = _headerOf(codeAddress);
        if (headerAddress == 0)
        {
            throw SubmissionScheduler.Fatal($"The shader has no registered header: label={label} shader=0x{codeAddress:X16}.");
        }

        var codeSize = RequireCodeSize(headerAddress, codeAddress, label);
        if (!_context.TryReadUInt16(headerAddress + ScratchDwordsPerThreadOffset, out var scratchDwords) ||
            !_context.TryReadUInt64(headerAddress + UserDataOffset, out var userDataAddress) ||
            !_context.TryReadUInt64(headerAddress + InputSemanticsOffset, out var inputSemanticsAddress) ||
            !_context.TryReadUInt32(headerAddress + InputSemanticsCountOffset, out var inputSemanticsCount))
        {
            throw SubmissionScheduler.Fatal($"The shader header is unreadable: label={label} shader=0x{codeAddress:X16} header=0x{headerAddress:X16}.");
        }

        var continuationAddress = 0ul;
        var continuationSize = 0u;
        if (_fusedPartsOf(codeAddress) is { } parts)
        {
            continuationAddress = parts.ContinuationAddress;
            continuationSize = RequireCodeSize(parts.ContinuationHeaderAddress, parts.ContinuationAddress, label);
        }

        return new RegisteredShader(
            codeAddress,
            headerAddress,
            codeSize,
            scratchDwords,
            userDataAddress,
            inputSemanticsAddress,
            inputSemanticsCount,
            continuationAddress,
            continuationSize);
    }

    private uint RequireCodeSize(ulong headerAddress, ulong codeAddress, string label)
    {
        if (!_context.TryReadUInt32(headerAddress + ShaderSizeOffset, out var codeSize))
        {
            throw SubmissionScheduler.Fatal($"The shader header is unreadable: label={label} shader=0x{codeAddress:X16} header=0x{headerAddress:X16}.");
        }

        if (codeSize == 0 || codeSize % sizeof(uint) != 0)
        {
            throw SubmissionScheduler.Fatal($"The shader header carries an invalid code size: label={label} shader=0x{codeAddress:X16} size=0x{codeSize:X8}.");
        }

        return codeSize;
    }
}
