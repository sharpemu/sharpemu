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
        if (type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InlineArrayAttribute), false) is [System.Runtime.CompilerServices.InlineArrayAttribute inline])
        {
            var element = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0].FieldType;
            var elements = typeof(RegisterBanksSnapshotTests).GetMethod(nameof(InlineElements), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(type, element).Invoke(null, [value, inline.Length]);
            Describe(elements, text);
            return;
        }

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
        Assert.Same(snapshot.Context.ScreenViewport.Secondary, banks.Context.ScreenViewport.Secondary);
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

    private static object?[] InlineElements<TArray, TElement>(object boxed, int length)
        where TArray : struct
    {
        var array = (TArray)boxed;
        var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
            ref System.Runtime.CompilerServices.Unsafe.As<TArray, TElement>(ref array), length);
        var elements = new object?[length];
        for (var index = 0; index < length; index++)
        {
            elements[index] = span[index];
        }

        return elements;
    }

    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void APrimaryViewportWriteSharesTheSecondaryViewports()
    {
        var banks = NewBanks();
        WriteContext(banks, PaClViewportXScale, Float(2f));
        var snapshot = banks.Snapshot();

        WriteContext(banks, PaClViewportXScale, Float(4f));
        WriteContext(banks, PaScViewportScissor0Tl, 3u | (5u << 16));

        Assert.Equal(2f, snapshot.Context.ScreenViewport.Primary.XScale);
        Assert.Equal(0, snapshot.Context.ScreenViewport.Primary.ScissorLeft);
        Assert.Equal(4f, banks.Context.ScreenViewport.Primary.XScale);
        Assert.Equal(3, banks.Context.ScreenViewport.Primary.ScissorLeft);
        Assert.NotSame(snapshot.Context.ScreenViewport, banks.Context.ScreenViewport);
        Assert.Same(snapshot.Context.ScreenViewport.Secondary, banks.Context.ScreenViewport.Secondary);
    }

    [Fact]
    public void ASecondaryViewportWriteCopiesTheSecondaryViewportsOnce()
    {
        var banks = NewBanks();
        var snapshot = banks.Snapshot();
        const uint viewportFieldCount = 6;

        WriteContext(banks, PaClViewportXScale + (5 * viewportFieldCount), Float(8f));
        var copied = banks.Context.ScreenViewport.Secondary;
        WriteContext(banks, PaClViewportXScale + (6 * viewportFieldCount), Float(9f));

        Assert.NotSame(snapshot.Context.ScreenViewport.Secondary, copied);
        Assert.Same(copied, banks.Context.ScreenViewport.Secondary);
        Assert.Equal(0f, snapshot.Context.ScreenViewport.Viewport(5).XScale);
        Assert.Equal(8f, banks.Context.ScreenViewport.Viewport(5).XScale);
        Assert.Equal(9f, banks.Context.ScreenViewport.Viewport(6).XScale);
        Assert.Equal(0f, banks.Context.ScreenViewport.Primary.XScale);
    }

    [Fact]
    public void AClipRectangleWriteAfterASnapshotLeavesItUnchanged()
    {
        var banks = NewBanks();
        WriteContext(banks, PaScClipRect0Tl + 2, 1u | (2u << 16));
        var snapshot = banks.Snapshot();

        WriteContext(banks, PaScClipRect0Tl + 2, 7u | (8u << 16));

        Assert.Equal(1, snapshot.Context.ScreenViewport.ClipRectangleLeft[1]);
        Assert.Equal(2, snapshot.Context.ScreenViewport.ClipRectangleTop[1]);
        Assert.Equal(7, banks.Context.ScreenViewport.ClipRectangleLeft[1]);
        Assert.Equal(8, banks.Context.ScreenViewport.ClipRectangleTop[1]);
    }

    [Fact]
    public void ASnapshotKeepsTheStampsOfItsDraw()
    {
        var banks = NewBanks();
        var snapshot = banks.Snapshot();
        var context = snapshot.ContextStamp;
        var pixelUserData = snapshot.Stamp(RegisterStamp.PixelUserData);

        WriteContext(banks, DbDepthControl, 0x0000_0006);
        WriteShader(banks, SpiShaderUserDataPs0, 1);

        Assert.Equal(context, snapshot.ContextStamp);
        Assert.Equal(pixelUserData, snapshot.Stamp(RegisterStamp.PixelUserData));
        Assert.NotEqual(context, banks.ContextStamp);
        Assert.NotEqual(pixelUserData, banks.Stamp(RegisterStamp.PixelUserData));
    }

    [Fact]
    public void ASnapshotIntoAnOldShellTakesTheLiveBanksAndStaysUnchanged()
    {
        var other = NewBanks();
        WriteContext(other, DbDepthControl, 0x0000_0000);
        WriteShader(other, SpiShaderUserDataPs0, 99);
        var shell = other.Snapshot();
        var banks = NewBanks();
        WriteContext(banks, DbDepthControl, 0x0000_0006);
        WriteShader(banks, SpiShaderUserDataPs0, 11);
        WriteShader(banks, SpiShaderPgmLoPs, 0x1000);
        WriteContext(banks, PaClViewportXScale, Float(2f));

        var snapshot = banks.Snapshot(shell);
        var fresh = Describe(banks.Snapshot().Context) + Describe(banks.Shader);
        WriteContext(banks, DbDepthControl, 0x0000_0000);
        WriteShader(banks, SpiShaderUserDataPs0, 21);
        WriteShader(banks, SpiShaderPgmLoPs, 0x5000);
        WriteContext(banks, PaClViewportXScale, Float(4f));

        Assert.Same(shell, snapshot);
        Assert.Equal(fresh, Describe(snapshot.Context) + Describe(snapshot.Shader));
        Assert.True(snapshot.Context.DepthTarget.DepthTestEnabled);
        Assert.Equal(11u, snapshot.Shader.Pixel.UserScalars.Values[0]);
        Assert.Equal(0x1000ul << 8, snapshot.Shader.Pixel.Address);
        Assert.Equal(2f, snapshot.Context.ScreenViewport.Primary.XScale);
        Assert.True(banks.Stamp(RegisterStamp.ShaderPrograms) > snapshot.Stamp(RegisterStamp.ShaderPrograms));
        Assert.Equal(99u, other.Shader.Pixel.UserScalars.Values[0]);
        Assert.False(other.Context.DepthTarget.DepthTestEnabled);
    }

    [Fact]
    public void ASnapshotIntoAShellAllocatesNothing()
    {
        var banks = NewBanks();
        var shell = banks.Snapshot();
        var allocated = AllocationMeasurement.SteadyState(
            () => banks.Snapshot(shell),
            () =>
            {
                for (var index = 0; index < 64; index++)
                {
                    shell = banks.Snapshot(shell);
                }
            });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ASnapshotAndTheWritesOfADrawAllocateLittle()
    {
        var banks = NewBanks();
        var userData = new uint[8];
        var viewport = new[] { Float(1f), Float(2f), Float(3f), Float(4f), Float(5f), Float(6f) };
        var depth = new uint[1];
        var blend = new uint[1];
        var scissor = new[] { 1u, 2u };
        var snapshots = new RegisterBanks[64];
        void Draw(int index)
        {
            snapshots[index] = banks.Snapshot(snapshots[index]);
            userData[0] = (uint)index;
            depth[0] = (uint)index & 6;
            blend[0] = (uint)index;
            WriteShader(banks, SpiShaderUserDataPs0, userData);
            WriteShader(banks, SpiShaderUserDataGs0, userData);
            WriteContext(banks, DbDepthControl, depth);
            WriteContext(banks, PaClViewportXScale, viewport);
            WriteContext(banks, CbBlend0Control, blend);
            WriteContext(banks, PaScViewportScissor0Tl, scissor);
        }

        var allocated = AllocationMeasurement.SteadyState(
            () => Draw(0),
            () =>
            {
                for (var index = 0; index < snapshots.Length; index++)
                {
                    Draw(index);
                }
            });

        Assert.True(allocated / snapshots.Length <= 1400 + sizeof(ulong), $"bytes per draw={allocated / snapshots.Length}");
    }
}
