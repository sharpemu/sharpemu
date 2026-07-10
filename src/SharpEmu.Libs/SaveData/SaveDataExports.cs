// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataExports
{
    private const int OrbisSaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int OrbisSaveDataErrorExists = unchecked((int)0x809F0007);
    private const int OrbisSaveDataErrorNotFound = unchecked((int)0x809F0008);
    private const int OrbisSaveDataErrorInternal = unchecked((int)0x809F000B);
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
    private static readonly object _stateGate = new();
    private static string? _titleId;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (_stateGate)
        {
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizePathSegment(titleId.Trim());
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
            if (!ctx.TryWriteUInt32(resultAddress + ResultHitNumOffset, checked((uint)entries.Count)) ||
                !ctx.TryWriteUInt32(resultAddress + ResultSetNumOffset, checked((uint)setNum)))
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
            !ctx.TryReadUInt32(mountAddress + 0x20, out var mountMode) ||
            !ctx.TryReadUInt32(mountAddress + 0x24, out var resource) ||
            !ctx.TryReadUInt32(mountAddress + 0x28, out var mode) ||
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
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var reserved = ctx[CpuRegister.Rsi];
        var resourceAddress = ctx[CpuRegister.Rdx];

        if (resourceAddress == 0)
        {
            return SetReturn(ctx, OrbisSaveDataErrorParameter);
        }

        var id = (uint)Interlocked.Increment(ref _nextTransactionResource);

        if (!ctx.TryWriteUInt32(resourceAddress, id))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceSaveData(
            $"create_transaction_resource user={userId} reserved=0x{reserved:X} resource_addr=0x{resourceAddress:X} id={id}");

        return SetReturn(ctx, 0);
    }

    private static bool TryReadSearchCond(CpuContext ctx, ulong address, out SearchCond cond)
    {
        cond = default;
        if (!TryReadInt32(ctx, address, out var userId) ||
            !ctx.TryReadUInt64(address + 0x08, out var titleIdAddress) ||
            !ctx.TryReadUInt64(address + 0x10, out var dirNameAddress) ||
            !ctx.TryReadUInt32(address + 0x18, out var sortKey) ||
            !ctx.TryReadUInt32(address + 0x1C, out var sortOrder))
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
            !ctx.TryReadUInt32(address + ResultDirNamesNumOffset, out var dirNamesNum) ||
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
        var param = new byte[SaveDataParamSize];
        WriteAscii(param.AsSpan(0x00, 128), "Saved Data");
        WriteAscii(param.AsSpan(0x100, 1024), entry.Name);
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

    private static string ResolveTitleSaveRoot(int userId, string titleId) =>
        Path.Combine(ResolveSaveDataRoot(), userId.ToString(), SanitizePathSegment(titleId));

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
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
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
}
