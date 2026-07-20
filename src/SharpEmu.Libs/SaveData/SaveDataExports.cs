// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataExports
{
    private const int OrbisSaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int OrbisSaveDataErrorExists = unchecked((int)0x809F0007);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const int OrbisSaveDataErrorInternal = unchecked((int)0x809F000B);
    private const int OrbisSaveDataErrorMemoryNotReady = unchecked((int)0x809F0012);
    private const int SaveDataTitleIdSize = 10;
    private const int SaveDataDirNameSize = 32;
    private const int SaveDataParamSize = 0x530;
    private const int SaveDataSearchInfoSize = 0x30;
    private const ulong ResultHitNumOffset = 0x00;
    private const ulong ResultDirNamesOffset = 0x08;
    private const ulong ResultDirNamesNumOffset = 0x10;
    private const ulong ResultSetNumOffset = 0x14;
    private const ulong ResultParamsOffset = 0x18;
    private const ulong ResultInfosOffset = 0x20;
    private const uint SortKeyFreeBlocks = 5;
    private const uint SortOrderDescent = 1;
    private const uint MountModeCreate = 1u << 2;
    private const uint MountModeCreate2 = 1u << 5;
    private const int MountResultSize = 0x40;
    // Emulator guard against corrupt or misread sizes, not a platform limit.
    private const ulong SaveDataMemoryMaxSize = 64UL * 1024 * 1024;
    private static readonly object _stateGate = new();
    private static readonly object _memoryGate = new();
    private static readonly HashSet<int> _preparedTransactionResources = [];
    private static string? _titleId;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (_stateGate)
        {
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId.Trim());
            _preparedTransactionResources.Clear();
        }

        lock (_eventGate)
        {
            _events.Clear();
        }

        lock (_mountGate)
        {
            _mounts.Clear();
        }
    }

    // Additional error codes and the async-event model (see sceSaveDataGetEventResult).
    private const int OrbisSaveDataErrorBusy = unchecked((int)0x809F0006);
    private const int OrbisSaveDataErrorNoEvent = unchecked((int)0x809F0008); // NOT_FOUND: no pending event
    private const int OrbisSaveDataErrorBadMounted = unchecked((int)0x809F0013);
    // SceSaveDataEventType
    private const uint EventTypeUmountBackupEnd = 1;
    private const uint EventTypeBackupEnd = 2;
    private const uint EventTypeSaveDataMemorySyncEnd = 3;
    private const int SaveDataEventSize = 0x60;
    private const int MountInfoSize = 0x40;
    private const uint DefaultBlockSize = 32768;
    private const ulong DefaultTotalBlocks = 0x8000; // 1 GiB of 32 KiB blocks

    private static readonly object _eventGate = new();
    private static readonly Queue<SaveDataEvent> _events = new();
    private static readonly object _mountGate = new();
    // mountPoint -> live mount, for umount/IsMounted/GetMountInfo.
    private static readonly Dictionary<string, MountEntry> _mounts = new(StringComparer.Ordinal);

    private readonly record struct SaveDataEvent(uint Type, int ErrorCode, int UserId, string DirName);
    private sealed record MountEntry(string SlotDir, string DirName, int UserId);

    private static void EnqueueEvent(uint type, int userId, string dirName, int errorCode = 0)
    {
        lock (_eventGate)
        {
            _events.Enqueue(new SaveDataEvent(type, errorCode, userId, dirName));
        }
        TraceSaveData($"event.enqueue type={type} user={userId} dir='{dirName}' err=0x{errorCode:X}");
    }

    [SysAbiExport(
        Nid = "j8xKtiFj0SY",
        ExportName = "sceSaveDataGetEventResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetEventResult(CpuContext ctx)
    {
        // rdi: SceSaveDataEventParam* (filter, ignored). rsi: SceSaveDataEvent* out.
        var eventAddress = ctx[CpuRegister.Rsi];
        if (eventAddress == 0)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        SaveDataEvent pending;
        lock (_eventGate)
        {
            if (_events.Count == 0)
            {
                // No queued completion. Games poll this from a worker; report the
                // defined "no event" status so the loop keeps polling instead of
                // acting on an uninitialized event struct.
                return SetReturn(ctx, OrbisSaveDataErrorNoEvent);
            }

            pending = _events.Dequeue();
        }

        Span<byte> ev = stackalloc byte[SaveDataEventSize];
        ev.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(ev[0x00..], pending.Type);
        BinaryPrimitives.WriteInt32LittleEndian(ev[0x04..], pending.ErrorCode);
        BinaryPrimitives.WriteInt32LittleEndian(ev[0x08..], pending.UserId);
        WriteAscii(ev.Slice(0x10, SaveDataDirNameSize), pending.DirName);
        if (!ctx.Memory.TryWrite(eventAddress, ev))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "hsKd5c21sQc",
        ExportName = "sceSaveDataRegisterEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataRegisterEventCallback(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "v-AK1AxQhS0",
        ExportName = "sceSaveDataUnregisterEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUnregisterEventCallback(CpuContext ctx) => SetReturn(ctx, 0);

    // ---- lifecycle ----
    [SysAbiExport(Nid = "ZkZhskCPXFw", ExportName = "sceSaveDataInitialize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize(CpuContext ctx) => SaveDataInitializeCommon(ctx);

    [SysAbiExport(Nid = "l1NmDeDpNGU", ExportName = "sceSaveDataInitialize2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize2(CpuContext ctx) => SaveDataInitializeCommon(ctx);

    private static int SaveDataInitializeCommon(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return SetReturn(ctx, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(Nid = "yKDy8S5yLA0", ExportName = "sceSaveDataTerminate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataTerminate(CpuContext ctx) => SetReturn(ctx, 0);

    // ---- mount variants (all share the SceSaveDataMount layout) ----
    [SysAbiExport(Nid = "32HQAQdwM2o", ExportName = "sceSaveDataMount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMount(CpuContext ctx) => SaveDataMount3(ctx);

    [SysAbiExport(Nid = "0z45PIH+SNI", ExportName = "sceSaveDataMount2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMount2(CpuContext ctx) => SaveDataMount3(ctx);

    [SysAbiExport(Nid = "xz0YMi6BfNk", ExportName = "sceSaveDataMount5", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataMount5(CpuContext ctx) => SaveDataMount3(ctx);

    [SysAbiExport(Nid = "BMR4F-Uek3E", ExportName = "sceSaveDataUmount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataUmount(CpuContext ctx) => SaveDataUmount2(ctx);

    [SysAbiExport(Nid = "ieP6jP138Qo", ExportName = "sceSaveDataIsMounted", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataIsMounted(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        int mountCount;
        lock (_mountGate)
        {
            mountCount = _mounts.Count;
        }

        if (outAddress != 0)
        {
            TryWriteUInt32(ctx, outAddress, mountCount > 0 ? 1u : 0u);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(Nid = "65VH0Qaaz6s", ExportName = "sceSaveDataGetMountInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetMountInfo(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || infoAddress == 0 ||
            !TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        MountEntry? entry;
        lock (_mountGate)
        {
            _mounts.TryGetValue(mountPoint, out entry);
        }

        if (entry is null)
        {
            return SetReturn(ctx, OrbisSaveDataErrorBadMounted);
        }

        var used = SafeDirectorySize(entry.SlotDir);
        var usedBlocks = (ulong)((used + DefaultBlockSize - 1) / DefaultBlockSize);
        Span<byte> info = stackalloc byte[MountInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x00..], DefaultTotalBlocks);       // blocks
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], usedBlocks);               // freeBlocks slot reused as used
        return ctx.Memory.TryWrite(infoAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // ---- delete ----
    [SysAbiExport(Nid = "S1GkePI17zQ", ExportName = "sceSaveDataDelete", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDelete(CpuContext ctx) => SaveDataDeleteCommon(ctx);

    [SysAbiExport(Nid = "SQWusLoK8Pw", ExportName = "sceSaveDataDelete5", Target = Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataDelete5(CpuContext ctx) => SaveDataDeleteCommon(ctx);

    private static int SaveDataDeleteCommon(CpuContext ctx)
    {
        // SceSaveDataDelete: +0x00 userId, +0x08 dirName*, ... (dirName drives the slot).
        var deleteAddress = ctx[CpuRegister.Rdi];
        if (deleteAddress == 0 ||
            !TryReadInt32(ctx, deleteAddress, out var userId) ||
            !ctx.TryReadUInt64(deleteAddress + 0x08, out var dirNameAddress) ||
            dirNameAddress == 0 ||
            !TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out var dirName) ||
            string.IsNullOrWhiteSpace(dirName))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        try
        {
            var slotDir = SaveDataStorage.SlotDir(ResolveTitleSaveRoot(userId, ResolveConfiguredTitleId()), dirName);
            if (!Directory.Exists(slotDir))
            {
                return SetReturn(ctx, OrbisSaveDataErrorNotFound);
            }

            Directory.Delete(slotDir, recursive: true);
            TraceSaveData($"delete user={userId} dir='{dirName}' path='{slotDir}'");
            return SetReturn(ctx, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    // ---- params (metadata shown in the save UI) ----
    [SysAbiExport(Nid = "XgvSuIdnMlw", ExportName = "sceSaveDataGetParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetParam(CpuContext ctx) => TransferParam(ctx, write: false);

    [SysAbiExport(Nid = "85zul--eGXs", ExportName = "sceSaveDataSetParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetParam(CpuContext ctx) => TransferParam(ctx, write: true);

    private static int TransferParam(CpuContext ctx, bool write)
    {
        // rdi: mount-point string (16 bytes). rsi: paramType. rdx: SceSaveDataParam*. rcx: size.
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rdx];
        if (mountPointAddress == 0 || paramAddress == 0 ||
            !TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        MountEntry? entry;
        lock (_mountGate)
        {
            _mounts.TryGetValue(mountPoint, out entry);
        }

        if (entry is null)
        {
            return SetReturn(ctx, OrbisSaveDataErrorBadMounted);
        }

        try
        {
            if (write)
            {
                Span<byte> raw = stackalloc byte[SaveDataParamSize];
                if (!ctx.Memory.TryRead(paramAddress, raw))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                var metadata = new SaveDataMetadata
                {
                    Title = ReadAsciiField(raw.Slice(0x00, 128)),
                    SubTitle = ReadAsciiField(raw.Slice(0x80, 128)),
                    Detail = ReadAsciiField(raw.Slice(0x100, 1024)),
                    UserParam = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x500..]),
                };
                SaveDataStorage.WriteMetadata(entry.SlotDir, metadata);
                TraceSaveData($"set_param mount='{mountPoint}' title='{metadata.Title}'");
                return SetReturn(ctx, 0);
            }

            var loaded = SaveDataStorage.ReadMetadata(entry.SlotDir);
            var param = new byte[SaveDataParamSize];
            WriteAscii(param.AsSpan(0x00, 128), loaded.Title);
            WriteAscii(param.AsSpan(0x80, 128), loaded.SubTitle);
            WriteAscii(param.AsSpan(0x100, 1024), loaded.Detail);
            BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x500), loaded.UserParam);
            BinaryPrimitives.WriteInt64LittleEndian(
                param.AsSpan(0x508),
                new DateTimeOffset(SafeLastWriteUtc(entry.SlotDir)).ToUnixTimeSeconds());
            return ctx.Memory.TryWrite(paramAddress, param)
                ? SetReturn(ctx, 0)
                : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    // ---- icons ----
    [SysAbiExport(Nid = "c88Yy54Mx0w", ExportName = "sceSaveDataSaveIcon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSaveIcon(CpuContext ctx) => TransferIconForMount(ctx, write: true);

    [SysAbiExport(Nid = "cGjO3wM3V28", ExportName = "sceSaveDataLoadIcon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataLoadIcon(CpuContext ctx) => TransferIconForMount(ctx, write: false);

    private static int TransferIconForMount(CpuContext ctx, bool write)
    {
        // rdi: mount-point string. rsi: SceSaveDataIcon* {buf@+0x00, bufSize@+0x08, dataSize@+0x10}.
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var iconAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || iconAddress == 0 ||
            !TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) ||
            !ctx.TryReadUInt64(iconAddress + 0x00, out var bufferAddress) ||
            !ctx.TryReadUInt64(iconAddress + 0x08, out var bufferSize))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        MountEntry? entry;
        lock (_mountGate)
        {
            _mounts.TryGetValue(mountPoint, out entry);
        }

        if (entry is null)
        {
            return SetReturn(ctx, OrbisSaveDataErrorBadMounted);
        }

        var iconPath = SaveDataStorage.IconPath(entry.SlotDir);
        try
        {
            if (write)
            {
                var length = checked((int)Math.Min(bufferSize, (ulong)16 * 1024 * 1024));
                var bytes = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (!ctx.Memory.TryRead(bufferAddress, bytes.AsSpan(0, length)))
                    {
                        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                    File.WriteAllBytes(iconPath, bytes.AsSpan(0, length).ToArray());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }

                return SetReturn(ctx, 0);
            }

            if (!File.Exists(iconPath))
            {
                return SetReturn(ctx, OrbisSaveDataErrorNotFound);
            }

            var data = File.ReadAllBytes(iconPath);
            var copy = (int)Math.Min((ulong)data.Length, bufferSize);
            if (bufferAddress != 0 && copy > 0 && !ctx.Memory.TryWrite(bufferAddress, data.AsSpan(0, copy)))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TryWriteUInt32(ctx, iconAddress + 0x10, (uint)data.Length); // dataSize
            return SetReturn(ctx, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    // ---- size / progress / abort ----
    [SysAbiExport(Nid = "A1ThglSGUwA", ExportName = "sceSaveDataGetAllSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetAllSize(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        long total = 0;
        try
        {
            var titleRoot = ResolveTitleSaveRoot(0, ResolveConfiguredTitleId());
            if (Directory.Exists(titleRoot))
            {
                total = SafeDirectorySize(titleRoot);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Report zero on an unreadable tree rather than fail the query.
        }

        if (outAddress != 0)
        {
            var kib = (ulong)((total + 1023) / 1024);
            ctx.TryWriteUInt64(outAddress, kib);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(Nid = "ANmSWUiyyGQ", ExportName = "sceSaveDataGetProgress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetProgress(CpuContext ctx)
    {
        // Our operations complete synchronously, so any in-flight progress is 100%.
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress != 0)
        {
            Span<byte> progress = stackalloc byte[8];
            progress.Clear();
            BinaryPrimitives.WriteSingleLittleEndian(progress, 1.0f);
            ctx.Memory.TryWrite(outAddress, progress);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(Nid = "Wz-4JZfeO9g", ExportName = "sceSaveDataClearProgress", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataClearProgress(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(Nid = "dQ2GohUHXzk", ExportName = "sceSaveDataAbort", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataAbort(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(Nid = "eBSSNIG6hMk", ExportName = "sceSaveDataGetEventInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetEventInfo(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(Nid = "52pL2GKkdjA", ExportName = "sceSaveDataSetEventInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetEventInfo(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(Nid = "Z7z6HXWORJY", ExportName = "sceSaveDataSaveIconByPath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSaveIconByPath(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(Nid = "SN7rTPHS+Cg", ExportName = "sceSaveDataGetSaveDataCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataCount(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        var count = 0;
        try
        {
            var titleRoot = ResolveTitleSaveRoot(0, ResolveConfiguredTitleId());
            if (Directory.Exists(titleRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(titleRoot))
                {
                    if (!string.Equals(Path.GetFileName(dir), "sce_sdmemory", StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Report zero on an unreadable tree.
        }

        if (outAddress != 0)
        {
            TryWriteUInt32(ctx, outAddress, (uint)count);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(Nid = "pc4guaUPVqA", ExportName = "sceSaveDataGetMountedSaveDataCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetMountedSaveDataCount(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rsi];
        int mounted;
        lock (_mountGate)
        {
            mounted = _mounts.Count;
        }

        if (outAddress != 0)
        {
            TryWriteUInt32(ctx, outAddress, (uint)mounted);
        }

        return SetReturn(ctx, 0);
    }

    // ---- SaveDataMemory v1 aliases (identical arg layout to the v2 forms) ----
    [SysAbiExport(Nid = "v7AAAMo0Lz4", ExportName = "sceSaveDataSetupSaveDataMemory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetupSaveDataMemory(CpuContext ctx) => SaveDataSetupSaveDataMemory2(ctx);

    [SysAbiExport(Nid = "7Bt5pBC-Aco", ExportName = "sceSaveDataGetSaveDataMemory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory(CpuContext ctx) => SaveDataGetSaveDataMemory2(ctx);

    [SysAbiExport(Nid = "h3YURzXGSVQ", ExportName = "sceSaveDataSetSaveDataMemory", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory(CpuContext ctx) => SaveDataSetSaveDataMemory2(ctx);

    private static string ReadAsciiField(ReadOnlySpan<byte> field)
    {
        var length = field.IndexOf((byte)0);
        if (length < 0)
        {
            length = field.Length;
        }

        return Encoding.ASCII.GetString(field[..length]);
    }

    private static long SafeDirectorySize(string root)
    {
        try
        {
            return Directory.Exists(root) ? GetDirectorySize(root) : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }

    [SysAbiExport(
        Nid = "TywrFKCoLGY",
        ExportName = "sceSaveDataInitialize3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataInitialize3(CpuContext ctx)
    {
        try
        {
            Directory.CreateDirectory(ResolveSaveDataRoot());
            return SetReturn(ctx, 0);
        }
        catch (IOException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "dyIhnXq-0SM",
        ExportName = "sceSaveDataDirNameSearch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDirNameSearch(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (condAddress == 0 || resultAddress == 0)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        if (!TryReadSearchCond(ctx, condAddress, out var cond) ||
            !TryReadSearchResult(ctx, resultAddress, out var result))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (cond.UserId < 0 || cond.SortKey > SortKeyFreeBlocks || cond.SortOrder > SortOrderDescent)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        try
        {
            string titleId;
            if (cond.TitleIdAddress == 0)
            {
                titleId = ResolveConfiguredTitleId();
            }
            else if (!TryReadFixedAscii(ctx, cond.TitleIdAddress, SaveDataTitleIdSize, out titleId))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var root = ResolveTitleSaveRoot(cond.UserId, titleId);
            var entries = Directory.Exists(root)
                ? EnumerateSaveDirectories(root, cond.Pattern)
                : [];

            entries = SortEntries(entries, cond.SortKey, cond.SortOrder);
            var setNum = result.DirNamesNum == 0
                ? 0
                : Math.Min(result.DirNamesNum, entries.Count);
            if (!TryWriteUInt32(ctx, resultAddress + ResultHitNumOffset, checked((uint)entries.Count)) ||
                !TryWriteUInt32(ctx, resultAddress + ResultSetNumOffset, checked((uint)setNum)))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (setNum == 0)
            {
                TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set=0 root='{root}'");
                return SetReturn(ctx, 0);
            }

            if (result.DirNamesAddress == 0)
            {
                return SetReturn(ctx, OrbisSaveDataErrorParameter);
            }

            for (var i = 0; i < setNum; i++)
            {
                var entry = entries[i];
                if (!TryWriteFixedAscii(
                        ctx,
                        result.DirNamesAddress + ((ulong)i * SaveDataDirNameSize),
                        SaveDataDirNameSize,
                        entry.Name) ||
                    (result.ParamsAddress != 0 &&
                     !TryWriteParam(ctx, result.ParamsAddress + ((ulong)i * SaveDataParamSize), entry)) ||
                    (result.InfosAddress != 0 &&
                     !TryWriteSearchInfo(ctx, result.InfosAddress + ((ulong)i * SaveDataSearchInfoSize), entry)))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            TraceSaveData($"dir_name_search user={cond.UserId} title={titleId} hits={entries.Count} set={setNum} root='{root}'");
            return SetReturn(ctx, 0);
        }
        catch (IOException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "ZP4e7rlzOUk",
        ExportName = "sceSaveDataMount3",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataMount3(CpuContext ctx)
    {
        var mountAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (mountAddress == 0 || resultAddress == 0)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        if (!TryReadInt32(ctx, mountAddress, out var userId) ||
            !ctx.TryReadUInt64(mountAddress + 0x08, out var dirNameAddress) ||
            !ctx.TryReadUInt64(mountAddress + 0x10, out var blocks) ||
            !ctx.TryReadUInt64(mountAddress + 0x18, out var systemBlocks) ||
            !TryReadUInt32(ctx, mountAddress + 0x20, out var mountMode) ||
            !TryReadUInt32(ctx, mountAddress + 0x24, out var resource) ||
            !TryReadUInt32(ctx, mountAddress + 0x28, out var mode) ||
            dirNameAddress == 0 ||
            !TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out var dirName))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || string.IsNullOrWhiteSpace(dirName))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        try
        {
            var titleId = ResolveConfiguredTitleId();
            var savePath = Path.Combine(
                ResolveTitleSaveRoot(userId, titleId),
                SanitizePathSegment(dirName));
            var existed = Directory.Exists(savePath);
            var create = (mountMode & MountModeCreate) != 0;
            var createIfMissing = (mountMode & MountModeCreate2) != 0;

            if (!existed && !create && !createIfMissing)
            {
                return SetReturn(ctx, OrbisSaveDataErrorNotFound);
            }

            if (existed && create)
            {
                return SetReturn(ctx, OrbisSaveDataErrorExists);
            }

            if (!existed)
            {
                Directory.CreateDirectory(savePath);
            }

            const string mountPoint = "/savedata0";
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, savePath);
            lock (_mountGate)
            {
                _mounts[mountPoint] = new MountEntry(savePath, dirName, userId);
            }

            Span<byte> result = stackalloc byte[MountResultSize];
            result.Clear();
            WriteAscii(result[..16], mountPoint);
            BinaryPrimitives.WriteUInt32LittleEndian(result[0x1C..], createIfMissing && !existed ? 1u : 0u);
            if (!ctx.Memory.TryWrite(resultAddress, result))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceSaveData(
                $"mount3 user={userId} title={titleId} dir={dirName} blocks={blocks} " +
                $"system_blocks={systemBlocks} mount_mode=0x{mountMode:X} resource={resource} mode={mode} " +
                $"mount_point={mountPoint} created={!existed} root='{savePath}'");
            return SetReturn(ctx, 0);
        }
        catch (IOException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
        catch (ArgumentException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }
    }

    private static int _nextTransactionResource;
    [SysAbiExport(
        Nid = "gjRZNnw0JPE",
        ExportName = "sceSaveDataCreateTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCreateTransactionResource(CpuContext ctx)
    {
        // Demon's Souls first-run call:
        // RDI = 0xC0000, RSI = RDX + 8, RDX = resource output.
        // Writing integer handle 1 makes the title dereference [1 + 8],
        // causing the repeatable access violation at guest address 0x9.
        var desWorkSize = ctx[CpuRegister.Rdi];
        var desWorkAddress = ctx[CpuRegister.Rsi];
        var desResourceAddress = ctx[CpuRegister.Rdx];

        if (desWorkSize == 0xC0000 &&
            desResourceAddress != 0 &&
            desResourceAddress <= ulong.MaxValue - sizeof(ulong) &&
            desWorkAddress == desResourceAddress + sizeof(ulong))
        {
            if (!ctx.TryWriteUInt64(desResourceAddress, 0))
            {
                return SetReturn(
                    ctx,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceSaveData(
                $"create_transaction_resource_des_guard " +
                $"work_size=0x{desWorkSize:X} " +
                $"work=0x{desWorkAddress:X} " +
                $"resource_addr=0x{desResourceAddress:X} resource=0x0");

            return SetReturn(ctx, 0);
        }
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var reserved = ctx[CpuRegister.Rsi];

        var id = (uint)Interlocked.Increment(ref _nextTransactionResource);

        // A small RDX value is a flag, and RCX contains the output address.
        // A larger RDX value is the output address for the older ABI.
        var resourceAddress = 0UL;
        var selectedAddress = SelectTransactionResourceAddress(
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx]);
        if (selectedAddress != 0 && TryWriteUInt32(ctx, selectedAddress, id))
        {
            resourceAddress = selectedAddress;
        }

        TraceSaveData(
            $"create_transaction_resource user={userId} reserved=0x{reserved:X} resource_addr=0x{resourceAddress:X} id={id}");

        return SetReturn(ctx, 0);
    }

    internal static ulong SelectTransactionResourceAddress(ulong rdx, ulong rcx)
    {
        if (rdx == 0)
        {
            return 0;
        }

        return rdx <= ushort.MaxValue ? rcx : rdx;
    }

    [SysAbiExport(
        Nid = "lJUQuaKqoKY",
        ExportName = "sceSaveDataDeleteTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataDeleteTransactionResource(CpuContext ctx)
    {
        var resource = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_stateGate)
        {
            _preparedTransactionResources.Remove(resource);
        }

        TraceSaveData($"delete_transaction_resource resource={resource}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "uW4vfTwMQVo",
        ExportName = "sceSaveDataUmount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataUmount2(CpuContext ctx)
    {
        // rdi: SceSaveDataMountPoint* (16-byte mount point string) for umount2.
        var mountPointAddress = ctx[CpuRegister.Rdi];
        if (mountPointAddress != 0 && TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) &&
            !string.IsNullOrEmpty(mountPoint))
        {
            lock (_mountGate)
            {
                _mounts.Remove(mountPoint);
            }

            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            TraceSaveData($"umount2 mount='{mountPoint}'");
        }

        return SetReturn(ctx, 0);
    }

    private static bool TryReadSearchCond(CpuContext ctx, ulong address, out SearchCond cond)
    {
        cond = default;
        if (!TryReadInt32(ctx, address, out var userId) ||
            !ctx.TryReadUInt64(address + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(address + 0x10, out var dirNameAddress) ||
            !TryReadUInt32(ctx, address + 0x18, out var sortKey) ||
            !TryReadUInt32(ctx, address + 0x1C, out var sortOrder))
        {
            return false;
        }

        string pattern;
        if (dirNameAddress == 0)
        {
            pattern = string.Empty;
        }
        else if (!TryReadFixedAscii(ctx, dirNameAddress, SaveDataDirNameSize, out pattern))
        {
            return false;
        }

        cond = new SearchCond(userId, titleIdAddress, pattern, sortKey, sortOrder);
        return true;
    }

    private static bool TryReadSearchResult(CpuContext ctx, ulong address, out SearchResult result)
    {
        result = default;
        if (!ctx.TryReadUInt64(address + ResultDirNamesOffset, out var dirNamesAddress) ||
            !TryReadUInt32(ctx, address + ResultDirNamesNumOffset, out var dirNamesNum) ||
            !ctx.TryReadUInt64(address + ResultParamsOffset, out var paramsAddress) ||
            !ctx.TryReadUInt64(address + ResultInfosOffset, out var infosAddress))
        {
            return false;
        }

        result = new SearchResult(dirNamesAddress, dirNamesNum, paramsAddress, infosAddress);
        return true;
    }

    private static List<SaveEntry> EnumerateSaveDirectories(string root, string pattern)
    {
        var entries = new List<SaveEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("sce_", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(pattern) && !MatchPattern(name, pattern)))
            {
                continue;
            }

            var info = new DirectoryInfo(directory);
            entries.Add(new SaveEntry(name, directory, info.LastWriteTimeUtc));
        }

        return entries;
    }

    private static List<SaveEntry> SortEntries(List<SaveEntry> entries, uint sortKey, uint sortOrder)
    {
        IOrderedEnumerable<SaveEntry> sorted = sortKey switch
        {
            3 => entries.OrderBy(entry => entry.LastWriteUtc),
            _ => entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = sorted.ToList();
        if (sortOrder == SortOrderDescent)
        {
            list.Reverse();
        }

        return list;
    }

    private static bool TryWriteParam(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var metadata = SaveDataStorage.ReadMetadata(entry.Path);
        var param = new byte[SaveDataParamSize];
        WriteAscii(param.AsSpan(0x00, 128), metadata.Title);
        WriteAscii(param.AsSpan(0x80, 128), metadata.SubTitle);
        WriteAscii(param.AsSpan(0x100, 1024), string.IsNullOrEmpty(metadata.Detail) ? entry.Name : metadata.Detail);
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x500), metadata.UserParam);
        BinaryPrimitives.WriteInt64LittleEndian(
            param.AsSpan(0x508, sizeof(long)),
            new DateTimeOffset(entry.LastWriteUtc).ToUnixTimeSeconds());
        return ctx.Memory.TryWrite(address, param);
    }

    private static bool TryWriteSearchInfo(CpuContext ctx, ulong address, SaveEntry entry)
    {
        var size = GetDirectorySize(entry.Path);
        var usedBlocks = checked((ulong)((size + 32767) / 32768));
        var blocks = Math.Max(96UL, usedBlocks);
        Span<byte> info = stackalloc byte[SaveDataSearchInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x00..], blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x08..], blocks - usedBlocks);
        return ctx.Memory.TryWrite(address, info);
    }

    private static long GetDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static bool MatchPattern(string value, string pattern) =>
        MatchPattern(value.AsSpan(), pattern.AsSpan());

    private static bool MatchPattern(ReadOnlySpan<char> value, ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty)
        {
            return value.IsEmpty;
        }

        if (pattern[0] == '%')
        {
            for (var i = 0; i <= value.Length; i++)
            {
                if (MatchPattern(value[i..], pattern[1..]))
                {
                    return true;
                }
            }

            return false;
        }

        if (value.IsEmpty)
        {
            return false;
        }

        if (pattern[0] == '_' ||
            char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(value[0]))
        {
            return MatchPattern(value[1..], pattern[1..]);
        }

        return false;
    }

    // Saves are keyed by title id only (single-user emulation) under
    // ~/SharpEmu/Saves/<titleId>/; userId is accepted for API fidelity but not
    // part of the host path.
    private static string ResolveTitleSaveRoot(int userId, string titleId) =>
        SaveDataStorage.TitleRoot(ResolveSaveDataRoot(), titleId);

    private static string ResolveSaveDataMemoryPath(int userId) =>
        SaveDataStorage.MemoryPath(ResolveTitleSaveRoot(userId, ResolveConfiguredTitleId()));

    private static bool TryReadMemoryData(
        CpuContext ctx, ulong address, out ulong buffer, out ulong size, out ulong offset)
    {
        size = 0;
        offset = 0;
        return ctx.TryReadUInt64(address, out buffer) &&
            ctx.TryReadUInt64(address + 0x08, out size) &&
            ctx.TryReadUInt64(address + 0x10, out offset);
    }

    private static string ResolveSaveDataRoot() => SaveDataStorage.Root();

    private static string ResolveConfiguredTitleId()
    {
        lock (_stateGate)
        {
            if (!string.IsNullOrWhiteSpace(_titleId))
            {
                return _titleId;
            }
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var app0Name = string.IsNullOrWhiteSpace(app0Root)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (!string.IsNullOrWhiteSpace(app0Name))
        {
            var candidate = app0Name.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return SanitizePathSegment(candidate);
            }
        }

        return "default";
    }

    private static string SanitizePathSegment(string value) => SaveDataStorage.Sanitize(value);

    private static bool TryReadFixedAscii(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        Span<byte> buffer = stackalloc byte[length];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        var stringLength = buffer.IndexOf((byte)0);
        if (stringLength < 0)
        {
            stringLength = buffer.Length;
        }

        value = Encoding.ASCII.GetString(buffer[..stringLength]);
        return true;
    }

    private static bool TryWriteFixedAscii(CpuContext ctx, ulong address, int length, string value)
    {
        Span<byte> buffer = stackalloc byte[length];
        buffer.Clear();
        WriteAscii(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        var count = Math.Min(value.Length, Math.Max(0, destination.Length - 1));
        for (var i = 0; i < count; i++)
        {
            var ch = value[i];
            destination[i] = ch <= 0x7F ? (byte)ch : (byte)'?';
        }
    }

    private static bool TryReadInt32(CpuContext ctx, ulong address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceSaveData(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] savedata.{message}");
        }
    }

    private readonly record struct SearchCond(
        int UserId,
        ulong TitleIdAddress,
        string Pattern,
        uint SortKey,
        uint SortOrder);

    private readonly record struct SearchResult(
        ulong DirNamesAddress,
        uint DirNamesNum,
        ulong ParamsAddress,
        ulong InfosAddress);

    private readonly record struct SaveEntry(string Name, string Path, DateTime LastWriteUtc);

    [SysAbiExport(
        Nid = "sDCBrmc61XU",
        ExportName = "sceSaveDataPrepare",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataPrepare(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var resource = unchecked((int)ctx[CpuRegister.Rdx]);
        if (mountPointAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            if (resource != 0)
            {
                _preparedTransactionResources.Add(resource);
            }
        }

        TraceSaveData($"prepare mount_point={mountPoint} resource={resource}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ie7qhZ4X0Cc",
        ExportName = "sceSaveDataCommit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCommit(CpuContext ctx)
    {
        var commitAddress = ctx[CpuRegister.Rdi];
        if (commitAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            _preparedTransactionResources.Clear();
        }

        TraceSaveData($"commit commit=0x{commitAddress:X16}");
        return ctx.SetReturn(0);
    }

    // Save data memory: a small per-user blob titles read and write without
    // mounting anything, backed by one zero-filled file per user and title.
    [SysAbiExport(
        Nid = "oQySEUfgXRA",
        ExportName = "sceSaveDataSetupSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetupSaveDataMemory2(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadInt32(ctx, paramAddress + 0x04, out var userId) ||
            !ctx.TryReadUInt64(paramAddress + 0x08, out var memorySize))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0 || memorySize == 0 || memorySize > SaveDataMemoryMaxSize)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var path = ResolveSaveDataMemoryPath(userId);
            lock (_memoryGate)
            {
                var backing = new FileInfo(path);
                var existedSize = backing.Exists ? (ulong)backing.Length : 0;

                // The result write comes first so a faulted result pointer
                // cannot leave created or grown setup state behind.
                if (resultAddress != 0 && !ctx.TryWriteUInt64(resultAddress, existedSize))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (existedSize < memorySize)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    stream.SetLength((long)memorySize);
                }

                TraceSaveData($"memory-setup2 user={userId} size=0x{memorySize:X} existed=0x{existedSize:X}");
            }

            return ctx.SetReturn(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }

    [SysAbiExport(
        Nid = "QwOO7vegnV8",
        ExportName = "sceSaveDataGetSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataGetSaveDataMemory2(CpuContext ctx) =>
        TransferSaveDataMemory(ctx, write: false);

    [SysAbiExport(
        Nid = "cduy9v4YmT4",
        ExportName = "sceSaveDataSetSaveDataMemory2",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetSaveDataMemory2(CpuContext ctx) =>
        TransferSaveDataMemory(ctx, write: true);

    // Writes go straight through to the backing file, so a ready state is
    // all sync has to confirm.
    [SysAbiExport(
        Nid = "wiT9jeC7xPw",
        ExportName = "sceSaveDataSyncSaveDataMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSyncSaveDataMemory(CpuContext ctx)
    {
        var syncAddress = ctx[CpuRegister.Rdi];
        if (syncAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadInt32(ctx, syncAddress, out var userId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!File.Exists(ResolveSaveDataMemoryPath(userId)))
        {
            return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
        }

        // The write already reached disk synchronously, but the guest treats
        // sync as asynchronous and blocks a worker on sceSaveDataGetEventResult
        // until the SAVE_DATA_MEMORY_SYNC_END event arrives. Post it so that
        // poll completes (this is what wedged Dead Cells at FLIP 0 in-level).
        EnqueueEvent(EventTypeSaveDataMemorySyncEnd, userId, string.Empty);
        return ctx.SetReturn(0);
    }

    private static int TransferSaveDataMemory(CpuContext ctx, bool write)
    {
        var requestAddress = ctx[CpuRegister.Rdi];
        if (requestAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadInt32(ctx, requestAddress, out var userId) ||
            !ctx.TryReadUInt64(requestAddress + 0x08, out var dataAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userId < 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        try
        {
            var path = ResolveSaveDataMemoryPath(userId);
            lock (_memoryGate)
            {
                if (!File.Exists(path))
                {
                    return ctx.SetReturn(OrbisSaveDataErrorMemoryNotReady);
                }

                if (dataAddress == 0)
                {
                    return ctx.SetReturn(0);
                }

                if (!TryReadMemoryData(ctx, dataAddress, out var bufAddress, out var bufSize, out var offset))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                using var stream = new FileStream(
                    path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read);
                var length = (ulong)stream.Length;
                if (bufAddress == 0 || bufSize > length || offset > length - bufSize)
                {
                    return ctx.SetReturn(OrbisSaveDataErrorParameter);
                }

                // The guarded file length bounds bufSize, so one rented buffer
                // covers the transfer and a guest fault never partially writes.
                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(bufSize, 1));
                try
                {
                    var span = buffer.AsSpan(0, (int)bufSize);
                    stream.Seek((long)offset, SeekOrigin.Begin);
                    if (write)
                    {
                        if (!ctx.Memory.TryRead(bufAddress, span))
                        {
                            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }

                        stream.Write(span);
                    }
                    else
                    {
                        stream.ReadExactly(span);
                        if (!ctx.Memory.TryWrite(bufAddress, span))
                        {
                            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                TraceSaveData(
                    $"memory-{(write ? "set2" : "get2")} user={userId} offset=0x{offset:X} size=0x{bufSize:X}");
                return ctx.SetReturn(0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
    }
}
