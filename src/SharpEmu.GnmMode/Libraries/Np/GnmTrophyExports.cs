// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Np;
using SharpEmu.Libs.SystemService;

namespace SharpEmu.GnmMode.Libraries.Np;

public static class GnmTrophyExports
{
    private const int InvalidArgument = unchecked((int)0x80551604);
    private const int InvalidHandle = unchecked((int)0x80551608);
    private const int InvalidContext = unchecked((int)0x80551609);
    private const int InvalidTrophy = unchecked((int)0x8055160A);
    private const int NotRegistered = unchecked((int)0x8055160F);
    private static readonly object Gate = new();
    private static readonly Dictionary<int, bool> Handles = [];
    private static readonly Dictionary<int, Context> Contexts = [];
    private static int _nextId;

    private sealed class Context(string directory, GnmTrophyPackageReader.TrophySet set)
    {
        public string Directory { get; } = directory;
        public GnmTrophyPackageReader.TrophySet Set { get; } = set;
        public bool Registered { get; set; }
    }

    public static int CreateHandle(CpuContext cpu)
    {
        lock (Gate)
        {
            var id = ++_nextId;
            if (!WriteInt(cpu, cpu[CpuRegister.Rdi], id))
                return cpu.SetReturn(InvalidArgument);
            Handles.Add(id, false);
            return cpu.SetReturn(0);
        }
    }

    public static int CreateContext(CpuContext cpu)
    {
        if ((uint)cpu[CpuRegister.Rdx] != 0)
            return cpu.SetReturn(unchecked((int)0x80551621));
        if ((int)cpu[CpuRegister.Rsi] < 0)
            return cpu.SetReturn(unchecked((int)0x80551625));
        lock (Gate)
        {
            var id = ++_nextId;
            if (!WriteInt(cpu, cpu[CpuRegister.Rdi], id))
                return cpu.SetReturn(InvalidArgument);
            var directory = KernelMemoryCompatExports.ResolveGuestPath("/app0");
            var reader = TrophyCatalog.CompatibilityReader as GnmTrophyPackageReader;
            var set = reader?.LoadSet(directory, false, TrophyLanguage.FromSystemLanguage(SystemServiceExports.SystemLanguage))
                ?? GnmTrophyPackageReader.TrophySet.Empty;
            Contexts.Add(id, new Context(directory, set));
            return cpu.SetReturn(0);
        }
    }

    public static int DestroyContext(CpuContext cpu)
    {
        lock (Gate)
            return cpu.SetReturn(Contexts.Remove((int)cpu[CpuRegister.Rdi]) ? 0 : InvalidContext);
    }

    public static int DestroyHandle(CpuContext cpu)
    {
        lock (Gate)
            return cpu.SetReturn(Handles.Remove((int)cpu[CpuRegister.Rdi]) ? 0 : InvalidHandle);
    }

    public static int AbortHandle(CpuContext cpu)
    {
        lock (Gate)
        {
            var id = (int)cpu[CpuRegister.Rdi];
            if (!Handles.ContainsKey(id))
                return cpu.SetReturn(InvalidHandle);
            Handles[id] = true;
            return cpu.SetReturn(0);
        }
    }

