// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using Xunit;

namespace SharpEmu.CLI.Tests;

public sealed class DiagnosticReportIntegrationTests
{
    [Fact]
    public async Task ExplicitPath_WritesReportForUnhandledException()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var ebootDirectory = Path.Combine(
                temporaryDirectory,
                "test data");

            Directory.CreateDirectory(ebootDirectory);

            var ebootPath = Path.Combine(
                ebootDirectory,
                "invalid-eboot.bin");

            var reportPath = Path.Combine(
                temporaryDirectory,
                "diagnostic report.txt");

            await File.WriteAllBytesAsync(
                ebootPath,
                Encoding.ASCII.GetBytes("BEREZKA"));

            var result = await RunCliAsync(
                $"--diagnostic-report={reportPath}",
                ebootPath);

            Assert.Equal(3, result.ExitCode);

            Assert.True(
                File.Exists(reportPath),
                "The diagnostic report was not created." +
                Environment.NewLine +
                result.CombinedOutput);

            var report = await File.ReadAllTextAsync(
                reportPath,
                Encoding.UTF8);

            Assert.Contains(
                "SharpEmu Diagnostic Report",
                report);

            Assert.Contains(
                $"Executable: {Path.GetFullPath(ebootPath)}",
                report);

            Assert.Contains(
                "Result: unhandled exception",
                report);

            Assert.Contains(
                "=== Unhandled Exception ===",
                report);

            Assert.Contains(
                "System.IO.InvalidDataException",
                report);

            Assert.Contains(
                "=== Session Summary ===",
                report);

            Assert.Contains(
                "(not available)",
                report);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public async Task DefaultPath_UsesTitleIdAndWritesReport()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        string? createdReportPath = null;

        try
        {
            var titleId =
                $"TEST{Guid.NewGuid():N}"[..12]
                .ToUpperInvariant();

            var ebootPath = Path.Combine(
                temporaryDirectory,
                "invalid-eboot.bin");

            var paramPath = Path.Combine(
                temporaryDirectory,
                "param.json");

            await File.WriteAllBytesAsync(
                ebootPath,
                Encoding.ASCII.GetBytes("BEREZKA"));

            await File.WriteAllTextAsync(
                paramPath,
                "{\"titleId\":\"" + titleId + "\"}",
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            var result = await RunCliAsync(
                "--diagnostic-report",
                ebootPath);

            Assert.Equal(3, result.ExitCode);

            var reportsDirectory = Path.Combine(
                Path.GetDirectoryName(FindCliExecutable())!,
                "user",
                "reports");

            var reports = Directory.Exists(reportsDirectory)
                ? Directory.GetFiles(
                    reportsDirectory,
                    $"{titleId}-*.diagnostic.txt")
                : [];

            createdReportPath = Assert.Single(reports);

            var report = await File.ReadAllTextAsync(
                createdReportPath,
                Encoding.UTF8);

            Assert.Contains(
                "SharpEmu Diagnostic Report",
                report);

            Assert.Contains(
                "Result: unhandled exception",
                report);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(createdReportPath))
            {
                File.Delete(createdReportPath);
            }

            TryDeleteDirectory(temporaryDirectory);
        }
    }

    [Fact]
    public async Task EmptyExplicitPath_IsRejected()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var ebootPath = Path.Combine(
                temporaryDirectory,
                "invalid-eboot.bin");

            await File.WriteAllBytesAsync(
                ebootPath,
                Encoding.ASCII.GetBytes("BEREZKA"));

            var result = await RunCliAsync(
                "--diagnostic-report=",
                ebootPath);

            Assert.Equal(1, result.ExitCode);

            Assert.Contains(
                "Usage:",
                result.CombinedOutput);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<ProcessResult> RunCliAsync(
        params string[] arguments)
    {
        var executablePath = FindCliExecutable();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory =
                Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.Environment[
            "SHARPEMU_DISABLE_MITIGATION_RELAUNCH"] = "1";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process =
            Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Failed to start SharpEmu.");

        var standardOutputTask =
            process.StandardOutput.ReadToEndAsync();

        var standardErrorTask =
            process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(90));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup after a test timeout.
            }

            throw new TimeoutException(
                "SharpEmu did not exit within 90 seconds.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new ProcessResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    private static string FindCliExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(
                AppContext.BaseDirectory,
                "win-x64",
                "SharpEmu.exe"),

            Path.Combine(
                AppContext.BaseDirectory,
                "linux-x64",
                "SharpEmu"),

            Path.Combine(
                AppContext.BaseDirectory,
                "osx-x64",
                "SharpEmu"),

            Path.Combine(
                AppContext.BaseDirectory,
                "osx-arm64",
                "SharpEmu"),
        };

        return candidates.FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException(
                "The SharpEmu executable was not found." +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    candidates));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu.CLI.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch
        {
            // Temporary test files must not hide the test result.
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string CombinedOutput =>
            StandardOutput +
            Environment.NewLine +
            StandardError;
    }
}
