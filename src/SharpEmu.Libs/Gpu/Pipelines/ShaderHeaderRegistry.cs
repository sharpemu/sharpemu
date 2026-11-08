// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;

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
    public uint ContinuationScratchDwords { get; init; }
    public IShaderFrontend? Frontend { get; init; }
    public ReadOnlyMemory<uint> FrontendUserData { get; init; }
    public (ulong Address, uint SizeBytes)[] AdditionalRanges { get; init; } = [];

    public bool IsFused => ContinuationAddress != 0;

    // The code ranges the identity covers, in program order.
    public (ulong Address, uint SizeBytes)[] CodeRanges => IsFused
        ? [(CodeAddress, CodeSizeBytes), (ContinuationAddress, ContinuationSizeBytes), .. AdditionalRanges]
        : [(CodeAddress, CodeSizeBytes), .. AdditionalRanges];

    public uint TotalCodeSizeBytes => CodeSizeBytes + ContinuationSizeBytes + (uint)AdditionalRanges.Sum(range => range.SizeBytes);
}

// The two code objects the guest joined into one program; the entry ends in a jump to the continuation.
public readonly record struct FusedProgramParts(ulong ContinuationAddress, ulong ContinuationHeaderAddress);

internal sealed class ShaderHeaderEntry
{
    public ShaderHeaderEntry(RegisteredShader shader, long registrations)
    {
        Shader = shader;
        Registrations = registrations;
        CodeRanges = shader.CodeRanges;
    }

    public RegisteredShader Shader { get; }

    public long Registrations { get; }

    public (ulong Address, uint SizeBytes)[] CodeRanges { get; }

    public VertexTableMetadata? VertexTables { get; set; }

    public bool InputSemanticsRead { get; set; }

    public uint[]? InputSemantics { get; set; }

    public ulong IdentityHash;

    public int IdentityUses;
}

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
    private readonly IShaderFrontend? _frontend;
    private readonly Dictionary<ulong, ShaderHeaderEntry> _entries = new();
    private static long _registrations;

    public bool HasFrontend => _frontend is not null;
    internal bool SupportsParallelResolution => _frontend?.SupportsParallelResolution ?? true;
    internal long FrontendImageVersion => _frontend?.ImageNormalizationVersion?.Invoke() ?? 0;

    public ShaderHeaderRegistry(CpuContext context, Func<ulong, ulong> headerOf, Func<ulong, FusedProgramParts?> fusedPartsOf,
        IShaderFrontend? frontend = null)
    {
        _context = context;
        _headerOf = headerOf;
        _fusedPartsOf = fusedPartsOf;
        _frontend = frontend;
    }

    internal (ulong Address, byte[] Code)[] ReadDiagnosticCode(ulong codeAddress)
    {
        if (_frontend is not null && _headerOf(codeAddress) == 0)
        {
            var shader = _frontend.ReadShader(_context, codeAddress, "diagnostic");
            var bytes = new byte[shader.CodeSizeBytes];
            if (!_context.Memory.TryRead(codeAddress, bytes))
                throw new InvalidDataException("The shader code is not readable.");
            return [(codeAddress, bytes)];
        }
        var ranges = new List<(ulong Address, byte[] Code)>();
        void ReadRange(ulong address, ulong header)
        {
            Span<byte> sizeBytes = stackalloc byte[4];
            if (header == 0 || !_context.Memory.TryRead(checked(header + ShaderSizeOffset), sizeBytes))
                throw new InvalidDataException("The shader header is not readable.");
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
            if (size == 0 || size > 16 * 1024 * 1024)
                throw new InvalidDataException("The diagnostic shader size is outside the supported range.");
            var code = new byte[size];
            if (!_context.Memory.TryRead(address, code))
                throw new InvalidDataException("The shader code is not readable.");
            ranges.Add((address, code));
        }
        ReadRange(codeAddress, _headerOf(codeAddress));
        if (_fusedPartsOf(codeAddress) is { } parts)
            ReadRange(parts.ContinuationAddress, parts.ContinuationHeaderAddress);
        return ranges.ToArray();
    }

    public static void NoteRegistration() => Interlocked.Increment(ref _registrations);

    public static long Registrations => Interlocked.Read(ref _registrations) + Gen5ShaderTranslator.FusedProgramRegistrations;

    public RegisteredShader Require(ulong codeAddress, string label, ReadOnlySpan<uint> userData = default) => RequireEntry(codeAddress, label, userData).Shader;

    internal ShaderHeaderEntry RequireEntry(ulong codeAddress, string label, ReadOnlySpan<uint> userData = default)
    {
        var registrations = Registrations;
        if (_frontend is not null && _headerOf(codeAddress) == 0)
            return new ShaderHeaderEntry(Read(codeAddress, label, userData), registrations);
        lock (_entries)
        {
            if (_entries.TryGetValue(codeAddress, out var entry) && entry.Registrations == registrations)
            {
                return entry;
            }

            entry = new ShaderHeaderEntry(Read(codeAddress, label), registrations);
            _entries[codeAddress] = entry;
            return entry;
        }
    }

    private RegisteredShader Read(ulong codeAddress, string label, ReadOnlySpan<uint> userData = default)
    {
        var headerAddress = _headerOf(codeAddress);
        if (_frontend is not null && headerAddress == 0)
        {
            try
            {
                return _frontend.ReadShader(_context, codeAddress, label, userData) with { Frontend = _frontend };
            }
            catch (InvalidDataException exception)
            {
                throw SubmissionScheduler.Fatal($"The shader binary is invalid: label={label} shader=0x{codeAddress:X16} error={exception.Message}");
            }
        }
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
        ushort continuationScratchDwords = 0;
        if (_fusedPartsOf(codeAddress) is { } parts)
        {
            continuationAddress = parts.ContinuationAddress;
            continuationSize = RequireCodeSize(parts.ContinuationHeaderAddress, parts.ContinuationAddress, label);
            if (!_context.TryReadUInt16(parts.ContinuationHeaderAddress + ScratchDwordsPerThreadOffset,
                    out continuationScratchDwords))
            {
                throw SubmissionScheduler.Fatal($"The continuation shader scratch size is unreadable: label={label} shader=0x{parts.ContinuationAddress:X16}.");
            }
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
            continuationSize)
        {
            ContinuationScratchDwords = continuationScratchDwords,
        };
    }

    public RegisteredShader? RequireMergedHull(ulong localAddress, ulong hullAddress, ReadOnlySpan<uint> userData)
    {
        if (_frontend is null || _headerOf(localAddress) != 0)
            return null;
        try
        {
            return _frontend.ReadMergedHull(_context, localAddress, hullAddress, userData) is { } shader
                ? shader with { Frontend = _frontend }
                : null;
        }
        catch (InvalidDataException exception)
        {
            throw SubmissionScheduler.Fatal($"The shader binary is invalid: label=merged-hull shader=0x{localAddress:X16} hull=0x{hullAddress:X16} error={exception.Message}");
        }
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
