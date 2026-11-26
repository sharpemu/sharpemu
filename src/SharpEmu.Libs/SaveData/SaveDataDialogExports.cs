// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.Overlay;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SharpEmu.Libs.SaveData;

internal sealed record SaveDataDialogSlot(
    string DirName,
    string SlotDir,
    SaveDataMetadata Metadata,
    DateTime LastWriteUtc,
    long SizeBytes,
    string? IconPath);

public static class SaveDataDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    private const int ModeList = 1;
    private const int DispTypeSave = 1;
    private const int DispTypeLoad = 2;
    private const int DispTypeDelete = 3;
    private const int ResultOk = 0;
    private const int ResultUserCanceled = 1;
    private const int ButtonIdInvalid = 0;
    private const int ButtonIdOk = 1;
    private const uint FocusListTail = 1;
    private const uint FocusDataHead = 2;
    private const uint FocusDataTail = 3;
    private const uint FocusDataLatest = 4;
    private const uint FocusDataOldest = 5;
    private const uint FocusDirName = 6;
    private const uint OptionBackDisabled = 1;
    private const int DirNameSize = 32;
    private const int TitleIdSize = 10;
    private const int NewItemTitleMaxBytes = 128;
    private const int MaxDialogItems = 1024;

    private static readonly object Gate = new();
    private static int _status;
    private static int _lastMode;
    private static ulong _lastUserData;
    private static int _result;
    private static int _buttonId = ButtonIdOk;
    private static string _dirName = string.Empty;
    private static SaveDataDialogSlot? _slot;
    private static ListSession? _list;
    private static long _nextSession;

    [SysAbiExport(
        Nid = "s9e3+YpRnzw",
        ExportName = "sceSaveDataDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogInitialize(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status is not (StatusNone or StatusFinished))
            {
                return ctx.SetReturn(ErrorAlreadyInitialized);
            }

            _status = StatusInitialized;
            return ctx.SetReturn(ErrorOk);
        }
    }

    [SysAbiExport(
        Nid = "4tPhsP6FpDI",
        ExportName = "sceSaveDataDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        lock (Gate)
        {
            if (_status is not (StatusInitialized or StatusFinished))
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _lastMode = TryReadInt32(ctx, paramAddress + 0x34, out var mode) ? mode : 0;
            var dispType = TryReadInt32(ctx, paramAddress + 0x38, out var type) ? type : 0;
            _lastUserData = ctx.TryReadUInt64(paramAddress + 0x70, out var userData) ? userData : 0;
            var backEnabled = !(ctx.TryReadUInt64(paramAddress + 0x78, out var optionAddress) &&
                                optionAddress != 0 &&
                                ctx.TryReadUInt32(optionAddress, out var back) &&
                                back == OptionBackDisabled);
            var items = ReadItems(ctx, paramAddress, dispType);

            _result = ResultOk;
            _buttonId = ButtonIdOk;
            _dirName = items.FirstDirName ?? string.Empty;
            _slot = items.Slots.Find(slot => slot.DirName == _dirName);
            _status = StatusRunning;

            if (_lastMode == ModeList && (items.Slots.Count > 0 || items.NewItemTitle is not null))
            {
                _list = new ListSession(
                    Interlocked.Increment(ref _nextSession),
                    BuildEntries(items),
                    CaptionFor(dispType),
                    backEnabled,
                    dispType is DispTypeLoad or DispTypeSave);
                _list.Focus = InitialFocus(_list.Entries, items);
                _list.Begin();
                SystemDialogInput.Capture();
                SaveDataListLayer.Instance.Show(_list.View());
                Console.Error.WriteLine(
                    $"[LOADER][INFO] save_data_dialog.list type={dispType} items={_list.Entries.Count} focus={_list.Focus}");
            }

            TraceSaveDataDialog($"open mode={_lastMode} type={dispType} items={items.Slots.Count} userData=0x{_lastUserData:X16}");
            return ctx.SetReturn(ErrorOk);
        }
    }

    [SysAbiExport(
        Nid = "ERKzksauAJA",
        ExportName = "sceSaveDataDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogGetStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    [SysAbiExport(
        Nid = "KK3Bdg1RWK0",
        ExportName = "sceSaveDataDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    private static int PollStatus()
    {
        lock (Gate)
        {
            if (_status != StatusRunning)
            {
                return _status;
            }

            if (_list is null)
            {
                _status = StatusFinished;
                return _status;
            }

            switch (_list.Poll())
            {
                case ListInput.Changed:
                    SaveDataListLayer.Instance.Show(_list.View());
                    break;
                case ListInput.Delete:
                    DeleteFocusedEntry(_list);
                    break;
                case ListInput.Choose:
                    var entry = _list.Entries[_list.Focus];
                    FinishList(ResultOk, ButtonIdOk, entry.DirName, entry.Slot);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] save_data_dialog.chosen dir='{entry.DirName}'");
                    break;
                case ListInput.Back:
                    FinishList(ResultUserCanceled, ButtonIdInvalid, string.Empty, null);
                    Console.Error.WriteLine("[LOADER][INFO] save_data_dialog.canceled");
                    break;
            }

            return _status;
        }
    }

    [SysAbiExport(
        Nid = "en7gNVnh878",
        ExportName = "sceSaveDataDialogIsReadyToDisplay",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogIsReadyToDisplay(CpuContext ctx) => ctx.SetReturn(1);

    [SysAbiExport(
        Nid = "yEiJ-qqr6Cg",
        ExportName = "sceSaveDataDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        lock (Gate)
        {
            if (_status != StatusFinished)
            {
                return ctx.SetReturn(ErrorNotFinished);
            }

            Span<byte> head = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(head[0x00..], _lastMode);
            BinaryPrimitives.WriteInt32LittleEndian(head[0x04..], _result);
            BinaryPrimitives.WriteInt32LittleEndian(head[0x08..], _buttonId);
            if (!ctx.Memory.TryWrite(resultAddress, head) ||
                !ctx.TryWriteUInt64(resultAddress + 0x20, _lastUserData))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (_dirName.Length != 0 &&
                ctx.TryReadUInt64(resultAddress + 0x10, out var dirNameAddress) &&
                dirNameAddress != 0)
            {
                Span<byte> dirName = stackalloc byte[DirNameSize];
                dirName.Clear();
                Encoding.ASCII.GetBytes(_dirName.AsSpan(0, Math.Min(_dirName.Length, DirNameSize - 1)), dirName);
                if (!ctx.Memory.TryWrite(dirNameAddress, dirName))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (_slot is not null &&
                ctx.TryReadUInt64(resultAddress + 0x18, out var paramAddress) &&
                paramAddress != 0 &&
                !SaveDataExports.TryWriteDialogParam(ctx, paramAddress, _slot))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return ctx.SetReturn(ErrorOk);
        }
    }

    [SysAbiExport(
        Nid = "fH46Lag88XY",
        ExportName = "sceSaveDataDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogClose(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status != StatusRunning)
            {
                return ctx.SetReturn(ErrorNotRunning);
            }

            if (_list is not null)
            {
                FinishList(ResultUserCanceled, ButtonIdInvalid, string.Empty, null);
            }

            _status = StatusFinished;
            return ctx.SetReturn(ErrorOk);
        }
    }

    [SysAbiExport(
        Nid = "YuH2FA7azqQ",
        ExportName = "sceSaveDataDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogTerminate(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_list is not null)
            {
                EndList();
            }

            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            _status = StatusNone;
            _lastMode = 0;
            _lastUserData = 0;
            _result = ResultOk;
            _buttonId = ButtonIdOk;
            _dirName = string.Empty;
            _slot = null;
            return ctx.SetReturn(ErrorOk);
        }
    }

    [SysAbiExport(
        Nid = "V-uEeFKARJU",
        ExportName = "sceSaveDataDialogProgressBarInc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogProgressBarInc(CpuContext ctx) => ctx.SetReturn(ErrorOk);

    [SysAbiExport(
        Nid = "hay1CfTmLyA",
        ExportName = "sceSaveDataDialogProgressBarSetValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogProgressBarSetValue(CpuContext ctx) => ctx.SetReturn(ErrorOk);

    private static void FinishList(int result, int buttonId, string dirName, SaveDataDialogSlot? slot)
    {
        _result = result;
        _buttonId = buttonId;
        _dirName = dirName;
        _slot = slot;
        _status = StatusFinished;
        EndList();
    }

    private static void EndList()
    {
        _list = null;
        SaveDataListLayer.Instance.Hide();
        SystemDialogInput.Release();
    }

    private static DialogItems ReadItems(CpuContext ctx, ulong paramAddress, int dispType)
    {
        var items = new DialogItems();
        if (!ctx.TryReadUInt64(paramAddress + 0x48, out var itemsAddress) || itemsAddress == 0)
        {
            return items;
        }

        var userId = TryReadInt32(ctx, itemsAddress, out var user) ? user : 0;
        string? titleId = null;
        if (ctx.TryReadUInt64(itemsAddress + 0x08, out var titleIdAddress) && titleIdAddress != 0 &&
            TryReadString(ctx, titleIdAddress, TitleIdSize, out var title) && title.Length != 0)
        {
            titleId = title;
        }

        if (ctx.TryReadUInt64(itemsAddress + 0x10, out var dirNamesAddress) && dirNamesAddress != 0 &&
            ctx.TryReadUInt32(itemsAddress + 0x18, out var dirNameCount))
        {
            var count = (int)Math.Min(dirNameCount, MaxDialogItems);
            for (var index = 0; index < count; index++)
            {
                if (!TryReadString(ctx, dirNamesAddress + (ulong)(index * DirNameSize), DirNameSize, out var name) ||
                    name.Length == 0)
                {
                    continue;
                }

                items.FirstDirName ??= name;
                if (items.Slots.Exists(existing => existing.DirName == name))
                {
                    continue;
                }

                if (SaveDataExports.DescribeDialogSlot(userId, titleId, name) is { } slot)
                {
                    items.Slots.Add(slot);
                }
            }
        }

        if (dispType == DispTypeSave &&
            ctx.TryReadUInt64(itemsAddress + 0x20, out var newItemAddress) && newItemAddress != 0)
        {
            items.NewItemTitle =
                ctx.TryReadUInt64(newItemAddress, out var newTitleAddress) && newTitleAddress != 0 &&
                TryReadString(ctx, newTitleAddress, NewItemTitleMaxBytes, out var newTitle) && newTitle.Length != 0
                    ? newTitle
                    : string.Empty;
        }

        items.FocusPosition = ctx.TryReadUInt32(itemsAddress + 0x28, out var focus) ? focus : 0;
        if (items.FocusPosition == FocusDirName &&
            ctx.TryReadUInt64(itemsAddress + 0x30, out var focusNameAddress) && focusNameAddress != 0 &&
            TryReadString(ctx, focusNameAddress, DirNameSize, out var focusName))
        {
            items.FocusDirName = focusName;
        }

        return items;
    }

    private static List<ListEntry> BuildEntries(DialogItems items)
    {
        var entries = new List<ListEntry>(items.Slots.Count + 1);
        if (items.NewItemTitle is not null)
        {
            var title = items.NewItemTitle.Length != 0 ? items.NewItemTitle : SaveDataListLayer.Text("NewItem");
            entries.Add(new ListEntry(string.Empty, null,
                new SaveDataListItem(string.Empty, title, SaveDataListLayer.Text("NewItemDetail"), string.Empty, string.Empty,
                    null, IsNew: true)));
        }

        foreach (var slot in items.Slots)
        {
            entries.Add(new ListEntry(slot.DirName, slot, DescribeEntry(slot)));
        }

        return entries;
    }

    private static SaveDataListItem DescribeEntry(SaveDataDialogSlot slot)
    {
        var metadata = slot.Metadata;
        var hasSubTitle = !string.IsNullOrWhiteSpace(metadata.SubTitle);
        var hasTitle = !string.IsNullOrWhiteSpace(metadata.Title);
        var title = hasSubTitle ? metadata.SubTitle : hasTitle ? metadata.Title : slot.DirName;
        var overline = hasSubTitle && hasTitle ? metadata.Title : string.Empty;
        var detail = !string.IsNullOrWhiteSpace(metadata.Detail)
            ? metadata.Detail
            : title == slot.DirName ? string.Empty : slot.DirName;
        return new SaveDataListItem(
            overline,
            title,
            detail,
            OverlayText.FormatDate(slot.LastWriteUtc),
            OverlayText.FormatSize(slot.SizeBytes),
            slot.IconPath,
            IsNew: false);
    }

    private static int InitialFocus(IReadOnlyList<ListEntry> entries, DialogItems items)
    {
        var dataStart = items.NewItemTitle is null ? 0 : 1;
        switch (items.FocusPosition)
        {
            case FocusListTail:
            case FocusDataTail:
                return entries.Count - 1;
            case FocusDataHead:
                return Math.Min(dataStart, entries.Count - 1);
            case FocusDataLatest:
            case FocusDataOldest:
                var pick = -1;
                for (var index = dataStart; index < entries.Count; index++)
                {
                    var candidate = entries[index].Slot!.LastWriteUtc;
                    if (pick < 0 ||
                        (items.FocusPosition == FocusDataLatest
                            ? candidate > entries[pick].Slot!.LastWriteUtc
                            : candidate < entries[pick].Slot!.LastWriteUtc))
                    {
                        pick = index;
                    }
                }

                return Math.Max(0, pick);
            case FocusDirName:
                for (var index = 0; index < entries.Count; index++)
                {
                    if (string.Equals(entries[index].DirName, items.FocusDirName, StringComparison.Ordinal))
                    {
                        return index;
                    }
                }

                return 0;
            default:
                return 0;
        }
    }

    private static string CaptionFor(int dispType) => dispType switch
    {
        DispTypeSave => SaveDataListLayer.Text("Caption.Save"),
        DispTypeLoad => SaveDataListLayer.Text("Caption.Load"),
        DispTypeDelete => SaveDataListLayer.Text("Caption.Delete"),
        _ => SaveDataListLayer.Text("Caption.Other"),
    };

    private static void DeleteFocusedEntry(ListSession list)
    {
        var entry = list.Entries[list.Focus];
        if (entry.Slot is null || !SaveDataExports.TryDeleteDialogSlot(entry.Slot))
        {
            list.CancelDelete(SaveDataListLayer.Text("InUse"));
            SaveDataListLayer.Instance.Show(list.View());
            Console.Error.WriteLine($"[LOADER][WARN] save_data_dialog.delete_refused dir='{entry.DirName}'");
            return;
        }

        Console.Error.WriteLine($"[LOADER][INFO] save_data_dialog.deleted dir='{entry.DirName}'");
        list.Remove(list.Focus);
        if (list.Entries.Count == 0)
        {
            FinishList(ResultUserCanceled, ButtonIdInvalid, string.Empty, null);
            return;
        }

        SaveDataListLayer.Instance.Show(list.View());
    }

    private static bool TryReadString(CpuContext ctx, ulong address, int maxBytes, out string value)
    {
        value = string.Empty;
        Span<byte> bytes = stackalloc byte[maxBytes];
        var length = 0;
        Span<byte> one = stackalloc byte[1];
        while (length < maxBytes)
        {
            if (!ctx.Memory.TryRead(address + (ulong)length, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                break;
            }

            bytes[length++] = one[0];
        }

        value = Encoding.UTF8.GetString(bytes[..length]);
        return true;
    }

    private static bool TryReadInt32(CpuContext ctx, ulong address, out int value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static void TraceSaveDataDialog(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] save_data_dialog.{message}");
    }

    private sealed class DialogItems
    {
        public string? FirstDirName { get; set; }
        public List<SaveDataDialogSlot> Slots { get; } = [];
        public string? NewItemTitle { get; set; }
        public uint FocusPosition { get; set; }
        public string? FocusDirName { get; set; }
    }

    private readonly record struct ListEntry(string DirName, SaveDataDialogSlot? Slot, SaveDataListItem Item);

    private enum ListInput
    {
        None,
        Changed,
        Choose,
        Back,
        Delete,
    }

    private enum Press
    {
        Up,
        Down,
        Confirm,
        Cancel,
        Delete,
    }

    private sealed class ListSession(
        long session,
        List<ListEntry> entries,
        string caption,
        bool backEnabled,
        bool deleteEnabled)
    {
        private const byte StickUpThreshold = 64;
        private const byte StickDownThreshold = 192;
        private const int KeyBackspace = 0x08;
        private const int KeyEnter = 0x0D;
        private const int KeyEscape = 0x1B;
        private const int KeyUp = 0x26;
        private const int KeyDown = 0x28;
        private const int KeyDelete = 0x2E;
        private const int KeyV = 0x56;
        private const int KeyX = 0x58;
        private const int KeyZ = 0x5A;
        private static readonly long FirstRepeatTicks = Stopwatch.Frequency * 2 / 5;
        private static readonly long NextRepeatTicks = Stopwatch.Frequency / 9;

        private readonly List<uint> _controllerChanges = [];
        private readonly List<int> _keys = [];
        private readonly List<Press> _presses = [];
        private long _sequence;
        private uint _previousButtons;
        private int _heldDirection;
        private long _repeatAt;
        private bool _confirming;
        private string? _notice;
        private OverlayInputScheme _scheme;

        public List<ListEntry> Entries { get; } = entries;

        public int Focus { get; set; }

        public SaveDataListView View()
        {
            var items = new SaveDataListItem[Entries.Count];
            for (var index = 0; index < items.Length; index++)
            {
                items[index] = Entries[index].Item;
            }

            return new SaveDataListView(session, caption, items, Focus, backEnabled, deleteEnabled, _scheme,
                _confirming, _notice);
        }

        public void Begin()
        {
            var state = PadExports.ReadSystemDialogState();
            _sequence = PadExports.InputHistoryPosition;
            _previousButtons = state.ControllerButtons;
            _heldDirection = HeldDirection(state);
            _repeatAt = Stopwatch.GetTimestamp() + FirstRepeatTicks;
            _scheme = CurrentScheme();
        }

        public void CancelDelete(string? notice)
        {
            _confirming = false;
            _notice = notice;
        }

        public void Remove(int index)
        {
            Entries.RemoveAt(index);
            Focus = Math.Clamp(Focus, 0, Math.Max(0, Entries.Count - 1));
            _confirming = false;
            _notice = null;
        }

        public ListInput Poll()
        {
            var now = Stopwatch.GetTimestamp();
            var changed = false;
            var scheme = CurrentScheme();
            if (scheme != _scheme)
            {
                _scheme = scheme;
                changed = true;
            }

            _presses.Clear();
            _keys.Clear();
            SystemDialogInput.TakeKeys(_keys);
            foreach (var key in _keys)
            {
                switch (key)
                {
                    case KeyUp: _presses.Add(Press.Up); break;
                    case KeyDown: _presses.Add(Press.Down); break;
                    case KeyEnter or KeyZ: _presses.Add(Press.Confirm); break;
                    case KeyEscape or KeyBackspace or KeyX: _presses.Add(Press.Cancel); break;
                    case KeyDelete or KeyV: _presses.Add(Press.Delete); break;
                }
            }

            _controllerChanges.Clear();
            PadExports.TakeControllerHistory(ref _sequence, _controllerChanges);
            var current = PadExports.ReadSystemDialogState();
            _controllerChanges.Add(current.ControllerButtons);
            foreach (var buttons in _controllerChanges)
            {
                var pressed = buttons & ~_previousButtons;
                _previousButtons = buttons;
                if ((pressed & OrbisPadButton.Up) != 0) _presses.Add(Press.Up);
                if ((pressed & OrbisPadButton.Down) != 0) _presses.Add(Press.Down);
                if ((pressed & OrbisPadButton.Cross) != 0) _presses.Add(Press.Confirm);
                if ((pressed & OrbisPadButton.Circle) != 0) _presses.Add(Press.Cancel);
                if ((pressed & OrbisPadButton.Triangle) != 0) _presses.Add(Press.Delete);
            }

            foreach (var press in _presses)
            {
                if (_confirming)
                {
                    if (press == Press.Confirm)
                    {
                        return ListInput.Delete;
                    }

                    if (press == Press.Cancel)
                    {
                        _confirming = false;
                        changed = true;
                    }

                    continue;
                }

                switch (press)
                {
                    case Press.Confirm:
                        return ListInput.Choose;
                    case Press.Cancel when backEnabled:
                        return ListInput.Back;
                    case Press.Delete when deleteEnabled && Entries[Focus].Slot is { } slot:
                        if (SaveDataExports.IsDialogSlotMounted(slot))
                        {
                            _notice = SaveDataListLayer.Text("InUse");
                        }
                        else
                        {
                            _confirming = true;
                            _notice = null;
                        }

                        changed = true;
                        break;
                    case Press.Up or Press.Down:
                        var step = press == Press.Up ? -1 : 1;
                        changed |= Step(step);
                        _heldDirection = step;
                        _repeatAt = now + FirstRepeatTicks;
                        break;
                }
            }

            var direction = _confirming ? 0 : HeldDirection(current);
            if (direction == 0)
            {
                _heldDirection = 0;
            }
            else if (direction != _heldDirection)
            {
                changed |= Step(direction);
                _heldDirection = direction;
                _repeatAt = now + FirstRepeatTicks;
            }
            else if (now >= _repeatAt)
            {
                changed |= Step(direction);
                _repeatAt = now + NextRepeatTicks;
            }

            return changed ? ListInput.Changed : ListInput.None;
        }

        private bool Step(int step)
        {
            var next = Math.Clamp(Focus + step, 0, Entries.Count - 1);
            if (next == Focus)
            {
                return false;
            }

            Focus = next;
            _notice = null;
            return true;
        }

        private static int HeldDirection(PadState state)
        {
            var up = (state.ControllerButtons & OrbisPadButton.Up) != 0 || state.LeftY < StickUpThreshold ||
                     PadExports.IsHostKeyDown(KeyUp);
            var down = (state.ControllerButtons & OrbisPadButton.Down) != 0 || state.LeftY > StickDownThreshold ||
                       PadExports.IsHostKeyDown(KeyDown);
            return up == down ? 0 : up ? -1 : 1;
        }

        private static OverlayInputScheme CurrentScheme() => SystemDialogInput.CurrentScheme();
    }
}
