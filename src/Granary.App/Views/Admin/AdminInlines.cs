using Granary.Core.Texts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// A web text with its rich-text tags, as the admin pages show them: <c>&lt;b&gt;</c> in the foreground colour and
/// semibold, <c>&lt;code&gt;</c> in a fixed-width face, any other tag (a link the web makes) as plain text.
/// </summary>
internal static class AdminInlines
{
    public static void Fill(TextBlock block, string key, params (string Name, object? Value)[] values)
    {
        block.Inlines.Clear();
        foreach (var part in AppText.Runs(key, values))
        {
            var run = new Run { Text = part.Text };
            if (part.Tags.Contains("b"))
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Foreground = (Brush)Application.Current.Resources["SiloForegroundBrush"];
            }
            if (part.Tags.Contains("code"))
            {
                run.FontFamily = new FontFamily("Consolas");
            }
            block.Inlines.Add(run);
        }
    }
}
