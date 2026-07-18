// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidContext = unchecked((int)0x80930002);
    private const int OrbisAjmErrorInvalidInstance = unchecked((int)0x80930003);
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private const int OrbisAjmErrorOutOfResources = unchecked((int)0x80930007);
    private const int OrbisAjmErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int OrbisAjmErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int OrbisAjmErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const uint MaxCodecType = 23;
    private const int MaxInstanceIndex = 0x2FFF;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static int _nextContextId;

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, uint> InstancesBySlot { get; } = new();

        public int NextInstanceIndex { get; set; }
    }

    public static int AjmInitialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (reserved != 0 || outputAddress == 0)
        {
            return unchecked((int)0x806A0001);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return unchecked((int)0x806A0001);
        }

        Contexts[contextId] = new AjmContextState();
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.initialize reserved={reserved} out=0x{outputAddress:X16} context={contextId}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (codecType >= MaxCodecType || reserved != 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Add(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecAlreadyRegistered);
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.module_register context={contextId} codec={codecType} reserved={reserved}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceCreate(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorWrongRevisionFlag);
        }

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << 14) | instanceSlot;
            Span<byte> value = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
            if (!ctx.Memory.TryWrite(outputAddress, value))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            state.NextInstanceIndex = nextInstanceIndex;
            state.InstancesBySlot.Add(instanceSlot, instanceId);
        }

        Trace($"instance_create context={contextId} codec={codecType} flags=0x{flags:X} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        var instanceSlot = instanceId & 0x3FFF;
        lock (state.Gate)
        {
            if (instanceSlot == 0 || !state.InstancesBySlot.Remove(instanceSlot))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidInstance);
            }
        }

        Trace($"instance_destroy context={contextId} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // The caller owns and initializes the batch storage. This API resets
        // its submission cursor on hardware; FMOD does not consume a return
        // value or an additional output object here.
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }
}
