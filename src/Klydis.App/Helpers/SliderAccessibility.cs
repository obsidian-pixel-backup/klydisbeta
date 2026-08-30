using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Klydis.App.Helpers;

/// <summary>
/// Labels the two halves of a slider's track so they do not reach a screen reader unnamed.
/// </summary>
/// <remarks>
/// A WPF Slider's template puts a RepeatButton on each side of the thumb to page the value. They
/// are exposed to UI Automation as control *and* content elements, so a screen reader stops on
/// them, but the default template gives them no name -- an accessibility sweep of Settings found
/// 18 of them (three colour pickers, three channels each, two halves), every one announcing itself
/// as an unlabelled "button".
///
/// Naming them through the Track keeps WPF's own template, so nothing about the appearance
/// changes. Attached rather than templated for the same reason: replacing the Slider template to
/// add two names would restyle every slider in the app.
/// </remarks>
public static class SliderAccessibility
{
    public static readonly DependencyProperty NameTrackPartsProperty =
        DependencyProperty.RegisterAttached(
            "NameTrackParts",
            typeof(bool),
            typeof(SliderAccessibility),
            new PropertyMetadata(false, OnNameTrackPartsChanged));

    public static void SetNameTrackParts(DependencyObject element, bool value)
        => element.SetValue(NameTrackPartsProperty, value);

    public static bool GetNameTrackParts(DependencyObject element)
        => (bool)element.GetValue(NameTrackPartsProperty);

    private static void OnNameTrackPartsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        if (e.NewValue is true)
        {
            slider.Loaded += OnSliderLoaded;
            // A slider already loaded when the property is set never raises Loaded again.
            if (slider.IsLoaded) ApplyNames(slider);
        }
        else
        {
            slider.Loaded -= OnSliderLoaded;
        }
    }

    private static void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider slider) ApplyNames(slider);
    }

    private static void ApplyNames(Slider slider)
    {
        slider.ApplyTemplate();

        // The Track owns both repeat buttons, so this does not depend on the template's part names.
        if (slider.Template?.FindName("PART_Track", slider) is not Track track) return;

        string label = AutomationProperties.GetName(slider);
        if (string.IsNullOrWhiteSpace(label)) label = "value";

        if (track.DecreaseRepeatButton is { } decrease)
        {
            AutomationProperties.SetName(decrease, $"Decrease {label}");
        }

        if (track.IncreaseRepeatButton is { } increase)
        {
            AutomationProperties.SetName(increase, $"Increase {label}");
        }
    }
}
