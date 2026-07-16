// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const int PadErrorInvalidArgument = unchecked((int)0x80920001);
    private const int PadErrorInvalidHandle = unchecked((int)0x80920003);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InformationAddress = MemoryBase + 0x100;
    private const int ExtendedInformationSize = 0x14;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_ValidHandleWritesStandardControllerInformation()
    {
        Assert.True(_memory.TryWrite(InformationAddress, Enumerable.Repeat((byte)0xA5, ExtendedInformationSize).ToArray()));
        SetArguments(handle: 1, InformationAddress);

        Assert.Equal(0, PadExports.PadDeviceClassGetExtendedInformation(_ctx));

        var information = new byte[ExtendedInformationSize];
        Assert.True(_memory.TryRead(InformationAddress, information));
        Assert.Equal(new byte[ExtendedInformationSize], information);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DeviceClassGetExtendedInformation_AcceptsPrimaryCompatibilityHandles(int handle)
    {
        SetArguments(handle, InformationAddress);

        Assert.Equal(0, PadExports.PadDeviceClassGetExtendedInformation(_ctx));
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_InvalidHandleReturnsPadError()
    {
        SetArguments(handle: 2, InformationAddress);

        Assert.Equal(PadErrorInvalidHandle, PadExports.PadDeviceClassGetExtendedInformation(_ctx));
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_NullOutputReturnsPadError()
    {
        SetArguments(handle: 1, informationAddress: 0);

        Assert.Equal(PadErrorInvalidArgument, PadExports.PadDeviceClassGetExtendedInformation(_ctx));
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_UnmappedOutputReturnsMemoryFault()
    {
        SetArguments(handle: 1, informationAddress: 0xDEAD_0000_0000);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            PadExports.PadDeviceClassGetExtendedInformation(_ctx));
    }

    private void SetArguments(int handle, ulong informationAddress)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)(long)handle);
        _ctx[CpuRegister.Rsi] = informationAddress;
    }
}
