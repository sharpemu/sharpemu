// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpEmu.Libs.PlayGo;

public static class PlayGoExports
{
    private const int OrbisPlayGoErrorInvalidArgument = unchecked((int)0x80B20004);
    private const int OrbisPlayGoErrorNotInitialized = unchecked((int)0x80B20005);
    private const int OrbisPlayGoErrorAlreadyInitialized = unchecked((int)0x80B20006);
    private const int OrbisPlayGoErrorBadHandle = unchecked((int)0x80B20009);
    private const int OrbisPlayGoErrorBadPointer = unchecked((int)0x80B2000A);
    private const int OrbisPlayGoErrorBadSize = unchecked((int)0x80B2000B);
    private const int OrbisPlayGoErrorBadChunkId = unchecked((int)0x80B2000C);
    private const int OrbisPlayGoErrorNotSupportPlayGo = unchecked((int)0x80B2000E);
    private const int OrbisPlayGoErrorBadLocus = unchecked((int)0x80B20010);
    private const ulong PlayGoInitBufAddrOffset = 0;
    private const ulong PlayGoInitBufSizeOffset = 8;
    private const uint PlayGoMinimumInitBufferSize = 0x200000;
    private const uint PlayGoHandle = 1;
    private const int PlayGoLocusNotDownloaded = 0;
    private const int PlayGoLocusLocalSlow = 2;
    private const int PlayGoLocusLocalFast = 3;
    private const int PlayGoInstallSpeedSuspended = 0;
    private const int PlayGoInstallSpeedTrickle = 1;
    private const int PlayGoInstallSpeedFull = 2;
    private const uint MaxPlayGoQueryEntries = 0x4000;
    private const uint PlayGoAllEntriesSentinel = uint.MaxValue;

    private static readonly Regex ChunkIdPattern = new(
        @"<chunk\s+[^>]*\bid\s*=\s*""(?<id>\d+)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex DefaultChunkPattern = new(
        @"default_chunk\s*=\s*""(?<id>\d+)""",
        RegexOptions.CultureInvariant);

    private static readonly object _stateGate = new();
    private static bool _initialized;
    private static bool _opened;
    private static PlayGoMetadata _metadata = PlayGoMetadata.Empty;
    private static int _installSpeed = PlayGoInstallSpeedTrickle;
    private static ulong _languageMask = ulong.MaxValue;
    private static int _unknownChunkDiagnostics;
    private static int _locusTraceDiagnostics;

