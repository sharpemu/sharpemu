// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpEntitlementAccessExports
{
    private const int BootParamClearSize = 0x20;
    private const int EntitlementLabelSize = 17;
    private const int EntitlementLabelPadding = 3;
    private const int AddcontEntitlementInfoSize =
        EntitlementLabelSize + EntitlementLabelPadding + sizeof(uint) + sizeof(uint);

    // libSceNpEntitlementAccess package / download codes observed on Prospero.
    // package_type 3 = PSAL (license-style add-on); download_status 4 = INSTALLED.
    private const uint PackageTypePsal = 3;
    private const uint DownloadStatusInstalled = 4;
    private const uint SkuFlagFull = 3;

    private const int NpEntitlementAccessErrorParameter = unchecked((int)0x817D0002);
    private const int NpEntitlementAccessErrorNoEntitlement = unchecked((int)0x817D0007);
    private const int NpEntitlementAccessErrorSignedOut = unchecked((int)0x817D0014);
    private const int NpEntitlementAccessErrorRequestNotFound = unchecked((int)0x817D0015);
    private const int NpEntitlementAccessPollFinished = 0;
    private const int EntitlementKeySize = 16;

    // Asynchronous requests by id, with the result each one finished with.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> Requests = new();
    private static long _nextRequestId;

    // Offline entitlements queried by titles through NpEntitlementAccess.
    // GTA V Enhanced (PPSA04264) gates Story Mode on these three labels; without
    // them the frontend offers "Buy GTAV Story Mode" despite a full dump.
    // Ghost of Yotei (PPSA26344) separately checks its application and base
    // game entitlements before it creates the frontend.  These represent the
    // locally installed main package only; optional editions and add-ons remain
    // unowned.
    private static readonly AddcontEntitlement[] OwnedAddcontEntitlements =
    [
        new("85y-je", PackageTypePsal, DownloadStatusInstalled),
        new("5d5c48", PackageTypePsal, DownloadStatusInstalled),
        new("_mtqu6", PackageTypePsal, DownloadStatusInstalled),
        new("GHOST2APP0000000", PackageTypePsal, DownloadStatusInstalled),
        new("GHOST2BASE000000", PackageTypePsal, DownloadStatusInstalled),
    ];

    private readonly record struct AddcontEntitlement(
        string Label,
        uint PackageType,
        uint DownloadStatus);

    [SysAbiExport(
        Nid = "jO8DM8oyego",
        ExportName = "sceNpEntitlementAccessInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessInitialize(CpuContext ctx)
    {
        var initParam = ctx[CpuRegister.Rdi];
        var bootParam = ctx[CpuRegister.Rsi];
        if (initParam == 0 || bootParam == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> clear = stackalloc byte[BootParamClearSize];
        clear.Clear();
        if (!ctx.Memory.TryWrite(bootParam, clear))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNpEntitlementAccess($"initialize init=0x{initParam:X16} boot=0x{bootParam:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "lPDO62PpJIA",
        ExportName = "sceNpEntitlementAccessGetSkuFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetSkuFlag(CpuContext ctx)
    {
        var skuFlagAddress = ctx[CpuRegister.Rdi];
        if (skuFlagAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> skuFlagBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(skuFlagBytes, SkuFlagFull);
        if (!ctx.Memory.TryWrite(skuFlagAddress, skuFlagBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNpEntitlementAccess($"get_sku_flag -> {SkuFlagFull}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "y7JD1gfwyfA",
        ExportName = "sceNpEntitlementAccessGetGameTrialsFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetGameTrialsFlag(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> flagBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(flagBytes, 0);
        return ctx.Memory.TryWrite(flagAddress, flagBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "TFyU+KFBv54",
        ExportName = "sceNpEntitlementAccessGetAddcontEntitlementInfoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetAddcontEntitlementInfoList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rsi];
        var listNum = (uint)ctx[CpuRegister.Rdx];
        var hitNumAddress = ctx[CpuRegister.Rcx];

        if (hitNumAddress == 0 || (listAddress == 0 && listNum != 0))
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        var hitNum = (uint)OwnedAddcontEntitlements.Length;
        Span<byte> hitNumBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(hitNumBytes, hitNum);
        if (!ctx.Memory.TryWrite(hitNumAddress, hitNumBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (listAddress != 0 && listNum != 0)
        {
            var clearBytes = checked((int)listNum * AddcontEntitlementInfoSize);
            Span<byte> clear = clearBytes <= 512
                ? stackalloc byte[clearBytes]
                : new byte[clearBytes];
            clear.Clear();
            if (!ctx.Memory.TryWrite(listAddress, clear))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var copyNum = Math.Min(listNum, hitNum);
            for (var index = 0u; index < copyNum; index++)
            {
                if (!TryWriteAddcontEntitlementInfo(
                        ctx,
                        listAddress + index * (ulong)AddcontEntitlementInfoSize,
                        OwnedAddcontEntitlements[(int)index]))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        TraceNpEntitlementAccess(
            $"get_addcont_info_list service={ctx[CpuRegister.Rdi]} list=0x{listAddress:X16} " +
            $"list_num={listNum} hit_num={hitNum}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "xddD23+8TfQ",
        ExportName = "sceNpEntitlementAccessGetAddcontEntitlementInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetAddcontEntitlementInfo(CpuContext ctx)
    {
        var labelAddress = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        if (labelAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> labelBytes = stackalloc byte[EntitlementLabelSize];
        if (!ctx.Memory.TryRead(labelAddress, labelBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var label = ReadEntitlementLabel(labelBytes);
        Span<byte> info = stackalloc byte[AddcontEntitlementInfoSize];
        info.Clear();
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        foreach (var entitlement in OwnedAddcontEntitlements)
        {
            if (!string.Equals(label, entitlement.Label, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryWriteAddcontEntitlementInfo(ctx, infoAddress, entitlement))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceNpEntitlementAccess(
                $"get_addcont_info service={ctx[CpuRegister.Rdi]} label='{label}' -> owned");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        TraceNpEntitlementAccess(
            $"get_addcont_info service={ctx[CpuRegister.Rdi]} label='{label}' -> no entitlement");
        return ctx.SetReturn(NpEntitlementAccessErrorNoEntitlement);
    }

    [SysAbiExport(
        Nid = "5LiMEPuW0DQ",
        ExportName = "sceNpEntitlementAccessGetEntitlementKey",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetEntitlementKey(CpuContext ctx)
    {
        var labelAddress = ctx[CpuRegister.Rsi];
        var keyAddress = ctx[CpuRegister.Rdx];
        if (labelAddress == 0 || keyAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        Span<byte> labelBytes = stackalloc byte[EntitlementLabelSize];
        if (!ctx.Memory.TryRead(labelAddress, labelBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var label = ReadEntitlementLabel(labelBytes);
        Span<byte> key = stackalloc byte[EntitlementKeySize];
        key.Clear();
        if (!ctx.Memory.TryWrite(keyAddress, key))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OwnedAddcontEntitlements.Any(entitlement =>
            string.Equals(label, entitlement.Label, StringComparison.Ordinal))
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : NpEntitlementAccessErrorNoEntitlement);
    }

    // Unified entitlements are kept on the PSN servers. The user is signed out, as in libSceNpManager, so a request
    // finishes at once with that result and no entitlements.
    [SysAbiExport(
        Nid = "uCZf2L27th8",
        ExportName = "sceNpEntitlementAccessRequestUnifiedEntitlementInfoList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessRequestUnifiedEntitlementInfoList(CpuContext ctx)
    {
        // userId, serviceLabel, label list (null for every label), list size, parameters, request id out.
        var listAddress = ctx[CpuRegister.Rdx];
        var listNum = unchecked((uint)ctx[CpuRegister.Rcx]);
        var requestIdAddress = ctx[CpuRegister.R9];
        if (requestIdAddress == 0 || (listAddress == 0 && listNum != 0))
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        if (!ctx.TryWriteUInt64(requestIdAddress, unchecked((ulong)requestId)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Requests[requestId] = NpEntitlementAccessErrorSignedOut;
        TraceNpEntitlementAccess($"request_unified_list id={requestId} labels={listNum}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "nAEqawEZG5s",
        ExportName = "sceNpEntitlementAccessPollUnifiedEntitlementInfoList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessPollUnifiedEntitlementInfoList(CpuContext ctx)
    {
        // requestId, result out, info list, list size, hit count out, next offset out, previous offset out (stack).
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        var hitNumAddress = ctx[CpuRegister.R8];
        var nextOffsetAddress = ctx[CpuRegister.R9];
        if (!Requests.TryGetValue(requestId, out var result))
        {
            return ctx.SetReturn(NpEntitlementAccessErrorRequestNotFound);
        }

        if (resultAddress == 0)
        {
            return ctx.SetReturn(NpEntitlementAccessErrorParameter);
        }

        _ = ctx.TryGetImportStackArgument(0, out var previousOffsetAddress);
        if (!ctx.TryWriteInt32(resultAddress, result) ||
            (hitNumAddress != 0 && !ctx.TryWriteUInt32(hitNumAddress, 0)) ||
            (nextOffsetAddress != 0 && !ctx.TryWriteInt32(nextOffsetAddress, 0)) ||
            (previousOffsetAddress != 0 && !ctx.TryWriteInt32(previousOffsetAddress, 0)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(NpEntitlementAccessPollFinished);
    }

    [SysAbiExport(
        Nid = "Z0eQj8m7XA8",
        ExportName = "sceNpEntitlementAccessDeleteRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessDeleteRequest(CpuContext ctx) =>
        ctx.SetReturn(Requests.TryRemove(unchecked((long)ctx[CpuRegister.Rdi]), out _)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : NpEntitlementAccessErrorRequestNotFound);

    // Every request has finished by the time the title can abort it.
    [SysAbiExport(
        Nid = "HFcQl9TMcFQ",
        ExportName = "sceNpEntitlementAccessAbortRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessAbortRequest(CpuContext ctx) =>
        ctx.SetReturn(Requests.ContainsKey(unchecked((long)ctx[CpuRegister.Rdi]))
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : NpEntitlementAccessErrorRequestNotFound);

    private static bool TryWriteAddcontEntitlementInfo(
        CpuContext ctx,
        ulong address,
        AddcontEntitlement entitlement)
    {
        Span<byte> info = stackalloc byte[AddcontEntitlementInfoSize];
        info.Clear();

        var labelBytes = Encoding.ASCII.GetBytes(entitlement.Label);
        var labelLength = Math.Min(labelBytes.Length, EntitlementLabelSize - 1);
        labelBytes.AsSpan(0, labelLength).CopyTo(info);

        BinaryPrimitives.WriteUInt32LittleEndian(
            info.Slice(EntitlementLabelSize + EntitlementLabelPadding, sizeof(uint)),
            entitlement.PackageType);
        BinaryPrimitives.WriteUInt32LittleEndian(
            info.Slice(
                EntitlementLabelSize + EntitlementLabelPadding + sizeof(uint),
                sizeof(uint)),
            entitlement.DownloadStatus);

        return ctx.Memory.TryWrite(address, info);
    }

    private static string ReadEntitlementLabel(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNpEntitlementAccess(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.entitlement.{message}");
    }
}
