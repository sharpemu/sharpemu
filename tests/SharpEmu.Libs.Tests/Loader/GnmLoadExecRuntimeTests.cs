// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Mods;
using SharpEmu.Core.Runtime;
using SharpEmu.GnmMode.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.FileSystem;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmLoadExecRuntimeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void SelectedExecutableRetainsTheOriginalApplicationRootAndArguments(bool archiveBacked, bool selected, bool modded)
    {
        var directory = Directory.CreateTempSubdirectory("sharpemu-exec-runtime-").FullName;
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        string? cache = null;
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", null);
            var original = GnmLoaderTests.CreateImage(false);
            var alternate = GnmLoaderTests.CreateImage(false);
            BinaryPrimitives.WriteUInt64LittleEndian(alternate.AsSpan(24), 0xB0);
            var application = Path.Combine(directory, "eboot.bin");
            string expectedRoot;
            if (archiveBacked)
            {
                application = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zar");
                File.WriteAllBytes(application, ZArchiveTestWriter.Build(
                    [("eboot.bin", original), ("big3-ps4_Shipping.elf", alternate), ("data/level.bin", new byte[] { 1, 2, 3 })]));
                cache = expectedRoot = ZArchiveCache.GetCacheRoot(application);
            }
            else
            {
                File.WriteAllBytes(application, original);
                File.WriteAllBytes(Path.Combine(directory, "big3-ps4_Shipping.elf"), alternate);
                expectedRoot = directory;
            }
            var dispatcher = new RecordingDispatcher();
            var mods = new GameModRepository("CUSA99999", Path.Combine(directory, "mods"));
            if (modded)
            {
                var modDirectory = Directory.CreateDirectory(Path.Combine(mods.DirectoryPath, "Executable")).FullName;
                var replacement = (byte[])(selected ? alternate : original).Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(replacement.AsSpan(24), selected ? 0xD0UL : 0xC0UL);
                File.WriteAllBytes(Path.Combine(modDirectory, selected ? "big3-ps4_Shipping.elf" : "eboot.bin"), replacement);
                mods.SetEnabled("Executable", true);
            }
            KernelMemoryCompatExports.RegisterGuestPathMount("/app0", expectedRoot);
            string[] arguments = ["first argument", "", "four", "five"];
            using var runtime = new SharpEmuRuntime(new GnmSelfLoader(), new VirtualMemory(), dispatcher,
                new ModuleManager(), cpuExecutionOptions: new CpuExecutionOptions { ProcessArguments = arguments },
                executableRelativePath: selected ? "big3-ps4_Shipping.elf" : null, modsDirectory: mods.DirectoryPath);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, runtime.Run(application));
            Assert.Equal(selected ? "big3-ps4_Shipping.elf" : "eboot.bin", dispatcher.ImageName);
            Assert.Equal((selected ? 0x4000B0UL : 0x4000A0UL) + (modded ? 0x20UL : 0), dispatcher.EntryPoint);
            Assert.Equal(Path.GetFullPath(expectedRoot), Path.GetFullPath(dispatcher.ApplicationRoot!));
            Assert.Equal(arguments, dispatcher.Arguments);
            Assert.False(App0Archive.IsMounted);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(expectedRoot, "eboot.bin")));
            if (archiveBacked)
            {
                Assert.True(File.Exists(Path.Combine(cache!, "eboot.bin")));
                Assert.Equal(selected && !modded, File.Exists(Path.Combine(cache!, "big3-ps4_Shipping.elf")));
                Assert.False(File.Exists(Path.Combine(cache!, "data", "level.bin")));
            }
        }
        finally
        {
            App0Archive.Unmount();
            KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(directory, recursive: true);
            if (cache is not null && Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ps5ExecutableModsUseTheSameOverlayAndCanBeDisabled(bool archiveBacked)
    {
        var directory = Directory.CreateTempSubdirectory("sharpemu-ps5-mod-runtime-").FullName;
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        string? cache = null;
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", null);
            var original = SelfLoaderTests.CreateSelfImage(0x5414F5EE, 0x10, 0x10000101, 0x32);
            BinaryPrimitives.WriteUInt64LittleEndian(original.AsSpan(0x20 + 24), 0xA0);
            var replacement = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(replacement.AsSpan(0x20 + 24), 0xC0);
            var application = Path.Combine(directory, "eboot.bin");
            File.WriteAllBytes(application, original);
            var app0 = directory;
            if (archiveBacked)
            {
                application = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zar");
                File.WriteAllBytes(application, ZArchiveTestWriter.Build([("eboot.bin", original)]));
                cache = app0 = ZArchiveCache.GetCacheRoot(application);
            }
            var repository = new GameModRepository("PPSA99999", Path.Combine(directory, "mods"));
            var mod = Directory.CreateDirectory(Path.Combine(repository.DirectoryPath, "Executable")).FullName;
            File.WriteAllBytes(Path.Combine(mod, "eboot.bin"), replacement);
            repository.SetEnabled("Executable", true);
            KernelMemoryCompatExports.RegisterGuestPathMount("/app0", app0);
            var dispatcher = new RecordingDispatcher();
            using var runtime = new SharpEmuRuntime(new SelfLoader(), new VirtualMemory(), dispatcher,
                new ModuleManager(), modsDirectory: repository.DirectoryPath);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, runtime.Run(application));
            Assert.Equal(Generation.Gen5, dispatcher.Generation);
            Assert.Equal(0x8_0000_00C0UL, dispatcher.EntryPoint);
            repository.SetEnabled("Executable", false);
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, runtime.Run(application));
            Assert.Equal(0x8_0000_00A0UL, dispatcher.EntryPoint);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(app0, "eboot.bin")));
        }
        finally
        {
            App0Archive.Unmount();
            KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(directory, recursive: true);
            if (cache is not null && Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class RecordingDispatcher : ICpuDispatcher
    {
        public string? ImageName;
        public ulong EntryPoint;
        public Generation Generation;
        public string? ApplicationRoot;
        public IReadOnlyList<string>? Arguments;
        public CpuTrapInfo? LastTrapInfo => null;
        public CpuMemoryFaultInfo? LastMemoryFaultInfo => null;
        public CpuControlTransferInfo? LastControlTransferInfo => null;
        public CpuNotImplementedInfo? LastNotImplementedInfo => null;
        public string? LastImportResolutionTrace => null;
        public string? LastBasicBlockTrace => null;
        public string? LastMilestoneLog => null;
        public string? LastRecentInstructionWindow => null;
        public string? LastRecentControlTransferTrace => null;
        public CpuSessionSummary LastSessionSummary => default;

        public OrbisGen2Result DispatchEntry(ulong entryPoint, Generation generation,
            IReadOnlyDictionary<ulong, string>? importStubs = null,
            IReadOnlyDictionary<string, ulong>? runtimeSymbols = null, string processImageName = "eboot.bin",
            CpuExecutionOptions executionOptions = default)
        {
            EntryPoint = entryPoint;
            Generation = generation;
            ImageName = processImageName;
            ApplicationRoot = KernelMemoryCompatExports.ResolveGuestPath("/app0");
            Arguments = executionOptions.ProcessArguments;
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }

        public OrbisGen2Result DispatchModuleInitializer(ulong entryPoint, Generation generation,
            IReadOnlyDictionary<ulong, string>? importStubs = null,
            IReadOnlyDictionary<string, ulong>? runtimeSymbols = null, string moduleName = "module",
            CpuExecutionOptions executionOptions = default) => OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
