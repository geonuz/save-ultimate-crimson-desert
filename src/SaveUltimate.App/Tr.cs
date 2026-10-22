using System.IO;
using System.Text.Json;
using System.Windows.Markup;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace SaveUltimate.App;

// Share the preserved Python translation keys rather than inventing port-specific labels.
public sealed class Tr(string key) : MarkupExtension
{
    private sealed class Source : INotifyPropertyChanged
    {
        public string Language => Tr.Language;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Changed() => PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
    private sealed class Lookup : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Text((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
    private static readonly Source source = new();
    private static readonly Lookup lookup = new();
    public static string Language { get; private set; } = "en";
    private static Dictionary<string, string> Read(string language) => JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources", "locale", language + ".json")))!;
    private static Dictionary<string, string> strings = Read(Language);
    private static readonly Dictionary<string, string> english = Read("en");
    private static readonly Dictionary<string, string[]> captionKeys = english.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());
    private static Dictionary<string, Dictionary<string, string>>? names;
    internal static void ReloadNames() => names = null;
    internal static string EnglishCaption(string caption)
    {
        string? key = strings.FirstOrDefault(p => p.Value.Replace("&&", "&") == caption).Key;
        return key is null ? caption : english.GetValueOrDefault(key, caption);
    }
    public static string Name(string category, string key, string fallback)
    {
        names ??= ReadNames();
        return names.TryGetValue(category, out var group) ? group.GetValueOrDefault(key) ?? fallback : fallback;
    }
    private static Dictionary<string, Dictionary<string, string>> ReadNames()
    {
        if (Language == "en") return [];
        using var json = JsonDocument.Parse(File.ReadAllBytes(CatalogFiles.Find("locale/names_" + Language + ".json")));
        return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .ToDictionary(p => p.Name, p => p.Value.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetString()!));
    }
    public static void SetLanguage(string language)
    {
        language = new[] { "ko", "en", "ja", "ru" }.Contains(language) ? language : "en";
        if (language == Language) return;
        var old = strings; strings = Read(language); Language = language; names = null;
        ItemCatalog.Reset(); GameText.Reset(); KnowledgeText.Reset(); CharacterText.Reset();
        var replacements = old.GroupBy(p => p.Value.Replace("&&", "&")).Where(g => g.Select(p => Text(p.Key)).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => Text(g.First().Key));
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            foreach (Window window in Application.Current.Windows) Translate(window, replacements);
        source.Changed();
    }
    private static void Translate(System.Windows.DependencyObject parent, Dictionary<string, string> replacements)
    {
        foreach (var property in new[] { System.Windows.Controls.TextBlock.TextProperty, System.Windows.Controls.ContentControl.ContentProperty,
            System.Windows.Controls.HeaderedContentControl.HeaderProperty, System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            System.Windows.FrameworkElement.ToolTipProperty })
            if (parent.GetValue(property) is string text && replacements.TryGetValue(text, out string? translated))
                parent.SetCurrentValue(property, translated);
        if (parent is System.Windows.Controls.DataGrid grid)
            foreach (var column in grid.Columns)
                if (column.Header is string text && replacements.TryGetValue(text, out string? translated)) column.Header = translated;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>()) Translate(child, replacements);
    }
    public static string Text(string key)
    {
        if (strings.TryGetValue(key, out string? text)) return text.Replace("&&", "&");
        var captions = captionKeys.TryGetValue(key, out var keys) ? keys.Select(k => strings.GetValueOrDefault(k, key)).Distinct().ToArray() : [];
        return (captions.Length == 1 ? captions[0] : english.GetValueOrDefault(key, key)).Replace("&&", "&");
    }
    public static string Format(string key, params (string Name, object Value)[] values)
    {
        string text = Text(key);
        foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value.ToString());
        return text;
    }
    public static string Count(int total, int shown) => Format("ui.count_total", ("total", total.ToString("N0"))) +
        (shown == total ? "" : " · " + Format("ui.count_shown", ("shown", shown.ToString("N0"))));
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetProperty: DependencyProperty }
            ? new Binding(nameof(Source.Language)) { Source = source, Converter = lookup, ConverterParameter = key }.ProvideValue(serviceProvider) : Text(key);
}
