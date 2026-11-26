// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text;
using SharpEmu.Libs.Overlay;
using SharpEmu.Libs.Pad;

namespace SharpEmu.Libs.Ime;

internal enum ImeShift
{
    Off,
    Once,
    Locked,
}

internal enum ImeTextKind
{
    Default = 0,
    BasicLatin = 1,
    Url = 2,
    Mail = 3,
    Number = 4,
}

internal enum ImeEnterLabel
{
    Default = 0,
    Send = 1,
    Search = 2,
    Go = 3,
}

internal enum ImeKeyboardInput
{
    None,
    Changed,
    Done,
    Cancel,
}

internal sealed record ImeKeyboardView(
    long Session,
    string Title,
    string Placeholder,
    string Text,
    int Caret,
    int Length,
    int MaxLength,
    ImePage Page,
    ImeShift Shift,
    IReadOnlyList<IReadOnlyList<ImeKey>> Rows,
    int FocusRow,
    int FocusColumn,
    OverlayInputScheme Scheme,
    ImeEnterLabel EnterLabel,
    bool CaretVisible,
    bool NumbersOnly);

internal sealed class ImeKeyboardSession
{
    private const byte StickLow = 64;
    private const byte StickHigh = 192;
    private const int KeyBackspace = 0x08;
    private const int KeyEnter = 0x0D;
    private const int KeyEscape = 0x1B;
    private const int KeyLeft = 0x25;
    private const int KeyRight = 0x27;
    private const int KeyDelete = 0x2E;
    private const uint StickUp = 1u << 28;
    private const uint StickDown = 1u << 29;
    private const uint StickLeft = 1u << 30;
    private const uint StickRight = 1u << 31;
    private static readonly long FirstRepeatTicks = Stopwatch.Frequency * 2 / 5;
    private static readonly long NextRepeatTicks = Stopwatch.Frequency / 12;
    private static readonly long BlinkTicks = Stopwatch.Frequency * 53 / 100;

    private readonly long _session;
    private readonly string _title;
    private readonly string _placeholder;
    private readonly string? _language;
    private readonly int _maxLength;
    private readonly ImeTextKind _kind;
    private readonly bool _password;
    private readonly ImeEnterLabel _enterLabel;
    private readonly List<uint> _controllerChanges = [];
    private readonly List<int> _keys = [];
    private readonly List<string> _texts = [];
    private readonly Dictionary<Repeat, long> _repeatAt = [];
    private readonly StringBuilder _text = new();
    private IReadOnlyList<IReadOnlyList<ImeKey>> _rows;
    private long _sequence;
    private uint _previousButtons;
    private long _lastEdit;
    private bool _caretVisible = true;
    private OverlayInputScheme _scheme;

    internal ImeKeyboardSession(long session, string title, string placeholder, string initialText, int maxLength,
        ImeTextKind kind, bool password, bool autoCapitalize, ImeEnterLabel enterLabel, string? language)
    {
        _session = session;
        _title = title;
        _placeholder = placeholder;
        _language = language;
        _maxLength = Math.Max(1, maxLength);
        _kind = kind;
        _password = password;
        _enterLabel = enterLabel;
        Page = kind == ImeTextKind.Number ? ImePage.Symbols : ImePage.Letters;
        _rows = ImeKeyboardLayout.Rows(Page, language);
        Insert(initialText, fromKeyboard: true);
        Shift = autoCapitalize && _text.Length == 0 && kind is ImeTextKind.Default or ImeTextKind.BasicLatin
            ? ImeShift.Once
            : ImeShift.Off;
        FocusRow = 1;
        FocusColumn = 0;
    }

    private enum Repeat
    {
        Up,
        Down,
        Left,
        Right,
        Delete,
        CaretLeft,
        CaretRight,
        KeyBackspace,
        KeyDelete,
        KeyLeft,
        KeyRight,
    }

    internal string Text => _text.ToString();

    internal int Caret { get; private set; }

    internal ImePage Page { get; private set; }

    internal ImeShift Shift { get; private set; }

    internal int FocusRow { get; private set; }

    internal int FocusColumn { get; private set; }

