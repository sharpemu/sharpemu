// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

public sealed class SelfImage
{
    private readonly ulong _imageBase;

    public SelfImage(
        bool isSelf,
        ElfHeader elfHeader,
        IReadOnlyList<ProgramHeader> programHeaders,
        IReadOnlyList<VirtualMemoryRegion> mappedRegions,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        IReadOnlyList<ImportedSymbolRelocation>? importedRelocations = null,
        IReadOnlyList<ulong>? preInitializerFunctions = null,
        IReadOnlyList<ulong>? initializerFunctions = null,
        ulong initFunctionEntryPoint = 0,
        ulong imageBase = 0,
        ulong procParamAddress = 0,
        string? title = null,
        string? titleId = null,
        string? version = null,
        uint tlsModuleId = 0,
        ulong tlsMemorySize = 0,
        ulong tlsStaticOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(programHeaders);
        ArgumentNullException.ThrowIfNull(mappedRegions);

        IsSelf = isSelf;
        ElfHeader = elfHeader;
        ProgramHeaders = programHeaders;
        MappedRegions = mappedRegions;
        ImportStubs = importStubs ?? new Dictionary<ulong, string>();
        RuntimeSymbols = runtimeSymbols ?? new Dictionary<string, ulong>(StringComparer.Ordinal);
        ImportedRelocations = importedRelocations ?? Array.Empty<ImportedSymbolRelocation>();
        PreInitializerFunctions = preInitializerFunctions ?? Array.Empty<ulong>();
        InitializerFunctions = initializerFunctions ?? Array.Empty<ulong>();
        InitFunctionEntryPoint = initFunctionEntryPoint;
        _imageBase = imageBase;
        ProcParamAddress = procParamAddress;
        Title = title;
        TitleId = titleId;
        Version = version;
        TlsModuleId = tlsModuleId;
        TlsMemorySize = tlsMemorySize;
        TlsStaticOffset = tlsStaticOffset;
    }

    public bool IsSelf { get; }

    public ElfHeader ElfHeader { get; }

    public IReadOnlyList<ProgramHeader> ProgramHeaders { get; }

    public IReadOnlyList<VirtualMemoryRegion> MappedRegions { get; }

    public IReadOnlyDictionary<ulong, string> ImportStubs { get; }

    public IReadOnlyDictionary<string, ulong> RuntimeSymbols { get; }

    public IReadOnlyList<ImportedSymbolRelocation> ImportedRelocations { get; }

    public IReadOnlyList<ulong> PreInitializerFunctions { get; }

    public IReadOnlyList<ulong> InitializerFunctions { get; }

    public ulong InitFunctionEntryPoint { get; }

    public ulong EntryPoint => ElfHeader.EntryPoint + _imageBase;

    public ulong ProcParamAddress { get; }

    public string? Title { get; }

    public string? TitleId { get; }

    public string? Version { get; }

    public uint TlsModuleId { get; }

    public ulong TlsMemorySize { get; }

    /// <summary>Variant II distance from the thread pointer to this module's static TLS base.</summary>
    public ulong TlsStaticOffset { get; }
}
