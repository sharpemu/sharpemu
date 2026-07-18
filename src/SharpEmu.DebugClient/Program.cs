// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net.Sockets;
using SharpEmu.DebugClient;

return await ClientProgram.RunAsync(args).ConfigureAwait(false);

internal static class ClientProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        string? endpointArg = null;
        var execCommands = new List<string>();
        var quiet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--exec", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-e", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--exec requires a command argument.");
                    return 2;
                }

                execCommands.Add(args[++i]);
                continue;
            }

            if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase))
            {
                quiet = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option '{arg}'.");
                PrintUsage();
                return 2;
            }

            endpointArg ??= arg;
        }

        if (!ClientEndpoint.TryParse(endpointArg, out var host, out var port, out var endpointError))
        {
            Console.Error.WriteLine(endpointError);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        DebugClientConnection connection;
        try
        {
            connection = await DebugClientConnection.ConnectAsync(host, port, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Could not connect to {host}:{port}: {ex.Message}");
            Console.Error.WriteLine("Start the emulator with --debug-server first.");
            return 3;
        }

        await using (connection)
        {
            var receiveTask = connection.ReceiveLoopAsync(shutdown.Token);

            if (execCommands.Count > 0)
            {
                await RunOneShotAsync(connection, execCommands, shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                if (!quiet)
                {
                    Console.WriteLine($"Connected to SharpEmu debug server at {host}:{port}.");
                    Console.WriteLine("Type 'help' for commands, 'quit' to exit.");
                }

                await RunReplAsync(connection, shutdown).ConfigureAwait(false);
            }

            shutdown.Cancel();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    private static async Task RunOneShotAsync(
        DebugClientConnection connection,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            var result = CommandTranslator.Translate(command);
            switch (result.Kind)
            {
                case CommandTranslator.ActionKind.SendRequest:
                    await connection.SendAsync(result.Payload!, cancellationToken).ConfigureAwait(false);
                    break;
                case CommandTranslator.ActionKind.Error:
                    Console.Error.WriteLine(result.Error);
                    break;
                case CommandTranslator.ActionKind.ShowHelp:
                    Console.WriteLine(CommandTranslator.HelpText);
                    break;
            }
        }

        // Give the server a moment to answer before the client exits.
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunReplAsync(DebugClientConnection connection, CancellationTokenSource shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(shutdown.Token).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var result = CommandTranslator.Translate(line);
            switch (result.Kind)
            {
                case CommandTranslator.ActionKind.Quit:
                    return;
                case CommandTranslator.ActionKind.ShowHelp:
                    Console.WriteLine(CommandTranslator.HelpText);
                    break;
                case CommandTranslator.ActionKind.Error:
                    Console.Error.WriteLine(result.Error);
                    break;
                case CommandTranslator.ActionKind.Ignore:
                    break;
                case CommandTranslator.ActionKind.SendRequest:
                    try
                    {
                        await connection.SendAsync(result.Payload!, shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        Console.Error.WriteLine("Send failed; the connection is closed.");
                        return;
                    }

                    break;
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SharpEmu.DebugClient — live debugger client for the SharpEmu debug server.");
        Console.WriteLine();
        Console.WriteLine("Usage: SharpEmu.DebugClient [host:port] [--exec \"<command>\"]... [--quiet]");
        Console.WriteLine("  host:port   Server endpoint (default 127.0.0.1:5714).");
        Console.WriteLine("  --exec, -e  Run a command non-interactively (repeatable), then exit.");
        Console.WriteLine("  --quiet     Suppress the connection banner.");
        Console.WriteLine("  --help, -h  Show this help.");
        Console.WriteLine();
        Console.WriteLine(CommandTranslator.HelpText);
    }
}
