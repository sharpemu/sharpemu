// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.AppContent;

// The add-on state is static, so these tests must not run beside each other.
[CollectionDefinition(nameof(AddcontStateCollection), DisableParallelization = true)]
public sealed class AddcontStateCollection;

[Collection(nameof(AddcontStateCollection))]
public sealed class AddcontMountTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong LabelAddress = MemoryBase;
    private const ulong MountPointAddress = MemoryBase + 0x100;
    private const int ErrorParameter = unchecked((int)0x80D90002);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpemu-addcont-" + Guid.NewGuid().ToString("N"));

    public AddcontMountTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "addcont"));
        AddcontEntitlements.DataRootOverride = Path.Combine(_root, "data");
        AddcontEntitlements.AddcontRootOverride = Path.Combine(_root, "addcont");
        AppContentExports.ResetRuntimeState();
    }

    public void Dispose()
    {
        AppContentExports.ResetRuntimeState();
        AddcontEntitlements.DataRootOverride = null;
        AddcontEntitlements.AddcontRootOverride = null;
        KernelMemoryCompatExports.ConfigureApplicationInfo(null);
        AddcontEntitlements.ResetRuntimeState();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("85y-je", true)]
    [InlineData("GHOST2APP0000000", true)]
    [InlineData("_mtqu6", true)]
    [InlineData("", false)]
    [InlineData("12345678901234567", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\b", false)]
    [InlineData("c:evil", false)]
    [InlineData("a b", false)]
    [InlineData("café", false)]
    public void LabelsFollowTheEntitlementCharset(string label, bool valid) =>
        Assert.Equal(valid, AddcontEntitlements.IsValidLabel(label));

    [Fact]
    public void MountRejectsAnInvalidLabelWithoutMounting()
    {
        var (context, memory) = CreateContext("../evil");
        Assert.Equal(ErrorParameter, AppContentExports.AppContentAddcontMount(context));
        Assert.Equal(0, ReadMountPoint(memory)[0]);
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
    }

    [Fact]
    public void MountingTheSameLabelTwiceReturnsTheSameMountPoint()
    {
        var (context, memory) = CreateContext("85y-je");
        Assert.Equal(0, AppContentExports.AppContentAddcontMount(context));
        var first = ReadMountPoint(memory);

        Assert.Equal(0, AppContentExports.AppContentAddcontMount(context));
        Assert.Equal(first, ReadMountPoint(memory));

        WriteLabel(memory, "5d5c48");
        Assert.Equal(0, AppContentExports.AppContentAddcontMount(context));
        Assert.NotEqual(first, ReadMountPoint(memory));
    }

    [Fact]
    public void OwnedLabelsComeFromTheTitleDefaultsAndOnlyForThatTitle()
    {
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA26344");
        AddcontEntitlements.ResetRuntimeState();
        Assert.Equal(
            new[] { "GHOST2APP0000000", "GHOST2BASE000000" },
            AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));
        Assert.True(AddcontEntitlements.Owns("GHOST2APP0000000"));
        Assert.True(AddcontEntitlements.TryGet("GHOST2BASE000000", out var entitlement));
        Assert.Equal(4u, entitlement.DownloadStatus);
        Assert.Same(AddcontEntitlements.Owned(), AddcontEntitlements.Owned());

        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA00000");
        Assert.Empty(AddcontEntitlements.Owned());
        Assert.False(AddcontEntitlements.Owns("GHOST2APP0000000"));
    }

    [Fact]
    public void OnlyValidAddcontFolderNamesAreEnumerated()
    {
        Directory.CreateDirectory(Path.Combine(_root, "addcont", "PACK-1"));
        Directory.CreateDirectory(Path.Combine(_root, "addcont", "bad name"));
        Directory.CreateDirectory(Path.Combine(_root, "addcont", "THIS_LABEL_IS_FAR_TOO_LONG"));
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA00000");
        AddcontEntitlements.ResetRuntimeState();
        Assert.Equal(new[] { "PACK-1" }, AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));
    }

    private static (CpuContext Context, FakeCpuMemory Memory) CreateContext(string label)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteLabel(memory, label);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = LabelAddress;
        context[CpuRegister.Rdx] = MountPointAddress;
        return (context, memory);
    }

    private static void WriteLabel(FakeCpuMemory memory, string label)
    {
        var bytes = new byte[17];
        Encoding.UTF8.GetBytes(label).AsSpan(0, Math.Min(17, Encoding.UTF8.GetByteCount(label))).CopyTo(bytes);
        Assert.True(memory.TryWrite(LabelAddress, bytes));
    }

    private static string ReadMountPoint(FakeCpuMemory memory)
    {
        var bytes = new byte[16];
        Assert.True(memory.TryRead(MountPointAddress, bytes));
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }
}
