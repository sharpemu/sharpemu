// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.GnmMode;

public static partial class GnmRuntime
{
    public static ISharpEmuRuntime CreateDefault(SharpEmuRuntimeOptions options = default, ISelfLoader? loader = null,
        GnmLoadExecSession? session = null)
    {
        Libraries.Np.GnmTrophyPackageReader.Configure(Environment.GetEnvironmentVariable(Libraries.Np.GnmTrophyPackageReader.KeyEnvironmentName));
        var memory = new PhysicalVirtualMemory(
            viewHost: HostViewMemory.Create(),
            preReserveGuestAddressSpace: true,
            adoptStartupAddressReservations: options.AdoptStartupAddressReservations);
        try
        {
            var modules = new ModuleManager();
            modules.RegisterExports(GnmExportRegistry.CreateExports(session));
            modules.Freeze();
            var gpuMemory = new GuestGpuMemory(memory);
            GuestGpuMemoryHook.Attach(gpuMemory);
            return new SharpEmuRuntime(loader ?? new Loader.GnmSelfLoader(), memory,
                new CpuDispatcher(memory, modules), modules, Aerolib.Instance,
                new CpuExecutionOptions
                {
                    CpuEngine = options.CpuEngine,
                    StrictDynlibResolution = options.StrictDynlibResolution,
                    ImportTraceLimit = options.ImportTraceLimit,
                    DebugHook = options.DebugHook,
                    ProcessArguments = session?.Startup?.Arguments,
                }, new PhysicalFileSystem(), gpuMemory, options.SystemLanguage ?? 1,
                executableRelativePath: session?.Startup?.Executable,
                modsDirectory: options.ModsDirectory);
        }
        catch
        {
            memory.Dispose();
            throw;
        }
    }
}
