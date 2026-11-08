// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.UserConfigRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

public sealed class RegisterStampsTests
{
    private const ulong PacketAddress = 0x1_0000_1000;

    private static readonly string[] DynamicContextMembers = ["ScreenViewport", "BlendColor", "LineWidth", "PolygonOffset"];

    private static readonly string[] ColorTargetMembers = ["ColorTargets", "ColorClearWord1", "RenderTargetMask", "ColorControl"];

    private static readonly string[] DepthTargetMembers =
    [
        "DepthTarget", "DepthRenderOverride", "DepthBoundsMin", "DepthBoundsMax", "DepthClearValue", "StencilClearValue", "StencilControl",
        "StencilMask", "ReservedDepthRegister", "ReservedDepthRegister1", "ReservedDepthRegister3", "DepthCountControl",
    ];

    private static readonly string[] ShaderInterfaceMembers = ["ShaderInterface", "Clip", "ShaderStages"];

    private static readonly RegisterStamp[] ContextStamps =
    [
        RegisterStamp.ColorTargets, RegisterStamp.DepthTargets, RegisterStamp.ShaderInterface, RegisterStamp.PipelineState, RegisterStamp.ContextDynamic,
    ];

    private static RegisterStamp StampOfContextMember(string member) =>
        ColorTargetMembers.Contains(member) ? RegisterStamp.ColorTargets
        : DepthTargetMembers.Contains(member) ? RegisterStamp.DepthTargets
        : ShaderInterfaceMembers.Contains(member) ? RegisterStamp.ShaderInterface
        : RegisterStamp.PipelineState;

