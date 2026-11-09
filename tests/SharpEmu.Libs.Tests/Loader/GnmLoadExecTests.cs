// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.FileSystem;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmLoadExecTests : IDisposable
{
    private const ulong Address = 0x1_0000_0000;
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-loadexec-").FullName;
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");

    public GnmLoadExecTests()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _directory);
        KernelMemoryCompatExports.RegisterGuestPathMount("/app0", _directory);
        HostSessionControl.ResetShutdownRequest();
        File.WriteAllBytes(Path.Combine(_directory, "eboot.bin"), GnmLoaderTests.CreateImage(false));
        File.WriteAllBytes(Path.Combine(_directory, "big2-ps4_Shipping.elf"), GnmLoaderTests.CreateImage(false));
    }

    public void Dispose()
    {
        App0Archive.Unmount();
        KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
        HostSessionControl.ResetShutdownRequest();
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _previousRoot);
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadExecCopiesArgumentsAndRequiresTheDedicatedExitCode(bool archiveBacked)
    {
        var application = Path.Combine(_directory, "eboot.bin");
        if (archiveBacked)
        {
            application = Path.Combine(_directory, "game.zar");
            File.WriteAllBytes(application, ZArchiveTestWriter.Build(
                [("eboot.bin", GnmLoaderTests.CreateImage(false)),
                 ("big2-ps4_Shipping.elf", GnmLoaderTests.CreateImage(false))]));
            File.Delete(Path.Combine(_directory, "big2-ps4_Shipping.elf"));
            App0Archive.Mount(ZArchive.Open(application)!, _directory);
        }
        using var parent = GnmLoadExecSession.Create(application);
        Assert.False(GnmLoadExecSession.IsRestart(parent.FilePath));
        var exits = new List<int>();
        using var guest = GnmLoadExecSession.Open(parent.FilePath, application, exits.Add);
        var context = Context("/app0/big2-ps4_Shipping.elf", ["with spaces", "", "Türkçe"]);
        var export = Assert.Single(GnmExportRegistry.CreateExports(guest), export => export.Nid == "JoBqSQt1yyA");
        Assert.Equal(Generation.Gen4, export.Target);
        Assert.Equal("sceSystemServiceLoadExec", export.Name);
        Assert.Equal(0, export.Function(context));
        Assert.Equal([GnmLoadExecSession.RestartExitCode], exits);
        Assert.False(parent.TryTakeRequest(0, out _));
        Assert.False(parent.TryTakeRequest(-2, out _));
        Assert.False(parent.TryTakeRequest(3, out _));
        Assert.True(parent.TryTakeRequest(GnmLoadExecSession.RestartExitCode, out var request));
        Assert.Equal("big2-ps4_Shipping.elf", request.Executable);
        Assert.Equal(["with spaces", "", "Türkçe"], request.Arguments);
        Assert.False(parent.TryTakeRequest(GnmLoadExecSession.RestartExitCode, out _));
        parent.SetStartup(request);
        Assert.True(GnmLoadExecSession.IsRestart(parent.FilePath));
        using var next = GnmLoadExecSession.Open(parent.FilePath, application, exits.Add);
        Assert.Equal(application, next.ApplicationPath);
        Assert.Equal(request.Executable, next.Startup!.Executable);
        Assert.Equal(request.Arguments, next.Startup.Arguments);
        if (archiveBacked)
            Assert.False(File.Exists(Path.Combine(_directory, request.Executable)));
    }

    [Theory]
    [InlineData("/app0/../outside.elf")]
    [InlineData("/app0/C:/outside.elf")]
    [InlineData("/app0/dir\\outside.elf")]
    [InlineData("/savedata0/eboot.bin")]
    [InlineData("/app0/missing.elf")]
    public void InvalidOrMissingTargetsDoNotPublishARestart(string path)
    {
        var application = Path.Combine(_directory, "eboot.bin");
        using var parent = GnmLoadExecSession.Create(application);
        var exits = new List<int>();
        using var guest = GnmLoadExecSession.Open(parent.FilePath, application, exits.Add);
        Assert.NotEqual(0, guest.LoadExec(Context(path, [])));
        Assert.Empty(exits);
        Assert.False(parent.TryTakeRequest(GnmLoadExecSession.RestartExitCode, out _));
    }

    [Fact]
    public void ShutdownAndUnreadableArgumentVectorsDoNotRestart()
    {
        var application = Path.Combine(_directory, "eboot.bin");
        using var parent = GnmLoadExecSession.Create(application);
        var exits = new List<int>();
        using var guest = GnmLoadExecSession.Open(parent.FilePath, application, exits.Add);
        var context = Context("/app0/big2-ps4_Shipping.elf", []);
        context[CpuRegister.Rsi] = Address + 0xFFFF;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, guest.LoadExec(context));
        context[CpuRegister.Rsi] = 0;
        HostSessionControl.RequestShutdown("loadexec-test-stop");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, guest.LoadExec(context));
        Assert.Empty(exits);
        Assert.False(parent.TryTakeRequest(GnmLoadExecSession.RestartExitCode, out _));
    }

    [Fact]
    public void DuplicateRequestsAndSessionReuseAcrossApplicationsAreRejected()
    {
        var application = Path.Combine(_directory, "eboot.bin");
        using var parent = GnmLoadExecSession.Create(application);
        var exits = new List<int>();
        using var guest = GnmLoadExecSession.Open(parent.FilePath, application, exits.Add);
        var context = Context("/hostapp/big2-ps4_Shipping.elf", []);
        Assert.Equal(0, guest.LoadExec(context));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, guest.LoadExec(context));
        Assert.Single(exits);
        Assert.Throws<InvalidDataException>(() => GnmLoadExecSession.Open(parent.FilePath,
            Path.Combine(_directory, "another.bin"), exits.Add));
        Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == "JoBqSQt1yyA");
    }

    private static CpuContext Context(string path, string[] arguments)
    {
        var memory = new FakeCpuMemory(Address, 0x10000);
        var context = new CpuContext(memory, Generation.Gen4);
        context[CpuRegister.Rdi] = memory.WriteCString(Address, path);
        context[CpuRegister.Rsi] = Address + 0x800;
        for (var index = 0; index < arguments.Length; index++)
        {
            var pointer = memory.WriteCString(Address + 0x1000 + (ulong)index * 1024, arguments[index]);
            Assert.True(context.TryWriteUInt64(Address + 0x800 + (ulong)index * 8, pointer));
        }
        return context;
    }
}
