// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SharpEmu.Libs.Pad;

namespace SharpEmu.GUI;

public partial class InputMappingWindow : Window
{
    private static readonly PadLogicalControl[] ButtonOrder =
    [
        PadLogicalControl.Cross,
        PadLogicalControl.Circle,
        PadLogicalControl.Square,
        PadLogicalControl.Triangle,
        PadLogicalControl.DpadUp,
        PadLogicalControl.DpadDown,
        PadLogicalControl.DpadLeft,
        PadLogicalControl.DpadRight,
        PadLogicalControl.L1,
        PadLogicalControl.R1,
        PadLogicalControl.L2,
        PadLogicalControl.R2,
        PadLogicalControl.L3,
        PadLogicalControl.R3,
        PadLogicalControl.Options,
        PadLogicalControl.TouchPad,
    ];

    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#8B94A7"));

    private readonly Dictionary<PadLogicalControl, TextBlock> _buttonBindingTexts = [];
    private readonly Dictionary<string, TextBlock> _stickBindingTexts = [];
    private PadInputProfile _profile = PadInputProfileStore.Load();
    private CaptureTarget? _captureTarget;

    public InputMappingWindow()
    {
        InitializeComponent();

        HookHotspots();
        ResetDefaultsButton.Click += (_, _) =>
        {
            _profile = PadInputProfile.CreateDefault();
            CaptureStatusText.Text = Localization.Instance.Get("Input.Restored");
            RenderProfile();
        };
        SaveButton.Click += (_, _) =>
        {
            ReadControlsIntoProfile();
            PadInputProfileStore.Save(_profile);
            CaptureStatusText.Text = Localization.Instance.Format("Input.Saved", PadInputProfileStore.ProfilePath);
        };
        CloseButton.Click += (_, _) => Close();

        KeyboardMouseToggle.IsCheckedChanged += (_, _) => _profile.EnableKeyboardAndMouse = KeyboardMouseToggle.IsChecked == true;
        ExternalControllerToggle.IsCheckedChanged += (_, _) => _profile.EnableExternalController = ExternalControllerToggle.IsChecked == true;

        ApplyLocalization();
        RenderProfile();
        UpdateControllerStatus();
    }

    private void ApplyLocalization()
    {
        var loc = Localization.Instance;

        Title = loc.Get("Input.WindowTitle");
        WindowTitleText.Text = loc.Get("Input.WindowTitle");
        KeyboardMouseToggle.Content = loc.Get("Input.KeyboardMouse");
        ExternalControllerToggle.Content = loc.Get("Input.ExternalController");

        LayoutSectionTitle.Text = loc.Get("Input.Section.Layout");
        SticksSectionTitle.Text = loc.Get("Input.Section.Sticks");
        ButtonsSectionTitle.Text = loc.Get("Input.Section.Buttons");
        ArtCreditText.Text = loc.Get("Input.ArtCredit");

        ResetDefaultsButton.Content = loc.Get("Input.ResetDefaults");
        SaveButton.Content = loc.Get("Input.Save");
        CloseButton.Content = loc.Get("Input.Close");
    }

    private void HookHotspots()
    {
        HookHotspot(HotCross, PadLogicalControl.Cross);
        HookHotspot(HotCircle, PadLogicalControl.Circle);
        HookHotspot(HotSquare, PadLogicalControl.Square);
        HookHotspot(HotTriangle, PadLogicalControl.Triangle);
        HookHotspot(HotDpadUp, PadLogicalControl.DpadUp);
        HookHotspot(HotDpadDown, PadLogicalControl.DpadDown);
        HookHotspot(HotDpadLeft, PadLogicalControl.DpadLeft);
        HookHotspot(HotDpadRight, PadLogicalControl.DpadRight);
        HookHotspot(HotL1, PadLogicalControl.L1);
        HookHotspot(HotR1, PadLogicalControl.R1);
        HookHotspot(HotL2, PadLogicalControl.L2);
        HookHotspot(HotR2, PadLogicalControl.R2);
        HookHotspot(HotL3, PadLogicalControl.L3);
        HookHotspot(HotR3, PadLogicalControl.R3);
        HookHotspot(HotOptions, PadLogicalControl.Options);
        HookHotspot(HotTouchPad, PadLogicalControl.TouchPad);
    }

