// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

using Microsoft.UI.Xaml;

namespace ZuTM.App;

/// <summary>x:Bind function converters (no IConverter resources needed).</summary>
public static class Converters
{
    public static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ToCollapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
