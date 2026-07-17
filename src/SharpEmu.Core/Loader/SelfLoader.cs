// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Loader;

public sealed class SelfLoader : ISelfLoader
{
    private const uint SelfMagic = 0x4F153D1D;
    private const ulong SelfSegmentFlag = 0x800;
    private const int PageSize = 0x1000;
    private const ulong ImportStubBaseAddress = 0x0000_7000_0000_0000UL;
    private const ulong ImportStubAddressStride = 0x0000_0000_0100_0000UL;
    private const ulong ImportStubSlotSize = 0x10;
    private const byte StubTrapOpcode = 0xCC;
    private const byte StubReturnOpcode = 0xC3;

    private const int DynamicEntrySize = 16;
    private const int ElfSymbolSize = 24;
    private const int ElfRelocationSize = 24;
    private const int ElfSectionHeaderSize = 64;
    private const uint SectionTypeSymbolTable = 2;
    private const uint SectionTypeRela = 4;

    private const long DtNull = 0;
    private const long DtPltRelSize = 0x02;
    private const long DtPltGot = 0x03;
    private const long DtStrTab = 0x05;
    private const long DtSymTab = 0x06;
    private const long DtRela = 0x07;
    private const long DtRelaSize = 0x08;
    private const long DtInit = 0x0C;
    private const long DtStrSize = 0x0A;
    private const long DtJmpRel = 0x17;
    private const long DtInitArray = 0x19;
    private const long DtInitArraySize = 0x1B;
    private const long DtPreInitArray = 0x20;
    private const long DtPreInitArraySize = 0x21;
    private const long DtSceJmpRel = 0x61000029;
    private const long DtScePltRelSize = 0x6100002D;
    private const long DtSceRela = 0x6100002F;
    private const long DtSceRelaSize = 0x61000031;
    private const long DtSceStrTab = 0x61000035;
    private const long DtSceStrSize = 0x61000037;
    private const long DtSceSymTab = 0x61000039;
    private const long DtSceSymTabSize = 0x6100003F;

    private const uint RelocationTypeNone = 0;
    private const uint RelocationTypeAbsolute64 = 1;
    private const uint RelocationTypePc32 = 2;
    private const uint RelocationTypePlt32 = 4;
    private const uint RelocationTypeGlobalData = 6;
    private const uint RelocationTypeJumpSlot = 7;
    private const uint RelocationTypeRelative = 8;
    private const uint RelocationTypeUnsigned32 = 10;
    private const uint RelocationTypeSigned32 = 11;
    private const uint RelocationTypeTlsModuleId = 16;   // R_X86_64_DTPMOD64
    private const uint RelocationTypeTlsDtpOff64 = 17;    // R_X86_64_DTPOFF64
    private const uint RelocationTypeTlsTpOff64 = 18;     // R_X86_64_TPOFF64
    private const uint RelocationTypePc64 = 24;
    private const uint RelocationTypeSize32 = 32;
    private const uint RelocationTypeSize64 = 33;
    private const uint RelocationTypeRelative64 = 38;
    private const ulong Ps5MainImageBase = 0x0000000800000000UL;
    private const ulong Ps4MainImageBase = 0x0000000000400000UL;
    private const ulong Ps5ModuleSearchStart = 0x0000000804000000UL;
    private const ulong Ps5ModuleSearchEnd = 0x0000000900000000UL;
    private const ulong Ps4ModuleSearchStart = 0x0000000002000000UL;
    private const ulong Ps4ModuleSearchEnd = 0x0000000040000000UL;
    private const ulong ModulePlacementStep = 0x00200000UL;
    private const ulong FocusRelocGuestStart = 0x0000000807BA25B0UL;
    private const ulong FocusRelocGuestEnd = 0x0000000807BA2608UL;
    private const byte SymbolBindLocal = 0;
    private const byte SymbolBindGlobal = 1;
    private const byte SymbolBindWeak = 2;
    private const byte SymbolTypeObject = 1;

    private IModuleManager? _moduleManager;
    private uint _nextTlsModuleId = 1;

    private static readonly IReadOnlyDictionary<ulong, string> EmptyImportStubs = new Dictionary<ulong, string>();
    private static readonly IReadOnlyDictionary<string, ulong> EmptyRuntimeSymbols =
        new Dictionary<string, ulong>(StringComparer.Ordinal);
    private static readonly IReadOnlyList<ulong> EmptyInitializerFunctions = Array.Empty<ulong>();
    private static readonly int SelfHeaderSize = Unsafe.SizeOf<SelfHeader>();
    private static readonly int SelfSegmentSize = Unsafe.SizeOf<SelfSegment>();
    private static readonly int ProgramHeaderSize = Unsafe.SizeOf<ProgramHeader>();

    static SelfLoader()
    {
        RunRelocationSelfChecks();
    }