    [SysAbiExport(
        Nid = "ts6GlZOKRrE",
        ExportName = "scePlayGoInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoInitialize(CpuContext ctx)
    {
        var initParamsAddress = ctx[CpuRegister.Rdi];
        if (initParamsAddress == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (!ctx.TryReadUInt64(initParamsAddress + PlayGoInitBufAddrOffset, out var bufferAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<byte> bufferSizeBytes = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(initParamsAddress + PlayGoInitBufSizeOffset, bufferSizeBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var bufferSize = BinaryPrimitives.ReadUInt32LittleEndian(bufferSizeBytes);

        if (bufferAddress == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (bufferSize < PlayGoMinimumInitBufferSize)
        {
            return OrbisPlayGoErrorBadSize;
        }

        lock (_stateGate)
        {
            if (_initialized)
            {
                return OrbisPlayGoErrorAlreadyInitialized;
            }

            _metadata = LoadPlayGoMetadata();
            _installSpeed = PlayGoInstallSpeedTrickle;
            _languageMask = ulong.MaxValue;
            _opened = false;
            _initialized = true;
            TracePlayGo($"initialize chunks={_metadata.ChunkIds.Length} available={_metadata.Available}");
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "M1Gma1ocrGE",
        ExportName = "scePlayGoOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoOpen(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        if (outHandleAddress == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (paramAddress != 0)
        {
            return OrbisPlayGoErrorInvalidArgument;
        }

        lock (_stateGate)
        {
            if (!_initialized)
            {
                return OrbisPlayGoErrorNotInitialized;
            }

            if (!_metadata.Available)
            {
                return OrbisPlayGoErrorNotSupportPlayGo;
            }

            _opened = true;
            TracePlayGo($"open handle={PlayGoHandle} chunks={_metadata.ChunkIds.Length}");
        }

        Span<byte> handleBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(handleBytes, PlayGoHandle);
        if (!ctx.Memory.TryWrite(outHandleAddress, handleBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MPe0EeBGM-E",
        ExportName = "scePlayGoTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoTerminate(CpuContext ctx)
    {
        _ = ctx;
        lock (_stateGate)
        {
            if (!_initialized)
            {
                return OrbisPlayGoErrorNotInitialized;
            }

            _initialized = false;
            _opened = false;
            _metadata = PlayGoMetadata.Empty;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Uco1I0dlDi8",
        ExportName = "scePlayGoClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoClose(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        lock (_stateGate)
        {
            if (!_initialized)
            {
                return OrbisPlayGoErrorNotInitialized;
            }

            if (handle != PlayGoHandle)
            {
                return OrbisPlayGoErrorBadHandle;
            }

            _opened = false;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "73fF1MFU8hA",
        ExportName = "scePlayGoGetChunkId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetChunkId(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outChunkIdList = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var outEntries = ctx[CpuRegister.Rcx];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (outEntries == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (outChunkIdList != 0 && numberOfEntries == 0)
        {
            return OrbisPlayGoErrorBadSize;
        }

        ushort[] chunkIds;
        lock (_stateGate)
        {
            chunkIds = _metadata.ChunkIds;
        }

        var availableEntries = chunkIds.Length == 0 ? 1u : (uint)chunkIds.Length;
        if (outChunkIdList == 0)
        {
            TracePlayGo($"get_chunk_id count_only entries={availableEntries} out_entries=0x{outEntries:X16}");
            return ctx.TryWriteUInt32(outEntries, availableEntries)
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var entriesToWrite = Math.Min(numberOfEntries, availableEntries);
        if (entriesToWrite > MaxPlayGoQueryEntries)
        {
            TracePlayGo($"get_chunk_id bad_size requested={numberOfEntries} available={availableEntries}");
            return OrbisPlayGoErrorBadSize;
        }

        for (uint i = 0; i < entriesToWrite; i++)
        {
            var chunkId = chunkIds.Length == 0 ? (ushort)0 : chunkIds[i];
            if (!ctx.TryWriteUInt16(outChunkIdList + (i * sizeof(ushort)), chunkId))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        TracePlayGo($"get_chunk_id write requested={numberOfEntries} wrote={entriesToWrite} available={availableEntries}");
        return ctx.TryWriteUInt32(outEntries, entriesToWrite)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "v6EZ-YWRdMs",
        ExportName = "scePlayGoGetEta",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetEta(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var chunkIds = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var outEta = ctx[CpuRegister.Rcx];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (chunkIds == 0 || outEta == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (numberOfEntries == 0 || numberOfEntries > MaxPlayGoQueryEntries)
        {
            TracePlayGo($"get_eta bad_size entries={numberOfEntries} chunk_ids=0x{chunkIds:X16} out=0x{outEta:X16}");
            return OrbisPlayGoErrorBadSize;
        }

        return ValidateChunkIds(ctx, chunkIds, numberOfEntries) is { } chunkError && chunkError != 0
            ? chunkError
            : ctx.TryWriteInt64(outEta, 0)
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "rvBSfTimejE",
        ExportName = "scePlayGoGetInstallSpeed",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetInstallSpeed(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outSpeed = ctx[CpuRegister.Rsi];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (outSpeed == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        int speed;
        lock (_stateGate)
        {
            speed = _installSpeed;
        }

        return ctx.TryWriteInt32(outSpeed, speed)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "3OMbYZBaa50",
        ExportName = "scePlayGoGetLanguageMask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetLanguageMask(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outLanguageMask = ctx[CpuRegister.Rsi];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (outLanguageMask == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        ulong languageMask;
        lock (_stateGate)
        {
            languageMask = _languageMask;
        }

        return ctx.TryWriteUInt64(outLanguageMask, languageMask)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "uWIYLFkkwqk",
        ExportName = "scePlayGoGetLocus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetLocus(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var chunkIds = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var outLoci = ctx[CpuRegister.Rcx];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (chunkIds == 0 || outLoci == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (numberOfEntries == PlayGoAllEntriesSentinel)
        {
            TracePlayGo($"get_locus sentinel entries={numberOfEntries} chunk_ids=0x{chunkIds:X16} out=0x{outLoci:X16}");
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (numberOfEntries == 0 || numberOfEntries > MaxPlayGoQueryEntries)
        {
            TracePlayGo($"get_locus bad_size entries={numberOfEntries} chunk_ids=0x{chunkIds:X16} out=0x{outLoci:X16}");
            return OrbisPlayGoErrorBadSize;
        }

        var loci = new byte[numberOfEntries];
        for (uint i = 0; i < numberOfEntries; i++)
        {
            if (!ctx.TryReadUInt16(chunkIds + (i * sizeof(ushort)), out var chunkId))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (!IsKnownChunkId(chunkId))
            {
                if (Interlocked.Increment(ref _unknownChunkDiagnostics) <= 8)
                {
                    ushort[] knownChunkIds;
                    lock (_stateGate)
                    {
                        knownChunkIds = _metadata.ChunkIds;
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] playgo.unknown_chunk_id id={chunkId} entries={numberOfEntries} " +
                        $"known=[{string.Join(',', knownChunkIds)}]");
                }

                // Real firmware rejects chunk ids outside the package's chunk set.
                // Titles rely on this as an enumeration terminator: Monster Truck
                // scans ids 0,1,2,... until BAD_CHUNK_ID, and answering OK for every
                // id makes that scan wrap the ushort range and spin forever.
                loci[i] = PlayGoLocusNotDownloaded;
                return ctx.Memory.TryWrite(outLoci, loci)
                    ? OrbisPlayGoErrorBadChunkId
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            loci[i] = PlayGoLocusLocalFast;
        }

        TracePlayGoLocus(ctx, numberOfEntries, chunkIds, outLoci);
        return ctx.Memory.TryWrite(outLoci, loci)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "-RJWNMK3fC8",
        ExportName = "scePlayGoGetProgress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetProgress(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var chunkIds = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var outProgress = ctx[CpuRegister.Rcx];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (chunkIds == 0 || outProgress == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (numberOfEntries == 0 || numberOfEntries > MaxPlayGoQueryEntries)
        {
            TracePlayGo($"get_progress bad_size entries={numberOfEntries} chunk_ids=0x{chunkIds:X16} out=0x{outProgress:X16}");
            return OrbisPlayGoErrorBadSize;
        }

        var chunkError = ValidateChunkIds(ctx, chunkIds, numberOfEntries);
        if (chunkError != 0)
        {
            return chunkError;
        }

        TracePlayGo($"get_progress entries={numberOfEntries}");
        Span<byte> progress = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(progress, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(progress[sizeof(ulong)..], 0);
        return ctx.Memory.TryWrite(outProgress, progress)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "Nn7zKwnA5q0",
        ExportName = "scePlayGoGetToDoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoGetToDoList(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outTodoList = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var outEntries = ctx[CpuRegister.Rcx];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (outTodoList == 0 || outEntries == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (numberOfEntries == 0)
        {
            TracePlayGo("get_todo bad_size entries=0");
            return OrbisPlayGoErrorBadSize;
        }

        TracePlayGo($"get_todo requested={numberOfEntries} wrote=0");
        return ctx.TryWriteUInt32(outEntries, 0)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "-Q1-u1a7p0g",
        ExportName = "scePlayGoPrefetch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoPrefetch(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var chunkIds = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);
        var minimumLocus = unchecked((int)ctx[CpuRegister.Rcx]);

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (chunkIds == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        if (numberOfEntries == 0 || numberOfEntries > MaxPlayGoQueryEntries)
        {
            return OrbisPlayGoErrorBadSize;
        }

        if (minimumLocus is not PlayGoLocusNotDownloaded and not PlayGoLocusLocalSlow and not PlayGoLocusLocalFast)
        {
            return OrbisPlayGoErrorBadLocus;
        }

        return ValidateChunkIds(ctx, chunkIds, numberOfEntries) is { } chunkError && chunkError != 0
            ? chunkError
            : (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4AAcTU9R3XM",
        ExportName = "scePlayGoSetInstallSpeed",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoSetInstallSpeed(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var speed = unchecked((int)ctx[CpuRegister.Rsi]);

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (speed is not PlayGoInstallSpeedSuspended and not PlayGoInstallSpeedTrickle and not PlayGoInstallSpeedFull)
        {
            return OrbisPlayGoErrorInvalidArgument;
        }

        lock (_stateGate)
        {
            _installSpeed = speed;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LosLlHOpNqQ",
        ExportName = "scePlayGoSetLanguageMask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoSetLanguageMask(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var languageMask = ctx[CpuRegister.Rsi];

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        lock (_stateGate)
        {
            _languageMask = languageMask;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gUPGiOQ1tmQ",
        ExportName = "scePlayGoSetToDoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePlayGo")]
    public static int PlayGoSetToDoList(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var todoList = ctx[CpuRegister.Rsi];
        var numberOfEntries = unchecked((uint)ctx[CpuRegister.Rdx]);

        var validation = ValidateHandle(handle);
        if (validation != 0)
        {
            return validation;
        }

        if (todoList == 0)
        {
            return OrbisPlayGoErrorBadPointer;
        }

        return numberOfEntries == 0
            ? OrbisPlayGoErrorBadSize
            : (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ValidateHandle(uint handle)
    {
        lock (_stateGate)
        {
            if (!_initialized)
            {
                return OrbisPlayGoErrorNotInitialized;
            }

            if (handle != PlayGoHandle || !_opened)
            {
                return OrbisPlayGoErrorBadHandle;
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ValidateChunkIds(CpuContext ctx, ulong chunkIds, uint numberOfEntries)
    {
        for (uint i = 0; i < numberOfEntries; i++)
        {
            if (!ctx.TryReadUInt16(chunkIds + (i * sizeof(ushort)), out var chunkId))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            _ = chunkId;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool IsKnownChunkId(ushort chunkId)
    {
        lock (_stateGate)
        {
            return _metadata.ChunkIdKnowledge == PlayGoChunkIdKnowledge.Unknown ||
                Array.BinarySearch(_metadata.ChunkIds, chunkId) >= 0;
        }
    }

    private static PlayGoMetadata LoadPlayGoMetadata()
    {
        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0Root))
        {
            // No app0 override to probe for sidecar files: same fully-installed
            // single-chunk fallback as below, or scePlayGoOpen fails fatally.
            return new PlayGoMetadata(
                true,
                [(ushort)0],
                PlayGoChunkIdKnowledge.Authoritative);
        }

        var playGoDat = Path.Combine(app0Root, "sce_sys", "playgo-chunk.dat");
        var scenarioJson = Path.Combine(app0Root, "sce_sys", "playgo-scenario.json");
        var chunkDefsXml = Path.Combine(app0Root, "playgo-chunkdefs.xml");

        var hasMetadata = File.Exists(playGoDat) || File.Exists(scenarioJson) || File.Exists(chunkDefsXml);
        if (!hasMetadata)
        {
            // No PlayGo sidecar: report a fully-installed single chunk. Available must
            // stay true or scePlayGoOpen fails with NotSupportPlayGo (fatal PS5-component
            // init failure for UE titles); chunk 0 reports LocalFast and every other id
            // returns BAD_CHUNK_ID, terminating title-side chunk enumeration.
            TracePlayGo("metadata_missing; fully-installed single chunk");
            return new PlayGoMetadata(
                true,
                [(ushort)0],
                PlayGoChunkIdKnowledge.Authoritative);
        }

        var chunkIds = LoadChunkIds(chunkDefsXml);
        return new PlayGoMetadata(
            true,
            chunkIds,
            chunkIds.Length == 0
                ? PlayGoChunkIdKnowledge.Unknown
                : PlayGoChunkIdKnowledge.Authoritative);
    }

    private static ushort[] LoadChunkIds(string chunkDefsXml)
    {
        if (!File.Exists(chunkDefsXml))
        {
            return Array.Empty<ushort>();
        }

        try
        {
            var xml = File.ReadAllText(chunkDefsXml);
            _ = XDocument.Parse(xml, LoadOptions.None);

            var chunkIds = new HashSet<ushort>();
            AddChunkIds(xml, DefaultChunkPattern, chunkIds);
            AddChunkIds(xml, ChunkIdPattern, chunkIds);

            var sorted = chunkIds.ToArray();
            Array.Sort(sorted);
            return sorted;
        }
        catch (IOException)
        {
            return Array.Empty<ushort>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ushort>();
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<ushort>();
        }
    }

    private static void AddChunkIds(string xml, Regex pattern, HashSet<ushort> chunkIds)
    {
        foreach (Match match in pattern.Matches(xml))
        {
            if (ushort.TryParse(match.Groups["id"].Value, out var chunkId))
            {
                chunkIds.Add(chunkId);
            }
        }
    }

    private static void TracePlayGo(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PLAYGO"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] playgo.{message}");
        }
    }

    private static void TracePlayGoLocus(CpuContext ctx, uint entries, ulong chunkIds, ulong outLoci)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PLAYGO"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var count = Interlocked.Increment(ref _locusTraceDiagnostics);
        if (entries != 1 || count <= 32 || count % 1000 == 0)
        {
            _ = ctx.TryReadUInt16(chunkIds, out var firstChunkId);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] playgo.get_locus entries={entries} first_chunk={firstChunkId} " +
                $"chunk_ids=0x{chunkIds:X16} out=0x{outLoci:X16}");
        }
    }

    internal static void ResetForTests()
    {
        lock (_stateGate)
        {
            _initialized = false;
            _opened = false;
            _metadata = PlayGoMetadata.Empty;
            _installSpeed = PlayGoInstallSpeedTrickle;
            _languageMask = ulong.MaxValue;
        }

        Interlocked.Exchange(ref _unknownChunkDiagnostics, 0);
        Interlocked.Exchange(ref _locusTraceDiagnostics, 0);
    }

    private enum PlayGoChunkIdKnowledge
    {
        Unknown,
        Authoritative,
    }

    private sealed record PlayGoMetadata(
        bool Available,
        ushort[] ChunkIds,
        PlayGoChunkIdKnowledge ChunkIdKnowledge)
    {
        public static readonly PlayGoMetadata Empty = new(
            false,
            Array.Empty<ushort>(),
            PlayGoChunkIdKnowledge.Unknown);
    }
}
