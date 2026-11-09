// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.GnmMode;

namespace SharpEmu.CLI;

internal static partial class Program
{
    private static bool TryRunGnmSupervisor(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!TryParseArguments(args, out var applicationPath, out _, out _, out _, out _) ||
            !GnmRuntime.UsesCompatibilityMode(applicationPath))
            return false;
        using var session = GnmLoadExecSession.Create(applicationPath);
        string[] childArgs = [.. args, GnmLoadExecSession.ArgumentPrefix + session.FilePath];
        while (true)
        {
            if (!TryRunMitigatedChild(childArgs, out exitCode))
                exitCode = RunGnmChild(childArgs);
            if (!session.TryTakeRequest(exitCode, out var request))
                return true;
            session.SetStartup(request);
        }
    }

    private static int RunGnmChild(string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the emulator executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the emulator process.");
        void StopChild()
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; StopChild(); };
        EventHandler exiting = (_, _) => StopChild();
        Console.CancelKeyPress += cancel;
        AppDomain.CurrentDomain.ProcessExit += exiting;
        using var terminate = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; StopChild(); });
        try
        {
            child.WaitForExit();
            return child.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= exiting;
        }
    }
}