    private void HookHotspot(Button button, PadLogicalControl control)
    {
        button.Click += (_, _) => StartCapture(CaptureTarget.Button(control),
            Localization.Instance.Format("Input.Prompt.Button", DisplayName(control)));
    }

    private void RenderProfile()
    {
        _captureTarget = null;
        _profile.EnsureDefaults();
        KeyboardMouseToggle.IsChecked = _profile.EnableKeyboardAndMouse;
        ExternalControllerToggle.IsChecked = _profile.EnableExternalController;
        _buttonBindingTexts.Clear();
        _stickBindingTexts.Clear();
        ButtonMappingPanel.Children.Clear();
        LeftStickPanel.Children.Clear();
        RightStickPanel.Children.Clear();

        BuildStickPanel(LeftStickPanel, PadStickSide.Left, "Input.Stick.Left");
        BuildStickPanel(RightStickPanel, PadStickSide.Right, "Input.Stick.Right");

        foreach (var control in ButtonOrder)
        {
            ButtonMappingPanel.Children.Add(CreateButtonRow(control));
        }

        RefreshBindingTexts();
    }

    private Control CreateButtonRow(PadLogicalControl control)
    {
        var label = new TextBlock
        {
            Text = DisplayName(control),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 110,
        };
        var bindingText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _buttonBindingTexts[control] = bindingText;

        var addButton = GhostButton(Localization.Instance.Get("Input.AddKey"));
        addButton.Click += (_, _) => StartCapture(CaptureTarget.Button(control),
            Localization.Instance.Format("Input.Prompt.Button", DisplayName(control)));

        var clearButton = GhostButton(Localization.Instance.Get("Input.Clear"));
        clearButton.Click += (_, _) =>
        {
            _profile.Buttons[control].Bindings.Clear();
            RefreshBindingTexts();
        };

        return new Border
        {
            Classes = { "mappingRow" },
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                ColumnSpacing = 8,
                Children =
                {
                    label,
                    WithColumn(bindingText, 1),
                    WithColumn(addButton, 2),
                    WithColumn(clearButton, 3),
                },
            },
        };
    }

    private void BuildStickPanel(StackPanel panel, PadStickSide side, string titleKey)
    {
        var loc = Localization.Instance;
        var mapping = _profile.Sticks[side];
        panel.Children.Add(new TextBlock { Text = loc.Get(titleKey), FontWeight = FontWeight.SemiBold });

        var sourceBox = new ComboBox
        {
            ItemsSource = new[]
            {
                loc.Get("Input.Source.Keyboard"),
                loc.Get("Input.Source.Mouse"),
                loc.Get("Input.Source.ExternalController"),
            },
            SelectedIndex = mapping.Source switch
            {
                PadStickSource.Keyboard => 0,
                PadStickSource.Mouse => 1,
                PadStickSource.ExternalController => 2,
                _ => 0,
            },
        };
        sourceBox.SelectionChanged += (_, _) =>
        {
            mapping.Source = sourceBox.SelectedIndex switch
            {
                1 => PadStickSource.Mouse,
                2 => PadStickSource.ExternalController,
                _ => PadStickSource.Keyboard,
            };
        };
        panel.Children.Add(sourceBox);

        panel.Children.Add(CreateStickKeyRow(side, loc.Get("Input.Direction.Left"), negativeX: true));
        panel.Children.Add(CreateStickKeyRow(side, loc.Get("Input.Direction.Right"), positiveX: true));
        panel.Children.Add(CreateStickKeyRow(side, loc.Get("Input.Direction.Up"), negativeY: true));
        panel.Children.Add(CreateStickKeyRow(side, loc.Get("Input.Direction.Down"), positiveY: true));

        var sensitivity = new Slider
        {
            Minimum = 0.1,
            Maximum = 6,
            Value = mapping.MouseSensitivity,
        };
        sensitivity.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                mapping.MouseSensitivity = sensitivity.Value;
            }
        };
        panel.Children.Add(new TextBlock
        {
            Text = loc.Get("Input.MouseSensitivity"),
            FontSize = 12,
            Foreground = MutedTextBrush,
        });
        panel.Children.Add(sensitivity);

        var invertX = new CheckBox { Content = loc.Get("Input.InvertX"), IsChecked = mapping.InvertX };
        invertX.IsCheckedChanged += (_, _) => mapping.InvertX = invertX.IsChecked == true;
        var invertY = new CheckBox { Content = loc.Get("Input.InvertY"), IsChecked = mapping.InvertY };
        invertY.IsCheckedChanged += (_, _) => mapping.InvertY = invertY.IsChecked == true;
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { invertX, invertY },
        });
    }

    private Control CreateStickKeyRow(
        PadStickSide side,
        string labelText,
        bool negativeX = false,
        bool positiveX = false,
        bool negativeY = false,
        bool positiveY = false)
    {
        var loc = Localization.Instance;
        var target = CaptureTarget.Stick(side, negativeX, positiveX, negativeY, positiveY);
        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 52,
        };
        var bindingText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _stickBindingTexts[target.Key] = bindingText;

        var setButton = GhostButton(loc.Get("Input.Set"));
        setButton.Click += (_, _) => StartCapture(target,
            Localization.Instance.Format("Input.Prompt.Stick", DisplayName(side), labelText.ToLowerInvariant()));

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                label,
                WithColumn(bindingText, 1),
                WithColumn(setButton, 2),
            },
        };
    }

    private static Button GhostButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Avalonia.Thickness(10, 5),
        };
        button.Classes.Add("ghost");
        return button;
    }

    private static T WithColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private void StartCapture(CaptureTarget target, string message)
    {
        _captureTarget = target;
        CaptureStatusText.Text = message;
        Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (_captureTarget is null)
        {
            return;
        }

        var key = NormalizeKey(args.Key);
        if (key.Length == 0)
        {
            return;
        }

        if (_captureTarget.Control is { } control)
        {
            var bindings = _profile.Buttons[control].Bindings;
            if (!bindings.Any(binding =>
                    binding.Kind == PadInputBindingKind.KeyboardKey &&
                    binding.Code.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                bindings.Add(PadInputBinding.Key(key));
            }
        }
        else if (_captureTarget.Side is { } side)
        {
            var mapping = _profile.Sticks[side];
            if (_captureTarget.NegativeX) mapping.NegativeXKey = key;
            if (_captureTarget.PositiveX) mapping.PositiveXKey = key;
            if (_captureTarget.NegativeY) mapping.NegativeYKey = key;
            if (_captureTarget.PositiveY) mapping.PositiveYKey = key;
        }

        _captureTarget = null;
        CaptureStatusText.Text = Localization.Instance.Format("Input.Mapped", key);
        RefreshBindingTexts();
        args.Handled = true;
    }

    private void ReadControlsIntoProfile()
    {
        _profile.EnableKeyboardAndMouse = KeyboardMouseToggle.IsChecked == true;
        _profile.EnableExternalController = ExternalControllerToggle.IsChecked == true;
    }

    private void RefreshBindingTexts()
    {
        var unmapped = Localization.Instance.Get("Input.Unmapped");

        foreach (var (control, text) in _buttonBindingTexts)
        {
            text.Text = _profile.Buttons.TryGetValue(control, out var mapping) && mapping.Bindings.Count > 0
                ? string.Join(", ", mapping.Bindings.Select(DisplayBinding))
                : unmapped;
        }

        foreach (var (key, text) in _stickBindingTexts)
        {
            var target = CaptureTarget.FromKey(key);
            var mapping = _profile.Sticks[target.Side!.Value];
            text.Text = target switch
            {
                { NegativeX: true } => ValueOrUnmapped(mapping.NegativeXKey, unmapped),
                { PositiveX: true } => ValueOrUnmapped(mapping.PositiveXKey, unmapped),
                { NegativeY: true } => ValueOrUnmapped(mapping.NegativeYKey, unmapped),
                { PositiveY: true } => ValueOrUnmapped(mapping.PositiveYKey, unmapped),
                _ => unmapped,
            };
        }
    }

    private void UpdateControllerStatus()
    {
        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        ControllerStatusText.Text = DualSenseReader.TryGetState(out _)
            ? Localization.Instance.Get("Input.Controller.DualSense")
            : XInputReader.TryGetState(out _)
                ? Localization.Instance.Get("Input.Controller.XInput")
                : Localization.Instance.Get("Input.Controller.None");
    }

    private static string NormalizeKey(Key key)
    {
        var text = key.ToString();
        return text switch
        {
            "Return" => "Enter",
            "Esc" => "Escape",
            "BackSpace" => "Back",
            _ => text,
        };
    }

    private static string DisplayBinding(PadInputBinding binding) => binding.Kind switch
    {
        PadInputBindingKind.KeyboardKey => binding.Code,
        PadInputBindingKind.MouseButton => binding.Code,
        _ => binding.Code,
    };

    private static string ValueOrUnmapped(string value, string unmapped) =>
        string.IsNullOrWhiteSpace(value) ? unmapped : value;

    private static string DisplayName(PadLogicalControl control) => control switch
    {
        PadLogicalControl.Cross => Localization.Instance.Get("Input.Button.Cross"),
        PadLogicalControl.Circle => Localization.Instance.Get("Input.Button.Circle"),
        PadLogicalControl.Square => Localization.Instance.Get("Input.Button.Square"),
        PadLogicalControl.Triangle => Localization.Instance.Get("Input.Button.Triangle"),
        PadLogicalControl.L1 => Localization.Instance.Get("Input.Button.L1"),
        PadLogicalControl.R1 => Localization.Instance.Get("Input.Button.R1"),
        PadLogicalControl.L2 => Localization.Instance.Get("Input.Button.L2"),
        PadLogicalControl.R2 => Localization.Instance.Get("Input.Button.R2"),
        PadLogicalControl.L3 => Localization.Instance.Get("Input.Button.L3"),
        PadLogicalControl.R3 => Localization.Instance.Get("Input.Button.R3"),
        PadLogicalControl.Options => Localization.Instance.Get("Input.Button.Options"),
        PadLogicalControl.TouchPad => Localization.Instance.Get("Input.Button.TouchPad"),
        PadLogicalControl.DpadUp => Localization.Instance.Get("Input.Button.DpadUp"),
        PadLogicalControl.DpadDown => Localization.Instance.Get("Input.Button.DpadDown"),
        PadLogicalControl.DpadLeft => Localization.Instance.Get("Input.Button.DpadLeft"),
        PadLogicalControl.DpadRight => Localization.Instance.Get("Input.Button.DpadRight"),
        _ => control.ToString(),
    };

    private static string DisplayName(PadStickSide side) =>
        side == PadStickSide.Left
            ? Localization.Instance.Get("Input.Stick.Left")
            : Localization.Instance.Get("Input.Stick.Right");

    private sealed record CaptureTarget(
        PadLogicalControl? Control,
        PadStickSide? Side,
        bool NegativeX,
        bool PositiveX,
        bool NegativeY,
        bool PositiveY)
    {
        public string Key =>
            Control?.ToString() ??
            $"{Side}:{NegativeX}:{PositiveX}:{NegativeY}:{PositiveY}";

        public static CaptureTarget Button(PadLogicalControl control) =>
            new(control, null, false, false, false, false);

        public static CaptureTarget Stick(
            PadStickSide side,
            bool negativeX,
            bool positiveX,
            bool negativeY,
            bool positiveY) =>
            new(null, side, negativeX, positiveX, negativeY, positiveY);

        public static CaptureTarget FromKey(string key)
        {
            var parts = key.Split(':');
            return new CaptureTarget(
                null,
                Enum.Parse<PadStickSide>(parts[0]),
                bool.Parse(parts[1]),
                bool.Parse(parts[2]),
                bool.Parse(parts[3]),
                bool.Parse(parts[4]));
        }
    }
}
