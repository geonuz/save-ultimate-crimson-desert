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
        source = Regex.Replace(source, @"</?(?:p|h[1-6]|ul|ol)[^>]*>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
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
