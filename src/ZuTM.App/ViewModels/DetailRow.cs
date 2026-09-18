// ZuTM (c) Ze'ev Russak <zutm@20032014.xyz> — ZuTM Attribution License.

namespace ZuTM.App.ViewModels;

/// <summary>Label/value row for the detail pane lists (mutable for XAML activation).</summary>
public sealed class DetailRow
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DetailRow()
    {
    }

    public DetailRow(string label, string value)
    {
        Label = label;
        Value = value;
    }
}
