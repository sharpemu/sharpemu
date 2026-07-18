// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Libs.AppContent;

public static class AppContentExports
{
    private const ulong BootParamAttrOffset = 4;
    private const string Temp0MountPoint = "/temp0";
    private const uint AppParamSkuFlag = 0;
    private const int AppParamSkuFlagFull = 3;

    [SysAbiExport(
        Nid = "R9lA82OraNs",
        ExportName = "sceAppContentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentInitialize(CpuContext ctx)
    {
        var initParamAddress = ctx[CpuRegister.Rdi];
        var bootParamAddress = ctx[CpuRegister.Rsi];
        if (initParamAddress == 0 || bootParamAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> attrBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(attrBytes, 0);
        if (!ctx.Memory.TryWrite(bootParamAddress + BootParamAttrOffset, attrBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "xnd8BJzAxmk",
        ExportName = "sceAppContentGetAddcontInfoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentGetAddcontInfoList(CpuContext ctx)
    {
        var hitCountAddress = ctx[CpuRegister.Rcx];
        if (hitCountAddress != 0)
        {
            Span<byte> hitCountBytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(hitCountBytes, 0);
            if (!ctx.Memory.TryWrite(hitCountAddress, hitCountBytes))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "99b82IKXpH4",
        ExportName = "sceAppContentAppParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentAppParamGetInt(CpuContext ctx)
    {
        var paramId = (uint)ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        int value;
        if (paramId == AppParamSkuFlag)
        {
            value = AppParamSkuFlagFull;
        }
        else if (!TryReadUserDefinedParam(paramId, out value))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        if (!ctx.Memory.TryWrite(valueAddress, valueBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAppContent($"app_param_get_int id={paramId} value={value}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "buYbeLOGWmA",
        ExportName = "sceAppContentTemporaryDataMount2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentTemporaryDataMount2(CpuContext ctx)
    {
        var mountPointAddress = ctx[CpuRegister.Rsi];
        if (mountPointAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Directory.CreateDirectory(ResolveTemp0Root());
        var mountPointBytes = Encoding.ASCII.GetBytes($"{Temp0MountPoint}\0");
        if (!ctx.Memory.TryWrite(mountPointAddress, mountPointBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Download data is not emulated as a real quota; report a comfortable
    // fixed amount of free space so titles never take the "storage full" path.
    [SysAbiExport(
        Nid = "Gl6w5i0JokY",
        ExportName = "sceAppContentDownloadDataGetAvailableSpaceKb",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAppContent")]
    public static int AppContentDownloadDataGetAvailableSpaceKb(CpuContext ctx)
    {
        const ulong availableSpaceKb = 1024UL * 1024UL; // 1 GiB
        var availableSpaceAddress = ctx[CpuRegister.Rsi];
        if (availableSpaceAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> spaceBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(spaceBytes, availableSpaceKb);
        if (!ctx.Memory.TryWrite(availableSpaceAddress, spaceBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryReadUserDefinedParam(uint paramId, out int value)
    {
        value = 0;
        if (paramId is < 1 or > 4)
        {
            return false;
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0Root))
        {
            return true;
        }

        var paramJsonPath = Path.Combine(app0Root, "sce_sys", "param.json");
        if (!File.Exists(paramJsonPath))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(paramJsonPath);
            using var document = JsonDocument.Parse(stream);
            var propertyName = $"userDefinedParam{paramId}";
            if (document.RootElement.TryGetProperty(propertyName, out var element) &&
                element.TryGetInt32(out var parsedValue))
            {
                value = parsedValue;
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static void TraceAppContent(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_APP_CONTENT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] app_content.{message}");
    }

    private static string ResolveTemp0Root()
    {
        const string temp0VariableName = "SHARPEMU_TEMP0_DIR";
        var configuredRoot = Environment.GetEnvironmentVariable(temp0VariableName);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        var appName = string.IsNullOrWhiteSpace(app0Root)
            ? "default"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(app0Root));
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = "default";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        appName = new string(appName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu", appName, "temp0");
        Environment.SetEnvironmentVariable(temp0VariableName, root);
        return root;
    }
}
