// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace SharpEmu.GUI;

public static class ShortcutLauncher
{
    public static int Run(string gamePath) =>
        AppBuilder.Configure(() => new ShortcutApplication(Path.GetFullPath(gamePath)))
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);

    private sealed class ShortcutApplication(string gamePath) : Application
    {
        private IClassicDesktopStyleApplicationLifetime? _desktop;
        private ShortcutSplashWindow? _splash;
        private Process? _process;
        private volatile bool _stopping;

        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            _desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            _desktop.Exit += (_, _) => StopProcess();
            var cached = ShortcutSplashCache.PathForGame(gamePath);
            try
            {
                if (File.Exists(cached))
                    _splash = new ShortcutSplashWindow(cached);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Console.Error.WriteLine($"[GUI][WARN] Could not load the shortcut splash: {exception.Message}");
            }

            if (_splash is { } splash)
            {
                _desktop.MainWindow = splash;
                splash.Opened += (_, _) => Dispatcher.UIThread.Post(StartGame, DispatcherPriority.Background);
                splash.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_splash, splash))
                    {
                        _splash = null;
                        _desktop.Shutdown(EmulatorProcess.HostStopExitCode);
                    }
                };
            }
            else
                Dispatcher.UIThread.Post(StartGame, DispatcherPriority.Background);
            base.OnFrameworkInitializationCompleted();
        }

        private void StartGame()
        {
            if (_stopping)
                return;
            try
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the emulator executable.");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                start.ArgumentList.Add(LaunchConfiguration.NoConsoleArgument);
                start.ArgumentList.Add(gamePath);
                // The normal CLI retains its mitigation and GNM load-exec supervisors.
                _process = new Process { StartInfo = start, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnOutput;
                _process.Exited += (_, _) =>
                {
                    if (!_stopping)
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!_stopping)
                                Finish(_process.ExitCode);
                        });
                };
                if (!_process.Start())
                    throw new InvalidOperationException("Cannot start the emulator process.");
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
            {
                Console.Error.WriteLine($"[GUI][ERROR] Could not launch the shortcut: {exception.Message}");
                Finish(3);
            }
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (!_stopping && args.Data is { } line && EmulatorProcess.IsWindowReadyMessage(line))
                Dispatcher.UIThread.Post(HideSplash);
        }

        private void HideSplash()
        {
            var splash = _splash;
            _splash = null;
            splash?.Close();
        }

        private void Finish(int exitCode)
        {
            HideSplash();
            _desktop!.Shutdown(exitCode);
        }

        private void StopProcess()
        {
            _stopping = true;
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                Console.Error.WriteLine($"[GUI][WARN] Could not stop the shortcut process: {exception.Message}");
            }
            finally { _process?.Dispose(); }
        }
    }
}
