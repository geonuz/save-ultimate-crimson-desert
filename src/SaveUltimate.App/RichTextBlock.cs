using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SaveUltimate.App;

internal static class RichTextBlock
{
    public static TextBlock Create(string source)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Padding = new(16), LineHeight = 25 };
        // Help is bundled HTML; render its text and emphasis without hosting a browser.
        // A heading gets one blank line above it and none below, so it stays with its list.
        source = Regex.Replace(source, @"<h[1-6][^>]*>(.*?)</h[1-6]>", "\n\n<b>$1</b>", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"</?(?:ul|ol|li)[^>]*>", m => m.Value.StartsWith("<li", StringComparison.OrdinalIgnoreCase) ? "\n• " : "", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"</?p[^>]*>|<br\s*/?>", "\n", RegexOptions.IgnoreCase).Trim('\n');
        bool bold = false;
        foreach (string part in Regex.Split(source, @"(</?(?:b|strong)>|\*\*)", RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(part, @"^(</?(?:b|strong)>|\*\*)$", RegexOptions.IgnoreCase)) { bold = !bold; continue; }
            var run = new Run(WebUtility.HtmlDecode(Regex.Replace(part, "<[^>]+>", ""))) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            text.Inlines.Add(run);
        }
        return text;
    }
}
