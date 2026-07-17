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
    private const int OrbisSaveDataErrorBusy = unchecked((int)0x809F0003);
    private const int OrbisSaveDataErrorNotMounted = unchecked((int)0x809F0004);
    private const int OrbisSaveDataErrorExists = unchecked((int)0x809F0007);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const int OrbisSaveDataErrorInternal = unchecked((int)0x809F000B);
    private const int OrbisSaveDataErrorMemoryNotReady = unchecked((int)0x809F0012);
    private const int OrbisSaveDataErrorMountFull = unchecked((int)0x809F000C);
    private const int SaveDataTitleIdSize = 10;
    private const int SaveDataDirNameSize = 32;
    private const int SaveDataParamSize = 0x530;
    private const int SaveDataParamTitleOffset = 0x00;
    private const int SaveDataParamSubTitleOffset = 0x80;
    private const int SaveDataParamDetailOffset = 0x100;
    private const int SaveDataParamUserParamOffset = 0x500;
    private const int SaveDataParamMtimeOffset = 0x508;
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
    private const int MaxMountPoints = 16;
    private static readonly object _stateGate = new();
    private static readonly object _memoryGate = new();
    private static readonly Dictionary<int, string> _preparedTransactionResources = [];
    private static readonly Dictionary<string, string> _mountedSavePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly StringComparison _hostPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static string? _titleId;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (_stateGate)
        {
            foreach (var mountPoint in _mountedSavePaths.Keys)
            {
                KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            }

            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId.Trim());
            _preparedTransactionResources.Clear();
            _mountedSavePaths.Clear();
            _nextTransactionResource = 0;
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
            !TryReadInt32(ctx, mountAddress + 0x28, out var resource) ||
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
            lock (_stateGate)
            {
                var titleId = ResolveConfiguredTitleId();
                if (!TryResolveSavePath(userId, titleId, dirName, out var savePath))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorParameter);
                }

                if (_mountedSavePaths.Values.Any(
                        mountedPath => string.Equals(mountedPath, savePath, _hostPathComparison)))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorBusy);
                }

                string? mountPoint = null;
                for (var index = 0; index < MaxMountPoints; index++)
                {
                    var candidate = $"/savedata{index}";
                    if (!_mountedSavePaths.ContainsKey(candidate))
                    {
                        mountPoint = candidate;
                        break;
                    }
                }

                if (mountPoint is null)
                {
                    return SetReturn(ctx, OrbisSaveDataErrorMountFull);
                }

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

                var created = false;
                if (!existed)
                {
                    Directory.CreateDirectory(savePath);
                    created = true;
                }

                Span<byte> result = stackalloc byte[MountResultSize];
                result.Clear();
                WriteAscii(result[..16], mountPoint);
                BinaryPrimitives.WriteUInt32LittleEndian(result[0x1C..], created ? 1u : 0u);
                if (!ctx.Memory.TryWrite(resultAddress, result))
                {
                    if (created)
                    {
                        Directory.Delete(savePath, recursive: true);
                    }

                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, savePath);
                _mountedSavePaths[mountPoint] = savePath;

                TraceSaveData(
                    $"mount3 user={userId} title={titleId} dir={dirName} blocks={blocks} " +
                    $"system_blocks={systemBlocks} mount_mode=0x{mountMode:X} resource={resource} " +
                    $"mount_point={mountPoint} created={!existed} root='{savePath}'");
                return SetReturn(ctx, 0);
            }
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

    [SysAbiExport(
        Nid = "85zul--eGXs",
        ExportName = "sceSaveDataSetParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataSetParam(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var paramType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var paramAddress = ctx[CpuRegister.Rdx];
        var paramSize = ctx[CpuRegister.Rcx];
        if (mountPointAddress == 0 || paramAddress == 0 || paramType > 4)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var fieldCapacity = paramType switch
        {
            0 => SaveDataParamSize,
            1 or 2 => 128,
            3 => 1024,
            4 => sizeof(uint),
            _ => 0,
        };
        var inputSize = paramType is 1 or 2 or 3
            ? checked((int)Math.Min(paramSize, (ulong)fieldCapacity))
            : fieldCapacity;
        if (inputSize == 0 ||
            (paramType is 0 or 4 && paramSize < (ulong)fieldCapacity))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        try
        {
            lock (_stateGate)
            {
                if (!_mountedSavePaths.TryGetValue(mountPoint, out var savePath))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorNotMounted);
                }

                var input = new byte[inputSize];
                if (!ctx.Memory.TryRead(paramAddress, input))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (paramType is 1 or 2 or 3 && !input.Contains((byte)0))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorParameter);
                }

                var loadStatus = TryLoadSavedParam(savePath, out var param);
                if (loadStatus is SavedParamLoadStatus.Invalid or SavedParamLoadStatus.Error)
                {
                    return SetReturn(ctx, OrbisSaveDataErrorInternal);
                }

                param ??= CreateDefaultParam(savePath);
                switch (paramType)
                {
                    case 0:
                        input.AsSpan(SaveDataParamTitleOffset, 128)
                            .CopyTo(param.AsSpan(SaveDataParamTitleOffset, 128));
                        input.AsSpan(SaveDataParamSubTitleOffset, 128)
                            .CopyTo(param.AsSpan(SaveDataParamSubTitleOffset, 128));
                        input.AsSpan(SaveDataParamDetailOffset, 1024)
                            .CopyTo(param.AsSpan(SaveDataParamDetailOffset, 1024));
                        input.AsSpan(SaveDataParamUserParamOffset, sizeof(uint))
                            .CopyTo(param.AsSpan(SaveDataParamUserParamOffset, sizeof(uint)));
                        param.AsSpan(0x504, sizeof(uint)).Clear();
                        param.AsSpan(0x510).Clear();
                        break;
                    case 1:
                        ReplaceParamField(param, SaveDataParamTitleOffset, 128, input);
                        break;
                    case 2:
                        ReplaceParamField(param, SaveDataParamSubTitleOffset, 128, input);
                        break;
                    case 3:
                        ReplaceParamField(param, SaveDataParamDetailOffset, 1024, input);
                        break;
                    case 4:
                        input.CopyTo(param, SaveDataParamUserParamOffset);
                        break;
                }

                SetSavedParamMtime(param, DateTime.UtcNow);
                WriteSavedParam(savePath, param);
                TraceSaveData(
                    $"set_param mount_point={mountPoint} type={paramType} size={inputSize} root='{savePath}'");
                return SetReturn(ctx, 0);
            }
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

    private static int _nextTransactionResource;
    [SysAbiExport(
        Nid = "gjRZNnw0JPE",
        ExportName = "sceSaveDataCreateTransactionResource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataCreateTransactionResource(CpuContext ctx)
    {
        var memorySize = ctx[CpuRegister.Rdi];
        int resource;
        lock (_stateGate)
        {
            resource = ++_nextTransactionResource;
        }

        TraceSaveData(
            $"create_transaction_resource memory_size=0x{memorySize:X} resource={resource}");

        return SetReturn(ctx, resource);
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
        var mode = unchecked((uint)ctx[CpuRegister.Rdi]);
        // Async backup completion events are not modeled by this focused
        // persistence layer; retain the mode for diagnostics without guessing
        // an event-queue contract.
        var mountPointAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        try
        {
            lock (_stateGate)
            {
                if (!_mountedSavePaths.TryGetValue(mountPoint, out var savePath))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorNotMounted);
                }

                if (!TryRefreshSavedParamMtime(savePath))
                {
                    return SetReturn(ctx, OrbisSaveDataErrorInternal);
                }

                var preparedResources = _preparedTransactionResources
                    .Where(entry => string.Equals(entry.Value, savePath, _hostPathComparison))
                    .Select(entry => entry.Key)
                    .ToArray();
                foreach (var resource in preparedResources)
                {
                    _preparedTransactionResources.Remove(resource);
                }
                _mountedSavePaths.Remove(mountPoint);
                KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            }
        }
        catch (IOException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return SetReturn(ctx, OrbisSaveDataErrorInternal);
        }

        TraceSaveData($"umount2 mode=0x{mode:X} mount_point={mountPoint}");
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
            entries.Add(new SaveEntry(name, directory, ResolveSaveMtimeUtc(directory, info.LastWriteTimeUtc)));
        }

        return entries;
    }

    private static List<SaveEntry> SortEntries(List<SaveEntry> entries, uint sortKey, uint sortOrder)
    {
        IOrderedEnumerable<SaveEntry> sorted = sortKey switch
        {
            1 => entries.OrderBy(entry => ResolveSaveUserParam(entry.Path)),
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
        var param = LoadSavedParam(entry.Path);
        if (param is null)
        {
            param = CreateDefaultParam(entry.Path);
            SetSavedParamMtime(param, entry.LastWriteUtc);
        }

        return ctx.Memory.TryWrite(address, param);
    }

    private static byte[]? LoadSavedParam(string savePath)
    {
        return TryLoadSavedParam(savePath, out var param) == SavedParamLoadStatus.Loaded
            ? param
            : null;
    }

    private static SavedParamLoadStatus TryLoadSavedParam(string savePath, out byte[]? param)
    {
        param = null;
        var metadataPath = ResolveParamMetadataPath(savePath);
        if (!File.Exists(metadataPath))
        {
            return SavedParamLoadStatus.Missing;
        }

        try
        {
            var loaded = File.ReadAllBytes(metadataPath);
            if (loaded.Length != SaveDataParamSize)
            {
                return SavedParamLoadStatus.Invalid;
            }

            param = loaded;
            return SavedParamLoadStatus.Loaded;
        }
        catch (IOException)
        {
            return SavedParamLoadStatus.Error;
        }
        catch (UnauthorizedAccessException)
        {
            return SavedParamLoadStatus.Error;
        }
    }

    private static byte[] CreateDefaultParam(string savePath)
    {
        var param = new byte[SaveDataParamSize];
        WriteAscii(param.AsSpan(SaveDataParamTitleOffset, 128), "Saved Data");
        var mtime = Directory.Exists(savePath)
            ? Directory.GetLastWriteTimeUtc(savePath)
            : DateTime.UtcNow;
        BinaryPrimitives.WriteInt64LittleEndian(
            param.AsSpan(SaveDataParamMtimeOffset, sizeof(long)),
            new DateTimeOffset(mtime).ToUnixTimeSeconds());
        return param;
    }

    private static string ResolveParamMetadataPath(string savePath)
    {
        var titleRoot = Directory.GetParent(savePath)?.FullName ?? ResolveSaveDataRoot();
        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(savePath));
        return Path.Combine(titleRoot, "sce_params", $"{dirName}.bin");
    }

    private static void WriteSavedParam(string savePath, byte[] param)
    {
        var metadataPath = ResolveParamMetadataPath(savePath);
        var metadataRoot = Path.GetDirectoryName(metadataPath)!;
        Directory.CreateDirectory(metadataRoot);
        var temporaryPath = Path.Combine(
            metadataRoot,
            $".{Path.GetFileName(metadataPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, param);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool TryRefreshSavedParamMtime(string savePath)
    {
        var loadStatus = TryLoadSavedParam(savePath, out var param);
        if (loadStatus is SavedParamLoadStatus.Invalid or SavedParamLoadStatus.Error)
        {
            return false;
        }

        param ??= CreateDefaultParam(savePath);
        SetSavedParamMtime(param, DateTime.UtcNow);
        WriteSavedParam(savePath, param);
        return true;
    }

    private static void SetSavedParamMtime(byte[] param, DateTime mtimeUtc)
    {
        BinaryPrimitives.WriteInt64LittleEndian(
            param.AsSpan(SaveDataParamMtimeOffset, sizeof(long)),
            new DateTimeOffset(mtimeUtc).ToUnixTimeSeconds());
    }

    private static DateTime ResolveSaveMtimeUtc(string savePath, DateTime fallback)
    {
        var param = LoadSavedParam(savePath);
        if (param is null)
        {
            return fallback;
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(
            param.AsSpan(SaveDataParamMtimeOffset, sizeof(long)));
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    private static int ResolveSaveUserParam(string savePath)
    {
        var param = LoadSavedParam(savePath);
        return param is null
            ? 0
            : BinaryPrimitives.ReadInt32LittleEndian(
                param.AsSpan(SaveDataParamUserParamOffset, sizeof(int)));
    }

    private static void ReplaceParamField(byte[] param, int offset, int length, byte[] value)
    {
        var field = param.AsSpan(offset, length);
        field.Clear();
        value.AsSpan(0, Math.Min(value.Length, field.Length)).CopyTo(field);
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

    private static string ResolveTitleSaveRoot(int userId, string titleId) =>
        Path.GetFullPath(Path.Combine(ResolveSaveDataRoot(), userId.ToString(), SanitizePathSegment(titleId)));

    private static bool TryResolveSavePath(int userId, string titleId, string dirName, out string savePath)
    {
        savePath = string.Empty;
        if (!IsGuestPathSegment(titleId) || !IsGuestPathSegment(dirName))
        {
            return false;
        }

        var titleRoot = ResolveTitleSaveRoot(userId, titleId);
        var candidate = Path.GetFullPath(Path.Combine(titleRoot, SanitizePathSegment(dirName)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(titleRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, _hostPathComparison))
        {
            return false;
        }

        savePath = candidate;
        return true;
    }

    private static bool IsGuestPathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, ".", StringComparison.Ordinal) &&
        !string.Equals(value, "..", StringComparison.Ordinal) &&
        !value.Contains('/') &&
        !value.Contains('\\');

    private static string ResolveSaveDataMemoryPath(int userId) =>
        Path.Combine(ResolveTitleSaveRoot(userId, ResolveConfiguredTitleId()), "sce_sdmemory", "memory.dat");

    private static bool TryReadMemoryData(
        CpuContext ctx, ulong address, out ulong buffer, out ulong size, out ulong offset)
    {
        size = 0;
        offset = 0;
        return ctx.TryReadUInt64(address, out buffer) &&
            ctx.TryReadUInt64(address + 0x08, out size) &&
            ctx.TryReadUInt64(address + 0x10, out offset);
    }

    private static string ResolveSaveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "user", "savedata")
            : configured;
        return Path.GetFullPath(root);
    }

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

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "default" : sanitized;
    }

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

    private enum SavedParamLoadStatus
    {
        Missing,
        Loaded,
        Invalid,
        Error,
    }

    [SysAbiExport(
        Nid = "sDCBrmc61XU",
        ExportName = "sceSaveDataPrepare",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveData")]
    public static int SaveDataPrepare(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0 || paramAddress == 0)
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        if (!TryReadFixedAscii(ctx, mountPointAddress, 16, out var mountPoint) ||
            !TryReadInt32(ctx, paramAddress, out var resource) ||
            !TryReadUInt32(ctx, paramAddress + sizeof(int), out var prepareMode))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return ctx.SetReturn(OrbisSaveDataErrorParameter);
        }

        lock (_stateGate)
        {
            if (!_mountedSavePaths.TryGetValue(mountPoint, out var savePath))
            {
                return ctx.SetReturn(OrbisSaveDataErrorNotMounted);
            }

            _preparedTransactionResources[resource] = savePath;
        }

        TraceSaveData(
            $"prepare mount_point={mountPoint} resource={resource} prepare_mode=0x{prepareMode:X}");
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

        if (!TryReadInt32(ctx, commitAddress, out var resource) ||
            !TryReadUInt32(ctx, commitAddress + sizeof(int), out var commitMode))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Async backup completion events are deliberately deferred until the
        // save-data event lifecycle is implemented and observed independently.

        var committed = false;
        try
        {
            lock (_stateGate)
            {
                if (_preparedTransactionResources.TryGetValue(resource, out var savePath))
                {
                    if (!TryRefreshSavedParamMtime(savePath))
                    {
                        return ctx.SetReturn(OrbisSaveDataErrorInternal);
                    }

                    _preparedTransactionResources.Remove(resource);
                    committed = true;
                }
            }
        }
        catch (IOException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }
        catch (UnauthorizedAccessException)
        {
            return ctx.SetReturn(OrbisSaveDataErrorInternal);
        }

        TraceSaveData(
            $"commit resource={resource} commit_mode=0x{commitMode:X} " +
            $"prepared={(committed ? 1 : 0)} commit=0x{commitAddress:X16}");
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

        return ctx.SetReturn(
            File.Exists(ResolveSaveDataMemoryPath(userId)) ? 0 : OrbisSaveDataErrorMemoryNotReady);
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
