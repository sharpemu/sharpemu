// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// scePthreadAttrSetsolosched was unimplemented, so a title calling it got
/// ORBIS_GEN2_ERROR_NOT_FOUND and "Missing HLE export for NID: Dk6FC-TI+7Q" during thread setup.
///
/// Solo scheduling asks the kernel to give a thread a core to itself. SharpEmu maps guest threads
/// onto host threads and reserves no cores, so the attribute has no scheduling effect - but it is
/// a real attribute with a getter, and the contract a title relies on is that reading it back
/// returns what was written. These pin that, so the pair cannot decay into a bare success that
/// discards the value.
/// </summary>
public sealed class PthreadAttrSoloSchedTests
{
    private const ulong MemoryBase = 0x5_0000_0000;
    private const ulong AttrAddress = MemoryBase + 0x100;
    private const ulong OutAddress = MemoryBase + 0x200;

    /// <summary>
    /// The reported failure was a resolution failure, not a behavioural one: the loader logged
    /// "Missing HLE export for NID: Dk6FC-TI+7Q" and returned ORBIS_GEN2_ERROR_NOT_FOUND. This is
    /// the test that pins it fixed.
    /// </summary>
    [Theory]
    [InlineData("Dk6FC-TI+7Q", "scePthreadAttrSetsolosched")]
    [InlineData("9RnL-m0+diQ", "scePthreadAttrGetsolosched")]
    public void RegistryResolvesTheSoloSchedExports(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(0x7FFF)]
    public void SoloSchedRoundTripsThroughTheAttribute(int value)
    {
        var context = CreateContext();

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = unchecked((ulong)(long)value);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrSetsolosched(context));

        Assert.Equal(value, GetSoloSched(context));
    }

    /// <summary>
    /// A freshly seen attribute reports solo scheduling off rather than whatever a previous
    /// attribute at another address happened to set.
    /// </summary>
    [Fact]
    public void UnsetAttributeReportsSoloSchedulingDisabled()
    {
        var context = CreateContext();

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrSetsolosched(context));

        // A different attribute object must not inherit the value.
        context[CpuRegister.Rdi] = AttrAddress + 0x40;
        context[CpuRegister.Rsi] = OutAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetsolosched(context));
        Assert.Equal(0, ReadInt32(context, OutAddress));
    }

    [Fact]
    public void SetterRejectsANullAttribute()
    {
        var context = CreateContext();

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadExtendedCompatExports.PthreadAttrSetsolosched(context));
    }

    [Fact]
    public void GetterRejectsANullOutputPointer()
    {
        var context = CreateContext();

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelPthreadExtendedCompatExports.PthreadAttrGetsolosched(context));
    }

    /// <summary>
    /// Setting solo scheduling must not disturb the other attributes sharing the record.
    /// </summary>
    [Fact]
    public void SettingSoloSchedLeavesDetachStateIntact()
    {
        var context = CreateContext();

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = 1; // PTHREAD_CREATE_DETACHED
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrSetdetachstate(context));

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrSetsolosched(context));

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = OutAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetdetachstate(context));
        Assert.Equal(1, ReadInt32(context, OutAddress));

        Assert.Equal(1, GetSoloSched(context));
    }

    private static int GetSoloSched(CpuContext context)
    {
        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = OutAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetsolosched(context));
        return ReadInt32(context, OutAddress);
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static int ReadInt32(CpuContext context, ulong address)
    {
        var bytes = new byte[sizeof(int)];
        Assert.True(context.Memory.TryRead(address, bytes));
        return BitConverter.ToInt32(bytes);
    }
}
