// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace SharpEmu.GUI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            desktop.MainWindow = window;

            // A second launch brings this window forward instead of opening another launcher.
            SingleInstance.Current?.Listen(() => Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Maximized;
                }

                window.Show();
                window.Activate();
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
