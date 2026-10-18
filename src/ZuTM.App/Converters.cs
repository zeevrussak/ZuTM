// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — Apache License 2.0.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ZuTM.App.Services;

namespace ZuTM.App;

/// <summary>x:Bind function converters (no IConverter resources needed).</summary>
public static class Converters
{
    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToCollapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // Mid-tone hexes readable on both light and dark surfaces; the dot is 12px,
    // so slight theme mismatch is invisible — and this needs no theme-resource
    // lookup that would go stale when the user overrides the app theme.
    private static readonly SolidColorBrush RunningBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x10, 0x89, 0x3E));
    private static readonly SolidColorBrush PausedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xB9, 0x00));
    private static readonly SolidColorBrush TransitionalBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush StoppedBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x8A, 0x88, 0x86));

    /// <summary>Status color language: green=running, amber=paused, blue=transitioning, gray=stopped.</summary>
    public static Brush StatusBrush(VmStatus status) => status switch
    {
        VmStatus.Running => RunningBrush,
        VmStatus.Paused or VmStatus.Pausing => PausedBrush,
        VmStatus.Starting or VmStatus.Resuming or VmStatus.Stopping => TransitionalBrush,
        _ => StoppedBrush,
    };
}
