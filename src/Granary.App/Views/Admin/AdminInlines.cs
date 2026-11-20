using Granary.Core.Texts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// A web text with its rich-text tags, as the admin pages show them: <c>&lt;b&gt;</c> in the foreground colour and
/// semibold, <c>&lt;code&gt;</c> in a fixed-width face, <c>&lt;link&gt;</c> as a link where the page gives it somewhere to
/// go (the foreground, medium, underlined, as the web's <c>SettingsLink</c>), and any other tag as plain text.
/// </summary>
internal static class AdminInlines
{
    public static void Fill(TextBlock block, string key, params (string Name, object? Value)[] values) => Fill(block, key, null, values);

    public static void Fill(TextBlock block, string key, Action? onLink, params (string Name, object? Value)[] values)
    {
        block.Inlines.Clear();
        var foreground = (Brush)Application.Current.Resources["SiloForegroundBrush"];
        foreach (var part in AppText.Runs(key, values))
        {
            var run = new Run { Text = part.Text };
            if (part.Tags.Contains("b"))
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Foreground = foreground;
            }
            if (part.Tags.Contains("code"))
            {
                run.FontFamily = new FontFamily("Consolas");
            }
            if (onLink is not null && part.Tags.Contains("link"))
            {
                var link = new Hyperlink { Foreground = foreground, FontWeight = FontWeights.Medium, UnderlineStyle = UnderlineStyle.Single };
                link.Inlines.Add(run);
                link.Click += (_, _) => onLink();
                block.Inlines.Add(link);
                continue;
            }
            block.Inlines.Add(run);
        }
    }
}