    internal ImeKeyboardView View()
    {
        var text = _password ? new string('●', _text.Length) : _text.ToString();
        return new ImeKeyboardView(_session, _title, _placeholder, text, Caret, _text.Length, _maxLength, Page, Shift,
            _rows, FocusRow, FocusColumn, _scheme, _enterLabel, _caretVisible, _kind == ImeTextKind.Number);
    }

    internal void Begin()
    {
        var state = PadExports.ReadSystemDialogState();
        _sequence = PadExports.InputHistoryPosition;
        _previousButtons = state.ControllerButtons | StickMask(state);
        _scheme = SystemDialogInput.CurrentScheme();
        _lastEdit = Stopwatch.GetTimestamp();
    }

    internal ImeKeyboardInput Poll()
    {
        var now = Stopwatch.GetTimestamp();
        var changed = false;
        var scheme = SystemDialogInput.CurrentScheme();
        if (scheme != _scheme)
        {
            _scheme = scheme;
            changed = true;
        }

        _texts.Clear();
        SystemDialogInput.TakeTexts(_texts);
        foreach (var text in _texts)
        {
            changed |= Insert(text, fromKeyboard: true);
        }

        _keys.Clear();
        SystemDialogInput.TakeKeys(_keys);
        foreach (var key in _keys)
        {
            switch (key)
            {
                case KeyEnter:
                    return ImeKeyboardInput.Done;
                case KeyEscape:
                    return ImeKeyboardInput.Cancel;
                case KeyBackspace:
                    changed |= DeleteBackward();
                    Arm(Repeat.KeyBackspace, now);
                    break;
                case KeyDelete:
                    changed |= DeleteForward();
                    Arm(Repeat.KeyDelete, now);
                    break;
                case KeyLeft:
                    changed |= MoveCaret(-1);
                    Arm(Repeat.KeyLeft, now);
                    break;
                case KeyRight:
                    changed |= MoveCaret(1);
                    Arm(Repeat.KeyRight, now);
                    break;
            }
        }

        _controllerChanges.Clear();
        PadExports.TakeControllerHistory(ref _sequence, _controllerChanges);
        var current = PadExports.ReadSystemDialogState();
        var currentButtons = current.ControllerButtons | StickMask(current);
        _controllerChanges.Add(currentButtons);
        foreach (var buttons in _controllerChanges)
        {
            var pressed = buttons & ~_previousButtons;
            _previousButtons = buttons;
            if (pressed == 0)
            {
                continue;
            }

            if ((pressed & OrbisPadButton.R2) != 0)
            {
                return ImeKeyboardInput.Done;
            }

            if ((pressed & OrbisPadButton.Circle) != 0)
            {
                return ImeKeyboardInput.Cancel;
            }

            if ((pressed & (OrbisPadButton.Cross | OrbisPadButton.TouchPad)) != 0 && PressFocused() is var result &&
                result != ImeKeyboardInput.None)
            {
                if (result != ImeKeyboardInput.Changed)
                {
                    return result;
                }

                changed = true;
            }

            if ((pressed & OrbisPadButton.Square) != 0)
            {
                changed |= DeleteBackward();
                Arm(Repeat.Delete, now);
            }

            if ((pressed & OrbisPadButton.Triangle) != 0)
            {
                changed |= Insert(" ", fromKeyboard: false);
            }

            if ((pressed & OrbisPadButton.L1) != 0)
            {
                changed |= MoveCaret(-1);
                Arm(Repeat.CaretLeft, now);
            }

            if ((pressed & OrbisPadButton.R1) != 0)
            {
                changed |= MoveCaret(1);
                Arm(Repeat.CaretRight, now);
            }

            if ((pressed & OrbisPadButton.L2) != 0)
            {
                changed |= CycleShift();
            }

            if ((pressed & OrbisPadButton.L3) != 0)
            {
                NextPage();
                changed = true;
            }

            if ((pressed & (OrbisPadButton.Up | StickUp)) != 0)
            {
                changed |= MoveFocus(0, -1);
                Arm(Repeat.Up, now);
            }

            if ((pressed & (OrbisPadButton.Down | StickDown)) != 0)
            {
                changed |= MoveFocus(0, 1);
                Arm(Repeat.Down, now);
            }

            if ((pressed & (OrbisPadButton.Left | StickLeft)) != 0)
            {
                changed |= MoveFocus(-1, 0);
                Arm(Repeat.Left, now);
            }

            if ((pressed & (OrbisPadButton.Right | StickRight)) != 0)
            {
                changed |= MoveFocus(1, 0);
                Arm(Repeat.Right, now);
            }
        }

        changed |= Held(Repeat.Up, (currentButtons & (OrbisPadButton.Up | StickUp)) != 0, now, () => MoveFocus(0, -1));
        changed |= Held(Repeat.Down, (currentButtons & (OrbisPadButton.Down | StickDown)) != 0, now, () => MoveFocus(0, 1));
        changed |= Held(Repeat.Left, (currentButtons & (OrbisPadButton.Left | StickLeft)) != 0, now, () => MoveFocus(-1, 0));
        changed |= Held(Repeat.Right, (currentButtons & (OrbisPadButton.Right | StickRight)) != 0, now, () => MoveFocus(1, 0));
        changed |= Held(Repeat.Delete, (currentButtons & OrbisPadButton.Square) != 0, now, DeleteBackward);
        changed |= Held(Repeat.CaretLeft, (currentButtons & OrbisPadButton.L1) != 0, now, () => MoveCaret(-1));
        changed |= Held(Repeat.CaretRight, (currentButtons & OrbisPadButton.R1) != 0, now, () => MoveCaret(1));
        changed |= Held(Repeat.KeyBackspace, PadExports.IsHostKeyDown(KeyBackspace), now, DeleteBackward);
        changed |= Held(Repeat.KeyDelete, PadExports.IsHostKeyDown(KeyDelete), now, DeleteForward);
        changed |= Held(Repeat.KeyLeft, PadExports.IsHostKeyDown(KeyLeft), now, () => MoveCaret(-1));
        changed |= Held(Repeat.KeyRight, PadExports.IsHostKeyDown(KeyRight), now, () => MoveCaret(1));

        if (changed)
        {
            _lastEdit = now;
        }

        var visible = (now - _lastEdit) / BlinkTicks % 2 == 0;
        if (visible != _caretVisible)
        {
            _caretVisible = visible;
            changed = true;
        }

        return changed ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
    }