    [Fact]
    public void EachContextRegisterFamilyMovesOnlyItsConsumersStamp()
    {
        Assert.Equal(RegisterStamp.DepthTargets, RegisterStamps.ContextStampOf(DbDepthControl));
        Assert.Equal(RegisterStamp.DepthTargets, RegisterStamps.ContextStampOf(DbZInfo));
        Assert.Equal(RegisterStamp.DepthTargets, RegisterStamps.ContextStampOf(DbStencilControl));
        Assert.Equal(RegisterStamp.ColorTargets, RegisterStamps.ContextStampOf(CbColor0Base + 0x0F));
        Assert.Equal(RegisterStamp.ColorTargets, RegisterStamps.ContextStampOf(CbColor0Attrib3 + 7));
        Assert.Equal(RegisterStamp.ShaderInterface, RegisterStamps.ContextStampOf(CbShaderMask));
        Assert.Equal(RegisterStamp.ShaderInterface, RegisterStamps.ContextStampOf(SpiShaderColFormat));
        Assert.Equal(RegisterStamp.ShaderInterface, RegisterStamps.ContextStampOf(PaClClipCntl));
        Assert.Equal(RegisterStamp.PipelineState, RegisterStamps.ContextStampOf(CbBlend0Control));
        Assert.Equal(RegisterStamp.PipelineState, RegisterStamps.ContextStampOf(PaSuScModeCntl));
        Assert.Equal(RegisterStamp.ContextDynamic, RegisterStamps.ContextStampOf(PaClViewportXScale));

        var banks = NewBanks();
        var before = Stamps(banks);
        RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, 1), DbDepthControl, [0x0000_0006]);
        Assert.Equal([RegisterStamp.DepthTargets], Moved(banks, before));
    }

    [Fact]
    public void TheContextStampIsTheLatestOfTheNonDynamicContextStamps()
    {
        var banks = NewBanks();
        var nonDynamic = new[] { RegisterStamp.ColorTargets, RegisterStamp.DepthTargets, RegisterStamp.ShaderInterface, RegisterStamp.PipelineState };
        Assert.Equal(nonDynamic.Select(stamp => banks.Stamp(stamp)).Max(), banks.ContextStamp);
        foreach (var offset in new[] { CbTargetMask, DbDepthControl, CbShaderMask, CbBlend0Control })
        {
            var previous = banks.ContextStamp;
            RegisterWriteTable.WriteContextEntry(banks, offset, 1, 0x5000);
            Assert.True(banks.ContextStamp > previous);
            Assert.Equal(nonDynamic.Select(stamp => banks.Stamp(stamp)).Max(), banks.ContextStamp);
        }

        var unchanged = banks.ContextStamp;
        RegisterWriteTable.WriteContextEntry(banks, PaClViewportXScale, 1, 0x5000);
        Assert.Equal(unchanged, banks.ContextStamp);
    }

    private static RegisterBanks NewBanks() => new(static message => new InvalidOperationException(message));

    private static PacketContext Packet(uint opcode, int valueCount) =>
        new(PacketHeader.Make((uint)valueCount + 2, opcode), PacketAddress, 0, 100, 100);

    private static ulong[] Stamps(RegisterBanks banks) =>
        Enumerable.Range(0, RegisterStamps.Count).Select(stamp => banks.Stamp((RegisterStamp)stamp)).ToArray();

    private static RegisterStamp[] Moved(RegisterBanks banks, ulong[] before) =>
        Enumerable.Range(0, RegisterStamps.Count).Where(stamp => banks.Stamp((RegisterStamp)stamp) != before[stamp])
            .Select(static stamp => (RegisterStamp)stamp).ToArray();

    [Fact]
    public void AContextWriteMovesTheContextStampAndADynamicStateWriteItsOwn()
    {
        var banks = NewBanks();
        var before = Stamps(banks);
        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, 0xF, 0x5000);
        Assert.Equal([RegisterStamp.ColorTargets], Moved(banks, before));

        before = Stamps(banks);
        RegisterWriteTable.WriteContextEntry(banks, PaClViewportXScale, BitConverter.SingleToUInt32Bits(960f), 0x5000);
        Assert.Equal([RegisterStamp.ContextDynamic], Moved(banks, before));

        before = Stamps(banks);
        RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, 2), PaScGenericScissorTl, [0, 0x04380780]);
        Assert.Equal([RegisterStamp.ContextDynamic], Moved(banks, before));
    }

    [Fact]
    public void ASnapshotKeepsTheStampsItWasTakenWith()
    {
        var banks = NewBanks();
        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, 0xF, 0x5000);
        var snapshot = banks.Snapshot();
        var taken = Stamps(snapshot);
        Assert.Equal(Stamps(banks), taken);

        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, 0x3, 0x5000);
        Assert.Equal([RegisterStamp.ColorTargets], Moved(banks, taken));
        Assert.Empty(Moved(snapshot, taken));
        Assert.Equal(Stamps(banks), Stamps(banks.Clone()));
    }

    [Fact]
    public void ARangeAcrossBothKindsOfContextRegistersMovesBothStamps()
    {
        Assert.Equal(1u << (int)RegisterStamp.ContextDynamic, RegisterStamps.ContextBitsFor(PaScGenericScissorTl, 2));
        Assert.Equal((1u << (int)RegisterStamp.ContextDynamic) | (1u << (int)RegisterStamp.PipelineState), RegisterStamps.ContextBitsFor(PaScGenericScissorBr, 2));
        Assert.Equal((1u << (int)RegisterStamp.ColorTargets) | (1u << (int)RegisterStamp.ShaderInterface), RegisterStamps.ContextBitsFor(CbTargetMask, 2));
        Assert.Equal(0u, RegisterStamps.ContextBitsFor(CbTargetMask, 0));
    }

    [Fact]
    public void TheDynamicContextRegistersAreTheViewportScissorBlendColourLineAndPolygonOffsetFamilies()
    {
        var dynamic = Enumerable.Range(0, (int)RegisterBankLayout.ContextRegisterCount).Where(static offset => RegisterStamps.IsDynamicContext((uint)offset)).ToArray();

        Assert.Equal(187, dynamic.Length);
        Assert.All(dynamic, static offset =>
            Assert.True(RegisterWriteTable.Context[offset] is not null || RegisterWriteTable.ContextIndirect[offset] is not null, $"offset 0x{offset:X3} has no writer"));
    }

    [Fact]
    public void AShaderWriteMovesTheStampOfItsUserDataWindowOrOfThePrograms()
    {
        var banks = NewBanks();
        var before = Stamps(banks);
        RegisterWriteTable.WriteShaderPacket(banks, Packet(PacketOpcode.SetShaderRegister, 2), SpiShaderUserDataPs0 + 3, [1, 2]);
        Assert.Equal([RegisterStamp.PixelUserData], Moved(banks, before));

        before = Stamps(banks);
        RegisterWriteTable.WriteShaderEntry(banks, SpiShaderUserDataGs0 + 31, 7, 0x5000);
        Assert.Equal([RegisterStamp.VertexUserData], Moved(banks, before));

        before = Stamps(banks);
        RegisterWriteTable.WriteShaderEntry(banks, ComputeUserData0, 7, 0x5000);
        Assert.Equal([RegisterStamp.ComputeUserData], Moved(banks, before));

        before = Stamps(banks);
        RegisterWriteTable.WriteShaderEntry(banks, SpiShaderPgmLoPs, 0x1000, 0x5000);
        Assert.Equal([RegisterStamp.ShaderPrograms], Moved(banks, before));

        Assert.Equal(RegisterStamp.ShaderPrograms, RegisterStamps.ShaderStampOf(SpiShaderUserDataPs0 - 1));
        Assert.Equal(RegisterStamp.ShaderPrograms, RegisterStamps.ShaderStampOf(SpiShaderUserDataPs0 + 32));
        Assert.Equal(RegisterStamp.ShaderPrograms, RegisterStamps.ShaderStampOf(SpiShaderUserDataVs0));
        Assert.Equal(RegisterStamp.ShaderPrograms, RegisterStamps.ShaderStampOf(ComputeUserData0 + 16));
    }

    [Fact]
    public void AUserConfigWriteMovesItsStamp()
    {
        var banks = NewBanks();
        var before = Stamps(banks);

        RegisterWriteTable.WriteUserConfigPacket(banks, Packet(PacketOpcode.SetUserConfigRegister, 1), VgtPrimitiveType, [4]);

        Assert.Equal([RegisterStamp.UserConfig], Moved(banks, before));
    }

    [Theory]
    [InlineData(ContextStateOperation.Clear)]
    [InlineData(ContextStateOperation.Push)]
    [InlineData(ContextStateOperation.PushClear)]
    public void AContextStateOperationMovesBothContextStamps(ContextStateOperation operation)
    {
        var banks = NewBanks();
        var before = Stamps(banks);

        banks.ApplyContextState(operation);

        Assert.Equal(ContextStamps, Moved(banks, before));
    }

    [Fact]
    public void APopAndAResetMoveTheStampsOfWhatTheyReplace()
    {
        var banks = NewBanks();
        banks.ApplyContextState(ContextStateOperation.Push);
        var before = Stamps(banks);
        banks.ApplyContextState(ContextStateOperation.Pop);
        Assert.Equal(ContextStamps, Moved(banks, before));

        before = Stamps(banks);
        banks.Reset();
        Assert.Equal(RegisterStamps.Count, Moved(banks, before).Length);
    }

    [Fact]
    public void ACloneKeepsItsOwnStamps()
    {
        var banks = NewBanks();
        var clone = banks.Clone();
        var before = Stamps(clone);

        RegisterWriteTable.WriteContextEntry(banks, CbTargetMask, 0xF, 0x5000);

        Assert.Empty(Moved(clone, before));
    }

    [Fact]
    public void EveryRegisterWriteMovesAStampThatCoversWhatItChanged()
    {
        uint[] probes = [1, 0x3F800000, 0xFFFFFFFF, 0x00010001, 0x12345678];
        var changingWrites = 0;
        void Check(string bank, uint offset, Action<RegisterBanks, uint> write)
        {
            foreach (var probe in probes)
            {
                var banks = NewBanks();
                var original = banks.Clone();
                var before = Stamps(banks);
                try
                {
                    write(banks, probe);
                }
                catch (Exception)
                {
                    continue;
                }

                var moved = Moved(banks, before);
                var changes = ChangedMembers(original, banks);
                changingWrites += changes.Count != 0 ? 1 : 0;
                foreach (var change in changes)
                {
                    Assert.True(Covers(moved, change),
                        $"{bank} offset 0x{offset:X3} value 0x{probe:X8} changed {change} but moved only [{string.Join(",", moved)}]");
                }
            }
        }

        for (var offset = 0u; offset < RegisterBankLayout.ContextRegisterCount; offset++)
        {
            var register = offset;
            if (RegisterWriteTable.ContextIndirect[register] is not null)
                Check("context entry", register, (banks, value) => RegisterWriteTable.WriteContextEntry(banks, register, value, 0x5000));
            if (RegisterWriteTable.Context[register] is not null || RegisterWriteTable.ContextIndirect[register] is not null)
                Check("context packet", register, (banks, value) =>
                    RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, 1), register, [value]));
        }

        for (var offset = 0u; offset < RegisterBankLayout.ShaderRegisterCount; offset++)
        {
            var register = offset;
            if (RegisterWriteTable.ShaderIndirect[register] is not null)
                Check("shader entry", register, (banks, value) => RegisterWriteTable.WriteShaderEntry(banks, register, value, 0x5000));
            if (RegisterWriteTable.Shader[register] is not null || RegisterWriteTable.ShaderIndirect[register] is not null)
                Check("shader packet", register, (banks, value) =>
                    RegisterWriteTable.WriteShaderPacket(banks, Packet(PacketOpcode.SetShaderRegister, 1), register, [value]));
        }

        for (var offset = 0u; offset < RegisterBankLayout.UserConfigRegisterCount; offset++)
        {
            var register = offset;
            if (RegisterWriteTable.UserConfigIndirect[register] is not null)
                Check("user config entry", register, (banks, value) => RegisterWriteTable.WriteUserConfigEntry(banks, register, value, 0x5000));
            if (RegisterWriteTable.UserConfig[register] is not null || RegisterWriteTable.UserConfigIndirect[register] is not null)
                Check("user config packet", register, (banks, value) =>
                    RegisterWriteTable.WriteUserConfigPacket(banks, Packet(PacketOpcode.SetUserConfigRegister, 1), register, [value]));
        }

        Assert.True(changingWrites > 500, $"only {changingWrites} writes changed a bank");
    }

    private static bool Covers(RegisterStamp[] moved, string change)
    {
        var separator = change.IndexOf('.');
        var bank = change[..separator];
        var member = change[(separator + 1)..];
        return bank switch
        {
            "Context" => moved.Contains(StampOfContextMember(member)) ||
                (moved.Contains(RegisterStamp.ContextDynamic) && DynamicContextMembers.Contains(member)),
            "Shader" => member switch
            {
                "Pixel.UserScalars" => moved.Contains(RegisterStamp.PixelUserData),
                "Vertex.GeometryUserScalars" => moved.Contains(RegisterStamp.VertexUserData),
                "Compute.UserScalars" => moved.Contains(RegisterStamp.ComputeUserData),
                _ => moved.Contains(RegisterStamp.ShaderPrograms),
            },
            _ => moved.Contains(RegisterStamp.UserConfig),
        };
    }

    private static List<string> ChangedMembers(RegisterBanks before, RegisterBanks after)
    {
        var changes = new List<string>();
        foreach (var (name, left, right) in Members(before.Context, after.Context))
        {
            if (!DeepEquals(left, right)) changes.Add($"Context.{name}");
        }

        foreach (var (stage, leftStage, rightStage) in Members(before.Shader, after.Shader))
        {
            if (leftStage is null || rightStage is null || leftStage.GetType().IsValueType || leftStage is Array)
            {
                if (!DeepEquals(leftStage, rightStage)) changes.Add($"Shader.{stage}");
                continue;
            }

            foreach (var (name, left, right) in Members(leftStage, rightStage))
            {
                if (!DeepEquals(left, right)) changes.Add($"Shader.{stage}.{name}");
            }
        }

        foreach (var (name, left, right) in Members(before.UserConfig, after.UserConfig))
        {
            if (!DeepEquals(left, right)) changes.Add($"UserConfig.{name}");
        }

        return changes;
    }

    private static IEnumerable<(string Name, object? Left, object? Right)> Members(object left, object right)
    {
        foreach (var field in left.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var name = field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>')] : field.Name;
            yield return (name, field.GetValue(left), field.GetValue(right));
        }
    }

    private static bool DeepEquals(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        var type = left.GetType();
        if (type != right.GetType()) return false;
        if (type.IsPrimitive || type.IsEnum || left is string) return left.Equals(right);
        if (left is Delegate) return true;
        if (type.IsValueType && type.GetCustomAttribute<InlineArrayAttribute>() is not null)
            return (bool)typeof(RegisterStampsTests).GetMethod(nameof(SameBytes), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type).Invoke(null, [left, right])!;
        if (left is IEnumerable leftItems)
        {
            var leftList = leftItems.Cast<object?>().ToList();
            var rightList = ((IEnumerable)right).Cast<object?>().ToList();
            return leftList.Count == rightList.Count && leftList.Zip(rightList).All(static pair => DeepEquals(pair.First, pair.Second));
        }

        return Members(left, right).All(static member => DeepEquals(member.Left, member.Right));
    }

    private static bool SameBytes<T>(object left, object right)
        where T : struct
    {
        var leftValue = (T)left;
        var rightValue = (T)right;
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref leftValue))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref rightValue)));
    }
}
