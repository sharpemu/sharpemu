// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.GUI;

/// <summary>
/// Mirrors process-wide console output into the launcher console while
/// retaining the original streams for shell users and file logging.
/// </summary>
internal sealed class GuiConsoleMirror : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private int _disposed;

    private GuiConsoleMirror(Action<string, bool> writeLine)
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(new MirroringWriter(_originalOut, line => writeLine(line, false)));
        Console.SetError(new MirroringWriter(_originalError, line => writeLine(line, true)));
    }

    public static GuiConsoleMirror Install(Action<string, bool> writeLine) => new(writeLine);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    private sealed class MirroringWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action<string> _writeLine;
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public MirroringWriter(TextWriter inner, Action<string> writeLine)
        {
            _inner = inner;
            _writeLine = writeLine;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _inner.Write(value);
                Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _inner.Write(value);
                foreach (var character in value)
                {
                    Append(character);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _inner.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                {
                    _line.Append(value);
                }

                FlushLine();
            }
        }

        private void Append(char value)
        {
            if (value == '\r')
            {
                return;
            }

            if (value == '\n')
            {
                FlushLine();
                return;
            }

            _line.Append(value);
        }

        private void FlushLine()
        {
            _writeLine(_line.ToString());
            _line.Clear();
        }
    }
}
