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
    private const int ErrorDrmNoEntitlement = unchecked((int)0x80D90007);

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
        AddcontEntitlements.ConfigureConceptId(null);
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
        Assert.Equal(string.Empty, ReadMountPoint(memory));
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
    }

    [Fact]
    public void MountingTheSameLabelTwiceReturnsTheSameMountPoint()
    {
        Directory.CreateDirectory(Path.Combine(_root, "addcont", "85y-je"));
        Directory.CreateDirectory(Path.Combine(_root, "addcont", "5d5c48"));
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
    public void AMissingPackageIsRefusedWithoutAMountPoint()
    {
        var (context, memory) = CreateContext("85y-je");
        Assert.Equal(ErrorDrmNoEntitlement, AppContentExports.AppContentAddcontMount(context));
        Assert.Equal(string.Empty, ReadMountPoint(memory));
        Assert.False(Directory.Exists(Path.Combine(_root, "addcont", "85y-je")));
    }

    [Fact]
    public void OwnedLabelsComeFromTheTitleDefaultsAndOnlyForThatTitle()
    {
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA26344");
        AddcontEntitlements.ConfigureConceptId("10003923");
        Assert.Equal(
            new[] { "GHOST2APP0000000", "GHOST2BASE000000" },
            AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));
        Assert.True(AddcontEntitlements.Owns("GHOST2APP0000000"));
        Assert.True(AddcontEntitlements.TryGet("GHOST2BASE000000", out var entitlement));
        Assert.Equal(4u, entitlement.DownloadStatus);
        Assert.Same(AddcontEntitlements.Owned(), AddcontEntitlements.Owned());

        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA00000");
        AddcontEntitlements.ConfigureConceptId(null);
        Assert.Empty(AddcontEntitlements.Owned());
        Assert.False(AddcontEntitlements.Owns("GHOST2APP0000000"));
    }

    // Every regional release of a game shares one concept id: the EU GTA V dump
    // (PPSA04263) owns the same labels as the US one.
    [Fact]
    public void ARegionalTitleIdMatchesTheDefaultsOfItsConceptId()
    {
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA04263");
        AddcontEntitlements.ConfigureConceptId("201930");
        Assert.Equal(
            new[] { "85y-je", "5d5c48", "_mtqu6" },
            AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));
    }

    // Street Fighter 6 has no default row: an unknown concept id owns nothing.
    [Fact]
    public void AnUnknownConceptIdOwnsNothing()
    {
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA02633");
        AddcontEntitlements.ConfigureConceptId("10001615");
        Assert.Empty(AddcontEntitlements.Owned());
    }

    [Fact]
    public void ATitleIdEntryInTheConfigurationFileWinsOverAConceptIdEntry()
    {
        File.WriteAllText(
            Path.Combine(_root, "data", "entitlements.json"),
            "{}");
        Directory.CreateDirectory(Path.Combine(_root, "data", "user"));
        File.WriteAllText(
            Path.Combine(_root, "data", "user", "entitlements.json"),
            "{ \"10001615\": [\"CONCEPT-1\"], \"PPSA02633\": [\"TITLE-1\"] }");
        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA02633");
        AddcontEntitlements.ConfigureConceptId("10001615");
        Assert.Equal(new[] { "TITLE-1" }, AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));

        KernelMemoryCompatExports.ConfigureApplicationInfo("PPSA02634");
        Assert.Equal(new[] { "CONCEPT-1" }, AddcontEntitlements.Owned().Select(entitlement => entitlement.Label));
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
