// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.AppContent;
using SharpEmu.Libs.SaveData;
using SharpEmu.Libs.Fiber;
using SharpEmu.Libs.SystemService;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpEmu.Core.Runtime;

public sealed class SharpEmuRuntime : ISharpEmuRuntime
{
    private readonly record struct LoadedModuleImage(string Path, SelfImage Image, int Handle, bool StartAtBoot);

    private static readonly HashSet<string> PreloadSkipModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "libkernel.prx",
        "libkernel_sys.prx",
    };

    private readonly ISelfLoader _selfLoader;
    private readonly IVirtualMemory _virtualMemory;
    private readonly ICpuDispatcher _cpuDispatcher;
    private readonly IModuleManager _moduleManager;
    private readonly ISymbolCatalog _symbolCatalog;
    private readonly CpuExecutionOptions _cpuExecutionOptions;
    private readonly IFileSystem _fileSystem;
    private bool _disposed;

    public string? LastExecutionDiagnostics { get; private set; }

    public string? LastExecutionTrace { get; private set; }

    public string? LastSessionSummary { get; private set; }

    public string? LastBasicBlockTrace { get; private set; }

    public string? LastMilestoneLog { get; private set; }

    public SharpEmuRuntime(
        ISelfLoader selfLoader,
        IVirtualMemory virtualMemory,
        ICpuDispatcher cpuDispatcher,
        IModuleManager moduleManager,
        ISymbolCatalog? symbolCatalog = null,
        CpuExecutionOptions cpuExecutionOptions = default,
        IFileSystem? fileSystem = null)
    {
        _selfLoader = selfLoader ?? throw new ArgumentNullException(nameof(selfLoader));
        _virtualMemory = virtualMemory ?? throw new ArgumentNullException(nameof(virtualMemory));
        _cpuDispatcher = cpuDispatcher ?? throw new ArgumentNullException(nameof(cpuDispatcher));
        _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
        _symbolCatalog = symbolCatalog ?? Aerolib.Empty;
        _cpuExecutionOptions = new CpuExecutionOptions
        {
            CpuEngine = cpuExecutionOptions.CpuEngine,
            StrictDynlibResolution = cpuExecutionOptions.StrictDynlibResolution,
            ImportTraceLimit = Math.Max(0, cpuExecutionOptions.ImportTraceLimit),
        };
        _fileSystem = fileSystem ?? new PhysicalFileSystem();
    }

    public static ISharpEmuRuntime CreateDefault(SharpEmuRuntimeOptions options = default)
    {
        var cpuExecutionOptions = new CpuExecutionOptions
        {
            CpuEngine = options.CpuEngine,
            StrictDynlibResolution = options.StrictDynlibResolution,
            ImportTraceLimit = Math.Max(0, options.ImportTraceLimit),
        };
        var moduleManager = new ModuleManager();
        // The compile-time generated registry (SharpEmu.SourceGenerators) is the sole
        // registration source; content tests in SharpEmu.Libs.Tests pin its invariants.
        moduleManager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5));
        moduleManager.Freeze();

        var virtualMemory = new PhysicalVirtualMemory();

        var fileSystem = new PhysicalFileSystem();

        return new SharpEmuRuntime(
            new SelfLoader(),
            virtualMemory,
            new CpuDispatcher(virtualMemory, moduleManager),
            moduleManager,
            Aerolib.Instance,
            cpuExecutionOptions,
            fileSystem);
    }

    public SelfImage LoadImage(string ebootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ebootPath);

        var fullPath = Path.GetFullPath(ebootPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Executable file was not found.", fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > int.MaxValue)
        {
            throw new NotSupportedException("Images larger than 2 GB are not currently supported.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)fileInfo.Length);
        using (var stream = File.OpenRead(fullPath))
        {
            stream.ReadExactly(bytes);
        }

        var mountRoot = Path.GetDirectoryName(fullPath);

        return _selfLoader.Load(bytes.AsSpan(), _virtualMemory, _moduleManager, _fileSystem, mountRoot);
    }

    public OrbisGen2Result Run(string ebootPath)
    {
        var normalizedEbootPath = Path.GetFullPath(ebootPath);
        using var app0Binding = BindApp0Root(normalizedEbootPath);
        Console.Error.WriteLine($"[RUNTIME] Loading: {ebootPath}");
        LastExecutionDiagnostics = null;
        LastExecutionTrace = null;
        LastSessionSummary = null;
        LastBasicBlockTrace = null;
        LastMilestoneLog = null;
        FiberExports.ResetRuntimeState();
        KernelModuleRegistry.Reset();
        var image = LoadImage(normalizedEbootPath);
        VideoOutExports.ConfigureApplicationInfo(image.Title, image.TitleId, image.Version);
        SaveDataExports.ConfigureApplicationInfo(image.TitleId);
        SystemServiceExports.ConfigureApplicationInfo(image.TitleId);
        _ = RegisterLoadedModule(normalizedEbootPath, image, isMain: true, isSystemModule: false);
        KernelRuntimeCompatExports.ConfigureProcessProcParamAddress(image.ProcParamAddress);
        Console.Error.WriteLine($"[RUNTIME] Entry: 0x{image.EntryPoint:X16}");
        var generation = image.ElfHeader.AbiVersion == 2 ? Generation.Gen5 : Generation.Gen4;
        var activeImportStubs = new Dictionary<ulong, string>(image.ImportStubs);
        var activeRuntimeSymbols = new Dictionary<string, ulong>(image.RuntimeSymbols, StringComparer.Ordinal);
        var processImageName = Path.GetFileName(ebootPath);
        if (string.IsNullOrWhiteSpace(processImageName))
        {
            processImageName = "eboot.bin";
        }

        HleDataSymbols.ConfigureProcessImageName(processImageName);
        MergeKnownHleDataSymbols(activeRuntimeSymbols);
        var loadedModuleImages = LoadAdjacentSceModules(ebootPath, activeImportStubs, activeRuntimeSymbols);
        RebindImportedDataSymbols(image, loadedModuleImages, activeRuntimeSymbols);
        var initializerResult = RunAllInitializers(
            image,
            loadedModuleImages,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
        if (initializerResult is { } failedInitializerResult)
        {
            Console.Error.WriteLine($"[RUNTIME] Initializer dispatch failed: {failedInitializerResult}");
            LastExecutionTrace = _cpuDispatcher.LastImportResolutionTrace;
            LastMilestoneLog = _cpuDispatcher.LastMilestoneLog;
            LastSessionSummary = BuildSessionSummary(_cpuDispatcher.LastSessionSummary);
            LastBasicBlockTrace = _cpuDispatcher.LastBasicBlockTrace;
            return failedInitializerResult;
        }

        Console.Error.WriteLine($"[RUNTIME] Dispatching, gen: {generation}");
        Console.Error.WriteLine($"[RUNTIME] About to call DispatchEntry with entryPoint=0x{image.EntryPoint:X16}");

        var result = _cpuDispatcher.DispatchEntry(
            image.EntryPoint,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName,
            _cpuExecutionOptions);

        Console.Error.WriteLine($"[RUNTIME] DispatchEntry returned: {result}");
        Console.Error.WriteLine($"[RUNTIME] Dispatch result: {result}");
        LastExecutionTrace = _cpuDispatcher.LastImportResolutionTrace;
        LastMilestoneLog = _cpuDispatcher.LastMilestoneLog;
        LastSessionSummary = BuildSessionSummary(_cpuDispatcher.LastSessionSummary);
        LastBasicBlockTrace = _cpuDispatcher.LastBasicBlockTrace;
        if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP && _cpuDispatcher.LastTrapInfo is { } trapInfo)
        {
            var opcodeBytes = ReadOpcodePreview(trapInfo.InstructionPointer, 8);
            var decodedTrapText = string.Empty;
            var ud2Hint = string.Empty;
            if (_cpuExecutionOptions.EnableDisasmDiagnostics && TryDecodeInstructionAt(trapInfo.InstructionPointer, out var trapInstruction))
            {
                decodedTrapText = BuildDecodedInstructionFields(in trapInstruction);
                if (string.Equals(trapInstruction.Mnemonic, "Ud2", StringComparison.OrdinalIgnoreCase))
                {
                    ud2Hint = ", trap=ud2";
                }
            }
            else if (opcodeBytes.StartsWith("0F 0B", StringComparison.Ordinal))
            {
                ud2Hint = ", trap=ud2";
            }

            var longModeHint = IsInvalidLongModeOpcode(trapInfo.Opcode)
                ? ", hint=invalid opcode for x64 long mode; likely wrong jump target or decode desync"
                : string.Empty;

            var hint = string.Empty;
            if (image.IsSelf &&
                activeImportStubs.Count == 0 &&
                trapInfo.InstructionPointer == 0 &&
                trapInfo.Opcode == 0xCC)
            {
                hint = ", hint=SELF appears encrypted or unresolved; use a decrypted ELF/FSELF image";
            }

            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText}";
            }

            var ripStubText = activeImportStubs.TryGetValue(trapInfo.InstructionPointer, out var trapStubNid)
                ? $", rip_stub={trapStubNid}"
                : string.Empty;
            var diagnosticsBuilder = new StringBuilder(1024);
            diagnosticsBuilder.Append(
                $"CPU trap at RIP=0x{trapInfo.InstructionPointer:X16}, opcode=0x{trapInfo.Opcode:X2}, bytes={opcodeBytes}{decodedTrapText}, import_stubs={activeImportStubs.Count}{ud2Hint}{longModeHint}{hint}{ripStubText}{transferText}");
            if (!string.IsNullOrWhiteSpace(_cpuDispatcher.LastRecentControlTransferTrace))
            {
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append("Recent transfers:");
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append(_cpuDispatcher.LastRecentControlTransferTrace);
            }

            if (!string.IsNullOrWhiteSpace(_cpuDispatcher.LastRecentInstructionWindow))
            {
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append("Recent instructions:");
                diagnosticsBuilder.AppendLine();
                diagnosticsBuilder.Append(_cpuDispatcher.LastRecentInstructionWindow);
            }

            LastExecutionDiagnostics = diagnosticsBuilder.ToString();
        }
        else if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT && _cpuDispatcher.LastMemoryFaultInfo is { } faultInfo)
        {
            var opcodeText = faultInfo.Opcode.HasValue ? $"0x{faultInfo.Opcode.Value:X2}" : "??";
            var decodedFaultText = string.Empty;
            if (_cpuExecutionOptions.EnableDisasmDiagnostics && TryDecodeInstructionAt(faultInfo.InstructionPointer, out var faultInstruction))
            {
                decodedFaultText = BuildDecodedInstructionFields(in faultInstruction);
                if (!faultInfo.Opcode.HasValue && faultInstruction.Bytes.Length > 0)
                {
                    opcodeText = $"0x{faultInstruction.Bytes[0]:X2}";
                }
            }

            var accessType = faultInfo.Access.IsWrite ? "write" : "read";
            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                var rbxTarget = TryReadUInt64At(transferInfo.Rbx, out var rbxDeref)
                    ? $"*rbx=0x{rbxDeref:X16}"
                    : "*rbx=??";
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText} rax=0x{transferInfo.Rax:X16} rbx=0x{transferInfo.Rbx:X16} {rbxTarget} rsp=0x{transferInfo.Rsp:X16} rbp=0x{transferInfo.Rbp:X16}";
            }

            var ripStubText = activeImportStubs.TryGetValue(faultInfo.InstructionPointer, out var faultStubNid)
                ? $", rip_stub={faultStubNid}"
                : string.Empty;

            LastExecutionDiagnostics =
                $"Memory fault at RIP=0x{faultInfo.InstructionPointer:X16}, opcode={opcodeText}{decodedFaultText}, {accessType}@0x{faultInfo.Access.Address:X16} size={faultInfo.Access.Size}, import_stubs={activeImportStubs.Count}{ripStubText}{transferText}";
        }
        else if (result == OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED && _cpuDispatcher.LastNotImplementedInfo is { } notImplementedInfo)
        {
            var inferredNid = notImplementedInfo.Nid;
            var decodedNotImplementedText = TryDecodeInstructionAt(notImplementedInfo.InstructionPointer, out var notImplementedInstruction)
                ? BuildDecodedInstructionFields(in notImplementedInstruction)
                : string.Empty;
            var ripStubText = string.Empty;
            if (activeImportStubs.TryGetValue(notImplementedInfo.InstructionPointer, out var ripStubNid))
            {
                ripStubText = $", rip_stub={ripStubNid}";
                if (string.IsNullOrWhiteSpace(inferredNid))
                {
                    inferredNid = ripStubNid;
                }
            }

            var inferredExportName = notImplementedInfo.ExportName;
            var inferredLibraryName = notImplementedInfo.LibraryName;
            if (!string.IsNullOrWhiteSpace(inferredNid) &&
                _moduleManager.TryGetExport(inferredNid, out var export))
            {
                inferredExportName = string.IsNullOrWhiteSpace(inferredExportName) ? export.Name : inferredExportName;
                inferredLibraryName = string.IsNullOrWhiteSpace(inferredLibraryName) ? export.LibraryName : inferredLibraryName;
            }

            var nidText = string.IsNullOrWhiteSpace(inferredNid) ? "?" : inferredNid;
            var exportText = string.IsNullOrWhiteSpace(inferredExportName) ? "?" : inferredExportName;
            var libraryText = string.IsNullOrWhiteSpace(inferredLibraryName) ? "?" : inferredLibraryName;
            var detailText = string.IsNullOrWhiteSpace(notImplementedInfo.Detail) ? string.Empty : $", detail={notImplementedInfo.Detail}";
            var transferText = string.Empty;
            if (_cpuDispatcher.LastControlTransferInfo is { } transferInfo)
            {
                var transferMode = transferInfo.IsIndirect ? "indirect" : "direct";
                var transferBytes = ReadOpcodePreview(transferInfo.SourceInstructionPointer, 8);
                var transferDecodedText = TryDecodeInstructionAt(transferInfo.SourceInstructionPointer, out var transferInstruction)
                    ? BuildDecodedInstructionFields(in transferInstruction, fieldPrefix: "src_inst")
                    : string.Empty;
                var transferStubText = activeImportStubs.TryGetValue(transferInfo.TargetInstructionPointer, out var transferStubNid)
                    ? $" stub={transferStubNid}"
                    : string.Empty;
                transferText =
                    $", last_transfer={transferInfo.Kind}:{transferMode} src=0x{transferInfo.SourceInstructionPointer:X16} op=0x{transferInfo.Opcode:X2} bytes={transferBytes} dst=0x{transferInfo.TargetInstructionPointer:X16}{transferDecodedText}{transferStubText}";
            }

            var aerolibText = string.Empty;
            if (!string.IsNullOrWhiteSpace(inferredExportName) &&
                _symbolCatalog.TryGetByExportName(inferredExportName, out var symbol))
            {
                aerolibText = $", aerolib_nid={symbol.Nid}";
            }
            else if (!string.IsNullOrWhiteSpace(inferredNid) &&
                     _symbolCatalog.TryGetByNid(inferredNid, out var symbolByNid))
            {
                aerolibText = $", aerolib_export={symbolByNid.ExportName}";
            }

            LastExecutionDiagnostics =
                $"Not implemented: source={notImplementedInfo.Source}, rip=0x{notImplementedInfo.InstructionPointer:X16}{decodedNotImplementedText}, nid={nidText}, export={exportText}, library={libraryText}, import_stubs={activeImportStubs.Count}{ripStubText}{aerolibText}{detailText}{transferText}";
        }

        return result;
    }

    private static App0BindingScope? BindApp0Root(string normalizedEbootPath)
    {
        const string app0VariableName = "SHARPEMU_APP0_DIR";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(app0VariableName)))
        {
            return null;
        }

        var app0Root = Path.GetDirectoryName(normalizedEbootPath);
        if (string.IsNullOrWhiteSpace(app0Root))
        {
            return null;
        }

        // Dump tools commonly place decrypted executables in an app0/decrypted
        // sidecar while leaving Unity data in the parent app0 directory. Keep
        // loading code/modules beside the decrypted eboot, but mount /app0 at
        // the content-bearing parent so boot.config, metadata and assets resolve.
        if (string.Equals(Path.GetFileName(app0Root), "decrypted", StringComparison.OrdinalIgnoreCase))
        {
            var parentRoot = Path.GetDirectoryName(app0Root);
            if (!string.IsNullOrWhiteSpace(parentRoot) &&
                Directory.Exists(Path.Combine(parentRoot, "Media")))
            {
                app0Root = parentRoot;
            }
        }

        Environment.SetEnvironmentVariable(app0VariableName, app0Root);
        return new App0BindingScope(app0VariableName);
    }

    private sealed class App0BindingScope(string variableName) : IDisposable
    {
        private readonly string _variableName = variableName;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Environment.SetEnvironmentVariable(_variableName, null);
            _disposed = true;
        }
    }

    private OrbisGen2Result? RunAllInitializers(
        SelfImage mainImage,
        IReadOnlyList<LoadedModuleImage> loadedModuleImages,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        var moduleStartResult = RunPreloadedModuleInitializers(
            loadedModuleImages,
            generation,
            activeImportStubs,
            activeRuntimeSymbols);
        if (moduleStartResult is not null)
        {
            return moduleStartResult;
        }

        // On current PS5 dumps DT_INIT commonly resolves to imageBase+0x10, which is inside
        // the mapped ELF header rather than a callable guest routine. Startup must remain
        // guest-driven until the PS5 init/module ABI is identified precisely.
        return null;
    }

    private bool TryGetEhFrameInfo(
        SelfImage image,
        ulong imageSize,
        out ulong ehFrameHeaderAddress,
        out ulong ehFrameAddress,
        out ulong ehFrameSize)
    {
        ehFrameHeaderAddress = 0;
        ehFrameAddress = 0;
        ehFrameSize = 0;
        var imageBase = image.EntryPoint >= image.ElfHeader.EntryPoint
            ? image.EntryPoint - image.ElfHeader.EntryPoint
            : 0UL;
        for (var i = 0; i < image.ProgramHeaders.Count; i++)
        {
            var header = image.ProgramHeaders[i];
            if (header.HeaderType != ProgramHeaderType.GnuEhFrame || header.MemorySize < 8)
            {
                continue;
            }

            var headerAddress = imageBase + header.VirtualAddress;
            Span<byte> ehHeader = stackalloc byte[8];
            if (!_virtualMemory.TryRead(headerAddress, ehHeader) ||
                ehHeader[0] != 1 ||
                ehHeader[1] != 0x1B)
            {
                continue;
            }

            var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(ehHeader[4..]);
            ehFrameHeaderAddress = headerAddress;
            ehFrameAddress = unchecked((ulong)((long)headerAddress + 4 + relativeOffset));
            if (ehFrameAddress < 0x10000)
            {
                continue;
            }

            var relativeEhFrameAddress = ehFrameAddress - imageBase;
            ehFrameSize = header.VirtualAddress > relativeEhFrameAddress
                ? header.VirtualAddress - relativeEhFrameAddress
                : imageSize > header.VirtualAddress
                    ? imageSize - header.VirtualAddress
                    : 0;
            return true;
        }

        return false;
    }

    private OrbisGen2Result? RunPreloadedModuleInitializers(
        IReadOnlyList<LoadedModuleImage> loadedModuleImages,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols)
    {
        for (var i = 0; i < loadedModuleImages.Count; i++)
        {
            var loadedModule = loadedModuleImages[i];
            if (!loadedModule.StartAtBoot)
            {
                continue;
            }

            var initEntryPoint = loadedModule.Image.InitFunctionEntryPoint;
            if (initEntryPoint < 0x10000)
            {
                continue;
            }

            if (!KernelModuleRegistry.TryBeginModuleStart(loadedModule.Handle, out _))
            {
                continue;
            }

            var moduleName = Path.GetFileName(loadedModule.Path);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                moduleName = $"module#{i}";
            }

            Console.Error.WriteLine(
                $"[RUNTIME] Starting module {moduleName}: dt_init=0x{initEntryPoint:X16}");

            var result = _cpuDispatcher.DispatchModuleInitializer(
                initEntryPoint,
                generation,
                activeImportStubs,
                activeRuntimeSymbols,
                moduleName,
                _cpuExecutionOptions);
            KernelModuleRegistry.CompleteModuleStart(
                loadedModule.Handle,
                result == OrbisGen2Result.ORBIS_GEN2_OK);
            if (result != OrbisGen2Result.ORBIS_GEN2_OK)
            {
                Console.Error.WriteLine(
                    $"[RUNTIME] Module start failed: {moduleName} -> {result}");
                return result;
            }
        }

        return null;
    }

    private OrbisGen2Result? RunImageInitializers(
        string label,
        SelfImage image,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        if (image.PreInitializerFunctions.Count == 0 && image.InitializerFunctions.Count == 0)
        {
            return null;
        }

        Console.Error.WriteLine(
            $"[RUNTIME] Running initializers for {label}: preinit={image.PreInitializerFunctions.Count}, init={image.InitializerFunctions.Count}");

        var result = RunInitializerList(
            $"{label}:preinit",
            image.PreInitializerFunctions,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
        if (result is not null)
        {
            return result;
        }

        return RunInitializerList(
            $"{label}:init",
            image.InitializerFunctions,
            generation,
            activeImportStubs,
            activeRuntimeSymbols,
            processImageName);
    }

    private OrbisGen2Result? RunInitializerList(
        string label,
        IReadOnlyList<ulong> initializerFunctions,
        Generation generation,
        IReadOnlyDictionary<ulong, string> activeImportStubs,
        IReadOnlyDictionary<string, ulong> activeRuntimeSymbols,
        string processImageName)
    {
        for (var i = 0; i < initializerFunctions.Count; i++)
        {
            var initializerAddress = initializerFunctions[i];
            if (initializerAddress < 0x10000)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[RUNTIME]   Initializer {label}[{i}] -> 0x{initializerAddress:X16}");

            var result = _cpuDispatcher.DispatchEntry(
                initializerAddress,
                generation,
                activeImportStubs,
                activeRuntimeSymbols,
                processImageName,
                _cpuExecutionOptions);
            if (result != OrbisGen2Result.ORBIS_GEN2_OK)
            {
                return result;
            }
        }

        return null;
    }

    private List<LoadedModuleImage> LoadAdjacentSceModules(
        string ebootPath,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols)
    {
        var loadedImages = new List<LoadedModuleImage>();
        var ebootDirectory = Path.GetDirectoryName(ebootPath);
        if (string.IsNullOrWhiteSpace(ebootDirectory))
        {
            return loadedImages;
        }

        var moduleDirectories = new[]
        {
            (Path: Path.Combine(ebootDirectory, "sce_module"), StartAtBoot: true),
            (Path: Path.Combine(ebootDirectory, "sce_modules"), StartAtBoot: true),
            (Path: Path.Combine(ebootDirectory, "Media", "Modules"), StartAtBoot: true),
            // Unity native plugins are loaded later through sceKernelLoadStartModule. Map
            // them up front so the HLE loader can return a real module handle and dlsym
            // can resolve their exports, but defer DT_INIT until the guest requests them.
            (Path: Path.Combine(ebootDirectory, "Media", "Plugins"), StartAtBoot: false),
        }
        .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Where(entry => Directory.Exists(entry.Path))
        .ToArray();

        if (moduleDirectories.Length == 0)
        {
            return loadedImages;
        }

        var allModulePaths = moduleDirectories
            .SelectMany(directory => Directory
                .EnumerateFiles(directory.Path)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => (Path: path, directory.StartAtBoot)))
            .Where(entry =>
            {
                var extension = Path.GetExtension(entry.Path);
                return string.Equals(extension, ".prx", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(extension, ".sprx", StringComparison.OrdinalIgnoreCase);
            })
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var modulePaths = allModulePaths
            .Where(entry => ShouldPreloadModule(entry.Path))
            .ToArray();
        var skippedModules = allModulePaths
            .Where(entry => !ShouldPreloadModule(entry.Path))
            .Select(entry => Path.GetFileName(entry.Path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (skippedModules.Length > 0)
        {
            Console.Error.WriteLine($"[RUNTIME] Skipping {skippedModules.Length} core module(s): {string.Join(", ", skippedModules)}");
        }

        if (modulePaths.Length == 0)
        {
            return loadedImages;
        }

        Console.Error.WriteLine($"[RUNTIME] Module search directories: {string.Join(", ", moduleDirectories.Select(entry => entry.Path))}");
        Console.Error.WriteLine($"[RUNTIME] Loading {modulePaths.Length} module(s)...");
        var loadedModules = 0;
        var failedModules = 0;
        var mergedImportCount = 0;
        var mergedSymbolCount = 0;
        foreach (var moduleEntry in modulePaths)
        {
            var modulePath = moduleEntry.Path;
            try
            {
                var fileInfo = new FileInfo(modulePath);
                if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > int.MaxValue)
                {
                    failedModules++;
                    continue;
                }

                var moduleBytes = GC.AllocateUninitializedArray<byte>((int)fileInfo.Length);
                using (var stream = File.OpenRead(modulePath))
                {
                    stream.ReadExactly(moduleBytes);
                }

                var moduleImage = _selfLoader.LoadAdditional(
                    moduleBytes.AsSpan(),
                    _virtualMemory,
                    _moduleManager,
                    _fileSystem,
                    Path.GetDirectoryName(modulePath));

                mergedImportCount += MergeImportStubs(importStubs, moduleImage.ImportStubs, modulePath);
                mergedSymbolCount += MergeRuntimeSymbols(runtimeSymbols, moduleImage.RuntimeSymbols);
                InstallNativePluginCompatibilityHooks(importStubs, moduleImage, modulePath);
                var moduleHandle = RegisterLoadedModule(modulePath, moduleImage, isMain: false, isSystemModule: false);
                var moduleName = Path.GetFileName(modulePath);
                var startAtBoot = moduleEntry.StartAtBoot ||
                    string.Equals(moduleName, "libfmod.prx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(moduleName, "libfmodstudio.prx", StringComparison.OrdinalIgnoreCase);
                loadedImages.Add(new LoadedModuleImage(modulePath, moduleImage, moduleHandle, startAtBoot));
                loadedModules++;

                Console.Error.WriteLine(
                    $"[RUNTIME] Loaded module {Path.GetFileName(modulePath)}: entry=0x{moduleImage.EntryPoint:X16}, imports={moduleImage.ImportStubs.Count}, symbols={moduleImage.RuntimeSymbols.Count}");
            }
            catch (Exception ex)
            {
                failedModules++;
                Console.Error.WriteLine($"[RUNTIME] Module load failed: {modulePath} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        Console.Error.WriteLine(
            $"[RUNTIME] Module preload summary: loaded={loadedModules}, failed={failedModules}, merged_imports={mergedImportCount}, merged_symbols={mergedSymbolCount}");
        return loadedImages;
    }

    private static void InstallNativePluginCompatibilityHooks(
        IDictionary<ulong, string> importStubs,
        SelfImage moduleImage,
        string modulePath)
    {
        if (!string.Equals(Path.GetFileName(modulePath), "libfmod.prx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReadOnlySpan<string> compatibilityNids = ["uPLTdl3psGk"];
        foreach (var nid in compatibilityNids)
        {
            if (moduleImage.RuntimeSymbols.TryGetValue(nid, out var address) && address >= 0x10000)
            {
                importStubs[address] = nid;
                Console.Error.WriteLine(
                    $"[RUNTIME] Installed FMOD compatibility hook: {nid} -> 0x{address:X16}");
            }
        }
    }

    private void RebindImportedDataSymbols(
        SelfImage mainImage,
        IReadOnlyList<LoadedModuleImage> loadedModuleImages,
        IReadOnlyDictionary<string, ulong> runtimeSymbols)
    {
        var rebound = 0;
        var unresolved = 0;

        rebound += RebindImportedDataSymbols(mainImage, runtimeSymbols, ref unresolved);
        for (var i = 0; i < loadedModuleImages.Count; i++)
        {
            rebound += RebindImportedDataSymbols(loadedModuleImages[i].Image, runtimeSymbols, ref unresolved);
        }

        if (rebound != 0 || unresolved != 0)
        {
            Console.Error.WriteLine(
                $"[RUNTIME] Imported data rebind: rebound={rebound}, unresolved={unresolved}");
        }
    }

    private int RebindImportedDataSymbols(
        SelfImage image,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        ref int unresolved)
    {
        if (image.ImportedRelocations.Count == 0)
        {
            return 0;
        }

        var rebound = 0;
        var logRebind = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_DATA_REBIND"),
            "1",
            StringComparison.Ordinal);
        for (var i = 0; i < image.ImportedRelocations.Count; i++)
        {
            var relocation = image.ImportedRelocations[i];
            if (!relocation.IsData)
            {
                continue;
            }

            if (!runtimeSymbols.TryGetValue(relocation.Nid, out var symbolAddress) ||
                !IsUsableRuntimeSymbolAddress(symbolAddress))
            {
                if (logRebind)
                {
                    Console.Error.WriteLine(
                        $"[RUNTIME] Imported data unresolved: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} addend=0x{unchecked((ulong)relocation.Addend):X16}");
                }

                unresolved++;
                continue;
            }

            var reboundValue = AddSigned(symbolAddress, relocation.Addend);
            if (!TryWriteUInt64(_virtualMemory, relocation.TargetAddress, reboundValue))
            {
                if (logRebind)
                {
                    Console.Error.WriteLine(
                        $"[RUNTIME] Imported data write-failed: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} value=0x{reboundValue:X16}");
                }

                unresolved++;
                continue;
            }

            if (logRebind)
            {
                Console.Error.WriteLine(
                    $"[RUNTIME] Imported data rebound: nid={relocation.Nid} target=0x{relocation.TargetAddress:X16} value=0x{reboundValue:X16}");
            }

            rebound++;
        }

        return rebound;
    }

    private static void MergeKnownHleDataSymbols(IDictionary<string, ulong> runtimeSymbols)
    {
        foreach (var nid in HleDataSymbols.EnumerateKnownNids())
        {
            if (runtimeSymbols.ContainsKey(nid) ||
                !HleDataSymbols.TryGetAddress(nid, out var symbolAddress) ||
                !IsUsableRuntimeSymbolAddress(symbolAddress))
            {
                continue;
            }

            runtimeSymbols[nid] = symbolAddress;
        }
    }

    private static int MergeImportStubs(
        IDictionary<ulong, string> destination,
        IReadOnlyDictionary<ulong, string> source,
        string modulePath)
    {
        var added = 0;
        foreach (var (address, nid) in source)
        {
            if (destination.TryGetValue(address, out var existingNid))
            {
                if (!string.Equals(existingNid, nid, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"[RUNTIME] Import stub conflict at 0x{address:X16}: keep={existingNid}, skip={nid} ({Path.GetFileName(modulePath)})");
                }

                continue;
            }

            destination[address] = nid;
            added++;
        }

        return added;
    }

    private static int MergeRuntimeSymbols(
        IDictionary<string, ulong> destination,
        IReadOnlyDictionary<string, ulong> source)
    {
        var added = 0;
        foreach (var (name, address) in source)
        {
            if (string.IsNullOrWhiteSpace(name) || !IsUsableRuntimeSymbolAddress(address))
            {
                continue;
            }

            if (destination.TryGetValue(name, out var existingAddress))
            {
                if (IsPreferredRuntimeSymbolAddress(existingAddress, address))
                {
                    destination[name] = address;
                    added++;
                }

                continue;
            }

            destination[name] = address;
            added++;
        }

        return added;
    }

    private static bool IsPreferredRuntimeSymbolAddress(ulong existingAddress, ulong candidateAddress)
    {
        return !IsUsableRuntimeSymbolAddress(existingAddress) && IsUsableRuntimeSymbolAddress(candidateAddress);
    }

    private static bool IsUsableRuntimeSymbolAddress(ulong address)
    {
        return address >= 0x10000 && !IsUnresolvedRuntimeSentinel(address);
    }

    private static bool TryWriteUInt64(IVirtualMemory virtualMemory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return virtualMemory.TryWrite(address, bytes);
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

    private static bool IsUnresolvedRuntimeSentinel(ulong value)
    {
        return value == 0xFFFEUL ||
               value == 0xFFFFFFFEUL ||
               value == 0xFFFFFFFFFFFFFFFEUL;
    }

    private static bool ShouldPreloadModule(string modulePath)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PRELOAD_ALL_SCE_MODULES"), "1", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileName(modulePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return !PreloadSkipModules.Contains(fileName);
    }

    private int RegisterLoadedModule(string modulePath, SelfImage image, bool isMain, bool isSystemModule)
    {
        if (!TryComputeImageRange(image, out var baseAddress, out var size))
        {
            baseAddress = 0;
            size = 0;
        }

        _ = TryGetEhFrameInfo(
            image,
            size,
            out var ehFrameHeaderAddress,
            out var ehFrameAddress,
            out var ehFrameSize);
        var handle = KernelModuleRegistry.RegisterModule(
            modulePath,
            baseAddress,
            size,
            image.EntryPoint,
            image.InitFunctionEntryPoint,
            ehFrameHeaderAddress,
            ehFrameAddress,
            ehFrameSize,
            isMain,
            isSystemModule);
        KernelModuleRegistry.RegisterModuleSymbols(handle, image.RuntimeSymbols);
        Console.Error.WriteLine(
            $"[RUNTIME] Registered module handle={handle} name={Path.GetFileName(modulePath)} base=0x{baseAddress:X16} size=0x{size:X16}");
        return handle;
    }

    private static bool TryComputeImageRange(SelfImage image, out ulong baseAddress, out ulong size)
    {
        baseAddress = 0;
        size = 0;
        if (image.ProgramHeaders.Count == 0)
        {
            return false;
        }

        var imageBase = image.EntryPoint >= image.ElfHeader.EntryPoint
            ? image.EntryPoint - image.ElfHeader.EntryPoint
            : 0UL;
        var found = false;
        ulong minAddress = ulong.MaxValue;
        ulong maxAddress = 0;
        for (var i = 0; i < image.ProgramHeaders.Count; i++)
        {
            var header = image.ProgramHeaders[i];
            if (header.HeaderType != ProgramHeaderType.Load || header.MemorySize == 0)
            {
                continue;
            }

            var segmentStart = unchecked(imageBase + header.VirtualAddress);
            var segmentEnd = unchecked(segmentStart + header.MemorySize);
            if (!found || segmentStart < minAddress)
            {
                minAddress = segmentStart;
            }

            if (!found || segmentEnd > maxAddress)
            {
                maxAddress = segmentEnd;
            }

            found = true;
        }

        if (!found || maxAddress <= minAddress)
        {
            return false;
        }

        baseAddress = minAddress;
        size = maxAddress - minAddress;
        return true;
    }

    private static string BuildSessionSummary(CpuSessionSummary summary)
    {
        var resultText = summary.Result == OrbisGen2Result.ORBIS_GEN2_OK ? "OK" : summary.Result.ToString();
        var exitText = summary.ExitCode.HasValue ? summary.ExitCode.Value.ToString() : "?";
        return
            $"Summary: result={resultText} reason={summary.Reason} exit={exitText} last_guest_rip=0x{summary.LastGuestRip:X16} last_stub_rip=0x{summary.LastStubRip:X16} instr={summary.TotalInstructions} imports={summary.ImportsHit} unique_nids={summary.UniqueNidsHit}";
    }

    private string ReadOpcodePreview(ulong instructionPointer, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return "??";
        }

        Span<byte> oneByte = stackalloc byte[1];
        var parts = new string[maxBytes];
        var count = 0;
        for (var i = 0; i < maxBytes; i++)
        {
            if (!_virtualMemory.TryRead(instructionPointer + (ulong)i, oneByte))
            {
                break;
            }

            parts[count] = oneByte[0].ToString("X2");
            count++;
        }

        return count == 0 ? "??" : string.Join(' ', parts, 0, count);
    }

    private bool TryReadUInt64At(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!_virtualMemory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private bool TryDecodeInstructionAt(ulong instructionPointer, out DecodedInst decodedInstruction)
    {
        if (!IcedDecoder.TryReadGuestBytes(_virtualMemory, instructionPointer, maxLen: 15, out var instructionBytes))
        {
            decodedInstruction = default;
            return false;
        }

        return IcedDecoder.TryDecode(instructionPointer, instructionBytes, out decodedInstruction);
    }

    private static bool IsInvalidLongModeOpcode(byte opcode)
    {
        return opcode is
            0x06 or // PUSH ES
            0x07 or // POP ES
            0x0E or // PUSH CS
            0x16 or // PUSH SS
            0x17 or // POP SS
            0x1E or // PUSH DS
            0x1F or // POP DS
            0x27 or // DAA
            0x2F or // DAS
            0x37 or // AAA
            0x3F or // AAS
            0x60 or // PUSHA/PUSHAD
            0x61 or // POPA/POPAD
            0xD4 or // AAM
            0xD5;   // AAD
    }

    private static string BuildDecodedInstructionFields(in DecodedInst instruction, string fieldPrefix = "inst")
    {
        var text = $", {fieldPrefix}={instruction.Text}, {fieldPrefix}_len={instruction.Length}, {fieldPrefix}_mnemonic={instruction.Mnemonic}, {fieldPrefix}_flow={instruction.FlowControl}";
        if (instruction.NearBranchTarget is { } target)
        {
            text += $", {fieldPrefix}_target=0x{target:X16}";
        }

        if (instruction.MemoryAddress is { } memoryAddress)
        {
            text += $", {fieldPrefix}_mem=0x{memoryAddress:X16}";
        }

        if (instruction.Bytes.Length > 0)
        {
            text += $", {fieldPrefix}_bytes={IcedDecoder.FormatBytes(instruction.Bytes)}";
        }

        return text;
    }

    public OrbisGen2Result DispatchHleCall(string nid, CpuContext context)
    {
        return _moduleManager.Dispatch(nid, context);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_cpuDispatcher is IDisposable disposableDispatcher)
        {
            disposableDispatcher.Dispose();
        }

        if (_virtualMemory is IDisposable disposableMemory)
        {
            disposableMemory.Dispose();
        }
    }

}
