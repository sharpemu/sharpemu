// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.UserConfigRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// A snapshot shares the live banks' parts until the live banks write them; it never changes.
public sealed class RegisterBanksSnapshotTests
{
    private const ulong PacketAddress = 0x1_0000_1000;

    private static RegisterBanks NewBanks() => new(static message => new InvalidOperationException(message));

    private static PacketContext Packet(uint opcode, int valueCount) =>
        new(PacketHeader.Make((uint)valueCount + 2, opcode), PacketAddress, 0, 100, 100);

    private static void WriteContext(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, values.Length), offset, values);

    private static void WriteShader(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteShaderPacket(banks, Packet(PacketOpcode.SetShaderRegister, values.Length), offset, values);

    private static void WriteUserConfig(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteUserConfigPacket(banks, Packet(PacketOpcode.SetUserConfigRegister, values.Length), offset, values);

    [Fact]
    public void WritesAfterASnapshotLeaveItUnchanged()
    {
        var banks = NewBanks();
        WriteContext(banks, DbDepthControl, 0x0000_0006);
        WriteShader(banks, SpiShaderPgmLoPs, 0x1000);
        WriteShader(banks, SpiShaderUserDataPs0, 11, 12);
        WriteShader(banks, SpiShaderPgmLoEs, 0x2000);
        WriteShader(banks, ComputePgmLo, 0x3000);
        WriteUserConfig(banks, VgtPrimitiveType, 4);
        var snapshot = banks.Snapshot();

        WriteContext(banks, DbDepthControl, 0x0000_0000);
        WriteShader(banks, SpiShaderPgmLoPs, 0x5000);
        WriteShader(banks, SpiShaderUserDataPs0, 21, 22);
        WriteShader(banks, SpiShaderPgmLoEs, 0x6000);
        WriteShader(banks, ComputePgmLo, 0x7000);
        WriteUserConfig(banks, VgtPrimitiveType, 6);

        Assert.True(snapshot.Context.DepthTarget.DepthTestEnabled);
        Assert.False(banks.Context.DepthTarget.DepthTestEnabled);
        Assert.Equal(0x1000ul << 8, snapshot.Shader.Pixel.Address);
        Assert.Equal(0x5000ul << 8, banks.Shader.Pixel.Address);
        Assert.Equal([11u, 12u], snapshot.Shader.Pixel.UserScalars.Values[..2]);
        Assert.Equal([21u, 22u], banks.Shader.Pixel.UserScalars.Values[..2]);
        Assert.Equal(0x2000ul << 8, snapshot.Shader.Vertex.ExportAddress);
        Assert.Equal(0x6000ul << 8, banks.Shader.Vertex.ExportAddress);
        Assert.Equal(0x3000ul << 8, snapshot.Shader.Compute.Address);
        Assert.Equal(0x7000ul << 8, banks.Shader.Compute.Address);
        Assert.Equal(4u, snapshot.UserConfig.PrimitiveType);
        Assert.Equal(6u, banks.UserConfig.PrimitiveType);
    }

    [Fact]
    public void AWriteCopiesOnlyThePartItStoresInto()
    {
        var banks = NewBanks();
        var snapshot = banks.Snapshot();

        WriteShader(banks, SpiShaderUserDataPs0, 7);

        Assert.NotSame(snapshot.Shader.Pixel, banks.Shader.Pixel);
        Assert.Same(snapshot.Shader.Vertex, banks.Shader.Vertex);
        Assert.Same(snapshot.Shader.Compute, banks.Shader.Compute);
        Assert.Same(snapshot.Context, banks.Context);
        Assert.Same(snapshot.UserConfig, banks.UserConfig);
    }

    [Fact]
    public void AUserScalarWriteCopiesOnlyItsSet()
    {
        var banks = NewBanks();
        WriteShader(banks, SpiShaderUserDataGs0, 1);
        WriteShader(banks, SpiShaderUserDataHs0, 2);
        var snapshot = banks.Snapshot();

        WriteShader(banks, SpiShaderUserDataGs0, 3);
        WriteShader(banks, SpiShaderPgmLoPs, 0x4000);

        Assert.NotSame(snapshot.Shader.Vertex, banks.Shader.Vertex);
        Assert.NotSame(snapshot.Shader.Vertex.GeometryUserScalars, banks.Shader.Vertex.GeometryUserScalars);
        Assert.Same(snapshot.Shader.Vertex.HullUserScalars, banks.Shader.Vertex.HullUserScalars);
        Assert.Equal(1u, snapshot.Shader.Vertex.GeometryUserScalars.Values[0]);
        Assert.Equal(3u, banks.Shader.Vertex.GeometryUserScalars.Values[0]);
        Assert.NotSame(snapshot.Shader.Pixel, banks.Shader.Pixel);
        Assert.Same(snapshot.Shader.Pixel.UserScalars, banks.Shader.Pixel.UserScalars);

        // The set copied after the snapshot is the live banks' own: a later write does not copy it again.
        var geometry = banks.Shader.Vertex.GeometryUserScalars;
        WriteShader(banks, SpiShaderUserDataGs0, 4);
        Assert.Same(geometry, banks.Shader.Vertex.GeometryUserScalars);
        Assert.Equal(1u, snapshot.Shader.Vertex.GeometryUserScalars.Values[0]);
    }

    [Fact]
    public void ContextStateOperationsAfterASnapshotLeaveItUnchanged()
    {
        var banks = NewBanks();
        WriteContext(banks, DbDepthControl, 0x0000_0006);
        var snapshot = banks.Snapshot();

        banks.ApplyContextState(ContextStateOperation.PushClear);
        WriteContext(banks, DbDepthControl, 0x0000_0000);
        banks.ApplyContextState(ContextStateOperation.Pop);
        WriteContext(banks, DbDepthControl, 0x0000_0000);

        Assert.True(snapshot.Context.DepthTarget.DepthTestEnabled);
        Assert.False(banks.Context.DepthTarget.DepthTestEnabled);
    }

    // Every context register, through the table path and the packet path, against a snapshot
    // taken after a first write: the context's nested parts are copied only by their writers.
    [Fact]
    public void EveryContextRegisterWriteLeavesASnapshotUnchanged()
    {
        var changed = 0;
        for (var offset = 0u; offset < RegisterBankLayout.ContextRegisterCount; offset++)
        {
            foreach (var packet in new[] { false, true })
            {
                var banks = NewBanks();
                WriteContext(banks, DbDepthControl, 0x0000_0006);
                var snapshot = banks.Snapshot();
                var before = Describe(snapshot.Context);
                foreach (var value in new[] { 0xFFFF_FFFFu, 0x1234_5678u })
                {
                    try
                    {
                        if (packet)
                            WriteContext(banks, offset, value);
                        else
                            RegisterWriteTable.WriteContextEntry(banks, offset, value, PacketAddress);
                    }
                    catch (InvalidOperationException)
                    {
                        // A register whose writer refuses a single value.
                    }
                }

                Assert.True(before == Describe(snapshot.Context), $"offset=0x{offset:X} packet={packet}");
                if (Describe(banks.Context) != before)
                    changed++;
            }
        }

        // The description sees the writes: most registers store something.
        Assert.True(changed > 400, $"changed={changed}");
    }

    private static string Describe(object? value)
    {
        var text = new System.Text.StringBuilder();
        Describe(value, text);
        return text.ToString();
    }

    private static void Describe(object? value, System.Text.StringBuilder text)
    {
        switch (value)
        {
            case null:
                text.Append("null;");
                return;
            case System.Collections.IDictionary dictionary:
                foreach (var key in dictionary.Keys.Cast<object>().OrderBy(key => key))
                    text.Append(key).Append('=').Append(dictionary[key]).Append(',');
                text.Append(';');
                return;
            case Array array:
                text.Append('[');
                foreach (var element in array)
                    Describe(element, text);
                text.Append(']');
                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string)
        {
            text.Append(value).Append(';');
            return;
        }

        text.Append('{');
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            text.Append(field.Name).Append(':');
            Describe(field.GetValue(value), text);
        }

        text.Append('}');
    }

    [Fact]
    public void AContextWriteCopiesOnlyTheNestedPartsItStoresInto()
    {
        var banks = NewBanks();
        var snapshot = banks.Snapshot();

        WriteContext(banks, DbDepthControl, 0x0000_0006);
        WriteContext(banks, PaClVteCntl, 0x43F);

        Assert.NotSame(snapshot.Context, banks.Context);
        Assert.NotSame(snapshot.Context.ScreenViewport, banks.Context.ScreenViewport);
        Assert.Same(snapshot.Context.ScreenViewport.Viewports, banks.Context.ScreenViewport.Viewports);
        Assert.Same(snapshot.Context.ShaderInterface, banks.Context.ShaderInterface);
        Assert.Same(snapshot.Context.ColorTargets, banks.Context.ColorTargets);
        Assert.Same(snapshot.Context.UnmodeledTableRegisters, banks.Context.UnmodeledTableRegisters);
    }

    [Fact]
    public void ASecondSnapshotSharesAgainUntilTheNextWrite()
    {
        var banks = NewBanks();
        var first = banks.Snapshot();
        WriteContext(banks, DbDepthControl, 0x0000_0006);
        var second = banks.Snapshot();
        WriteContext(banks, DbDepthControl, 0x0000_0000);

        Assert.False(first.Context.DepthTarget.DepthTestEnabled);
        Assert.True(second.Context.DepthTarget.DepthTestEnabled);
        Assert.False(banks.Context.DepthTarget.DepthTestEnabled);
    }
}