    internal ImeKeyboardInput PressFocused()
    {
        var key = _rows[FocusRow][FocusColumn];
        switch (key.Kind)
        {
            case ImeKeyKind.Character:
                var inserted = Insert(Shift == ImeShift.Off || Page == ImePage.Symbols ? key.Lower : key.Upper,
                    fromKeyboard: false);
                return inserted ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            case ImeKeyKind.Shift:
                return CycleShift() ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            case ImeKeyKind.Page:
                NextPage();
                return ImeKeyboardInput.Changed;
            case ImeKeyKind.Space:
                return Insert(" ", fromKeyboard: false) ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            case ImeKeyKind.Backspace:
                return DeleteBackward() ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            case ImeKeyKind.CursorLeft:
                return MoveCaret(-1) ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            case ImeKeyKind.CursorRight:
                return MoveCaret(1) ? ImeKeyboardInput.Changed : ImeKeyboardInput.None;
            default:
                return ImeKeyboardInput.Done;
        }
    }

    internal bool Insert(string text, bool fromKeyboard)
    {
        var inserted = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || !Accepts(rune))
            {
                continue;
            }

            var encoded = rune.ToString();
            if (_text.Length + encoded.Length > _maxLength)
            {
                break;
            }

            _text.Insert(Caret, encoded);
            Caret += encoded.Length;
            inserted = true;
        }

        if (inserted && !fromKeyboard && Shift == ImeShift.Once)
        {
            Shift = ImeShift.Off;
        }

        return inserted;
    }

    internal bool DeleteBackward()
    {
        if (Caret == 0)
        {
            return false;
        }

        var length = Caret >= 2 && char.IsSurrogatePair(_text[Caret - 2], _text[Caret - 1]) ? 2 : 1;
        _text.Remove(Caret - length, length);
        Caret -= length;
        return true;
    }

    internal bool DeleteForward()
    {
        if (Caret >= _text.Length)
        {
            return false;
        }

        var length = Caret + 1 < _text.Length && char.IsSurrogatePair(_text[Caret], _text[Caret + 1]) ? 2 : 1;
        _text.Remove(Caret, length);
        return true;
    }

    internal bool MoveCaret(int step)
    {
        var next = Caret;
        if (step < 0 && next > 0)
        {
            next -= next >= 2 && char.IsSurrogatePair(_text[next - 2], _text[next - 1]) ? 2 : 1;
        }
        else if (step > 0 && next < _text.Length)
        {
            next += next + 1 < _text.Length && char.IsSurrogatePair(_text[next], _text[next + 1]) ? 2 : 1;
        }

        if (next == Caret)
        {
            return false;
        }

        Caret = next;
        return true;
    }

    internal bool CycleShift()
    {
        if (Page == ImePage.Symbols)
        {
            return false;
        }

        Shift = Shift switch
        {
            ImeShift.Off => ImeShift.Once,
            ImeShift.Once => ImeShift.Locked,
            _ => ImeShift.Off,
        };
        return true;
    }

    internal void NextPage()
    {
        Page = Page switch
        {
            ImePage.Letters => ImePage.Symbols,
            ImePage.Symbols => ImePage.Accents,
            _ => ImePage.Letters,
        };
        _rows = ImeKeyboardLayout.Rows(Page, _language);
        FocusRow = Math.Clamp(FocusRow, 0, _rows.Count - 1);
        FocusColumn = Math.Clamp(FocusColumn, 0, _rows[FocusRow].Count - 1);
    }

    internal bool MoveFocus(int columns, int rows)
    {
        var row = FocusRow;
        var column = FocusColumn;
        if (columns != 0)
        {
            var count = _rows[row].Count;
            column = ((column + columns) % count + count) % count;
        }

        if (rows != 0)
        {
            var target = Math.Clamp(row + rows, 0, _rows.Count - 1);
            if (target != row)
            {
                column = Nearest(_rows[target], KeyCenter(_rows[row], column));
                row = target;
            }
        }

        if (row == FocusRow && column == FocusColumn)
        {
            return false;
        }

        FocusRow = row;
        FocusColumn = column;
        return true;
    }

    private bool Accepts(Rune rune)
    {
        if (_kind != ImeTextKind.Number)
        {
            return true;
        }

        return Rune.IsDigit(rune) || rune.Value is '.' or ',' or '-' or '+';
    }

    private static float KeyCenter(IReadOnlyList<ImeKey> row, int column)
    {
        var x = (ImeKeyboardLayout.Columns - ImeKeyboardLayout.RowWidth(row)) / 2;
        for (var index = 0; index < column; index++)
        {
            x += row[index].Width;
        }

        return x + row[column].Width / 2;
    }

    private static int Nearest(IReadOnlyList<ImeKey> row, float center)
    {
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < row.Count; index++)
        {
            var distance = Math.Abs(KeyCenter(row, index) - center);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static uint StickMask(PadState state)
    {
        var mask = 0u;
        if (state.ControllerLeftY < StickLow) mask |= StickUp;
        if (state.ControllerLeftY > StickHigh) mask |= StickDown;
        if (state.ControllerLeftX < StickLow) mask |= StickLeft;
        if (state.ControllerLeftX > StickHigh) mask |= StickRight;
        return mask;
    }

    private void Arm(Repeat repeat, long now) => _repeatAt[repeat] = now + FirstRepeatTicks;

    private bool Held(Repeat repeat, bool down, long now, Func<bool> action)
    {
        if (!down)
        {
            _repeatAt.Remove(repeat);
            return false;
        }

        if (!_repeatAt.TryGetValue(repeat, out var at))
        {
            _repeatAt[repeat] = now + FirstRepeatTicks;
            return false;
        }

        if (now < at)
        {
            return false;
        }

        _repeatAt[repeat] = now + NextRepeatTicks;
        return action();
    }
}
