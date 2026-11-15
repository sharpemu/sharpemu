// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The exact library used by one linked program, independent of live tables and the next session's latest union.
// Code is stored once per target address; sites retain target order, arguments and return registers.
internal sealed record LinkedShaderPrewarm(bool RuntimeRecords, bool CallMarks, bool BindlessHeap,
    LinkedPrewarmFunction[] Functions, LinkedPrewarmSite[] Sites)
{
    public static LinkedShaderPrewarm Capture(ShaderCallLibrary library, bool runtimeRecords, bool callMarks, bool bindlessHeap)
    {
        var functions = library.Calls.SelectMany(site => site.Targets).Select(target => target.Address).Distinct().Order()
            .Select(address => new LinkedPrewarmFunction(address, library.CodeRuns[address]
                .Select(run => new CodeRange(run.Address, run.Bytes.ToArray())).ToArray())).ToArray();
        var sites = library.Calls.Select(site => new LinkedPrewarmSite(
            site.Pc, site.AddressRegister, site.ArgumentRegister, site.ReturnRegister, site.ReturnAddress,
            site.Targets.Select(target => new LinkedPrewarmTarget(target.Address, target.Argument)).ToArray())).ToArray();
        return new(runtimeRecords, callMarks, bindlessHeap, functions, sites);
    }

    public Gen5ShaderProgram Link(Gen5ShaderProgram caller)
    {
        var functions = new Dictionary<ulong, Gen5ShaderProgram>();
        foreach (var function in Functions)
        {
            var memory = new ReplayCpuMemory(function.Ranges);
            var decoded = ShaderFunctionReader.Read(function.Address, (ulong address, out uint word) =>
            {
                Span<byte> bytes = stackalloc byte[sizeof(uint)];
                var read = memory.TryRead(address, bytes);
                word = read ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
                return read;
            });
            if (!decoded.Complete)
                throw new InvalidOperationException($"The prewarm callee cannot be decoded: address=0x{function.Address:X16} {decoded.Error}");
            functions.Add(function.Address, decoded.Program);
        }

        var sites = Sites.Select(site => new ShaderCallSite(site.Pc, site.AddressRegister, site.ArgumentRegister,
            site.ReturnRegister, site.ReturnAddress, site.Targets.Select(target => new ShaderCallTarget(
                target.Address, target.Argument, functions[target.Address])).ToArray())).ToArray();
        // The captured library already excludes any callees runtime linking refused. Replay must not drop more.
        return Gen5ShaderCallLinker.Link(caller, sites, RuntimeRecords);
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(RuntimeRecords);
        writer.Write(CallMarks);
        writer.Write(BindlessHeap);
        writer.Write(Functions.Length);
        foreach (var function in Functions)
        {
            writer.Write(function.Address);
            writer.Write(function.Ranges.Length);
            foreach (var range in function.Ranges)
            {
                writer.Write(range.Address);
                writer.Write(range.Bytes.Length);
                writer.Write(range.Bytes);
            }
        }
        writer.Write(Sites.Length);
        foreach (var site in Sites)
        {
            writer.Write(site.Pc);
            writer.Write(site.AddressRegister);
            writer.Write(site.ArgumentRegister);
            writer.Write(site.ReturnRegister);
            writer.Write(site.ReturnAddress);
            writer.Write(site.Targets.Length);
            foreach (var target in site.Targets)
            {
                writer.Write(target.Address);
                writer.Write(target.Argument);
            }
        }
    }

    public static LinkedShaderPrewarm Read(BinaryReader reader)
    {
        var runtimeRecords = reader.ReadBoolean();
        var callMarks = reader.ReadBoolean();
        var bindlessHeap = reader.ReadBoolean();
        if (callMarks && !runtimeRecords) throw new ArgumentException("Prewarm call marks require runtime records.");
        var functions = new LinkedPrewarmFunction[Count(reader, 12)];
        var addresses = new HashSet<ulong>();
        for (var index = 0; index < functions.Length; index++)
        {
            var address = reader.ReadUInt64();
            if (!addresses.Add(address)) throw new ArgumentException("Duplicate prewarm callee address.");
            var ranges = new CodeRange[Count(reader, 16)];
            if (ranges.Length == 0) throw new ArgumentException("The prewarm callee has no code.");
            ulong end = 0;
            for (var run = 0; run < ranges.Length; run++)
            {
                var start = reader.ReadUInt64();
                var length = Count(reader, 1);
                if (length == 0 || (length & 3) != 0 || (start & 3) != 0 ||
                    start < address || (run != 0 && start < end) || start > ulong.MaxValue - (ulong)length)
                    throw new ArgumentException("Invalid prewarm callee code range.");
                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                ranges[run] = new CodeRange(start, bytes);
                end = start + (ulong)length;
            }
            functions[index] = new LinkedPrewarmFunction(address, ranges);
        }
        var sites = new LinkedPrewarmSite[Count(reader, 28)];
        var pcs = new HashSet<uint>();
        for (var index = 0; index < sites.Length; index++)
        {
            var pc = reader.ReadUInt32();
            if (!pcs.Add(pc)) throw new ArgumentException("Duplicate prewarm call site.");
            var addressRegister = reader.ReadUInt32();
            var argumentRegister = reader.ReadUInt32();
            var returnRegister = reader.ReadUInt32();
            var returnAddress = reader.ReadUInt64();
            var targets = new LinkedPrewarmTarget[Count(reader, 16)];
            for (var target = 0; target < targets.Length; target++)
            {
                var address = reader.ReadUInt64();
                var argument = reader.ReadUInt64();
                if (!addresses.Contains(address)) throw new ArgumentException("The prewarm call target has no code.");
                targets[target] = new LinkedPrewarmTarget(address, argument);
            }
            sites[index] = new LinkedPrewarmSite(pc, addressRegister, argumentRegister, returnRegister, returnAddress, targets);
        }
        return new(runtimeRecords, callMarks, bindlessHeap, functions, sites);
    }

    private static int Count(BinaryReader reader, int minimumBytes)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
            throw new ArgumentException("The prewarm linked record count is out of range.");
        return count;
    }
}

internal sealed record LinkedPrewarmFunction(ulong Address, CodeRange[] Ranges);
internal sealed record LinkedPrewarmTarget(ulong Address, ulong Argument);
internal sealed record LinkedPrewarmSite(uint Pc, uint AddressRegister, uint ArgumentRegister, uint ReturnRegister,
    ulong ReturnAddress, LinkedPrewarmTarget[] Targets);
