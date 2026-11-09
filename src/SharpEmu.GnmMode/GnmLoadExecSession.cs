// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.GnmMode;

public sealed record GnmLoadExecRequest(string Executable, string[] Arguments);

public sealed class GnmLoadExecSession : IDisposable
{
    public const string ArgumentPrefix = "--gnm-session=";
    public const int RestartExitCode = 83;
    private const int MaximumFileSize = 1024 * 1024;
    private readonly bool _ownsFiles;
    private readonly Action<int> _exitProcess;
    private readonly object _gate = new();
    private sealed record Definition(string ApplicationPath, GnmLoadExecRequest? Startup);

    public string FilePath { get; }
    public string ApplicationPath { get; }
    public GnmLoadExecRequest? Startup { get; private set; }
    private string RequestPath => FilePath + ".request";

    private GnmLoadExecSession(string path, Definition definition, bool ownsFiles, Action<int>? exitProcess = null)
    {
        FilePath = path;
        ApplicationPath = definition.ApplicationPath;
        Startup = definition.Startup;
        _ownsFiles = ownsFiles;
        _exitProcess = exitProcess ?? Environment.Exit;
    }

    public static GnmLoadExecSession Create(string applicationPath, GnmLoadExecRequest? startup = null)
    {
        if (startup is not null && !IsValid(startup))
            throw new ArgumentException("Invalid guest executable or arguments.", nameof(startup));
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmu", "loadexec");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var definition = new Definition(Path.GetFullPath(applicationPath), startup);
        WriteNew(path, definition);
        return new GnmLoadExecSession(path, definition, ownsFiles: true);
    }

    public static GnmLoadExecSession Open(string path, string applicationPath, Action<int>? exitProcess = null)
    {
        var definition = Read<Definition>(path);
        if (!string.Equals(Path.GetFullPath(applicationPath), definition.ApplicationPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            definition.Startup is { } startup && !IsValid(startup))
            throw new InvalidDataException("The executable session does not match this application.");
        return new GnmLoadExecSession(Path.GetFullPath(path), definition, ownsFiles: false, exitProcess);
    }

    public static bool IsRestart(string path) => Read<Definition>(path).Startup is { } startup && IsValid(startup);

    public bool TryTakeRequest(int exitCode, out GnmLoadExecRequest request)
    {
        request = null!;
        if (exitCode != RestartExitCode || !File.Exists(RequestPath))
            return false;
        try
        {
            var candidate = Read<GnmLoadExecRequest>(RequestPath);
            if (!IsValid(candidate))
                return false;
            File.Delete(RequestPath);
            request = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[GNMMODE][WARN] Invalid LoadExec request: {exception.Message}");
            return false;
        }
    }

    public void SetStartup(GnmLoadExecRequest request)
    {
        if (!_ownsFiles || !IsValid(request))
            throw new InvalidOperationException("Invalid executable session update.");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Definition(ApplicationPath, request)));
        Startup = request;
    }

    public int LoadExec(CpuContext context)
    {
        if (!context.TryReadNullTerminatedUtf8(context[CpuRegister.Rdi], 1024, out var path))
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        var prefix = path.StartsWith("/app0/", StringComparison.Ordinal) ? "/app0/" :
            path.StartsWith("/hostapp/", StringComparison.Ordinal) ? "/hostapp/" : null;
        if (prefix is null)
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        var arguments = new List<string>();
        var argv = context[CpuRegister.Rsi];
        if (argv != 0)
        {
            for (var index = 0; ; index++)
            {
                if (!context.TryReadUInt64(argv + (ulong)index * 8, out var address))
                    return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                if (address == 0)
                    break;
                if (index >= 64 || !context.TryReadNullTerminatedUtf8(address, 1024, out var argument))
                    return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                arguments.Add(argument);
            }
        }
        var request = new GnmLoadExecRequest(path[prefix.Length..], arguments.ToArray());
        if (!IsValid(request))
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        try
        {
            var target = KernelMemoryCompatExports.ResolveGuestPath("/app0/" + request.Executable);
            using (var image = App0Archive.OpenReadOrFile(target))
                if (!Loader.GnmLoader.IsGnmExecutable(image))
                    return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            lock (_gate)
            {
                if (HostSessionControl.IsShutdownRequested || File.Exists(RequestPath))
                    return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                var temporary = RequestPath + ".pending";
                try
                {
                    WriteNew(temporary, request);
                    File.Move(temporary, RequestPath);
                }
                finally
                {
                    File.Delete(temporary);
                }
                Console.Error.WriteLine($"[GNMMODE][LOADEXEC] Restart requested: /app0/{request.Executable}");
                Console.Error.Flush();
                _exitProcess(RestartExitCode);
                return context.SetReturn(0);
            }
        }
        catch (FileNotFoundException)
        {
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"[GNMMODE][WARN] LoadExec failed: {exception.Message}");
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
    }

    public static bool IsValid(GnmLoadExecRequest request) =>
        request.Executable is { Length: > 0 and <= 1024 } executable &&
        !executable.Contains('\\') && !executable.Contains(':') && !executable.Contains('\0') &&
        executable.Split('/').All(part => part.Length != 0 && part is not ("." or "..")) &&
        request.Arguments is { Length: <= 64 } arguments &&
        arguments.All(argument => argument is { Length: < 1024 } && !argument.Contains('\0'));

    private static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFileSize)
            throw new InvalidDataException("The executable session is too large.");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("Empty executable session.");
    }

    private static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (!_ownsFiles)
            return;
        foreach (var path in new[] { FilePath, RequestPath, RequestPath + ".pending" })
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
