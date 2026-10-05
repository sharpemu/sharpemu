// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpCommerceExports
{
    private const ulong CommonDialogBaseSize = 0x30;
    private const int ParameterSize = 0x80;
    private const int ParameterSizeOpen2 = 0x88;
    private const int ResultSize = 0x30;
    private const int MaximumTargets = 10;
    private const int MaximumTargetBytes = 128;

    [SysAbiExport(
        Nid = "0aR2aWmQal4",
        ExportName = "sceNpCommerceDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogInitialize(CpuContext ctx) =>
        SetResult(ctx, NpCommerceDialogState.Initialize());

    [SysAbiExport(
        Nid = "m-I92Ab50W8",
        ExportName = "sceNpCommerceDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogTerminate(CpuContext ctx) =>
        SetResult(ctx, NpCommerceDialogState.Terminate());

    [SysAbiExport(
        Nid = "DfSCDRA3EjY",
        ExportName = "sceNpCommerceDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogOpen(CpuContext ctx) => Open(ctx, isOpen2: false);

    [SysAbiExport(
        Nid = "IXmfUaze9So",
        ExportName = "sceNpCommerceDialogOpen2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogOpen2(CpuContext ctx) => Open(ctx, isOpen2: true);

    [SysAbiExport(
        Nid = "LR5cwFMMCVE",
        ExportName = "sceNpCommerceDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogUpdateStatus(CpuContext ctx) =>
        SetResult(ctx, NpCommerceDialogState.UpdateStatus());

    [SysAbiExport(
        Nid = "r42bWcQbtZY",
        ExportName = "sceNpCommerceDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommerce")]
    public static int NpCommerceDialogGetResult(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return SetResult(ctx, NpCommerceDialogState.ErrorArgumentNull);
        }

        var stateResult = NpCommerceDialogState.TryGetResult(out var userData);
        if (stateResult != NpCommerceDialogState.ErrorOk)
        {
            return SetResult(ctx, stateResult);
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result, NpCommerceDialogState.ResultUserCanceled);
        result[0x04] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], userData);
        return ctx.Memory.TryWrite(outputAddress, result)
            ? SetResult(ctx, NpCommerceDialogState.ErrorOk)
            : SetResult(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int Open(CpuContext ctx, bool isOpen2)
    {
        var status = NpCommerceDialogState.Status;
        if (status == NpCommerceDialogStatus.None)
        {
            return SetResult(ctx, NpCommerceDialogState.ErrorNotInitialized);
        }

        if (status == NpCommerceDialogStatus.Running)
        {
            return SetResult(ctx, NpCommerceDialogState.ErrorBusy);
        }

        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return SetResult(ctx, NpCommerceDialogState.ErrorArgumentNull);
        }

        if (!TryReadRequest(ctx, parameterAddress, isOpen2, out var request, out var error))
        {
            return SetResult(ctx, error);
        }

        return SetResult(ctx, NpCommerceDialogState.Open(request));
    }

    private static bool TryReadRequest(
        CpuContext ctx,
        ulong address,
        bool isOpen2,
        out NpCommerceDialogRequest request,
        out int error)
    {
        request = null!;
        error = 0;
        if (!ctx.TryReadUInt64(address, out var baseSize) ||
            !ctx.TryReadInt32(address + 0x30, out var declaredSize) ||
            !ctx.TryReadInt32(address + 0x34, out var userId) ||
            !ctx.TryReadInt32(address + 0x38, out var mode) ||
            !ctx.TryReadUInt32(address + 0x3C, out var serviceLabel) ||
            !ctx.TryReadUInt64(address + 0x40, out var targetArrayAddress) ||
            !ctx.TryReadUInt32(address + 0x48, out var targetCount) ||
            !ctx.TryReadUInt64(address + 0x50, out var features) ||
            !ctx.TryReadUInt64(address + 0x58, out var userData))
        {
            error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        var requiredSize = isOpen2 ? ParameterSizeOpen2 : ParameterSize;
        if (baseSize < CommonDialogBaseSize || declaredSize < requiredSize)
        {
            error = NpCommerceDialogState.ErrorParameterInvalid;
            return false;
        }

        var count = (int)Math.Min(targetCount, MaximumTargets);
        var targets = new List<string>(count);
        if (count > 0 && targetArrayAddress != 0)
        {
            for (var index = 0; index < count; index++)
            {
                if (!ctx.TryReadUInt64(targetArrayAddress + checked((ulong)(index * sizeof(ulong))), out var targetAddress))
                {
                    error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    return false;
                }

                if (targetAddress == 0)
                {
                    continue;
                }

                if (!ctx.TryReadNullTerminatedUtf8(targetAddress, MaximumTargetBytes, out var target))
                {
                    error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    return false;
                }

                targets.Add(target);
            }
        }

        request = new NpCommerceDialogRequest(
            isOpen2,
            userId,
            mode,
            serviceLabel,
            mode == 5 ? features : 0,
            userData,
            targets);
        return true;
    }

    private static int SetResult(CpuContext ctx, int result) => ctx.SetReturn(result, typeof(long));
}
