// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// DPP16 write rules and the wave32 row reductions of Gen5SpirvTranslator.WaveReductions, run on the device.
public sealed class WaveReductionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint EvenLanes = 0x5555_5555;

    // v4 starts at 1000 + lane, then V_MOV_B32 v4, v3 (v3 = lane + 1) runs with the DPP control under the EXEC mask.
    [Theory]
    [InlineData("InactiveSource", 0x111u, false, false, 15u, EvenLanes)]
    [InlineData("InactiveSourceBoundControl", 0x111u, false, true, 15u, EvenLanes)]
    [InlineData("InactiveSourceFetchInactive", 0x111u, true, false, 15u, EvenLanes)]
    [InlineData("BankMask", 0xE4u, true, false, 0b0010u, uint.MaxValue)]
    public void Dpp16_WritesFollowSourceValidityAndBanks(string name, uint control, bool fetchInactive, bool boundControl, uint bankMask, uint exec)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Build(
            Vop2(0, "VAddI32", 3, Operand(1), Gen5Operand.Vector(0)),
            Vop2(0, "VAddI32", 4, Operand(1000), Gen5Operand.Vector(0)),
            MoveScalar(0, 126, exec),
            Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with
            {
                Control = new Gen5DppControl(control, fetchInactive, boundControl, 0, 0, bankMask, 15),
            },
            MoveScalar(0, 126, uint.MaxValue),
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        var actual = Run(vulkan, program, threads: 32, subgroupArithmetic: false, bytes: 128);
        for (uint lane = 0; lane < 32; lane++)
        {
            var active = (exec >> (int)lane & 1) != 0;
            var rowLane = lane & 15;
            var source = name == "BankMask" ? lane : lane - 1;
            var sourceValid = name == "BankMask" || rowLane != 0 && (fetchInactive || (exec >> (int)source & 1) != 0);
            var bankEnabled = (bankMask >> (int)(lane >> 2 & 3) & 1) != 0;
            var expected = !active || !bankEnabled ? 1000 + lane
                : sourceValid ? source + 1
                : boundControl ? 0u
                : 1000 + lane;
            Assert.True(expected == Read(actual, lane), $"{name}: lane {lane} expected {expected}, got {Read(actual, lane)}");
        }
    }

    // A guest scan: s80 copies the entry EXEC and drops lanes 19 and up, lanes outside s0 = EXEC & s80 take the
    // identity, a DPP row_shr 1/2/4/8 scan runs, then lanes 15 and 31 are read and combined. A workgroup of fewer than 32
    // threads leaves host invocations missing from the subgroup. Each lane stores the two reads and the combined value.
    [Theory]
    [InlineData("VMaxU32", 32u)]
    [InlineData("VMinU32", 32u)]
    [InlineData("VOrB32", 32u)]
    [InlineData("VAddU32", 32u)]
    [InlineData("VMaxU32", 20u)]
    [InlineData("VMinU32", 20u)]
    [InlineData("VOrB32", 20u)]
    [InlineData("VAddU32", 20u)]
    [InlineData("VMaxU32", 12u)]
    [InlineData("VMinU32", 12u)]
    [InlineData("VOrB32", 12u)]
    [InlineData("VAddU32", 12u)]
    public void Wave32RowScan_ReadsEachRowOverTheLanesThatExist(string operation, uint threads)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var actual = Run(vulkan, Build(ScanProgram(operation)), threads, subgroupArithmetic: true, bytes: 512);
        var rowZero = Fold(operation, 0, threads);
        var rowOne = Fold(operation, 1, threads);
        var combined = Combine(operation, rowZero, rowOne);
        for (uint lane = 0; lane < threads; lane++)
        {
            Assert.Equal(rowZero, Read(actual, lane * 4));
            Assert.Equal(rowOne, Read(actual, lane * 4 + 1));
            Assert.Equal(combined, Read(actual, lane * 4 + 2));
        }

        // A full subgroup gives the same values without the reduction.
        if (threads == 32)
        {
            var scanned = Run(vulkan, Build(ScanProgram(operation)), threads, subgroupArithmetic: false, bytes: 512);
            Assert.Equal(actual[..(32 * 16)], scanned[..(32 * 16)]);
        }
    }

    // Only the exact scan becomes a reduction.
    [Theory]
    [InlineData("Exact", 2)]
    [InlineData("NoSubgroupArithmetic", 0)]
    [InlineData("IdentityChanged", 0)]
    [InlineData("ShiftOrder", 0)]
    [InlineData("BoundControl", 0)]
    [InlineData("OtherLane", 0)]
    [InlineData("ExecNotSaved", 0)]
    [InlineData("ScanSourceChanged", 0)]
    [InlineData("MaskAllLanes", 0)]
    [InlineData("MaskNotFromEntryCopy", 0)]
    [InlineData("MaskInVcc", 0)]
    [InlineData("EntryCopyOverwritten", 0)]
    [InlineData("EntryCopyAfterExecWrite", 0)]
    [InlineData("EntryCopyInOddRegister", 2)]
    [InlineData("EntryCopyPairWrite", 0)]
    [InlineData("MaskInOddRegister", 2)]
    [InlineData("MaskPairWrite", 0)]
    [InlineData("SavedSourceKept", 2)]
    [InlineData("SavedSourcePairWrite", 0)]
    public void Wave32RowScan_OnlyTheExactScanIsReduced(string variant, int reductions)
    {
        var instructions = ScanProgram(
            "VMaxU32",
            entry: variant is "EntryCopyInOddRegister" or "EntryCopyPairWrite" ? 81u : 80u,
            mask: variant is "MaskInOddRegister" or "MaskPairWrite" ? 1u : 0u).ToList();
        int Find(string opcode, int skip = 0) => instructions.FindIndex(skip, instruction => instruction.Opcode == opcode);
        var cndmask = Find("VCndmaskB32");
        var maskWrite = Find("SAndB32");
        switch (variant)
        {
            case "IdentityChanged":
                instructions[cndmask] = instructions[cndmask] with { Sources = [Operand(5), .. instructions[cndmask].Sources.Skip(1)] };
                break;
            case "ShiftOrder":
                (instructions[cndmask + 1], instructions[cndmask + 2]) = (instructions[cndmask + 2], instructions[cndmask + 1]);
                break;
            case "BoundControl":
                instructions[cndmask + 3] = instructions[cndmask + 3] with { Control = new Gen5DppControl(0x114, false, true, 0, 0, 15, 15) };
                break;
            case "OtherLane":
                var read = Find("VReadlaneB32");
                instructions[read] = ReadLane(0, 1, 2, 14);
                break;
            case "ExecNotSaved":
                var saveexec = Find("SOrn2SaveexecB32");
                instructions[saveexec] = Sop1(0, "SOrn2SaveexecB32", 106, Gen5Operand.Scalar(80));
                break;
            case "ScanSourceChanged":
                instructions[cndmask + 2] = instructions[cndmask + 2] with { Sources = [Gen5Operand.Vector(1), Gen5Operand.Vector(2)] };
                break;
            case "MaskAllLanes":
                // A lane missing from the host subgroup would take value, not the identity, on the PS5.
                instructions[maskWrite] = MoveScalar(0, 0, uint.MaxValue);
                break;
            case "MaskNotFromEntryCopy":
                instructions[maskWrite] = Sop2(0, "SAndB32", 0, Gen5Operand.Scalar(126), Gen5Operand.Scalar(81));
                instructions.Insert(0, MoveScalar(0, 81, 0x7FFFF));
                break;
            case "MaskInVcc":
                instructions[cndmask] = Vop2(0, "VCndmaskB32", 2, Operand(0), Gen5Operand.Vector(1));
                break;
            case "EntryCopyOverwritten":
                instructions.Insert(maskWrite, Sop2(0, "SOrB32", 80, Gen5Operand.Scalar(80), Operand(1u << 25)));
                break;
            case "EntryCopyAfterExecWrite":
                instructions.Insert(0, Sop2(0, "SAndB32", 126, Gen5Operand.Scalar(126), Operand(uint.MaxValue)));
                break;
            case "EntryCopyPairWrite":
                // S_MOV_B64 s[80:81] names s80 only.
                instructions.Insert(maskWrite, Sop1(0, "SMovB64", 80, Gen5Operand.Scalar(8)));
                break;
            case "MaskPairWrite":
                // S_MOV_B64 s[0:1] names s0 only.
                instructions.Insert(maskWrite + 1, Sop1(0, "SMovB64", 0, Gen5Operand.Scalar(8)));
                break;
            case "SavedSourceKept":
            case "SavedSourcePairWrite":
                // S_MOV_B32 exec_lo, s3 makes s3 hold EXEC; S_MOV_B64 s[2:3] names s2 only.
                var savedAt = Find("SOrn2SaveexecB32");
                instructions[savedAt] = Sop1(0, "SOrn2SaveexecB32", 106, Gen5Operand.Scalar(3));
                instructions.InsertRange(savedAt, variant == "SavedSourceKept"
                    ? [MoveScalarRegister(0, 3, 126), MoveScalarRegister(0, 126, 3)]
                    : [MoveScalarRegister(0, 3, 126), MoveScalarRegister(0, 126, 3), Sop1(0, "SMovB64", 2, Gen5Operand.Scalar(8))]);
                break;
        }

        var (plan, resources, layout) = Prepare(Build([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32,
            SubgroupArithmetic = variant != "NoSubgroupArithmetic",
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(reductions, CountOpcode(shader.Spirv, (ushort)SpirvOp.GroupNonUniformUMax));
    }

    // Each lane's value: 8 * lane + 100 (MAX, ADD), 1023 - 8 * lane (MIN), 1 << lane (OR).
    private static uint Value(string operation, uint lane) => operation switch
    {
        "VMinU32" => 1023 - 8 * lane,
        "VOrB32" => 1u << (int)lane,
        _ => 8 * lane + 100,
    };

    private static uint Identity(string operation) => operation == "VMinU32" ? uint.MaxValue : 0u;

    private static uint Combine(string operation, uint left, uint right) => operation switch
    {
        "VMaxU32" => Math.Max(left, right),
        "VMinU32" => Math.Min(left, right),
        "VOrB32" => left | right,
        _ => left + right,
    };

    // The operation over the lanes of a row that exist (below threads) and are in the scan mask (lanes 0 to 18).
    private static uint Fold(string operation, uint row, uint threads)
    {
        var result = Identity(operation);
        for (var lane = row * 16; lane < row * 16 + 16; lane++)
        {
            if (lane < threads && lane <= 18) result = Combine(operation, result, Value(operation, lane));
        }

        return result;
    }

    private static Gen5ShaderInstruction[] ScanProgram(string operation, uint entry = 80, uint mask = 0)
    {
        var list = new List<Gen5ShaderInstruction>
        {
            MoveScalarRegister(0, entry, 126),
            Sop2(0, "SAndn2B32", entry, Gen5Operand.Scalar(entry), Operand(0xFFF8_0000)),
            operation switch
            {
                "VMinU32" => Vop2(0, "VLshlrevB32", 1, Operand(3), Gen5Operand.Vector(0)),
                "VOrB32" => Vop2(0, "VLshlrevB32", 1, Gen5Operand.Vector(0), Operand(1)),
                _ => Vop2(0, "VLshlrevB32", 1, Operand(3), Gen5Operand.Vector(0)),
            },
            operation switch
            {
                "VMinU32" => Vop2(0, "VXorB32", 1, Operand(0x3FF), Gen5Operand.Vector(1)),
                "VOrB32" => Nop(0),
                _ => Vop2(0, "VAddI32", 1, Operand(100), Gen5Operand.Vector(1)),
            },
            Sop2(0, "SAndB32", mask, Gen5Operand.Scalar(126), Gen5Operand.Scalar(entry)),
            Sop1(0, "SOrn2SaveexecB32", 106, Gen5Operand.Scalar(126)),
            Vop3(0, "VCndmaskB32", 2, Operand(Identity(operation)), Gen5Operand.Vector(1), Gen5Operand.Scalar(mask)),
        };
        foreach (var shift in new uint[] { 1, 2, 4, 8 })
            list.Add(Vop2(0, operation, 2, Gen5Operand.Vector(2), Gen5Operand.Vector(2)) with
            {
                Control = new Gen5DppControl(0x110 + shift, false, false, 0, 0, 15, 15),
            });
        list.Add(MoveScalarRegister(0, 126, 106));
        list.Add(ReadLane(0, 1, 2, 15));
        list.Add(ReadLane(0, 2, 2, 31));
        list.Add(Sop2(0, operation switch { "VMaxU32" => "SMaxU32", "VMinU32" => "SMinU32", "VOrB32" => "SOrB32", _ => "SAddU32" },
            3, Gen5Operand.Scalar(1), Gen5Operand.Scalar(2)));
        list.Add(Sopc(0, "SCmpGtU32", Gen5Operand.Scalar(3), Operand(0)));
        list.Add(MoveVectorFromScalar(0, 4, 1));
        list.Add(MoveVectorFromScalar(0, 5, 2));
        list.Add(MoveVectorFromScalar(0, 6, 3));
        list.Add(Vop2(0, "VLshlrevB32", 7, Operand(4), Gen5Operand.Vector(0)));
        list.Add(BufferAccess(0, "BufferStoreDword", 4, offset: 0, vectorData: 4, offsetEnabled: true, vectorAddress: 7));
        list.Add(BufferAccess(0, "BufferStoreDword", 4, offset: 4, vectorData: 5, offsetEnabled: true, vectorAddress: 7));
        list.Add(BufferAccess(0, "BufferStoreDword", 4, offset: 8, vectorData: 6, offsetEnabled: true, vectorAddress: 7));
        return [.. list];
    }

    private static Gen5ShaderProgram Build(params Gen5ShaderInstruction[] instructions)
    {
        var placed = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        foreach (var instruction in instructions.Append(EndProgram(0)))
        {
            placed.Add(instruction with { Pc = pc });
            pc += 8;
        }

        return new Gen5ShaderProgram(0, [.. placed]);
    }

    private static byte[] Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, uint threads, bool subgroupArithmetic, int bytes)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = threads, LocalSizeY = 1, ThreadCountX = threads, ThreadCountY = 1, WaveSize = 32,
            SubgroupArithmetic = subgroupArithmetic,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer((ulong)bytes);
        var registers = new uint[256];
        registers[6] = (uint)bytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, (ulong)bytes);
        harness.AssertNoValidationMessages();
        return actual;
    }

    private static uint Read(byte[] data, uint word) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)word * 4));

    private static int CountOpcode(byte[] spirv, ushort opcode)
    {
        var count = 0;
        for (var offset = 20; offset + 4 <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            if ((word & 0xFFFF) == opcode) count++;
            offset += (int)(word >> 16) * 4;
            if (word >> 16 == 0) break;
        }

        return count;
    }
}
