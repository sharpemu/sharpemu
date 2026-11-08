// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode.Loader;
using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Libraries.AppContent;

public static class GnmAppContentExports
{
    private const int ParameterError = unchecked((int)0x80D90002);
    private const uint SkuFlagParam = 0;
    private const int SkuFlagFull = 3;
    private static int[] _userDefinedParams = new int[GnmParamSfo.UserDefinedParamCount];

    internal static void SetUserDefinedParams(int[] values) => Volatile.Write(ref _userDefinedParams, values);

    public static int AppParamGetInt(CpuContext context)
    {
        var parameter = (uint)context[CpuRegister.Rdi];
        var valueAddress = context[CpuRegister.Rsi];
        if (valueAddress == 0)
            return context.SetReturn(ParameterError);

        int value;
        if (parameter == SkuFlagParam)
            value = SkuFlagFull;
        else if (parameter is >= 1 and <= GnmParamSfo.UserDefinedParamCount)
            value = Volatile.Read(ref _userDefinedParams)[parameter - 1];
        else
            return context.SetReturn(ParameterError);

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return context.SetReturn(context.Memory.TryWrite(valueAddress, bytes)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
