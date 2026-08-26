// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial manages AGC driver resource-registration ownership.

    private const ulong ResourceRegistrationBytesPerResource = 0x118;
    private const ulong ResourceRegistrationBytesPerOwner = 0x1E0;
    private const int ResourceRegistrationMaxNameLength = 256;

    private readonly record struct RegisteredAgcResource(
        uint Owner,
        ulong Address,
        ulong Size,
        string Name,
        uint Type,
        uint Flags);

    [SysAbiExport(
    Nid = "uJziRsODk1c",
    ExportName = "sceAgcDriverGetResourceRegistrationMaxNameLength",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverGetResourceRegistrationMaxNameLength(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];

        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, outAddress, 256))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_resource_registration_max_name_length out=0x{outAddress:X16} value=256");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private const uint DefaultAgcOwner = 1;
    [SysAbiExport(
        Nid = "F0ZXt5q0ZTA",
        ExportName = "sceAgcDriverGetDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverGetDefaultOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];

        if (ownerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, ownerAddress, DefaultAgcOwner))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_default_owner out=0x{ownerAddress:X16} owner={DefaultAgcOwner}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
    Nid = "W5z4eZrjEas",
    ExportName = "sceAgcDriverRegisterResource",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverRegisterResource(CpuContext ctx)
    {
        var resourceAddress = ctx[CpuRegister.Rdi];
        var owner = (uint)ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        var type = (uint)ctx[CpuRegister.R8];
        var flags = (uint)ctx[CpuRegister.R9];

        TraceAgc(
            $"agc.driver_register_resource resource=0x{resourceAddress:X16} owner={owner} " +
            $"name=0x{nameAddress:X16} type={type} flags={flags}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "AOLcoIkQDgM",
        ExportName = "sceAgcDriverQueryResourceRegistrationUserMemoryRequirements",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverQueryResourceRegistrationUserMemoryRequirements(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rdi];
        var resourceCount = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (sizeAddress == 0 || resourceCount == 0 || ownerCount == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong requiredSize;
        try
        {
            requiredSize = checked(
                resourceCount * ResourceRegistrationBytesPerResource +
                ownerCount * ResourceRegistrationBytesPerOwner);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(sizeAddress, requiredSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_query_resource_registration_memory resources={resourceCount} " +
            $"owners={ownerCount} bytes=0x{requiredSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "F0Y42t-3e18",
        ExportName = "sceAgcDriverInitResourceRegistration",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverInitResourceRegistration(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rdi];
        var memorySize = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (memoryAddress == 0 || memorySize == 0 || ownerCount == 0 || ownerCount > uint.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.ResourceRegistrationInitialized = true;
            state.ResourceRegistrationMemory = memoryAddress;
            state.ResourceRegistrationMemorySize = memorySize;
            state.ResourceRegistrationMaxOwners = (uint)ownerCount;
            state.ResourceOwners.Clear();
            state.RegisteredResources.Clear();
            state.DefaultOwner = DefaultAgcOwner;
            state.NextOwner = 1;
            state.NextResource = 1;
        }

        TraceAgc(
            $"agc.driver_init_resource_registration memory=0x{memoryAddress:X16} " +
            $"bytes=0x{memorySize:X} owners={ownerCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "U9ueyEhSkF4",
        ExportName = "sceAgcDriverRegisterDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterDefaultOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.DefaultOwner = owner;
        }

        TraceAgc($"agc.driver_register_default_owner owner={owner}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "X-Nm5KLREeg",
        ExportName = "sceAgcDriverRegisterOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (ownerAddress == 0 || nameAddress == 0 ||
            !TryReadGuestCString(
                ctx,
                nameAddress,
                ResourceRegistrationMaxNameLength,
                out var nameBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        uint owner;
        lock (state.Gate)
        {
            if (state.ResourceRegistrationInitialized &&
                state.ResourceRegistrationMaxOwners != 0 &&
                state.ResourceOwners.Count >= state.ResourceRegistrationMaxOwners)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            owner = state.NextOwner;
            while (owner == state.DefaultOwner || state.ResourceOwners.ContainsKey(owner))
            {
                owner++;
                if (owner == 0)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                }
            }

            state.NextOwner = owner + 1;
            state.ResourceOwners.Add(owner, System.Text.Encoding.UTF8.GetString(nameBytes));
        }

        if (!ctx.TryWriteUInt32(ownerAddress, owner))
        {
            lock (state.Gate)
            {
                state.ResourceOwners.Remove(owner);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_register_owner out=0x{ownerAddress:X16} owner={owner} " +
            $"name={System.Text.Encoding.UTF8.GetString(nameBytes)}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int RemoveResourcesForOwner(SubmittedGpuState state, uint owner)
    {
        var stale = new List<uint>();
        foreach (var (handle, resource) in state.RegisteredResources)
        {
            if (resource.Owner == owner)
            {
                stale.Add(handle);
            }
        }

        foreach (var handle in stale)
        {
            state.RegisteredResources.Remove(handle);
        }

        return stale.Count;
    }

    [SysAbiExport(
        Nid = "ZLJk9r2+2Aw",
        ExportName = "sceAgcDriverUnregisterOwnerAndResources",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterOwnerAndResources(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            if (!state.ResourceOwners.Remove(owner))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            resources = RemoveResourcesForOwner(state, owner);
            state.ComputeQueues.Remove(owner);
        }

        TraceAgc($"agc.driver_unregister_owner owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SCoAN5fYlUM",
        ExportName = "sceAgcDriverUnregisterAllResourcesForOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterAllResourcesForOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            resources = RemoveResourcesForOwner(state, owner);
        }

        TraceAgc($"agc.driver_unregister_owner_resources owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pWLG7WOpVcw",
        ExportName = "sceAgcDriverUnregisterResource",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterResource(CpuContext ctx)
    {
        var resourceHandle = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            if (!state.RegisteredResources.Remove(resourceHandle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        TraceAgc($"agc.driver_unregister_resource handle={resourceHandle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Tessellation-factor ring and hull-shader off-chip buffers are guest-driver
    // configuration for on-hardware tessellation memory. Our translator handles
    // shader execution directly, so there is no guest-side ring to program: the
    // guest driver only needs these to report success so init proceeds. Games
    // (e.g. Unity titles) call them during GPU setup and stall if unresolved.
    [SysAbiExport(
        Nid = "XlNp7jzGiPo",
        ExportName = "sceAgcDriverSetTFRing",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetTFRing(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_set_tf_ring ring=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"size=0x{(uint)ctx[CpuRegister.Rsi]:X8}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "MM4IZSEYytQ",
        ExportName = "sceAgcDriverSetHsOffchipParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetHsOffchipParam(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_set_hs_offchip_param buffer=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"param=0x{(uint)ctx[CpuRegister.Rsi]:X8}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
