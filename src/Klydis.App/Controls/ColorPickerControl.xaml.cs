using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Klydis.App.Controls;

/// <summary>
/// A compact color picker: live preview, hex input, a row of preset swatches, and RGB
/// sliders. Exposes <see cref="SelectedColor"/> as a hex-string dependency property so a
/// ViewModel can two-way bind it directly.
/// </summary>
public partial class ColorPickerControl : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor),
            typeof(string),
            typeof(ColorPickerControl),
            new PropertyMetadata("#50E8F4", OnSelectedColorChanged));

    public string SelectedColor
    {
        get => (string)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    private static readonly string[] PresetColors =
    {
        "#50E8F4", "#B18CFF", "#FFC24B", "#FF8FB3", "#7BE39B",
        "#FF6B81", "#4D9FFF", "#34D399", "#F5C542", "#8B8CFF",
        "#C9A9FF", "#FF5CC8", "#6EE7B7", "#FFA04D", "#FFB49A",
        "#E5484D", "#5EB1FF", "#7DD3FC", "#2DD4BF", "#48C6EF",
        "#FFFFFF", "#C0C8CC", "#808890", "#3A4248", "#12181C", "#000000"
    };

    private bool _updating;

    public ColorPickerControl()
    {
        InitializeComponent();

        foreach (var hex in PresetColors)
        {
            if (TryParseHex(hex, out var color))
            {
                var swatch = new Button
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                    ToolTip = hex
                };
                // Swatches carry no text, so without this a screen reader announces nothing for
                // any of them — 26 presets in each of the three pickers.
                System.Windows.Automation.AutomationProperties.SetName(swatch, $"Colour {hex}");
                swatch.Click += OnPresetClick;
                PresetsPanel.Children.Add(swatch);
            }
        }

        HexBox.KeyDown += OnHexKeyDown;
        HexBox.LostKeyboardFocus += (_, _) => CommitHex();
        RedSlider.ValueChanged += OnSliderChanged;
        GreenSlider.ValueChanged += OnSliderChanged;
        BlueSlider.ValueChanged += OnSliderChanged;

        SyncFromHex();
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ColorPickerControl)d).SyncFromHex();
    }

    private void SyncFromHex()
    {
        if (_updating) return;
        if (!TryParseHex(SelectedColor, out var color)) return;

        _updating = true;
        try
        {
            PreviewSwatch.Background = new SolidColorBrush(color);
            HexBox.Text = ToHex(color);
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValue.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GreenValue.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BlueValue.Text = color.B.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }
    }

    private void CommitHex()
    {
        if (_updating) return;
        if (TryParseHex(HexBox.Text, out var color))
        {
            SetSelected(color);
        }
        else
        {
            // Revert to the last valid color.
            HexBox.Text = ToHex(TryParseHex(SelectedColor, out var current) ? current : Colors.Cyan);
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();
            e.Handled = true;
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && TryParseHex(hex, out var color))
        {
            SetSelected(color);
        }
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        SetSelected(Color.FromRgb(
            (byte)Math.Clamp(RedSlider.Value, 0, 255),
            (byte)Math.Clamp(GreenSlider.Value, 0, 255),
            (byte)Math.Clamp(BlueSlider.Value, 0, 255)));
    }

    private void SetSelected(Color color)
    {
        _updating = true;
        try
        {
            SetCurrentValue(SelectedColorProperty, ToHex(color));
        }
        finally
        {
            _updating = false;
        }
        SyncFromHex();
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim().TrimStart('#');
        if (t.Length == 3)
        {
            t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        }
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }
        color = Color.FromRgb(
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value);
        return true;
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
