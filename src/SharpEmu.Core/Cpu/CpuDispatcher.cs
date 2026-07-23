// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public sealed class CpuDispatcher : ICpuDispatcher, IDisposable
{
    private enum EntryFrameKind
    {
        ProcessEntry,
        ModuleInitializer,
    }

    private static class CpuLayout
    {
        public static ulong StackBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFF_F000_0000UL : 0x6FFF_F000_0000UL;
        public const ulong StackSize = 0x0020_0000UL;
        public static ulong TlsBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFE_0000_0000UL : 0x6FFE_0000_0000UL;
        public const ulong TlsSize = 0x0001_0000UL;
        public const ulong TlsPrefixSize = GuestTlsTemplate.StartupStaticTlsReservation;
        public static ulong BootstrapStubBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFD_F000_0000UL : 0x6FFD_F000_0000UL;
        public static ulong BootstrapPayloadBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFD_E000_0000UL : 0x6FFD_E000_0000UL;
        public static ulong DynlibFallbackStubBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFD_D000_0000UL : 0x6FFD_D000_0000UL;
        public static ulong ReturnToHostStubBaseAddress { get; } = OperatingSystem.IsWindows() ? 0x7FFD_C000_0000UL : 0x6FFD_C000_0000UL;
        public const ulong BootstrapRegionSize = 0x0000_1000UL;
        public const ulong ReturnToHostStubStride = 0x0100_0000UL;
        public const ulong BootstrapPayloadResultOffset = 0x28UL;
        public const ulong BootstrapStatusOffset = 0x100UL;
        public const ulong InitialRflags = 0x202;
        public const int MaxRetryAttempts = 32;
    }

    private static readonly byte[] BootstrapStubBytes = new byte[CpuLayout.BootstrapRegionSize] { 0xCC, 0xC3 };
    private static readonly byte[] DynlibFallbackStubBytes = new byte[CpuLayout.BootstrapRegionSize] { 0x31, 0xC0, 0xC3 };
    private static readonly byte[] ReturnToHostStubBytes = new byte[CpuLayout.BootstrapRegionSize] { 0xF4, 0xCC };

    private static readonly byte[] BootstrapStartSignature = new byte[]
    {
        0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56,
        0x41, 0x55, 0x41, 0x54, 0x53, 0x50, 0x48, 0x89,
    };

    private readonly IVirtualMemory _virtualMemory;
    private readonly IModuleManager _moduleManager;
    private readonly INativeCpuBackend _nativeCpuBackend;
    private readonly ILogger<CpuDispatcher> _logger;
    private bool _disposed;

    public CpuDispatcher(
        IVirtualMemory virtualMemory,
        IModuleManager moduleManager,
        INativeCpuBackend nativeCpuBackend,
        ILogger<CpuDispatcher> logger)
    {
        _virtualMemory = virtualMemory ?? throw new ArgumentNullException(nameof(virtualMemory));
        _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
        _nativeCpuBackend = nativeCpuBackend ?? throw new ArgumentNullException(nameof(nativeCpuBackend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CpuExecutionResult DispatchEntry(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string processImageName = "eboot.bin",
        CpuExecutionOptions executionOptions = default)
    {
        _logger.LogInformation("DispatchEntry START: entryPoint=0x{EntryPoint:X16}, generation={Generation}", entryPoint, generation);
        return DispatchEntryCore(entryPoint, generation, importStubs, runtimeSymbols, processImageName, executionOptions, EntryFrameKind.ProcessEntry);
    }

    public CpuExecutionResult DispatchModuleInitializer(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string moduleName = "module",
        CpuExecutionOptions executionOptions = default)
    {
        _logger.LogInformation("DispatchModuleInitializer START: entryPoint=0x{EntryPoint:X16}, generation={Generation}, module={ModuleName}",
            entryPoint, generation, moduleName);
        return DispatchEntryCore(entryPoint, generation, importStubs, runtimeSymbols, moduleName, executionOptions, EntryFrameKind.ModuleInitializer);
    }

    private CpuExecutionResult DispatchEntryCore(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols,
        string imageName,
        CpuExecutionOptions executionOptions,
        EntryFrameKind frameKind)
    {
        // 1. Map memory regions
        if (!TryMapStackRegion(out var stackBase))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to map stack");
        if (!TryMapTlsRegion(out var tlsBase))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to map TLS");
        if (!TryMapReturnToHostStubRegion(out var returnStub))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to map return-to-host stub");

        // 2. Create context and tracked memory
        var trackedMemory = new TrackedCpuMemory(_virtualMemory);
        var context = new CpuContext(trackedMemory, generation)
        {
            Rip = entryPoint,
            Rflags = CpuLayout.InitialRflags,
            FsBase = tlsBase,
            GsBase = tlsBase,
        };

        // 3. Initialise stack and TLS
        context[CpuRegister.Rsp] = stackBase + CpuLayout.StackSize - sizeof(ulong);
        if (!context.TryWriteUInt64(context[CpuRegister.Rsp], returnStub))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to push return stub");

        if (!InitializeGuestFrameChainSentinel(context))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to initialise frame chain");
        if (!InitializeTls(context, tlsBase))
            return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to initialise TLS");

        // 4. Prepare import stubs dictionary
        var effectiveImportStubs = importStubs is null
            ? new Dictionary<ulong, string>()
            : new Dictionary<ulong, string>(importStubs);

        // 5. Set up entry frame (arguments) depending on type
        bool entryParamsConfigured;
        if (frameKind == EntryFrameKind.ProcessEntry)
        {
            if (!TryMapDynlibFallbackStubRegion(out var exitHandler))
                return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to map exit handler stub");
            if (!InitializeProcessEntryFrame(context, imageName, exitHandler))
                return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to set up process entry frame");
            entryParamsConfigured = true;

            // Bootstrap injection
            if (ShouldInjectBootstrapPayload(entryPoint))
            {
                if (!TryInstallBootstrapPayload(context, effectiveImportStubs))
                    return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to install bootstrap payload");
            }
        }
        else
        {
            if (!InitializeModuleInitializerFrame(context))
                return CpuExecutionResult.FromError(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, CpuExitReason.UnhandledException, entryPoint, "Failed to set up module initializer frame");
            entryParamsConfigured = false;
        }

        // 6. Build diagnostic milestone log
        var milestoneLog = BuildEntryFrameDiagnostic(
            entryPoint,
            context,
            sentinelEnabled: true,
            sentinelValue: returnStub,
            entryParamsConfigured: entryParamsConfigured);

        // 7. Ensure engine is NativeOnly (the only supported)
        if (executionOptions.CpuEngine != CpuExecutionEngine.NativeOnly)
        {
            var notImpl = new CpuNotImplementedInfo(
                CpuNotImplementedSource.NativeBackend,
                entryPoint,
                null,
                "cpu_engine_unsupported",
                executionOptions.CpuEngine.ToString(),
                "Unsupported CPU engine mode.");
            _logger.LogWarning("Unsupported CPU engine: {Engine}", executionOptions.CpuEngine);
            return new CpuExecutionResult(
                OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
                CpuExitReason.NativeBackendUnavailable,
                null,
                entryPoint,
                0, 0, 0, 0,
                notImplementedInfo: notImpl,
                milestoneLog: milestoneLog);
        }

        // 8. Attach debug hook if present
        var debugHook = executionOptions.DebugHook;
        var debugFrame = debugHook is null
            ? null
            : new CpuContextDebugFrame(
                frameKind == EntryFrameKind.ProcessEntry
                    ? CpuDebugFrameKind.ProcessEntry
                    : CpuDebugFrameKind.ModuleInitializer,
                entryPoint,
                imageName,
                context,
                effectiveImportStubs);
        debugHook?.OnFrameEnter(debugFrame!);
        (_nativeCpuBackend as DirectExecutionBackend)?.SetActiveDebugFrame(debugFrame);

        // 9. Execute
        var backendResult = _nativeCpuBackend.TryExecute(
            context,
            entryPoint,
            generation,
            effectiveImportStubs,
            runtimeSymbols ?? new Dictionary<string, ulong>(StringComparer.Ordinal),
            executionOptions,
            out var nativeResult);

        debugHook?.OnFrameExit(debugFrame!, nativeResult);

        // 10. Build the final result with all diagnostic info
        var exitReason = backendResult
            ? (nativeResult == OrbisGen2Result.ORBIS_GEN2_OK ? CpuExitReason.ReturnedToHost : CpuExitReason.UnhandledException)
            : CpuExitReason.NativeBackendUnavailable;

        if (!backendResult)
        {
            var backendName = string.IsNullOrWhiteSpace(_nativeCpuBackend.BackendName)
                ? "native-backend" : _nativeCpuBackend.BackendName;
            var backendError = string.IsNullOrWhiteSpace(_nativeCpuBackend.LastError)
                ? "unknown backend error" : _nativeCpuBackend.LastError;
            var notImpl = new CpuNotImplementedInfo(
                CpuNotImplementedSource.NativeBackend,
                entryPoint,
                null,
                "cpu_engine_native_only",
                backendName,
                backendError);
            _logger.LogError("Native backend failed: {Error}", backendError);
            return new CpuExecutionResult(
                OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
                CpuExitReason.NativeBackendUnavailable,
                null,
                context.Rip,
                0, 0, 0, 0,
                notImplementedInfo: notImpl,
                milestoneLog: milestoneLog + $"\nNative backend failed: {backendError}");
        }

        // Success
        return new CpuExecutionResult(
            nativeResult,
            exitReason,
            null, // exitCode unknown
            context.Rip,
            0, // lastStubRip unknown
            0, // totalInstructions unknown
            0, // importsHit unknown
            0, // uniqueNidsHit unknown
            milestoneLog: milestoneLog);
    }

    // ------------------------------------------------------------------
    // Private helper methods
    // ------------------------------------------------------------------

    private bool TryMapStackRegion(out ulong baseAddress)
    {
        const ulong stride = 0x0100_0000UL;
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidate = CpuLayout.StackBaseAddress - ((ulong)i * stride);
            if (TryMapRegion(candidate, CpuLayout.StackSize, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write))
            {
                baseAddress = candidate;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapTlsRegion(out ulong baseAddress)
    {
        const ulong stride = 0x0100_0000UL;
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidateBase = CpuLayout.TlsBaseAddress - ((ulong)i * stride);
            var mappedBase = candidateBase - CpuLayout.TlsPrefixSize;
            if (TryMapRegion(mappedBase, CpuLayout.TlsSize + CpuLayout.TlsPrefixSize, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write))
            {
                baseAddress = candidateBase;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapReturnToHostStubRegion(out ulong baseAddress)
    {
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidate = CpuLayout.ReturnToHostStubBaseAddress - ((ulong)i * CpuLayout.ReturnToHostStubStride);
            if (TryMapRegion(candidate, CpuLayout.BootstrapRegionSize, ReturnToHostStubBytes, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute))
            {
                baseAddress = candidate;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapBootstrapStubRegion(out ulong baseAddress)
    {
        const ulong stride = 0x0100_0000UL;
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidate = CpuLayout.BootstrapStubBaseAddress - ((ulong)i * stride);
            if (TryMapRegion(candidate, CpuLayout.BootstrapRegionSize, BootstrapStubBytes, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute))
            {
                baseAddress = candidate;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapBootstrapPayloadRegion(out ulong baseAddress)
    {
        const ulong stride = 0x0100_0000UL;
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidate = CpuLayout.BootstrapPayloadBaseAddress - ((ulong)i * stride);
            if (TryMapRegion(candidate, CpuLayout.BootstrapRegionSize, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write))
            {
                baseAddress = candidate;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapDynlibFallbackStubRegion(out ulong baseAddress)
    {
        const ulong stride = 0x0100_0000UL;
        for (int i = 0; i < CpuLayout.MaxRetryAttempts; i++)
        {
            var candidate = CpuLayout.DynlibFallbackStubBaseAddress - ((ulong)i * stride);
            if (TryMapRegion(candidate, CpuLayout.BootstrapRegionSize, DynlibFallbackStubBytes, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute))
            {
                baseAddress = candidate;
                return true;
            }
        }
        baseAddress = 0;
        return false;
    }

    private bool TryMapRegion(ulong address, ulong size, ReadOnlySpan<byte> data, ProgramHeaderFlags flags)
    {
        try
        {
            _virtualMemory.Map(address, size, 0, data, flags);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Failed to map region at 0x{Address:X16} (size 0x{Size:X16})", address, size);
            return false;
        }
    }

    private bool InitializeGuestFrameChainSentinel(CpuContext context)
    {
        var stackTop = context[CpuRegister.Rsp] + sizeof(ulong);
        var sentinelFrame = AlignDown(stackTop - 0x20, 16);
        var seedRsp = sentinelFrame - sizeof(ulong);
        if (!context.TryWriteUInt64(sentinelFrame, 0) ||
            !context.TryWriteUInt64(sentinelFrame + sizeof(ulong), 0) ||
            !context.TryWriteUInt64(seedRsp, 0))
            return false;

        context[CpuRegister.Rbp] = sentinelFrame;
        context[CpuRegister.Rsp] = seedRsp;
        return true;
    }

    private bool InitializeTls(CpuContext context, ulong tlsBase)
    {
        // TLS initialisation with hardcoded offsets (original behaviour)
        if (!context.TryWriteUInt64(tlsBase - 0xF0, 0) ||
            !context.TryWriteUInt64(tlsBase + 0x00, tlsBase) ||
            !context.TryWriteUInt64(tlsBase + 0x10, tlsBase) ||
            !context.TryWriteUInt64(tlsBase + 0x28, 0xC0DEC0DECAFEBA00UL) ||
            !context.TryWriteUInt64(tlsBase + 0x60, tlsBase))
            return false;

        // Seed the static TLS block below the thread pointer
        SharpEmu.HLE.GuestTlsTemplate.SeedThreadBlock(context, tlsBase);
        return true;
    }

    private static bool InitializeProcessEntryFrame(
        CpuContext context,
        string processImageName,
        ulong programExitHandlerAddress)
    {
        var imageName = string.IsNullOrWhiteSpace(processImageName) ? "eboot.bin" : processImageName;
        var arguments = new List<string>(3) { imageName };
        var configuredArguments = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_ARGS");
        if (!string.IsNullOrWhiteSpace(configuredArguments))
        {
            var compatArgs = configuredArguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            arguments.AddRange(compatArgs.Take(2));
        }

        var cursor = context[CpuRegister.Rsp];
        var argumentAddresses = new ulong[arguments.Count];
        for (int index = arguments.Count - 1; index >= 0; index--)
        {
            var encoded = Encoding.UTF8.GetBytes(arguments[index] + '\0');
            cursor = AlignDown(cursor - (ulong)encoded.Length, 16);
            if (!context.Memory.TryWrite(cursor, encoded))
                return false;
            argumentAddresses[index] = cursor;
        }

        const ulong entryParamsSize = 0x20;
        var entryParamsAddress = AlignDown(cursor - entryParamsSize, 16);
        if (!TryWriteUInt32(context, entryParamsAddress + 0x00, (uint)arguments.Count) ||
            !TryWriteUInt32(context, entryParamsAddress + 0x04, 0) ||
            !context.TryWriteUInt64(entryParamsAddress + 0x08, argumentAddresses[0]) ||
            !context.TryWriteUInt64(entryParamsAddress + 0x10, argumentAddresses.Length > 1 ? argumentAddresses[1] : 0) ||
            !context.TryWriteUInt64(entryParamsAddress + 0x18, argumentAddresses.Length > 2 ? argumentAddresses[2] : 0))
            return false;

        var entryStackPointer = entryParamsAddress - sizeof(ulong);
        if (!context.TryWriteUInt64(entryStackPointer, 0))
            return false;

        context[CpuRegister.Rsp] = entryStackPointer;
        context[CpuRegister.Rdi] = entryParamsAddress;
        context[CpuRegister.Rsi] = programExitHandlerAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0;
        return true;
    }

    private static bool InitializeModuleInitializerFrame(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0;
        return true;
    }

    private bool ShouldInjectBootstrapPayload(ulong entryPoint)
    {
        Span<byte> probe = stackalloc byte[BootstrapStartSignature.Length];
        if (!_virtualMemory.TryRead(entryPoint, probe))
            return false;
        return probe.SequenceEqual(BootstrapStartSignature);
    }

    private bool TryInstallBootstrapPayload(CpuContext context, IDictionary<ulong, string> importStubs)
    {
        if (!TryMapBootstrapStubRegion(out var stubAddress))
            return false;
        if (!TryMapBootstrapPayloadRegion(out var payloadAddress))
            return false;

        var statusAddress = payloadAddress + CpuLayout.BootstrapStatusOffset;
        if (!context.TryWriteUInt64(payloadAddress, stubAddress) ||
            !context.TryWriteUInt64(payloadAddress + 0x08, statusAddress) ||
            !context.TryWriteUInt64(payloadAddress + 0x10, statusAddress) ||
            !context.TryWriteUInt64(payloadAddress + 0x18, statusAddress) ||
            !context.TryWriteUInt64(payloadAddress + 0x20, statusAddress) ||
            !context.TryWriteUInt64(payloadAddress + CpuLayout.BootstrapPayloadResultOffset, statusAddress) ||
            !TryWriteUInt32(context, statusAddress, 0))
            return false;

        importStubs[stubAddress] = RuntimeStubNids.BootstrapBridge;
        importStubs[stubAddress + 0x0A] = RuntimeStubNids.BootstrapBridge;

        context[CpuRegister.Rdi] = payloadAddress;
        return true;
    }

    private static bool TryWriteUInt32(CpuContext context, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return context.Memory.TryWrite(address, buffer);
    }

    private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

    private static string BuildEntryFrameDiagnostic(
        ulong entryPoint,
        CpuContext context,
        bool sentinelEnabled,
        ulong sentinelValue,
        bool entryParamsConfigured)
    {
        var initialRsp = context[CpuRegister.Rsp];
        var stackTopText = context.TryReadUInt64(initialRsp, out var stackTop) ? $"0x{stackTop:X16}" : "??";
        return $"EntryFrame: entry_rip=0x{entryPoint:X16} initial_rsp=0x{initialRsp:X16} [rsp]={stackTopText} sentinel_enabled={(sentinelEnabled ? "yes" : "no")} sentinel_value=0x{sentinelValue:X16} entry_params_configured={(entryParamsConfigured ? "yes" : "no")}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        (_nativeCpuBackend as IDisposable)?.Dispose();
        _disposed = true;
    }
}