    public SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory)
    {
        return Load(imageData, virtualMemory, fs: null, mountRoot: null);
    }

    public SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IFileSystem? fs, string? mountRoot)
    {
        return LoadCore(
            imageData,
            virtualMemory,
            fs,
            mountRoot,
            clearVirtualMemory: true,
            readParamJson: true);
    }

    public SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager)
    {
        return Load(imageData, virtualMemory, moduleManager, fs: null, mountRoot: null);
    }

    public SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager, IFileSystem? fs, string? mountRoot)
    {
        _moduleManager = moduleManager;
        return LoadCore(
            imageData,
            virtualMemory,
            fs,
            mountRoot,
            clearVirtualMemory: true,
            readParamJson: true);
    }

    public SelfImage LoadAdditional(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager, IFileSystem? fs, string? mountRoot)
    {
        _moduleManager = moduleManager;
        return LoadCore(
            imageData,
            virtualMemory,
            fs,
            mountRoot,
            clearVirtualMemory: false,
            readParamJson: false);
    }

    private SelfImage LoadCore(
        ReadOnlySpan<byte> imageData,
        IVirtualMemory virtualMemory,
        IFileSystem? fs,
        string? mountRoot,
        bool clearVirtualMemory,
        bool readParamJson)
    {
        ArgumentNullException.ThrowIfNull(virtualMemory);

        if (imageData.IsEmpty)
        {
            throw new InvalidDataException("Input image is empty.");
        }

        var applicationInfo = readParamJson
            ? TryLoadParamJson(fs, mountRoot)
            : default;

        if (clearVirtualMemory)
        {
            virtualMemory.Clear();
            _nextTlsModuleId = 1;
            GuestTlsTemplate.Reset();
        }

        var loadContext = ParseLayout(imageData);
        var elfHeader = ReadUnmanaged<ElfHeader>(imageData, loadContext.ElfOffset);
        ValidateElfHeader(elfHeader);

        var programHeaders = ParseProgramHeaders(imageData, loadContext, elfHeader);
        var hasTlsSegment = TryGetProgramHeader(programHeaders, ProgramHeaderType.Tls, out var processTlsHeader, out _) &&
            processTlsHeader.MemorySize != 0;
        var tlsModuleId = hasTlsSegment
            ? (_nextTlsModuleId == 0 ? 1u : _nextTlsModuleId)
            : 0u;
        Console.Error.WriteLine(
            $"[LOADER][TLS] load_start clear={clearVirtualMemory} next={_nextTlsModuleId} " +
            $"assigned={tlsModuleId} has_pt_tls={hasTlsSegment}");

        var totalImageSize = CalculateTotalImageSize(programHeaders);
        Console.WriteLine($"Total image size needed: 0x{totalImageSize:X} ({totalImageSize} bytes)");
        var isNextGen = elfHeader.AbiVersion == 2;
        var imageBase = DetermineRequestedImageBase(virtualMemory, totalImageSize, isNextGen, clearVirtualMemory);

        if (virtualMemory is PhysicalVirtualMemory physicalVm)
        {
            if (clearVirtualMemory)
            {
                if (!physicalVm.TryAllocateAtExact(imageBase, totalImageSize, executable: true, out var allocatedBase))
                {
                    var reason = physicalVm.DescribeAddressForDiagnostics(imageBase);
                    throw new InvalidOperationException(
                        $"Could not allocate main image at required base 0x{imageBase:X16} (size=0x{totalImageSize:X}): {reason}.");
                }

                imageBase = allocatedBase;
            }
            else if (!TryAllocateAdditionalImageAtExact(physicalVm, imageBase, totalImageSize, isNextGen, out imageBase))
            {
                var allocatedBase = physicalVm.AllocateAt(imageBase, totalImageSize, executable: true);
                if (allocatedBase != imageBase)
                {
                    Console.WriteLine($"[LOADER] Could not allocate module at preferred base 0x{imageBase:X16}");
                    Console.WriteLine($"[LOADER] Allocated module at 0x{allocatedBase:X16} instead.");
                }

                imageBase = allocatedBase;
            }
        }

        MapLoadSegments(imageData, loadContext, programHeaders, virtualMemory, imageBase);
        // Register every module before relocations so DTPMOD/DTPOFF/TPOFF use
        // the module's real PT_TLS identity and Variant II static offset.
        var tlsInfo = RegisterModuleTlsTemplate(
            programHeaders,
            virtualMemory,
            imageBase,
            tlsModuleId);
        var importStubs = ResolveAndPatchImportStubs(
            imageData,
            loadContext,
            elfHeader,
            programHeaders,
            virtualMemory,
            imageBase,
            _moduleManager,
            tlsModuleId,
            out var importedRelocations);
        var effectiveImportStubs = importStubs.Count == 0
            ? new Dictionary<ulong, string>()
            : new Dictionary<ulong, string>(importStubs);
        var runtimeSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        RegisterRuntimeSymbolsAndHooks(
            imageData,
            loadContext,
            programHeaders,
            elfHeader,
            virtualMemory,
            imageBase,
            effectiveImportStubs,
            runtimeSymbols);
        var finalizedImportStubs = effectiveImportStubs.Count == 0
            ? EmptyImportStubs
            : effectiveImportStubs;
        var finalizedRuntimeSymbols = runtimeSymbols.Count == 0
            ? EmptyRuntimeSymbols
            : runtimeSymbols;
        CollectInitializerFunctions(
            imageData,
            loadContext,
            programHeaders,
            virtualMemory,
            imageBase,
            out var initFunctionEntryPoint,
            out var preInitializerFunctions,
            out var initializerFunctions);
        var procParamAddress = ResolveProcParamAddress(programHeaders, imageBase);

        Console.WriteLine($"[LOADER] ELF e_entry: 0x{elfHeader.EntryPoint:X16}");
        Console.WriteLine($"[LOADER] Generation: {(isNextGen ? "Gen5 (PS5)" : "Gen4 (PS4)")}");
        Console.WriteLine($"[LOADER] Using image base: 0x{imageBase:X16}");
        Console.WriteLine($"[LOADER] Final entry point: 0x{elfHeader.EntryPoint + imageBase:X16}");
        if (procParamAddress != 0)
        {
            Console.WriteLine($"[LOADER] ProcParam: 0x{procParamAddress:X16}");
        }

        int count = ((IReadOnlyList<ProgramHeader>)programHeaders).Count;
        int phCountToLog = count < 3 ? count : 3;
        for (var i = 0; i < phCountToLog; i++)
        {
            var ph = programHeaders[i];
            Console.WriteLine($"[LOADER] PH[{i}]: type={ph.HeaderType}, vaddr=0x{ph.VirtualAddress:X16} -> 0x{ph.VirtualAddress + imageBase:X16}, memsz=0x{ph.MemorySize:X}");
        }

        if (tlsModuleId != 0 && _nextTlsModuleId == tlsModuleId && _nextTlsModuleId < uint.MaxValue)
        {
            _nextTlsModuleId++;
        }
        Console.Error.WriteLine($"[LOADER][TLS] load_done assigned={tlsModuleId} next={_nextTlsModuleId}");

        return new SelfImage(
            loadContext.IsSelf,
            elfHeader,
            programHeaders,
            virtualMemory.SnapshotRegions(),
            finalizedImportStubs,
            finalizedRuntimeSymbols,
            importedRelocations,
            preInitializerFunctions,
            initializerFunctions,
            initFunctionEntryPoint,
            imageBase,
            procParamAddress,
            applicationInfo.Title,
            applicationInfo.TitleId,
            applicationInfo.Version,
            tlsModuleId,
            tlsInfo.MemorySize,
            tlsInfo.StaticOffset);
    }

    private static (string? Title, string? TitleId, string? Version) TryLoadParamJson(
        IFileSystem? fs,
        string? mountRoot)
    {
        if (fs == null)
        {
            Console.WriteLine("[LOADER] param.json not found (no filesystem provided).");
            return default;
        }

        string[] possiblePaths = string.IsNullOrEmpty(mountRoot)
            ? new[] { "sce_sys/param.json", "param.json" }
            : new[] { $"{mountRoot}/sce_sys/param.json", $"{mountRoot}/param.json" };

        string? foundPath = null;
        foreach (var path in possiblePaths)
        {
            if (fs.Exists(path))
            {
                foundPath = path;
                break;
            }
        }

        if (foundPath == null)
        {
            Console.WriteLine("[LOADER] param.json not found (no root path / unknown layout).");
            return default;
        }

        var applicationInfo = Ps5ParamJsonReader.TryReadPs5Param(fs, foundPath);
        Console.WriteLine($"[LOADER] Loading param.json at {foundPath}");
        Console.WriteLine(
            $"[LOADER] Title: {applicationInfo.Title ?? "(unknown)"}, " +
            $"TitleId: {applicationInfo.TitleId ?? "(unknown)"}, " +
            $"Version: {applicationInfo.Version ?? "(unknown)"}");
        return applicationInfo;
    }

    private static LoadContext ParseLayout(ReadOnlySpan<byte> imageData)
    {
        if (imageData.Length < Unsafe.SizeOf<ElfHeader>())
        {
            throw new InvalidDataException("Input image is too small to contain an ELF header.");
        }

        if (imageData.Length >= sizeof(uint) && BinaryPrimitives.ReadUInt32BigEndian(imageData[..sizeof(uint)]) == SelfMagic)
        {
            var selfHeader = ReadUnmanaged<SelfHeader>(imageData, 0);
            if (!selfHeader.HasKnownLayout || selfHeader.Unknown != 0x22)
            {
                throw new InvalidDataException("SELF header signature is not recognized.");
            }

            var segmentCount = selfHeader.SegmentCount;
            var elfOffset = checked(SelfHeaderSize + (segmentCount * SelfSegmentSize));
            EnsureRange(imageData.Length, (ulong)elfOffset, (ulong)Unsafe.SizeOf<ElfHeader>());

            var segments = segmentCount == 0 ? Array.Empty<SelfSegment>() : GC.AllocateUninitializedArray<SelfSegment>(segmentCount);
            for (var i = 0; i < segmentCount; i++)
            {
                var segmentOffset = checked(SelfHeaderSize + (i * SelfSegmentSize));
                segments[i] = ReadUnmanaged<SelfSegment>(imageData, segmentOffset);
            }

            return new LoadContext(IsSelf: true, elfOffset, selfHeader.FileSize, segments);
        }

        // Not a recognized (fake-signed) SELF. Only a bare, decrypted ELF is
        // acceptable here; anything else — most commonly a still-encrypted
        // retail eboot — must be reported clearly rather than failing later
        // with an opaque "not a valid ELF header" message.
        const uint ElfMagicBigEndian = 0x7F454C46; // "\x7fELF"
        var leadingWord = BinaryPrimitives.ReadUInt32BigEndian(imageData[..sizeof(uint)]);
        if (leadingWord != ElfMagicBigEndian)
        {
            throw new InvalidDataException(
                $"Image is neither a decrypted ELF nor a recognized fake-signed SELF " +
                $"(leading bytes 0x{leadingWord:X8}). This is almost certainly a still-encrypted " +
                $"retail eboot — SharpEmu has no decryption keys and requires a decrypted / " +
                $"fake-signed (fSELF) image.");
        }

        return new LoadContext(IsSelf: false, ElfOffset: 0, SelfFileSize: 0, Array.Empty<SelfSegment>());
    }

    private static ProgramHeader[] ParseProgramHeaders(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ElfHeader elfHeader)
    {
        if (elfHeader.ProgramHeaderCount == 0)
        {
            return Array.Empty<ProgramHeader>();
        }

        if (elfHeader.ProgramHeaderEntrySize < ProgramHeaderSize)
        {
            throw new InvalidDataException("Program header entry size is smaller than expected.");
        }

        var tableOffset = checked(loadContext.ElfOffset + (int)elfHeader.ProgramHeaderOffset);
        EnsureRange(
            imageData.Length,
            (ulong)tableOffset,
            (ulong)elfHeader.ProgramHeaderCount * elfHeader.ProgramHeaderEntrySize);

        var headers = GC.AllocateUninitializedArray<ProgramHeader>(elfHeader.ProgramHeaderCount);
        for (var i = 0; i < headers.Length; i++)
        {
            var entryOffset = checked(tableOffset + (i * elfHeader.ProgramHeaderEntrySize));
            headers[i] = ReadUnmanaged<ProgramHeader>(imageData, entryOffset);
        }

        return headers;
    }

    private static void MapLoadSegments(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        IReadOnlyList<ProgramHeader> programHeaders,
        IVirtualMemory virtualMemory,
        ulong imageBase)
    {
        for (var index = 0; index < programHeaders.Count; index++)
        {
            var header = programHeaders[index];
            if (header.HeaderType != ProgramHeaderType.Load || header.MemorySize == 0)
            {
                continue;
            }

            if (header.FileSize > header.MemorySize)
            {
                throw new InvalidDataException("ELF segment file size cannot exceed memory size.");
            }

            var sourceOffset = header.FileSize == 0
                ? 0UL
                : ResolvePhysicalSegmentOffset(imageData.Length, loadContext, header, index);

            var virtualAddress = header.VirtualAddress + imageBase;

            Console.Error.WriteLine($"[LOADER] Segment {index}: VAddr=0x{virtualAddress:X16}, FileSize=0x{header.FileSize:X}, MemSize=0x{header.MemorySize:X}, Align=0x{header.Alignment:X}");
            if (header.Alignment > 1)
            {
                var vaddrMod = virtualAddress % header.Alignment;
                var offsetMod = header.Offset % header.Alignment;
                if (vaddrMod != offsetMod)
                {
                    Console.Error.WriteLine(
                        $"[LOADER] WARNING: Segment {index} ELF alignment mismatch! " +
                        $"VAddr=0x{virtualAddress:X}, Offset=0x{header.Offset:X}, Align=0x{header.Alignment:X}, " +
                        $"VAddr%Align=0x{vaddrMod:X}, Offset%Align=0x{offsetMod:X}");
                }
            }

            ReadOnlySpan<byte> fileData = ReadOnlySpan<byte>.Empty;
            if (header.FileSize != 0)
            {
                if (header.FileSize > int.MaxValue)
                {
                    throw new NotSupportedException("Segments larger than 2 GB are not currently supported.");
                }

                EnsureRange(imageData.Length, sourceOffset, header.FileSize);
                fileData = imageData.Slice((int)sourceOffset, (int)header.FileSize);
            }

            virtualMemory.Map(
                virtualAddress,
                header.MemorySize,
                sourceOffset,
                fileData,
                header.Flags);
        }
    }

    private static ulong ResolveProcParamAddress(IReadOnlyList<ProgramHeader> programHeaders, ulong imageBase)
    {
        for (var index = 0; index < programHeaders.Count; index++)
        {
            var header = programHeaders[index];
            if (header.HeaderType != ProgramHeaderType.SceProcParam)
            {
                continue;
            }

            return header.VirtualAddress + imageBase;
        }

        return 0;
    }

    private static ModuleTlsInfo RegisterModuleTlsTemplate(
        IReadOnlyList<ProgramHeader> programHeaders,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        uint tlsModuleId)
    {
        if (!TryGetProgramHeader(programHeaders, ProgramHeaderType.Tls, out var tlsHeader, out _) ||
            tlsHeader.MemorySize == 0)
        {
            return default;
        }

        // tdata (initialized) bytes come from the mapped segment; tbss is the
        // implicitly-zero remainder up to MemorySize.
        var fileSize = (int)Math.Min(tlsHeader.FileSize, tlsHeader.MemorySize);
        var initImage = fileSize > 0 ? new byte[fileSize] : [];
        if (fileSize > 0 &&
            !virtualMemory.TryRead(imageBase + tlsHeader.VirtualAddress, initImage))
        {
            Console.Error.WriteLine(
                $"[LOADER][TLS] Failed to read TLS init image at 0x{imageBase + tlsHeader.VirtualAddress:X}; seeding zeros.");
            initImage = [];
        }

        var staticOffset = GuestTlsTemplate.RegisterModule(
            tlsModuleId,
            initImage,
            tlsHeader.MemorySize,
            tlsHeader.Alignment,
            tlsHeader.VirtualAddress);
        Console.Error.WriteLine(
            $"[LOADER][TLS] Module {tlsModuleId} TLS template: memsz=0x{tlsHeader.MemorySize:X} " +
            $"filesz=0x{tlsHeader.FileSize:X} align=0x{tlsHeader.Alignment:X} " +
            $"static_offset=0x{staticOffset:X} total_static=0x{GuestTlsTemplate.StaticTlsSize:X}");
        return new ModuleTlsInfo(tlsHeader.MemorySize, staticOffset);
    }

    private static IReadOnlyDictionary<ulong, string> ResolveAndPatchImportStubs(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ElfHeader elfHeader,
        IReadOnlyList<ProgramHeader> programHeaders,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        IModuleManager? moduleManager,
        uint tlsModuleId,
        out IReadOnlyList<ImportedSymbolRelocation> importedRelocations)
    {
        importedRelocations = Array.Empty<ImportedSymbolRelocation>();
        if (!TryGetProgramHeader(programHeaders, ProgramHeaderType.Dynamic, out var dynamicHeader, out var dynamicHeaderIndex))
        {
            return EmptyImportStubs;
        }

        if (dynamicHeader.FileSize == 0)
        {
            return EmptyImportStubs;
        }

        if (dynamicHeader.FileSize > int.MaxValue)
        {
            throw new NotSupportedException("Dynamic metadata segments larger than 2 GB are not currently supported.");
        }

        if (!TryLoadDynamicTableBytes(
                imageData,
                loadContext,
                virtualMemory,
                imageBase,
                dynamicHeader,
                dynamicHeaderIndex,
                out var dynamicTable))
        {
            return EmptyImportStubs;
        }

        var elfData = imageData;

        var dynamicInfo = ParseDynamicInfo(dynamicTable);

        Console.WriteLine($"[LOADER] Dynamic Info: StrTab=0x{dynamicInfo.StrTabOffset:X}, StrTabSize=0x{dynamicInfo.StrTabSize:X}");
        Console.WriteLine($"[LOADER] Dynamic Info: SymTab=0x{dynamicInfo.SymTabOffset:X}, SymTabSize=0x{dynamicInfo.SymTabSize:X}");
        Console.WriteLine($"[LOADER] Dynamic Info: Rela=0x{dynamicInfo.RelaOffset:X}, RelaSize=0x{dynamicInfo.RelaSize:X}");
        Console.WriteLine($"[LOADER] Dynamic Info: JmpRel=0x{dynamicInfo.JmpRelOffset:X}, JmpRelSize=0x{dynamicInfo.JmpRelSize:X}");
        Console.WriteLine($"[LOADER] Dynamic Info: PltGot=0x{dynamicInfo.PltGotOffset:X}");
        Console.WriteLine($"[LOADER] TLS module id: {tlsModuleId}");
        Console.WriteLine($"[LOADER] HasImportMetadata: {dynamicInfo.HasImportMetadata}");

        var relocations = new List<ElfRelocation>(512);

        if (dynamicInfo.RelaSize != 0 &&
            TryLoadTableBytes(elfData, virtualMemory, imageBase, dynamicInfo.RelaOffset, dynamicInfo.RelaSize, out var relaBytes))
        {
            CollectRelocations(relaBytes, relocations);
        }

        if (dynamicInfo.JmpRelSize != 0 &&
            TryLoadTableBytes(elfData, virtualMemory, imageBase, dynamicInfo.JmpRelOffset, dynamicInfo.JmpRelSize, out var jmpRelBytes))
        {
            CollectRelocations(jmpRelBytes, relocations);
        }

        if (!dynamicInfo.HasImportMetadata)
        {
            Console.WriteLine($"[LOADER] No import metadata found in ELF!");
        }

        if (relocations.Count != 0)
        {
            Console.WriteLine($"[LOADER] ImageBase runtime: 0x{imageBase:X16}");
            Console.WriteLine($"[LOADER] Processing {relocations.Count} relocations...");
        }

        uint maxSymbolIndex = 0;
        foreach (var relocation in relocations)
        {
            if (relocation.Type == RelocationTypeNone)
            {
                continue;
            }

            if (!IsSupportedRelocationType(relocation.Type))
            {
                continue;
            }

            if (relocation.Type is RelocationTypeNone or RelocationTypeRelative or RelocationTypeRelative64 or RelocationTypeTlsModuleId)
            {
                continue;
            }

            if (relocation.SymbolIndex > maxSymbolIndex)
            {
                maxSymbolIndex = relocation.SymbolIndex;
            }
        }

        byte[] stringTable = Array.Empty<byte>();
        byte[] symbolTable = Array.Empty<byte>();
        if (maxSymbolIndex != 0)
        {
            if (!TryLoadTableBytes(
                    elfData,
                    virtualMemory,
                    imageBase,
                    dynamicInfo.StrTabOffset,
                    dynamicInfo.StrTabSize,
                    out stringTable))
            {
                return EmptyImportStubs;
            }

            var symTableSize = dynamicInfo.SymTabSize != 0
                ? dynamicInfo.SymTabSize
                : checked(((ulong)maxSymbolIndex + 1) * ElfSymbolSize);
            if (!TryLoadTableBytes(
                    elfData,
                    virtualMemory,
                    imageBase,
                    dynamicInfo.SymTabOffset,
                    symTableSize,
                    out symbolTable))
            {
                return EmptyImportStubs;
            }
        }

        var descriptors = new List<RelocationDescriptor>(256);
        var orderedImportNids = new List<string>(128);
        var seenImportNids = new HashSet<string>(StringComparer.Ordinal);
        AppendRelocationDescriptors(
            relocations,
            symbolTable,
            stringTable,
            virtualMemory,
            imageBase,
            tlsModuleId,
            descriptors,
            orderedImportNids,
            seenImportNids);

        if (descriptors.Count == 0)
        {
            var sectionFallbackRelocCount = AppendSectionRelocationDescriptors(
                imageData,
                loadContext,
                elfHeader,
                virtualMemory,
                imageBase,
                tlsModuleId,
                descriptors,
                orderedImportNids,
                seenImportNids);
            if (sectionFallbackRelocCount != 0)
            {
                Console.WriteLine(
                    $"[LOADER] Section relocation fallback recovered {sectionFallbackRelocCount} relocation entries, {orderedImportNids.Count} unique NIDs, {descriptors.Count} descriptors");
            }
        }

        Console.WriteLine($"[LOADER] Found {orderedImportNids.Count} unique NIDs, {descriptors.Count} descriptors");

        if (descriptors.Count == 0)
        {
            Console.WriteLine($"[LOADER] No relocation descriptors!");
            return EmptyImportStubs;
        }

        importedRelocations = BuildImportedRelocations(descriptors);

        var stubImportNids = orderedImportNids
            .Where(nid => ShouldCreateImportStub(nid, descriptors, moduleManager))
            .ToArray();
        var stubsByAddress = CreateImportStubMapping(virtualMemory, stubImportNids);
        Console.WriteLine($"[LOADER] Created {stubsByAddress.Count} import stubs");

        int printCount = Math.Min(10, stubImportNids.Length);
        for (int i = 0; i < printCount; i++)
        {
            var nid = stubImportNids[i];
            var addr = stubsByAddress.First(x => x.Value == nid).Key;
        }

        var nidNames = Aerolib.Instance.GetAllNidNames();

        var nidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ImportNid is not null)
            {
                nidCounts.TryGetValue(descriptor.ImportNid, out var count);
                nidCounts[descriptor.ImportNid] = count + 1;
            }
        }

        var addressesByNid = new Dictionary<string, ulong>(orderedImportNids.Count, StringComparer.Ordinal);
        foreach (var entry in stubsByAddress)
        {
            addressesByNid[entry.Value] = entry.Key;
        }

        foreach (var descriptor in descriptors)
        {
            ulong symbolValue;
            if (descriptor.ImportNid is null)
            {
                symbolValue = descriptor.SymbolValue;
            }
            else
            {
                if (addressesByNid.TryGetValue(descriptor.ImportNid, out var stubAddress))
                {
                    symbolValue = stubAddress;
                }
                else if (descriptor.IsWeak)
                {
                    // ELF unresolved weak definitions use S=0. They must not
                    // receive a trap import stub, which would turn a permitted
                    // null test into a call to an unresolved-import handler.
                    symbolValue = 0;
                }
                else
                {
                    throw new InvalidOperationException($"Import stub not found for NID '{descriptor.ImportNid}'.");
                }
            }

            var targetValue = ComputeRelocationValue(descriptor, symbolValue);

            if (targetValue < 0x1000 && descriptor.ValueKind is
                RelocationValueKind.TlsOffset or
                RelocationValueKind.PcRelative or
                RelocationValueKind.SymbolSize)
            {
                // A TLS offset (TPOFF64/DTPOFF64) is a signed displacement, not a
                // mapped address, so a small or negative value here is expected.
            }
            else if (targetValue < 0x1000 && !descriptor.IsWeak)
            {
                if (descriptor.ValueKind == RelocationValueKind.TlsModuleId)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TLS] Patching DTPMOD64 at 0x{descriptor.TargetAddress:X} with module id 0x{targetValue:X}");
                }
                else
                {
                    Console.Error.WriteLine($"[LOADER] !!! CRITICAL !!! Patching address 0x{descriptor.TargetAddress:X} with INVALID value 0x{targetValue:X} for NID {descriptor.ImportNid ?? "(null)"}");
                    Console.Error.WriteLine($"[LOADER]   SymbolValue=0x{descriptor.SymbolValue:X}, Addend=0x{descriptor.Addend:X}, StubAddress=0x{(addressesByNid.TryGetValue(descriptor.ImportNid ?? "", out var sa) ? sa : 0):X}");
                }
            }

            if (!TryWriteRelocationValue(virtualMemory, descriptor, targetValue, out var writeError))
            {
                throw new InvalidDataException(
                    $"Failed to patch relocation at 0x{descriptor.TargetAddress:X16}: {writeError}");
            }

            if (descriptor.TargetAddress >= 0x00000008030FC300UL &&
                descriptor.TargetAddress <= 0x00000008030FC3F0UL)
            {
                Console.Error.WriteLine(
                    $"[LOADER][RELOC] target=0x{descriptor.TargetAddress:X16} value=0x{targetValue:X16} addend=0x{descriptor.Addend:X} nid={(descriptor.ImportNid ?? "<sym>")}");
            }
        }

        return stubsByAddress;
    }

    private static int AppendSectionRelocationDescriptors(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ElfHeader elfHeader,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        uint tlsModuleId,
        ICollection<RelocationDescriptor> descriptors,
        IList<string> orderedImportNids,
        ISet<string> seenImportNids)
    {
        if (elfHeader.SectionHeaderOffset == 0 ||
            elfHeader.SectionHeaderCount == 0 ||
            elfHeader.SectionHeaderEntrySize < ElfSectionHeaderSize)
        {
            return 0;
        }

        var appendedRelocations = 0;
        for (var sectionIndex = 0; sectionIndex < elfHeader.SectionHeaderCount; sectionIndex++)
        {
            if (!TryReadSectionHeader(imageData, loadContext, elfHeader, sectionIndex, out var relocationHeader) ||
                relocationHeader.Type != SectionTypeRela ||
                relocationHeader.Size == 0 ||
                relocationHeader.EntrySize < ElfRelocationSize)
            {
                continue;
            }

            if (!TryReadElfRelativeSlice(imageData, loadContext, relocationHeader.Offset, relocationHeader.Size, out var relocationTable))
            {
                continue;
            }

            var relocations = new List<ElfRelocation>(checked((int)(relocationHeader.Size / relocationHeader.EntrySize)));
            CollectRelocations(relocationTable, relocations);
            if (relocations.Count == 0)
            {
                continue;
            }

            ReadOnlySpan<byte> symbolTable = ReadOnlySpan<byte>.Empty;
            ReadOnlySpan<byte> stringTable = ReadOnlySpan<byte>.Empty;
            if (relocationHeader.Link < elfHeader.SectionHeaderCount &&
                TryReadSectionHeader(imageData, loadContext, elfHeader, (int)relocationHeader.Link, out var symbolHeader) &&
                symbolHeader.Size != 0 &&
                symbolHeader.EntrySize >= ElfSymbolSize &&
                TryReadElfRelativeSlice(imageData, loadContext, symbolHeader.Offset, symbolHeader.Size, out symbolTable) &&
                symbolHeader.Link < elfHeader.SectionHeaderCount &&
                TryReadSectionHeader(imageData, loadContext, elfHeader, (int)symbolHeader.Link, out var stringHeader) &&
                stringHeader.Size != 0 &&
                TryReadElfRelativeSlice(imageData, loadContext, stringHeader.Offset, stringHeader.Size, out stringTable))
            {
            }

            AppendRelocationDescriptors(
                relocations,
                symbolTable,
                stringTable,
                virtualMemory,
                imageBase,
                tlsModuleId,
                descriptors,
                orderedImportNids,
                seenImportNids);
            appendedRelocations += relocations.Count;
        }

        return appendedRelocations;
    }

    private static void AppendRelocationDescriptors(
        IReadOnlyList<ElfRelocation> relocations,
        ReadOnlySpan<byte> symbolTable,
        ReadOnlySpan<byte> stringTable,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        uint tlsModuleId,
        ICollection<RelocationDescriptor> descriptors,
        IList<string> orderedImportNids,
        ISet<string> seenImportNids)
    {
        foreach (var relocation in relocations)
        {
            if (IsFocusRelocationOffset(relocation.Offset, imageBase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][FOCUS][SCAN] off=0x{relocation.Offset:X16} type={relocation.Type} sym={relocation.SymbolIndex} addend=0x{relocation.Addend:X}");
            }

            if (!IsSupportedRelocationType(relocation.Type))
            {
                // Surface unsupported relocation types loudly instead of
                // skipping silently: TLS-relative (17/18), COPY (5), and
                // IRELATIVE/ifunc (37) leave their targets unrelocated, which
                // manifests later as reads of zero or calls into address 0.
                ReportUnsupportedRelocation(relocation.Type, relocation.Offset, imageBase);
                if (relocation.Type is 5 or 37)
                {
                    throw new NotSupportedException(
                        $"Relocation type {relocation.Type} requires deferred runtime-linker processing.");
                }
                continue;
            }

            var relocationWriteSize = GetRelocationWriteSize(relocation.Type);
            if (!TryResolveMappedAddress(virtualMemory, relocation.Offset, imageBase, relocationWriteSize, out var targetAddress))
            {
                if (IsFocusRelocationOffset(relocation.Offset, imageBase))
                {
                    Console.Error.WriteLine("[LOADER][FOCUS][SKIP] target address not mapped");
                }
                continue;
            }

            if (relocation.Type is RelocationTypeRelative or RelocationTypeRelative64)
            {
                descriptors.Add(new RelocationDescriptor(
                    targetAddress,
                    relocation.Addend,
                    null,
                    imageBase,
                    RelocationValueKind.Pointer,
                    IsDataImport: false));
                continue;
            }

            if (relocation.Type == RelocationTypeTlsModuleId)
            {
                if (tlsModuleId == 0)
                {
                    throw new InvalidDataException(
                        $"R_X86_64_DTPMOD64 at 0x{targetAddress:X16} references a module without PT_TLS.");
                }
                var dtpmodValue = tlsModuleId;
                descriptors.Add(new RelocationDescriptor(
                    targetAddress,
                    0,
                    null,
                    dtpmodValue,
                    RelocationValueKind.TlsModuleId,
                    IsDataImport: false));
                continue;
            }

            var symbolIndex = relocation.SymbolIndex;
            ElfSymbol symbol;
            if (symbolIndex == 0)
            {
                symbol = default;
            }
            else if (!TryReadSymbol(symbolTable, symbolIndex, out symbol))
            {
                if (targetAddress >= FocusRelocGuestStart && targetAddress <= FocusRelocGuestEnd)
                {
                    Console.Error.WriteLine($"[LOADER][FOCUS][SKIP] symbol read failed index={symbolIndex}");
                }
                continue;
            }

            if (relocation.Type is RelocationTypeTlsDtpOff64 or RelocationTypeTlsTpOff64)
            {
                // Variant II static TLS: the module block sits at
                // [tp - blockSize, tp). DTPOFF64 is the module-relative offset
                // (st_value + addend); TPOFF64 is that offset expressed relative
                // to the thread pointer, i.e. minus the aligned block size.
                var tlsSymbolOffset = AddSigned(symbol.Value, relocation.Addend);
                if (!GuestTlsTemplate.TryGetStaticOffset(tlsModuleId, out var moduleStaticOffset))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TLS] Missing PT_TLS registration for module {tlsModuleId}; " +
                        $"cannot apply relocation {relocation.Type} at 0x{targetAddress:X16}.");
                    continue;
                }
                var tlsValue = relocation.Type == RelocationTypeTlsTpOff64
                    ? unchecked(tlsSymbolOffset - moduleStaticOffset)
                    : tlsSymbolOffset;
                descriptors.Add(new RelocationDescriptor(
                    targetAddress,
                    0,
                    null,
                    tlsValue,
                    RelocationValueKind.TlsOffset,
                    IsDataImport: false));
                continue;
            }

            var symbolBind = GetSymbolBind(symbol.Info);
            if (symbolIndex == 0)
            {
                descriptors.Add(CreateSymbolRelocationDescriptor(
                    relocation,
                    targetAddress,
                    symbol,
                    symbolAddress: 0,
                    importNid: null,
                    isWeak: false));
                continue;
            }

            if (symbolBind == SymbolBindLocal)
            {
                var symbolAddress = relocation.Type is RelocationTypeSize32 or RelocationTypeSize64
                    ? 0
                    : ResolveMappedAddressOrFallback(virtualMemory, symbol.Value, imageBase);
                if (symbolAddress == 0)
                {
                    if (relocation.Type is not (RelocationTypeSize32 or RelocationTypeSize64))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER] Skipping local relocation with invalid symbol value 0x{symbol.Value:X} " +
                            $"at target 0x{targetAddress:X16}, type={relocation.Type}, sym={symbolIndex}");
                        continue;
                    }
                }

                descriptors.Add(CreateSymbolRelocationDescriptor(
                    relocation,
                    targetAddress,
                    symbol,
                    symbolAddress,
                    importNid: null,
                    isWeak: false));
                continue;
            }

            if (symbol.Value != 0)
            {
                var symbolAddress = relocation.Type is RelocationTypeSize32 or RelocationTypeSize64
                    ? 0
                    : ResolveMappedAddressOrFallback(virtualMemory, symbol.Value, imageBase);
                if (symbolAddress == 0)
                {
                    if (relocation.Type is not (RelocationTypeSize32 or RelocationTypeSize64))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER] Skipping relocation with invalid symbol value 0x{symbol.Value:X} " +
                            $"at target 0x{targetAddress:X16}, type={relocation.Type}, sym={symbolIndex}");
                        continue;
                    }
                }

                descriptors.Add(CreateSymbolRelocationDescriptor(
                    relocation,
                    targetAddress,
                    symbol,
                    symbolAddress,
                    importNid: null,
                    isWeak: false));
                continue;
            }

            if (symbolBind is not (SymbolBindGlobal or SymbolBindWeak))
            {
                if (targetAddress >= FocusRelocGuestStart && targetAddress <= FocusRelocGuestEnd)
                {
                    Console.Error.WriteLine($"[LOADER][FOCUS][SKIP] bind={symbolBind} not importable");
                }
                continue;
            }

            if (!TryReadNullTerminatedAscii(stringTable, symbol.NameOffset, out var symbolName))
            {
                if (symbolBind == SymbolBindWeak)
                {
                    descriptors.Add(CreateSymbolRelocationDescriptor(
                        relocation,
                        targetAddress,
                        symbol,
                        symbolAddress: 0,
                        importNid: null,
                        isWeak: true));
                }
                if (targetAddress >= FocusRelocGuestStart && targetAddress <= FocusRelocGuestEnd)
                {
                    Console.Error.WriteLine($"[LOADER][FOCUS][SKIP] symbol name read failed offset={symbol.NameOffset}");
                }
                continue;
            }

            var nid = ExtractNid(symbolName);
            if (string.IsNullOrWhiteSpace(nid))
            {
                if (symbolBind == SymbolBindWeak)
                {
                    descriptors.Add(CreateSymbolRelocationDescriptor(
                        relocation,
                        targetAddress,
                        symbol,
                        symbolAddress: 0,
                        importNid: null,
                        isWeak: true));
                }
                continue;
            }

            if (seenImportNids.Add(nid))
            {
                orderedImportNids.Add(nid);
            }

            descriptors.Add(CreateSymbolRelocationDescriptor(
                relocation,
                targetAddress,
                symbol,
                symbolAddress: 0,
                importNid: nid,
                isWeak: symbolBind == SymbolBindWeak));
        }
    }

    private static RelocationDescriptor CreateSymbolRelocationDescriptor(
        ElfRelocation relocation,
        ulong targetAddress,
        ElfSymbol symbol,
        ulong symbolAddress,
        string? importNid,
        bool isWeak)
    {
        var valueKind = relocation.Type switch
        {
            RelocationTypePc32 or RelocationTypePlt32 or RelocationTypePc64 => RelocationValueKind.PcRelative,
            RelocationTypeSize32 or RelocationTypeSize64 => RelocationValueKind.SymbolSize,
            _ => RelocationValueKind.Pointer,
        };
        var writeKind = relocation.Type switch
        {
            RelocationTypePc32 or RelocationTypePlt32 or RelocationTypeSigned32 => RelocationWriteKind.Int32,
            RelocationTypeUnsigned32 or RelocationTypeSize32 => RelocationWriteKind.UInt32,
            _ => RelocationWriteKind.UInt64,
        };
        var symbolValue = valueKind == RelocationValueKind.SymbolSize
            ? symbol.Size
            : symbolAddress;
        var addend = relocation.Type is RelocationTypeGlobalData or RelocationTypeJumpSlot
            ? 0
            : relocation.Addend;
        return new RelocationDescriptor(
            targetAddress,
            addend,
            importNid,
            symbolValue,
            valueKind,
            IsDataImport: GetSymbolType(symbol.Info) == SymbolTypeObject,
            writeKind,
            isWeak);
    }

    private static bool ShouldCreateImportStub(
        string nid,
        IReadOnlyList<RelocationDescriptor> descriptors,
        IModuleManager? moduleManager)
    {
        for (var i = 0; i < descriptors.Count; i++)
        {
            var descriptor = descriptors[i];
            if (!string.Equals(descriptor.ImportNid, nid, StringComparison.Ordinal))
            {
                continue;
            }
            if (!descriptor.IsWeak || moduleManager?.TryGetExport(nid, out _) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void RegisterRuntimeSymbolsAndHooks(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        IReadOnlyList<ProgramHeader> programHeaders,
        ElfHeader elfHeader,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols)
    {
        var sectionSymbols = RegisterSectionRuntimeSymbols(imageData, loadContext, elfHeader, imageBase, importStubs, runtimeSymbols);
        var dynamicSymbols = RegisterDynamicRuntimeSymbols(
            imageData,
            loadContext,
            programHeaders,
            virtualMemory,
            imageBase,
            importStubs,
            runtimeSymbols);

        if (sectionSymbols > 0 || dynamicSymbols > 0)
        {
            Console.Error.WriteLine(
                $"[LOADER] Runtime symbol index populated: section={sectionSymbols}, dynamic={dynamicSymbols}, total={runtimeSymbols.Count}");
        }
    }

    private static void CollectInitializerFunctions(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        IReadOnlyList<ProgramHeader> programHeaders,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        out ulong initFunctionEntryPoint,
        out IReadOnlyList<ulong> preInitializerFunctions,
        out IReadOnlyList<ulong> initializerFunctions)
    {
        initFunctionEntryPoint = 0;
        preInitializerFunctions = EmptyInitializerFunctions;
        initializerFunctions = EmptyInitializerFunctions;

        if (!TryGetProgramHeader(programHeaders, ProgramHeaderType.Dynamic, out var dynamicHeader, out var dynamicHeaderIndex) ||
            dynamicHeader.FileSize == 0)
        {
            return;
        }

        if (!TryLoadDynamicTableBytes(
                imageData,
                loadContext,
                virtualMemory,
                imageBase,
                dynamicHeader,
                dynamicHeaderIndex,
                out var dynamicTable))
        {
            return;
        }

        var dynamicInfo = ParseDynamicInfo(dynamicTable);
        var preInitializers = new List<ulong>(4);
        var initializers = new List<ulong>(8);
        initFunctionEntryPoint = ResolveMappedAddressOrFallback(virtualMemory, dynamicInfo.InitOffset, imageBase);
        if (initFunctionEntryPoint < 0x10000)
        {
            initFunctionEntryPoint = 0;
        }

        AppendInitializerArrayEntries(
            preInitializers,
            imageData,
            virtualMemory,
            imageBase,
            dynamicInfo.PreInitArrayOffset,
            dynamicInfo.PreInitArraySize);

        AppendResolvedInitializer(initializers, dynamicInfo.InitOffset, virtualMemory, imageBase);
        AppendInitializerArrayEntries(
            initializers,
            imageData,
            virtualMemory,
            imageBase,
            dynamicInfo.InitArrayOffset,
            dynamicInfo.InitArraySize);

        if (preInitializers.Count != 0)
        {
            preInitializerFunctions = preInitializers;
        }

        if (initializers.Count != 0)
        {
            initializerFunctions = initializers;
        }

        if (preInitializers.Count != 0 || initializers.Count != 0)
        {
            Console.Error.WriteLine(
                $"[LOADER] Initializers discovered: preinit={preInitializers.Count}, init={initializers.Count}");
        }
    }

    private static void AppendInitializerArrayEntries(
        ICollection<ulong> destination,
        ReadOnlySpan<byte> imageData,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        ulong arrayOffset,
        ulong arraySize)
    {
        if (arrayOffset == 0 || arraySize < sizeof(ulong))
        {
            return;
        }

        if (!TryLoadTableBytes(imageData, virtualMemory, imageBase, arrayOffset, arraySize, out var arrayBytes))
        {
            return;
        }

        var entryCount = arrayBytes.Length / sizeof(ulong);
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = i * sizeof(ulong);
            var entryAddress = BinaryPrimitives.ReadUInt64LittleEndian(arrayBytes.AsSpan(entryOffset, sizeof(ulong)));
            AppendResolvedInitializer(destination, entryAddress, virtualMemory, imageBase);
        }
    }

    private static void AppendResolvedInitializer(
        ICollection<ulong> destination,
        ulong functionAddress,
        IVirtualMemory virtualMemory,
        ulong imageBase)
    {
        var resolvedAddress = ResolveMappedAddressOrFallback(virtualMemory, functionAddress, imageBase);
        if (resolvedAddress < 0x10000)
        {
            return;
        }

        foreach (var existing in destination)
        {
            if (existing == resolvedAddress)
            {
                return;
            }
        }

        destination.Add(resolvedAddress);
    }

    private static IReadOnlyList<ImportedSymbolRelocation> BuildImportedRelocations(
        IReadOnlyList<RelocationDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return Array.Empty<ImportedSymbolRelocation>();
        }

        var importedRelocations = new List<ImportedSymbolRelocation>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ImportNid is null || descriptor.ValueKind != RelocationValueKind.Pointer)
            {
                continue;
            }

            importedRelocations.Add(new ImportedSymbolRelocation(
                descriptor.TargetAddress,
                descriptor.Addend,
                descriptor.ImportNid,
                descriptor.IsDataImport));
        }

        return importedRelocations.Count == 0
            ? Array.Empty<ImportedSymbolRelocation>()
            : importedRelocations;
    }

    private static int RegisterSectionRuntimeSymbols(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ElfHeader elfHeader,
        ulong imageBase,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols)
    {
        if (elfHeader.SectionHeaderOffset == 0 ||
            elfHeader.SectionHeaderCount == 0 ||
            elfHeader.SectionHeaderEntrySize < ElfSectionHeaderSize)
        {
            return 0;
        }

        var added = 0;
        for (var sectionIndex = 0; sectionIndex < elfHeader.SectionHeaderCount; sectionIndex++)
        {
            if (!TryReadSectionHeader(imageData, loadContext, elfHeader, sectionIndex, out var sectionHeader))
            {
                continue;
            }

            if (sectionHeader.Type != SectionTypeSymbolTable ||
                sectionHeader.Size == 0 ||
                sectionHeader.EntrySize < ElfSymbolSize ||
                sectionHeader.Link >= elfHeader.SectionHeaderCount)
            {
                continue;
            }

            if (!TryReadSectionHeader(imageData, loadContext, elfHeader, (int)sectionHeader.Link, out var stringHeader))
            {
                continue;
            }

            if (!TryReadElfRelativeSlice(imageData, loadContext, stringHeader.Offset, stringHeader.Size, out var stringTable))
            {
                continue;
            }

            var symbolCount = sectionHeader.Size / sectionHeader.EntrySize;
            for (ulong symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
            {
                var symbolOffset = sectionHeader.Offset + (symbolIndex * sectionHeader.EntrySize);
                if (!TryReadElfRelativeSlice(imageData, loadContext, symbolOffset, ElfSymbolSize, out var symbolBytes))
                {
                    continue;
                }

                var symbol = ReadUnmanaged<ElfSymbol>(symbolBytes, 0);
                if (symbol.Value == 0 || symbol.NameOffset == 0)
                {
                    continue;
                }

                if (!TryReadNullTerminatedAscii(stringTable, symbol.NameOffset, out var symbolName))
                {
                    continue;
                }

                var symbolAddress = symbol.Value >= imageBase
                    ? symbol.Value
                    : unchecked(imageBase + symbol.Value);
                if (symbolAddress < 0x10000)
                {
                    continue;
                }

                if (RegisterRuntimeSymbol(runtimeSymbols, importStubs, symbolName, symbolAddress))
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static int RegisterDynamicRuntimeSymbols(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        IReadOnlyList<ProgramHeader> programHeaders,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols)
    {
        if (!TryGetProgramHeader(programHeaders, ProgramHeaderType.Dynamic, out var dynamicHeader, out var dynamicHeaderIndex))
        {
            return 0;
        }

        if (dynamicHeader.FileSize == 0 || dynamicHeader.FileSize > int.MaxValue)
        {
            return 0;
        }

        if (!TryLoadDynamicTableBytes(
                imageData,
                loadContext,
                virtualMemory,
                imageBase,
                dynamicHeader,
                dynamicHeaderIndex,
                out var dynamicTable))
        {
            return 0;
        }

        var dynamicInfo = ParseDynamicInfo(dynamicTable);
        if (dynamicInfo.SymTabOffset == 0 || dynamicInfo.StrTabOffset == 0)
        {
            return 0;
        }

        if (!TryLoadTableBytes(
                imageData,
                virtualMemory,
                imageBase,
                dynamicInfo.StrTabOffset,
                dynamicInfo.StrTabSize,
                out var stringTable) ||
            stringTable.Length == 0)
        {
            return 0;
        }

        ulong symTableSize = dynamicInfo.SymTabSize;
        if (symTableSize == 0 || symTableSize > int.MaxValue)
        {
            return 0;
        }

        if (!TryLoadTableBytes(
                imageData,
                virtualMemory,
                imageBase,
                dynamicInfo.SymTabOffset,
                symTableSize,
                out var symbolTable) ||
            symbolTable.Length < ElfSymbolSize)
        {
            return 0;
        }

        var symbolCount = (uint)(symbolTable.Length / ElfSymbolSize);
        var added = 0;
        for (uint symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
        {
            if (!TryReadSymbol(symbolTable, symbolIndex, out var symbol) ||
                symbol.Value == 0 ||
                symbol.NameOffset == 0 ||
                !TryReadNullTerminatedAscii(stringTable, symbol.NameOffset, out var symbolName))
            {
                continue;
            }

            var symbolAddress = symbol.Value >= imageBase
                ? symbol.Value
                : unchecked(imageBase + symbol.Value);
            if (symbolAddress < 0x10000)
            {
                continue;
            }

            if (RegisterRuntimeSymbol(runtimeSymbols, importStubs, symbolName, symbolAddress))
            {
                added++;
            }
        }

        return added;
    }

    private static bool RegisterRuntimeSymbol(
        IDictionary<string, ulong> runtimeSymbols,
        IDictionary<ulong, string> importStubs,
        string symbolName,
        ulong symbolAddress)
    {
        if (string.IsNullOrWhiteSpace(symbolName) || symbolAddress < 0x10000)
        {
            return false;
        }

        var addedAny = false;
        if (!runtimeSymbols.ContainsKey(symbolName))
        {
            runtimeSymbols[symbolName] = symbolAddress;
            addedAny = true;
        }

        var nid = ExtractNid(symbolName);
        if (!string.IsNullOrWhiteSpace(nid) &&
            !string.Equals(symbolName, nid, StringComparison.Ordinal) &&
            !runtimeSymbols.ContainsKey(nid))
        {
            runtimeSymbols[nid] = symbolAddress;
            addedAny = true;
        }

        if (symbolName.Length > 1 &&
            symbolName[0] == '_' &&
            !runtimeSymbols.ContainsKey(symbolName[1..]))
        {
            runtimeSymbols[symbolName[1..]] = symbolAddress;
            addedAny = true;
        }

        if (string.Equals(symbolName, "kernel_dynlib_dlsym", StringComparison.Ordinal))
        {
            importStubs[symbolAddress] = RuntimeStubNids.KernelDynlibDlsym;
        }

        return addedAny;
    }

    private static bool TryReadSectionHeader(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ElfHeader elfHeader,
        int sectionIndex,
        out ElfSectionHeader sectionHeader)
    {
        if ((uint)sectionIndex >= elfHeader.SectionHeaderCount)
        {
            sectionHeader = default;
            return false;
        }

        var headerOffset = elfHeader.SectionHeaderOffset + ((ulong)sectionIndex * elfHeader.SectionHeaderEntrySize);
        if (!TryReadElfRelativeSlice(imageData, loadContext, headerOffset, ElfSectionHeaderSize, out var sectionBytes))
        {
            sectionHeader = default;
            return false;
        }

        sectionHeader = ReadUnmanaged<ElfSectionHeader>(sectionBytes, 0);
        return true;
    }

    private static bool TryLoadDynamicTableBytes(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        ProgramHeader dynamicHeader,
        int dynamicHeaderIndex,
        out ReadOnlySpan<byte> dynamicTable)
    {
        if (TryLoadTableBytes(
                imageData,
                virtualMemory,
                imageBase,
                dynamicHeader.VirtualAddress,
                dynamicHeader.FileSize,
                out var loadedDynamicTable))
        {
            dynamicTable = loadedDynamicTable;
            return true;
        }

        var dynamicOffset = ResolvePhysicalSegmentOffset(imageData.Length, loadContext, dynamicHeader, dynamicHeaderIndex);
        if (!TrySlice(imageData, dynamicOffset, dynamicHeader.FileSize, out dynamicTable))
        {
            dynamicTable = default;
            return false;
        }

        return true;
    }

    private static bool TryReadElfRelativeSlice(
        ReadOnlySpan<byte> imageData,
        LoadContext loadContext,
        ulong elfRelativeOffset,
        ulong size,
        out ReadOnlySpan<byte> slice)
    {
        try
        {
            var absoluteOffset = checked((ulong)loadContext.ElfOffset + elfRelativeOffset);
            return TrySlice(imageData, absoluteOffset, size, out slice);
        }
        catch (OverflowException)
        {
            slice = default;
            return false;
        }
    }

    private static Dictionary<ulong, string> CreateImportStubMapping(IVirtualMemory virtualMemory, IReadOnlyList<string> orderedImportNids)
    {
        if (orderedImportNids.Count == 0)
        {
            return new Dictionary<ulong, string>();
        }

        var requiredBytes = checked((ulong)orderedImportNids.Count * ImportStubSlotSize);
        var mapSize = AlignUp(Math.Max(requiredBytes, (ulong)PageSize), PageSize);
        Console.Error.WriteLine(
            $"[LOADER] CreateImportStubMapping: nids={orderedImportNids.Count}, required=0x{requiredBytes:X}, map_size=0x{mapSize:X}");
        if (mapSize > int.MaxValue)
        {
            throw new NotSupportedException("Import stub mapping exceeds 2 GB and is not supported.");
        }

        var stubData = new byte[(int)mapSize];
        for (var i = 0; i < orderedImportNids.Count; i++)
        {
            var slotOffset = checked((int)((ulong)i * ImportStubSlotSize));
            stubData[slotOffset] = StubTrapOpcode;
            stubData[slotOffset + 1] = StubReturnOpcode;
            var nid = orderedImportNids[i];
            var nidHash = NidToUInt32(nid);
            BinaryPrimitives.WriteUInt32LittleEndian(stubData.AsSpan(slotOffset + 8), nidHash);
        }

        var stubBaseAddress = TryMapImportStubRegion(virtualMemory, mapSize, stubData);
        var byAddress = new Dictionary<ulong, string>(orderedImportNids.Count);
        for (var i = 0; i < orderedImportNids.Count; i++)
        {
            var address = stubBaseAddress + ((ulong)i * ImportStubSlotSize);
            byAddress[address] = orderedImportNids[i];
        }

        return byAddress;
    }

    private static ulong TryMapImportStubRegion(IVirtualMemory virtualMemory, ulong mapSize, ReadOnlySpan<byte> mapData)
    {
        for (var i = 0; i < 64; i++)
        {
            var candidateBase = ImportStubBaseAddress - ((ulong)i * ImportStubAddressStride);
            if (IsAddressRangeMapped(virtualMemory, candidateBase, mapSize))
            {
                continue;
            }

            try
            {
                virtualMemory.Map(
                    candidateBase,
                    mapSize,
                    fileOffset: 0,
                    mapData,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
                return candidateBase;
            }
            catch (InvalidOperationException)
            {
                continue;
            }
        }

        throw new InvalidOperationException("Unable to reserve an import stub region in virtual memory.");
    }

    private static bool IsAddressRangeMapped(IVirtualMemory virtualMemory, ulong start, ulong size)
    {
        var end = checked(start + size);
        var regions = virtualMemory.SnapshotRegions();
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var regionStart = region.VirtualAddress;
            var regionEnd = checked(regionStart + region.MemorySize);
            if (start < regionEnd && end > regionStart)
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectRelocations(ReadOnlySpan<byte> relocationTable, ICollection<ElfRelocation> relocations)
    {
        for (var offset = 0; offset + ElfRelocationSize <= relocationTable.Length; offset += ElfRelocationSize)
        {
            relocations.Add(ReadRelocation(relocationTable, offset));
        }
    }

    private static bool TryGetProgramHeader(
        IReadOnlyList<ProgramHeader> programHeaders,
        ProgramHeaderType targetType,
        out ProgramHeader header,
        out int headerIndex)
    {
        for (var i = 0; i < programHeaders.Count; i++)
        {
            if (programHeaders[i].HeaderType != targetType)
            {
                continue;
            }

            header = programHeaders[i];
            headerIndex = i;
            return true;
        }

        header = default;
        headerIndex = -1;
        return false;
    }

    private static DynamicInfo ParseDynamicInfo(ReadOnlySpan<byte> dynamicTable)
    {
        ulong strTabOffset = 0;
        ulong strTabSize = 0;
        ulong symTabOffset = 0;
        ulong symTabSize = 0;
        ulong initOffset = 0;
        ulong relaOffset = 0;
        ulong relaSize = 0;
        ulong jmpRelOffset = 0;
        ulong jmpRelSize = 0;
        ulong pltGotOffset = 0;
        ulong initArrayOffset = 0;
        ulong initArraySize = 0;
        ulong preInitArrayOffset = 0;
        ulong preInitArraySize = 0;

        for (var offset = 0; offset + DynamicEntrySize <= dynamicTable.Length; offset += DynamicEntrySize)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(dynamicTable.Slice(offset, sizeof(long)));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(dynamicTable.Slice(offset + sizeof(long), sizeof(ulong)));
            if (tag == DtNull)
            {
                break;
            }

            switch (tag)
            {
                case DtStrTab:
                    if (strTabOffset == 0)
                    {
                        strTabOffset = value;
                    }

                    break;
                case DtStrSize:
                    if (strTabSize == 0)
                    {
                        strTabSize = value;
                    }

                    break;
                case DtSymTab:
                    if (symTabOffset == 0)
                    {
                        symTabOffset = value;
                    }

                    break;
                case DtRela:
                    if (relaOffset == 0)
                    {
                        relaOffset = value;
                    }

                    break;
                case DtRelaSize:
                    if (relaSize == 0)
                    {
                        relaSize = value;
                    }

                    break;
                case DtInit:
                    if (initOffset == 0)
                    {
                        initOffset = value;
                    }

                    break;
                case DtJmpRel:
                    if (jmpRelOffset == 0)
                    {
                        jmpRelOffset = value;
                    }

                    break;
                case DtInitArray:
                    if (initArrayOffset == 0)
                    {
                        initArrayOffset = value;
                    }

                    break;
                case DtInitArraySize:
                    if (initArraySize == 0)
                    {
                        initArraySize = value;
                    }

                    break;
                case DtPreInitArray:
                    if (preInitArrayOffset == 0)
                    {
                        preInitArrayOffset = value;
                    }

                    break;
                case DtPreInitArraySize:
                    if (preInitArraySize == 0)
                    {
                        preInitArraySize = value;
                    }

                    break;
                case DtPltRelSize:
                    if (jmpRelSize == 0)
                    {
                        jmpRelSize = value;
                    }

                    break;
                case DtPltGot:
                    if (pltGotOffset == 0)
                    {
                        pltGotOffset = value;
                    }

                    break;
                case DtSceStrTab:
                    strTabOffset = value;
                    break;
                case DtSceStrSize:
                    strTabSize = value;
                    break;
                case DtSceSymTab:
                    symTabOffset = value;
                    break;
                case DtSceSymTabSize:
                    symTabSize = value;
                    break;
                case DtSceRela:
                    relaOffset = value;
                    break;
                case DtSceRelaSize:
                    relaSize = value;
                    break;
                case DtSceJmpRel:
                    jmpRelOffset = value;
                    break;
                case DtScePltRelSize:
                    jmpRelSize = value;
                    break;
            }
        }

        return new DynamicInfo(
            strTabOffset,
            strTabSize,
            symTabOffset,
            symTabSize,
            initOffset,
            relaOffset,
            relaSize,
            jmpRelOffset,
            jmpRelSize,
            pltGotOffset,
            initArrayOffset,
            initArraySize,
            preInitArrayOffset,
            preInitArraySize);
    }

    private static bool IsSupportedRelocationType(uint relocationType)
    {
        return relocationType is
            RelocationTypeNone or
            RelocationTypeAbsolute64 or
            RelocationTypePc32 or
            RelocationTypePlt32 or
            RelocationTypeGlobalData or
            RelocationTypeJumpSlot or
            RelocationTypeRelative or
            RelocationTypeUnsigned32 or
            RelocationTypeSigned32 or
            RelocationTypeTlsModuleId or
            RelocationTypeTlsDtpOff64 or
            RelocationTypeTlsTpOff64 or
            RelocationTypePc64 or
            RelocationTypeSize32 or
            RelocationTypeSize64 or
            RelocationTypeRelative64;
    }

    private static readonly HashSet<uint> _reportedUnsupportedRelocationTypes = new();

    private static void ReportUnsupportedRelocation(uint relocationType, ulong offset, ulong imageBase)
    {
        // Report each distinct unsupported type once to keep the log useful.
        lock (_reportedUnsupportedRelocationTypes)
        {
            if (!_reportedUnsupportedRelocationTypes.Add(relocationType))
            {
                if (IsFocusRelocationOffset(offset, imageBase))
                {
                    Console.Error.WriteLine($"[LOADER][FOCUS][SKIP] unsupported type={relocationType}");
                }

                return;
            }
        }

        var name = relocationType switch
        {
            5 => "R_X86_64_COPY",
            37 => "R_X86_64_IRELATIVE (ifunc)",
            _ => "unknown",
        };
        Console.Error.WriteLine(
            $"[LOADER][ERROR] Unsupported relocation type {relocationType} ({name}) rejected " +
            $"(first at off=0x{offset:X16}); COPY requires dependency symbol storage and IRELATIVE requires resolver execution.");
    }

    private static ulong DetermineRequestedImageBase(
        IVirtualMemory virtualMemory,
        ulong totalImageSize,
        bool isNextGen,
        bool clearVirtualMemory)
    {
        if (clearVirtualMemory)
        {
            return isNextGen ? Ps5MainImageBase : Ps4MainImageBase;
        }

        var (searchStart, searchEnd) = GetModuleSearchRange(isNextGen);
        var alignedSize = AlignUp(Math.Max(totalImageSize, (ulong)PageSize), (ulong)PageSize);
        var candidate = searchStart;
        foreach (var region in virtualMemory.SnapshotRegions())
        {
            if (region.VirtualAddress >= searchEnd)
            {
                continue;
            }

            var regionEnd = region.VirtualAddress + region.MemorySize;
            if (regionEnd <= searchStart)
            {
                continue;
            }

            var regionAlignedEnd = AlignUp(regionEnd + ModulePlacementStep, (ulong)PageSize);
            if (regionAlignedEnd > candidate)
            {
                candidate = regionAlignedEnd;
            }
        }

        if (candidate < searchStart)
        {
            candidate = searchStart;
        }

        if (candidate + alignedSize > searchEnd)
        {
            candidate = searchStart;
        }

        return AlignUp(candidate, (ulong)PageSize);
    }

    private static bool TryAllocateAdditionalImageAtExact(
        PhysicalVirtualMemory physicalVm,
        ulong preferredBase,
        ulong totalImageSize,
        bool isNextGen,
        out ulong allocatedBase)
    {
        var (searchStart, searchEnd) = GetModuleSearchRange(isNextGen);
        var alignedSize = AlignUp(Math.Max(totalImageSize, (ulong)PageSize), (ulong)PageSize);
        if (preferredBase < searchStart || preferredBase + alignedSize > searchEnd)
        {
            preferredBase = searchStart;
        }

        for (var attempt = 0; attempt < 256; attempt++)
        {
            var candidate = AlignUp(preferredBase + ((ulong)attempt * ModulePlacementStep), (ulong)PageSize);
            if (candidate + alignedSize > searchEnd)
            {
                break;
            }

            if (physicalVm.TryAllocateAtExact(candidate, alignedSize, executable: true, out allocatedBase))
            {
                return true;
            }
        }

        allocatedBase = 0;
        return false;
    }

    private static (ulong Start, ulong End) GetModuleSearchRange(bool isNextGen)
    {
        return isNextGen
            ? (Ps5ModuleSearchStart, Ps5ModuleSearchEnd)
            : (Ps4ModuleSearchStart, Ps4ModuleSearchEnd);
    }

    private static ulong CalculateTotalImageSize(IReadOnlyList<ProgramHeader> programHeaders)
    {
        ulong minAddr = ulong.MaxValue;
        ulong maxAddr = 0;

        foreach (var header in programHeaders)
        {
            if (header.HeaderType == ProgramHeaderType.Load && header.MemorySize > 0)
            {
                if (header.VirtualAddress < minAddr)
                    minAddr = header.VirtualAddress;

                var endAddr = header.VirtualAddress + header.MemorySize;
                if (endAddr > maxAddr)
                    maxAddr = endAddr;
            }
        }

        if (minAddr == ulong.MaxValue)
            return 0;

        return maxAddr - minAddr;
    }

    private static ulong ComputeImageBase(IReadOnlyList<ProgramHeader> programHeaders)
    {
        var hasBase = false;
        ulong imageBase = 0;

        for (var i = 0; i < programHeaders.Count; i++)
        {
            var header = programHeaders[i];
            if (header.HeaderType != ProgramHeaderType.Load || header.MemorySize == 0)
            {
                continue;
            }

            if (!hasBase || header.VirtualAddress < imageBase)
            {
                imageBase = header.VirtualAddress;
                hasBase = true;
            }
        }

        return hasBase ? imageBase : 0;
    }

    private static bool TryLoadTableBytes(
        ReadOnlySpan<byte> elfData,
        IVirtualMemory virtualMemory,
        ulong imageBase,
        ulong location,
        ulong size,
        out byte[] tableBytes)
    {
        if (size == 0 || size > int.MaxValue)
        {
            Console.WriteLine($"[LOADER] TryLoadTableBytes: size=0 or too big (0x{size:X})");
            tableBytes = Array.Empty<byte>();
            return false;
        }

        Console.WriteLine($"[LOADER] TryLoadTableBytes: trying location=0x{location:X}, size=0x{size:X}, imageBase=0x{imageBase:X}");

        tableBytes = GC.AllocateUninitializedArray<byte>((int)size);

        var guestAddr = location + imageBase;
        Console.Error.WriteLine($"[LOADER] TryLoadTableBytes: trying guest address 0x{guestAddr:X}");
        if (virtualMemory.TryRead(guestAddr, tableBytes))
        {
            Console.Error.WriteLine($"[LOADER] TryLoadTableBytes: loaded from guest memory at 0x{guestAddr:X}");
            return true;
        }

        if (virtualMemory.TryRead(location, tableBytes))
        {
            Console.Error.WriteLine($"[LOADER] TryLoadTableBytes: loaded from absolute guest address 0x{location:X}");
            return true;
        }

        if (!elfData.IsEmpty && location <= int.MaxValue && location + size <= (ulong)elfData.Length)
        {
            var slice = elfData.Slice((int)location, (int)size);
            tableBytes = slice.ToArray();
            Console.Error.WriteLine($"[LOADER] TryLoadTableBytes: loaded from elfData as file offset at 0x{location:X}");
            return true;
        }

        Console.Error.WriteLine($"[LOADER] TryLoadTableBytes: FAILED for location 0x{location:X}");
        tableBytes = Array.Empty<byte>();
        return false;
    }

    private static bool TryReadSymbol(ReadOnlySpan<byte> symbolTable, uint symbolIndex, out ElfSymbol symbol)
    {
        symbol = default;
        var offset64 = (long)symbolIndex * ElfSymbolSize;
        if (offset64 < 0 || offset64 + ElfSymbolSize > symbolTable.Length)
        {
            return false;
        }

        var offset = (int)offset64;
        var entry = symbolTable.Slice(offset, ElfSymbolSize);
        symbol = new ElfSymbol(
            BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0, sizeof(uint))),
            entry[4],
            entry[5],
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, sizeof(ushort))),
            BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8, sizeof(ulong))),
            BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(16, sizeof(ulong))));
        return true;
    }

    private static byte GetSymbolBind(byte info)
    {
        return (byte)(info >> 4);
    }

    private static byte GetSymbolType(byte info)
    {
        return (byte)(info & 0x0F);
    }

    private static bool IsFocusRelocationOffset(ulong relocationOffset, ulong imageBase)
    {
        if (relocationOffset >= FocusRelocGuestStart && relocationOffset <= FocusRelocGuestEnd)
        {
            return true;
        }

        if (relocationOffset <= ulong.MaxValue - imageBase)
        {
            var rebased = relocationOffset + imageBase;
            if (rebased >= FocusRelocGuestStart && rebased <= FocusRelocGuestEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong ResolveMappedAddressOrFallback(IVirtualMemory virtualMemory, ulong address, ulong imageBase)
    {
        Console.Error.WriteLine($"[LOADER][TEST] ResolveMappedAddressOrFallback addr=0x{address:X} imageBase=0x{imageBase:X16}");

        if (address == 0)
        {
            Console.Error.WriteLine("[LOADER][TEST] -> return 0 (null)");
            return 0;
        }

        if (TryResolveMappedAddress(virtualMemory, address, imageBase, 1, out var resolved))
        {
            Console.Error.WriteLine($"[LOADER][TEST] -> resolved raw 0x{resolved:X16}");
            return resolved;
        }

        if (address <= ulong.MaxValue - imageBase)
        {
            var rebased = address + imageBase;
            if (TryResolveMappedAddress(virtualMemory, rebased, imageBase, 1, out var resolvedRebased))
            {
                Console.Error.WriteLine($"[LOADER][TEST] -> resolved rebased 0x{resolvedRebased:X16}");
                return resolvedRebased;
            }
        }

        if (address < 0x10000)
        {
            Console.Error.WriteLine($"[LOADER][TEST] -> reject small 0x{address:X}");
            return 0;
        }

        Console.Error.WriteLine($"[LOADER][TEST] -> fallback raw 0x{address:X}");
        return address;
    }

    private static bool TryResolveMappedAddress(
        IVirtualMemory virtualMemory,
        ulong address,
        ulong imageBase,
        int requiredBytes,
        out ulong resolvedAddress)
    {
        if (CanAccessAddress(virtualMemory, address, requiredBytes))
        {
            resolvedAddress = address;
            return true;
        }

        if (address <= ulong.MaxValue - imageBase)
        {
            var rebased = address + imageBase;
            if (CanAccessAddress(virtualMemory, rebased, requiredBytes))
            {
                resolvedAddress = rebased;
                return true;
            }
        }

        resolvedAddress = 0;
        return false;
    }

    private static bool CanAccessAddress(IVirtualMemory virtualMemory, ulong address, int requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return true;
        }

        Span<byte> probe = stackalloc byte[sizeof(ulong)];
        var length = Math.Min(requiredBytes, probe.Length);
        return virtualMemory.TryRead(address, probe[..length]);
    }

    private static ElfRelocation ReadRelocation(ReadOnlySpan<byte> relocationTable, int offset)
    {
        var entry = relocationTable.Slice(offset, ElfRelocationSize);
        return new ElfRelocation(
            BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(0, sizeof(ulong))),
            BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8, sizeof(ulong))),
            BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(16, sizeof(long))));
    }

    private static bool TryReadNullTerminatedAscii(ReadOnlySpan<byte> source, uint offset, out string value)
    {
        if (offset >= (uint)source.Length)
        {
            value = string.Empty;
            return false;
        }

        var slice = source[(int)offset..];
        var terminatorIndex = slice.IndexOf((byte)0);
        if (terminatorIndex < 0)
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.ASCII.GetString(slice[..terminatorIndex]);
        return true;
    }

    private static string ExtractNid(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return string.Empty;
        }

        var separator = symbolName.IndexOf('#');
        return separator <= 0 ? symbolName : symbolName[..separator];
    }

    private static bool TryWriteUInt64(IVirtualMemory virtualMemory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return virtualMemory.TryWrite(address, buffer);
    }

    private static ulong ComputeRelocationValue(RelocationDescriptor descriptor, ulong resolvedSymbolValue)
    {
        var baseValue = descriptor.ValueKind == RelocationValueKind.SymbolSize
            ? descriptor.SymbolValue
            : resolvedSymbolValue;
        var value = AddSigned(baseValue, descriptor.Addend);
        return descriptor.ValueKind == RelocationValueKind.PcRelative
            ? unchecked(value - descriptor.TargetAddress)
            : value;
    }

    private static bool TryWriteRelocationValue(
        IVirtualMemory virtualMemory,
        RelocationDescriptor descriptor,
        ulong value,
        out string? error)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        var length = sizeof(ulong);
        switch (descriptor.WriteKind)
        {
            case RelocationWriteKind.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
                break;

            case RelocationWriteKind.UInt32:
                if (value > uint.MaxValue)
                {
                    error = $"value 0x{value:X16} overflows an unsigned 32-bit relocation";
                    return false;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)value);
                length = sizeof(uint);
                break;

            case RelocationWriteKind.Int32:
                var signedValue = unchecked((long)value);
                if (signedValue is < int.MinValue or > int.MaxValue)
                {
                    error = $"value {signedValue} overflows a signed 32-bit relocation";
                    return false;
                }
                BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)signedValue);
                length = sizeof(int);
                break;

            default:
                error = $"unknown relocation write kind {descriptor.WriteKind}";
                return false;
        }

        error = virtualMemory.TryWrite(descriptor.TargetAddress, buffer[..length])
            ? null
            : "target memory is not writable";
        return error is null;
    }

    private static int GetRelocationWriteSize(uint relocationType)
    {
        return relocationType is
            RelocationTypePc32 or
            RelocationTypePlt32 or
            RelocationTypeUnsigned32 or
            RelocationTypeSigned32 or
            RelocationTypeSize32
                ? sizeof(uint)
                : sizeof(ulong);
    }

    [Conditional("DEBUG")]
    private static void RunRelocationSelfChecks()
    {
        var pc32 = new RelocationDescriptor(
            TargetAddress: 0x1000,
            Addend: -4,
            ImportNid: null,
            SymbolValue: 0x1800,
            RelocationValueKind.PcRelative,
            IsDataImport: false,
            RelocationWriteKind.Int32);
        Debug.Assert(
            unchecked((long)ComputeRelocationValue(pc32, pc32.SymbolValue)) == 0x7FC,
            "R_X86_64_PC32 did not apply S + A - P.");

        var weak = new RelocationDescriptor(
            TargetAddress: 0x2000,
            Addend: 7,
            ImportNid: "weak",
            SymbolValue: 0,
            RelocationValueKind.Pointer,
            IsDataImport: false,
            IsWeak: true);
        Debug.Assert(
            ComputeRelocationValue(weak, resolvedSymbolValue: 0) == 7,
            "An unresolved weak relocation did not use S=0.");
        Debug.Assert(
            !ShouldCreateImportStub("weak", [weak], moduleManager: null),
            "An unresolved weak symbol incorrectly received a trap import stub.");
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static ulong AddSigned(ulong value, long addend)
    {
        if (addend >= 0)
        {
            return unchecked(value + (ulong)addend);
        }

        var magnitude = unchecked((ulong)(-(addend + 1))) + 1;
        return unchecked(value - magnitude);
    }

    private static uint NidToUInt32(string nid)
    {
        if (string.IsNullOrEmpty(nid))
            return 0;

        uint hash = 0;
        for (int i = 0; i < Math.Min(nid.Length, 8); i++)
        {
            hash = (hash << 4) | (byte)nid[i];
        }

        hash ^= (uint)nid.Length;

        return hash;
    }

    private static bool TrySlice(ReadOnlySpan<byte> source, ulong offset, ulong size, out ReadOnlySpan<byte> slice)
    {
        if (size == 0)
        {
            slice = ReadOnlySpan<byte>.Empty;
            return true;
        }

        if (size > int.MaxValue || offset > (ulong)source.Length)
        {
            slice = default;
            return false;
        }

        var end = offset + size;
        if (end < offset || end > (ulong)source.Length)
        {
            slice = default;
            return false;
        }

        slice = source.Slice((int)offset, (int)size);
        return true;
    }

    private static ulong ResolvePhysicalSegmentOffset(int imageLength, LoadContext loadContext, ProgramHeader header, int headerIndex)
    {
        if (!loadContext.IsSelf)
        {
            return checked((ulong)loadContext.ElfOffset + header.Offset);
        }

        if (!TryResolveSelfSegmentOffset(
                imageLength,
                loadContext.SelfSegments,
                header,
                headerIndex,
                out var offset,
                out var resolveStatus))
        {
            if (TryResolveSelfFallbackOffset(imageLength, loadContext, header, out var fallbackOffset))
            {
                return fallbackOffset;
            }

            if (resolveStatus is SelfSegmentResolveStatus.Encrypted or SelfSegmentResolveStatus.Compressed)
            {
                throw new NotSupportedException(
                    $"SELF segment for program header {headerIndex} is marked as {resolveStatus.ToString().ToLowerInvariant()} and no dumped payload could be resolved. " +
                    "Runtime decryption is not implemented yet. Use a decrypted ELF/FSELF image.");
            }

            throw new NotSupportedException($"SELF segment mapping for program header {headerIndex} could not be resolved.");
        }

        return offset;
    }

    private static bool TryResolveSelfFallbackOffset(
        int imageLength,
        LoadContext loadContext,
        ProgramHeader header,
        out ulong offset)
    {
        if (TryIsInRange(imageLength, header.Offset, header.FileSize))
        {
            offset = header.Offset;
            return true;
        }

        var elfRelative = checked((ulong)loadContext.ElfOffset + header.Offset);
        if (TryIsInRange(imageLength, elfRelative, header.FileSize))
        {
            offset = elfRelative;
            return true;
        }

        if (loadContext.SelfFileSize <= (ulong)imageLength)
        {
            var tailSize = (ulong)imageLength - loadContext.SelfFileSize;
            if (tailSize == header.FileSize && TryIsInRange(imageLength, loadContext.SelfFileSize, header.FileSize))
            {
                offset = loadContext.SelfFileSize;
                return true;
            }
        }

        offset = 0;
        return false;
    }

    private static bool TryIsInRange(int imageLength, ulong offset, ulong size)
    {
        if (offset > (ulong)imageLength)
        {
            return false;
        }

        var end = offset + size;
        return end >= offset && end <= (ulong)imageLength;
    }

    private static bool TryResolveSelfSegmentOffset(
        int imageLength,
        IReadOnlyList<SelfSegment> selfSegments,
        ProgramHeader header,
        int headerIndex,
        out ulong offset,
        out SelfSegmentResolveStatus status)
    {
        foreach (var segment in selfSegments)
        {
            if (!segment.IsBlocked)
            {
                continue;
            }

            var phdrId = segment.ProgramHeaderId;
            if (phdrId != (ulong)headerIndex)
            {
                continue;
            }

            if (segment.IsEncrypted || segment.IsCompressed)
            {
                if (TryIsInRange(imageLength, segment.Offset, header.FileSize))
                {
                    offset = segment.Offset;
                    status = SelfSegmentResolveStatus.ResolvedDumped;
                    return true;
                }

                offset = 0;
                status = segment.IsEncrypted
                    ? SelfSegmentResolveStatus.Encrypted
                    : SelfSegmentResolveStatus.Compressed;
                return false;
            }

            if (!TryIsInRange(imageLength, segment.Offset, header.FileSize))
            {
                continue;
            }

            offset = segment.Offset;
            status = SelfSegmentResolveStatus.Resolved;
            return true;
        }

        offset = 0;
        status = SelfSegmentResolveStatus.NotFound;
        return false;
    }

    private static void ValidateElfHeader(ElfHeader header)
    {
        if (!header.HasElfMagic)
        {
            throw new InvalidDataException("Input does not contain a valid ELF header.");
        }

        if (!header.Is64Bit)
        {
            throw new InvalidDataException("Only ELF64 images are currently supported.");
        }

        if (!header.IsLittleEndian)
        {
            throw new InvalidDataException("Only little-endian ELF images are currently supported.");
        }

        if (header.ProgramHeaderEntrySize != ProgramHeaderSize)
        {
            throw new InvalidDataException($"Unsupported ELF program header entry size: {header.ProgramHeaderEntrySize}.");
        }

        // The CPU backend executes guest instructions natively, so a non
        // x86-64 image can only fail deep in execution with an opaque SIGILL.
        // Catch it here with an actionable message. (EM_X86_64 == 62.)
        const ushort ElfMachineX86_64 = 62;
        if (header.Machine != ElfMachineX86_64)
        {
            throw new InvalidDataException(
                $"Unsupported ELF machine type 0x{header.Machine:X4}; SharpEmu only runs x86-64 (EM_X86_64) images.");
        }
    }

    private static unsafe T ReadUnmanaged<T>(ReadOnlySpan<byte> source, int offset)
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        EnsureRange(source.Length, checked((ulong)offset), (ulong)size);

        fixed (byte* ptr = &MemoryMarshal.GetReference(source))
        {
            return Unsafe.ReadUnaligned<T>(ptr + offset);
        }
    }

    private static void EnsureRange(int length, ulong offset, ulong size)
    {
        if (offset > (ulong)length)
        {
            throw new InvalidDataException("Segment offset exceeds image length.");
        }

        var end = offset + size;
        if (end < offset || end > (ulong)length)
        {
            throw new InvalidDataException("Segment extends beyond image bounds.");
        }
    }

    private readonly record struct LoadContext(bool IsSelf, int ElfOffset, ulong SelfFileSize, IReadOnlyList<SelfSegment> SelfSegments);

    private readonly record struct DynamicInfo(
        ulong StrTabOffset,
        ulong StrTabSize,
        ulong SymTabOffset,
        ulong SymTabSize,
        ulong InitOffset,
        ulong RelaOffset,
        ulong RelaSize,
        ulong JmpRelOffset,
        ulong JmpRelSize,
        ulong PltGotOffset,
        ulong InitArrayOffset,
        ulong InitArraySize,
        ulong PreInitArrayOffset,
        ulong PreInitArraySize)
    {
        public bool HasImportMetadata =>
            StrTabOffset != 0 &&
            StrTabSize != 0 &&
            SymTabOffset != 0 &&
            (RelaSize != 0 || JmpRelSize != 0);
    }

    private readonly record struct ElfSectionHeader(
        uint NameOffset,
        uint Type,
        ulong Flags,
        ulong Address,
        ulong Offset,
        ulong Size,
        uint Link,
        uint Info,
        ulong AddressAlign,
        ulong EntrySize);

    private readonly record struct ElfSymbol(
        uint NameOffset,
        byte Info,
        byte Other,
        ushort SectionIndex,
        ulong Value,
        ulong Size);

    private readonly record struct ElfRelocation(ulong Offset, ulong Info, long Addend)
    {
        public uint SymbolIndex => (uint)(Info >> 32);

        public uint Type => (uint)(Info & uint.MaxValue);
    }

    private readonly record struct ModuleTlsInfo(ulong MemorySize, ulong StaticOffset);

    private enum RelocationValueKind : byte
    {
        Pointer = 0,
        TlsModuleId = 1,
        // A pre-computed TLS offset written verbatim (TPOFF64/DTPOFF64). Unlike
        // Pointer it is a signed displacement, not a mapped address, so it is
        // patched as-is without the low-address validity warning.
        TlsOffset = 2,
        PcRelative = 3,
        SymbolSize = 4,
    }

    private enum RelocationWriteKind : byte
    {
        UInt64 = 0,
        UInt32 = 1,
        Int32 = 2,
    }

    private readonly record struct RelocationDescriptor(
        ulong TargetAddress,
        long Addend,
        string? ImportNid,
        ulong SymbolValue,
        RelocationValueKind ValueKind,
        bool IsDataImport,
        RelocationWriteKind WriteKind = RelocationWriteKind.UInt64,
        bool IsWeak = false);

    private enum SelfSegmentResolveStatus
    {
        NotFound = 0,
        Resolved = 1,
        ResolvedDumped = 2,
        Encrypted = 3,
        Compressed = 4,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SelfHeader
    {
        private readonly byte _ident0;
        private readonly byte _ident1;
        private readonly byte _ident2;
        private readonly byte _ident3;
        private readonly byte _ident4;
        private readonly byte _ident5;
        private readonly byte _ident6;
        private readonly byte _ident7;
        private readonly byte _ident8;
        private readonly byte _ident9;
        private readonly byte _ident10;
        private readonly byte _ident11;
        private readonly ushort _size1;
        private readonly ushort _size2;
        private readonly ulong _fileSize;
        private readonly ushort _segmentCount;
        private readonly ushort _unknown;
        private readonly uint _padding;

        public ushort SegmentCount => _segmentCount;

        public ushort Unknown => _unknown;

        public ulong FileSize => _fileSize;

        public bool HasKnownLayout =>
            _ident0 == 0x4F &&
            _ident1 == 0x15 &&
            _ident2 == 0x3D &&
            _ident3 == 0x1D &&
            _ident4 == 0x00 &&
            _ident5 == 0x01 &&
            _ident6 == 0x01 &&
            _ident7 == 0x12 &&
            _ident8 == 0x01 &&
            _ident9 == 0x01 &&
            _ident10 == 0x00 &&
            _ident11 == 0x00;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SelfSegment
    {
        private readonly ulong _type;
        private readonly ulong _offset;
        private readonly ulong _compressedSize;
        private readonly ulong _decompressedSize;

        public ulong Type => _type;

        public bool IsBlocked => (Type & SelfSegmentFlag) != 0;

        public ulong ProgramHeaderId => (Type >> 20) & 0xFFF;

        public bool IsEncrypted => (Type & 0x2) != 0;

        public bool IsCompressed => (Type & 0x8) != 0;

        public ulong Offset => _offset;

        public ulong CompressedSize => _compressedSize;

        public ulong DecompressedSize => _decompressedSize;
    }
}