    public static int RegisterContext(CpuContext cpu)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out var context, requireRegistered: false);
            if (error != 0)
                return cpu.SetReturn(error);
            if (context!.Registered)
                return cpu.SetReturn(unchecked((int)0x80551610));
            context.Registered = true;
            return cpu.SetReturn(0);
        }
    }

    public static int UnlockTrophy(CpuContext cpu)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out _);
            if (error != 0)
                return cpu.SetReturn(error);
            var address = cpu[CpuRegister.Rcx];
            if (!WriteInt(cpu, address, -1))
                return cpu.SetReturn(InvalidArgument);
            var result = TrophyService.UnlockDirect((int)cpu[CpuRegister.Rdx], out var platinum);
            if (result == 0 && !WriteInt(cpu, address, platinum))
                return cpu.SetReturn(InvalidArgument);
            return cpu.SetReturn(result);
        }
    }

    public static int GetTrophyUnlockState(CpuContext cpu)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out _);
            if (error != 0)
                return cpu.SetReturn(error);
            Span<byte> flags = stackalloc byte[16];
            flags.Clear();
            var trophies = TrophyService.Trophies;
            foreach (var trophy in trophies.Where(t => t.Unlocked && t.Id is >= 0 and < 128))
                flags[trophy.Id / 8] |= (byte)(1 << (trophy.Id % 8));
            return cpu.SetReturn(cpu[CpuRegister.Rdx] != 0 && cpu.Memory.TryWrite(cpu[CpuRegister.Rdx], flags) &&
                WriteInt(cpu, cpu[CpuRegister.Rcx], trophies.Length) ? 0 : InvalidArgument);
        }
    }

    public static int GetTrophyInfo(CpuContext cpu)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out _);
            if (error != 0)
                return cpu.SetReturn(error);
            var trophy = TrophyService.Trophies.FirstOrDefault(t => t.Id == (int)cpu[CpuRegister.Rdx]);
            if (trophy is null)
                return cpu.SetReturn(InvalidTrophy);
            var detailsAddress = cpu[CpuRegister.Rcx];
            var dataAddress = cpu[CpuRegister.R8];
            if (detailsAddress == 0 && dataAddress == 0)
                return cpu.SetReturn(InvalidArgument);
            Span<byte> details = stackalloc byte[1176];
            details.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(details, (ulong)details.Length);
            BinaryPrimitives.WriteInt32LittleEndian(details[8..], trophy.Id);
            BinaryPrimitives.WriteInt32LittleEndian(details[12..], trophy.Grade);
            BinaryPrimitives.WriteInt32LittleEndian(details[16..], trophy.GroupId);
            details[20] = trophy.Hidden ? (byte)1 : (byte)0;
            WriteText(details.Slice(24, 128), trophy.Name);
            WriteText(details.Slice(152, 1024), trophy.Description);
            Span<byte> data = stackalloc byte[24];
            data.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(data, (ulong)data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data[8..], trophy.Id);
            data[12] = trophy.Unlocked ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(data[16..], trophy.UnlockedAt);
            return cpu.SetReturn(WriteStructure(cpu, detailsAddress, details) && WriteStructure(cpu, dataAddress, data) ? 0 : InvalidArgument);
        }
    }

    public static int GetGameInfo(CpuContext cpu) => GetInfo(cpu, group: false);
    public static int GetGroupInfo(CpuContext cpu) => GetInfo(cpu, group: true);

    private static int GetInfo(CpuContext cpu, bool group)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out var context);
            if (error != 0)
                return cpu.SetReturn(error);
            var trophies = TrophyService.Trophies;
            var groupId = (int)cpu[CpuRegister.Rdx];
            if (group)
            {
                trophies = trophies.Where(t => t.GroupId == groupId).ToArray();
                if (trophies.Length == 0)
                    return cpu.SetReturn(unchecked((int)0x8055160B));
            }
            var detailsAddress = cpu[group ? CpuRegister.Rcx : CpuRegister.Rdx];
            var dataAddress = cpu[group ? CpuRegister.R8 : CpuRegister.Rcx];
            if (detailsAddress == 0 && dataAddress == 0)
                return cpu.SetReturn(InvalidArgument);
            Span<byte> details = stackalloc byte[1184];
            details.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(details, (ulong)details.Length);
            BinaryPrimitives.WriteInt32LittleEndian(details[8..], group ? groupId : trophies.Select(t => t.GroupId).Where(g => g >= 0).Distinct().Count());
            WriteCounts(details[12..], trophies, false);
            WriteText(details.Slice(32, 128), context!.Set.Title);
            WriteText(details.Slice(160, 1024), context.Set.Description);
            Span<byte> data = stackalloc byte[group ? 40 : 32];
            data.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(data, (ulong)data.Length);
            if (group)
                BinaryPrimitives.WriteInt32LittleEndian(data[8..], groupId);
            var countsOffset = group ? 12 : 8;
            WriteCounts(data[countsOffset..], trophies, true);
            BinaryPrimitives.WriteInt32LittleEndian(data[(countsOffset + 20)..], trophies.Length == 0 ? 0 : trophies.Count(t => t.Unlocked) * 100 / trophies.Length);
            return cpu.SetReturn(WriteStructure(cpu, detailsAddress, details) && WriteStructure(cpu, dataAddress, data) ? 0 : InvalidArgument);
        }
    }

    private static void WriteCounts(Span<byte> output, TrophyService.Trophy[] trophies, bool unlocked)
    {
        var selected = unlocked ? trophies.Where(t => t.Unlocked).ToArray() : trophies;
        BinaryPrimitives.WriteInt32LittleEndian(output, selected.Length);
        for (var grade = 1; grade <= 4; grade++)
            BinaryPrimitives.WriteInt32LittleEndian(output[(grade * 4)..], selected.Count(t => t.Grade == grade));
    }

    public static int GetGameIcon(CpuContext cpu) => GetIcon(cpu, 0);
    public static int GetGroupIcon(CpuContext cpu) => GetIcon(cpu, 1);
    public static int GetTrophyIcon(CpuContext cpu) => GetIcon(cpu, 2);

    private static int GetIcon(CpuContext cpu, int kind)
    {
        lock (Gate)
        {
            var error = Validate(cpu, out var context);
            if (error != 0)
                return cpu.SetReturn(error);
            var id = (int)cpu[CpuRegister.Rdx];
            if (kind == 2 && !TrophyService.Trophies.Any(t => t.Id == id))
                return cpu.SetReturn(InvalidTrophy);
            if (kind == 1 && !TrophyService.Trophies.Any(t => t.GroupId == id))
                return cpu.SetReturn(unchecked((int)0x8055160B));
            var icon = GnmTrophyPackageReader.ReadIcon(context!.Directory, kind switch
            {
                2 => $"TROP{id:D3}.PNG",
                1 when id >= 0 => $"GR{id:D3}.PNG",
                _ => "ICON0.PNG",
            });
            if (icon is null)
                return cpu.SetReturn(unchecked((int)0x80551614));
            var buffer = cpu[kind == 0 ? CpuRegister.Rdx : CpuRegister.Rcx];
            var sizeAddress = cpu[kind == 0 ? CpuRegister.Rcx : CpuRegister.R8];
            Span<byte> size = stackalloc byte[8];
            if (sizeAddress == 0 || !cpu.Memory.TryRead(sizeAddress, size))
                return cpu.SetReturn(InvalidArgument);
            var capacity = BinaryPrimitives.ReadUInt64LittleEndian(size);
            BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)icon.Length);
            if (!cpu.Memory.TryWrite(sizeAddress, size))
                return cpu.SetReturn(InvalidArgument);
            if (buffer == 0)
                return cpu.SetReturn(0);
            if (capacity < (ulong)icon.Length)
                return cpu.SetReturn(unchecked((int)0x80551605));
            return cpu.SetReturn(cpu.Memory.TryWrite(buffer, icon) ? 0 : InvalidArgument);
        }
    }

    public static int ShowTrophyList(CpuContext cpu)
    {
        lock (Gate)
            return cpu.SetReturn(Validate(cpu, out _));
    }

    private static int Validate(CpuContext cpu, out Context? context, bool requireRegistered = true)
    {
        context = null;
        if (!Contexts.TryGetValue((int)cpu[CpuRegister.Rdi], out context))
            return InvalidContext;
        if (!Handles.TryGetValue((int)cpu[CpuRegister.Rsi], out var aborted))
            return InvalidHandle;
        if (aborted)
            return unchecked((int)0x80551607);
        return requireRegistered && !context.Registered ? NotRegistered : 0;
    }

    private static bool WriteStructure(CpuContext cpu, ulong address, ReadOnlySpan<byte> data)
    {
        if (address == 0)
            return true;
        Span<byte> size = stackalloc byte[8];
        return cpu.Memory.TryRead(address, size) && BinaryPrimitives.ReadUInt64LittleEndian(size) == (ulong)data.Length &&
            cpu.Memory.TryWrite(address, data);
    }

    private static bool WriteInt(CpuContext cpu, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return address != 0 && cpu.Memory.TryWrite(address, bytes);
    }

    private static void WriteText(Span<byte> output, string value)
    {
        Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), output[..^1], true, out _, out _, out _);
    }
}
