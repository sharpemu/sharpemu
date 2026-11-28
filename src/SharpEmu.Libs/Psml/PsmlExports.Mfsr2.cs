// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Psml;

public static partial class PsmlExports
{
    private const int Mfsr2NotInitialized = unchecked((int)0x8A8A0001);
    private const int Mfsr2AlreadyInitialized = unchecked((int)0x8A8A0002);
    private const int Mfsr2InvalidArgument = unchecked((int)0x8A8A0005);
    private const int Mfsr2NullOutput = unchecked((int)0x8A8A0009);
    private const int Mfsr2UnsupportedProfile = unchecked((int)0x8A8A000D);
    private const int Mfsr2UnsupportedConsole = unchecked((int)0x8A8A0012);
    private const int Mfsr2SharedResourcesBusy = unchecked((int)0x8A8A0015);
    private const ulong Mfsr2BlockBytes = 0x200000;
    private const int Mfsr2PacketDwords = 0x800;

    private static readonly Dictionary<ulong, ulong> Mfsr2SharedResources = new();
    private static int _mfsr2Initialized;

    private static bool Mfsr2Initialized => ConsoleHardware.IsPro && Volatile.Read(ref _mfsr2Initialized) != 0;

    [SysAbiExport(Nid = "o+NM86gEwFE",
        ExportName = "scePsmlMfsr2Init",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2Init(CpuContext ctx)
    {
        if (!ConsoleHardware.IsPro)
        {
            return ctx.SetReturn(Mfsr2UnsupportedConsole);
        }

        if (Interlocked.Exchange(ref _mfsr2Initialized, 1) != 0)
        {
            return ctx.SetReturn(Mfsr2AlreadyInitialized);
        }

        Trace("mfsr2.init");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "1ic5q-kdOsc",
        ExportName = "scePsmlMfsr2GetSharedResourcesInitRequirement",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2GetSharedResourcesInitRequirement(CpuContext ctx)
    {
        Span<byte> parameters = stackalloc byte[0x20];
        var error = ReadMfsr2CreationParameters(ctx, parameters);
        return error == 0 ? WriteMfsr2Requirement(ctx) : ctx.SetReturn(error);
    }

    [SysAbiExport(Nid = "gMduXCLYrNg",
        ExportName = "scePsmlMfsr2CreateSharedResources",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2CreateSharedResources(CpuContext ctx)
    {
        Span<byte> parameters = stackalloc byte[0x20];
        var error = ReadMfsr2CreationParameters(ctx, parameters);
        if (error != 0 || (error = ReadMfsr2Block(ctx, parameters[8..], out var block)) != 0)
        {
            return ctx.SetReturn(error);
        }

        ulong handle;
        lock (Gate)
        {
            if (IsMfsr2BlockInUse(block))
            {
                return ctx.SetReturn(Mfsr2SharedResourcesBusy);
            }

            if (!TryCreateHandle(ctx, 0, out handle) || !ctx.TryWriteUInt64(ctx[CpuRegister.Rdi], handle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            Mfsr2SharedResources.Add(handle, block);
        }

        Trace($"mfsr2.create_shared_resources handle=0x{handle:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "0GAw7SmkwII",
        ExportName = "scePsmlMfsr2ReleaseSharedResources",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2ReleaseSharedResources(CpuContext ctx)
    {
        if (!Mfsr2Initialized)
        {
            return ctx.SetReturn(Mfsr2NotInitialized);
        }

        var handle = ctx[CpuRegister.Rdi];
        lock (Gate)
        {
            if (!Mfsr2SharedResources.ContainsKey(handle))
            {
                return ctx.SetReturn(Mfsr2InvalidArgument);
            }

            if (Contexts.Values.Any(state => state.Mfsr2SharedResources == handle))
            {
                return ctx.SetReturn(Mfsr2SharedResourcesBusy);
            }

            Mfsr2SharedResources.Remove(handle);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "5z2gBlqxJ+0",
        ExportName = "scePsmlMfsr2GetContextInitRequirement",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2GetContextInitRequirement(CpuContext ctx)
    {
        Span<byte> parameters = stackalloc byte[0x30];
        var error = ReadMfsr2CreationParameters(ctx, parameters);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        lock (Gate)
        {
            return Mfsr2SharedResources.ContainsKey(BinaryPrimitives.ReadUInt64LittleEndian(parameters[8..]))
                ? WriteMfsr2Requirement(ctx)
                : ctx.SetReturn(Mfsr2InvalidArgument);
        }
    }

    [SysAbiExport(Nid = "m9JLPc3wOQw",
        ExportName = "scePsmlMfsr2CreateContext",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2CreateContext(CpuContext ctx)
    {
        Span<byte> parameters = stackalloc byte[0x30];
        var error = ReadMfsr2CreationParameters(ctx, parameters);
        if (error != 0 || (error = ReadMfsr2Block(ctx, parameters[0x10..], out var block)) != 0)
        {
            return ctx.SetReturn(error);
        }

        var sharedResources = BinaryPrimitives.ReadUInt64LittleEndian(parameters[8..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(parameters[0x28..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(parameters[0x2A..]);
        var colorSpace = BinaryPrimitives.ReadUInt32LittleEndian(parameters[0x2C..]);
        if (width == 0 || height == 0 || width > MaxSurfaceSide || height > MaxSurfaceSide)
        {
            return ctx.SetReturn(Mfsr2InvalidArgument);
        }

        uint id;
        lock (Gate)
        {
            if (!Mfsr2SharedResources.ContainsKey(sharedResources) || IsMfsr2BlockInUse(block))
            {
                return ctx.SetReturn(Mfsr2InvalidArgument);
            }

            id = ++_nextContextId;
            if (!TryCreateHandle(ctx, id, out var handle) || !ctx.TryWriteUInt64(ctx[CpuRegister.Rdi], handle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            Contexts.Add(handle, new ContextState(id, width, height, string.Empty, sharedResources, block, colorSpace));
        }

        Console.Error.WriteLine($"[PSSR][INFO] Context {id} created (MFSR2): output={width}x{height} color_space={colorSpace}.");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Kyy1baXgaVU",
        ExportName = "scePsmlMfsr2ReleaseContext",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2ReleaseContext(CpuContext ctx)
    {
        if (!Mfsr2Initialized)
        {
            return ctx.SetReturn(Mfsr2NotInitialized);
        }

        var handle = ctx[CpuRegister.Rdi];
        lock (Gate)
        {
            if (!Contexts.TryGetValue(handle, out var state) || state.Mfsr2SharedResources == 0)
            {
                return ctx.SetReturn(Mfsr2InvalidArgument);
            }

            RemoveContextLocked(handle);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "ZLL31lzzxr4",
        ExportName = "scePsmlMfsr2GetDispatchPacketsSizeInDwords",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2GetDispatchPacketsSizeInDwords(CpuContext ctx)
    {
        var error = GetMfsr2Context(ctx[CpuRegister.Rdi], out _);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        var output = ctx[CpuRegister.Rsi];
        if (output == 0)
        {
            return ctx.SetReturn(Mfsr2NullOutput);
        }

        return ctx.SetReturn(ctx.TryWriteUInt32(output, Mfsr2PacketDwords)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "lrJwpLjKXRc",
        ExportName = "scePsmlMfsr2GetDispatchPackets",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsr2GetDispatchPackets(CpuContext ctx)
    {
        var error = GetMfsr2Context(ctx[CpuRegister.Rdi], out var state);
        if (error != 0)
        {
            return ctx.SetReturn(error);
        }

        var span = ctx[CpuRegister.Rsi];
        var parameters = ctx[CpuRegister.Rdx];
        if (span == 0 || parameters == 0 ||
            !ctx.TryReadUInt64(span, out var packetAddress) || packetAddress == 0 ||
            !ctx.TryReadUInt32(span + 8, out var dwords) || dwords < Mfsr2PacketDwords)
        {
            return ctx.SetReturn(Mfsr2InvalidArgument);
        }

        Span<byte> source = stackalloc byte[0x70];
        if (!ctx.Memory.TryRead(parameters, source))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (BinaryPrimitives.ReadUInt64LittleEndian(source) == 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]) == 0)
        {
            return ctx.SetReturn(Mfsr2InvalidArgument);
        }

        Span<byte> block = stackalloc byte[SuperResolutionPacket.DispatchParameterBytes];
        block.Clear();
        // MFSR2 drops the previous-frame textures and camera matrices from the dispatch.
        source[..0x18].CopyTo(block);
        source[0x18..0x28].CopyTo(block[0x20..]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x58..], state!.InputColorSpace);
        source[0x30..0x34].CopyTo(block[0x64..]);
        source[0x3C..0x44].CopyTo(block[0xF0..]);
        source[0x34..0x3C].CopyTo(block[0xF8..]);
        source[0x44..0x5C].CopyTo(block[0x100..]);
        block[0x11C] = source[0x64];
        source[0x68..0x6C].CopyTo(block[0x120..]);
        return WriteDispatchPacket(ctx, packetAddress, state, block, "MFSR2", Mfsr2PacketDwords);
    }

    private static int ReadMfsr2CreationParameters(CpuContext ctx, Span<byte> parameters)
    {
        if (!Mfsr2Initialized)
        {
            return Mfsr2NotInitialized;
        }

        if (ctx[CpuRegister.Rdi] == 0)
        {
            return Mfsr2NullOutput;
        }

        if (ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], parameters))
        {
            return Mfsr2InvalidArgument;
        }

        var profile = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        if (profile != 0)
        {
            Console.Error.WriteLine($"[PSSR][WARN] MFSR2 resource or context profile {profile} is unsupported.");
            return Mfsr2UnsupportedProfile;
        }

        return 0;
    }

    private static int WriteMfsr2Requirement(CpuContext ctx)
    {
        // The replacement upscaler owns its filtering resources on the host. Hold
        // one guest work block per object, which the guest may map as GPU-only.
        ReadOnlySpan<ulong> requirement = [Mfsr2BlockBytes, Mfsr2BlockBytes, 1];
        return ctx.SetReturn(ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], MemoryMarshal.AsBytes(requirement))
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ReadMfsr2Block(CpuContext ctx, ReadOnlySpan<byte> parameters, out ulong block)
    {
        block = 0;
        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(parameters);
        var count = BinaryPrimitives.ReadUInt64LittleEndian(parameters[8..]);
        if (count == 0 || blocks == 0 || blocks > ulong.MaxValue - 16 ||
            !ctx.TryReadUInt64(blocks, out block) || !ctx.TryReadUInt64(blocks + 8, out var size) ||
            block == 0 || block % Mfsr2BlockBytes != 0 || size < Mfsr2BlockBytes || block > ulong.MaxValue - Mfsr2BlockBytes)
        {
            return Mfsr2InvalidArgument;
        }

        return 0;
    }

    private static bool IsMfsr2BlockInUse(ulong block) =>
        Mfsr2SharedResources.ContainsValue(block) || Contexts.Values.Any(state => state.Mfsr2Block == block);

    private static int GetMfsr2Context(ulong handle, out ContextState? state)
    {
        state = null;
        if (!Mfsr2Initialized)
        {
            return Mfsr2NotInitialized;
        }

        lock (Gate)
        {
            return Contexts.TryGetValue(handle, out state) && state.Mfsr2SharedResources != 0
                ? 0
                : Mfsr2InvalidArgument;
        }
    }
}
