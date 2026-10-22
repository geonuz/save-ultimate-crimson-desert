using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SaveUltimate.App;

public sealed record EditorSettings
{
    public string Language { get; init; } = "en";
    public string Theme { get; init; } = "dark";
    public double FontScale { get; init; } = 1;
    public int UiScale { get; init; } = 100;
    public bool CompactMode { get; init; }
    public bool ShowIcons { get; init; } = true;
    public string SaveRoot { get; init; } = "";
    public string AdditionalSaveRoot { get; init; } = "";
    public string GameInstallPath { get; init; } = "";
    public bool AutoGameFolder { get; init; } = true;
    public Dictionary<string, string> CustomThemeColors { get; init; } = [];
    public string[] RecentSaveFiles { get; init; } = [];
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "editor_config.json");
    public static EditorSettings Current { get; private set; } = Read(FilePath);
    public static EditorSettings Read(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllBytes(path), json) ?? new();
            return value with
            {
                Language = value.Language ?? "en", Theme = value.Theme ?? "dark",
                SaveRoot = value.SaveRoot ?? "", AdditionalSaveRoot = value.AdditionalSaveRoot ?? "", GameInstallPath = value.GameInstallPath ?? "",
                CustomThemeColors = value.CustomThemeColors ?? [],
                RecentSaveFiles = (value.RecentSaveFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).ToArray()
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static void Save(EditorSettings settings)
    {
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, json)); File.Move(temp, FilePath, true); Current = settings; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Remember(string path) => Save(Current with { RecentSaveFiles = Current.RecentSaveFiles.Prepend(Path.GetFullPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray() });
}

public sealed record ThemeDefinition(string Name, string Description, Dictionary<string, string> Colors);
public static class Appearance
{
    public static readonly DependencyProperty RowNumbersProperty = DependencyProperty.RegisterAttached("RowNumbers", typeof(bool), typeof(Appearance),
        new PropertyMetadata(false, (d, e) => { if (d is DataGrid grid && (bool)e.NewValue) grid.LoadingRow += (_, row) => row.Row.Header = row.Row.GetIndex() + 1; }));
    public static bool GetRowNumbers(DependencyObject target) => (bool)target.GetValue(RowNumbersProperty);
    public static void SetRowNumbers(DependencyObject target, bool value) => target.SetValue(RowNumbersProperty, value);
    public static readonly Dictionary<string, ThemeDefinition> Themes = JsonSerializer.Deserialize<Dictionary<string, ThemeDefinition>>(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/data/ui_themes.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    public static event Action? Changed;
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static string Contrast(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 >= 150 ? "#111111" : "#FFFFFF";
    }
    public static Dictionary<string, string> Palette(EditorSettings settings)
    {
        var colors = new Dictionary<string, string>(Themes.GetValueOrDefault(settings.Theme, Themes["dark"]).Colors);
        if (settings.Theme == "custom")
            foreach (var (key, value) in settings.CustomThemeColors)
                if (colors.ContainsKey(key) && ValidColor(value)) colors[key] = value;
        return colors;
    }
    public static bool ValidColor(string text) => text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
    public static void Apply(EditorSettings settings)
    {
        var resources = Application.Current.Resources;
        foreach (var (key, value) in Palette(settings))
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); resources[key] = brush;
        }
        foreach (var (key, color) in new[] { (SystemColors.WindowBrushKey, "panel"), (SystemColors.WindowTextBrushKey, "text"), (SystemColors.ControlBrushKey, "header"),
            (SystemColors.ControlLightBrushKey, "header"), (SystemColors.ControlLightLightBrushKey, "panel"), (SystemColors.ControlDarkBrushKey, "border"),
            (SystemColors.ControlTextBrushKey, "text"), (SystemColors.HighlightBrushKey, "selected"), (SystemColors.HighlightTextBrushKey, "selection_text"),
            (SystemColors.InactiveSelectionHighlightBrushKey, "selected"), (SystemColors.InactiveSelectionHighlightTextBrushKey, "selection_text") })
            resources[key] = resources[color];
        double font = Math.Clamp(settings.FontScale, .75, 2), scale = Math.Clamp(settings.UiScale, 75, 200) / 100d;
        resources["BodySize"] = 16d * font; resources["SmallSize"] = 14d * font;
        resources["TitleSize"] = 22d * font; resources["BrandSize"] = 21d * font;
        resources["TableRowHeight"] = (settings.CompactMode ? 32d : 40d) * font;
        resources["ButtonPadding"] = settings.CompactMode ? new Thickness(10, 3, 10, 3) : new Thickness(16, 6, 16, 6);
        resources["InputPadding"] = settings.CompactMode ? new Thickness(6, 3, 6, 3) : new Thickness(8, 5, 8, 5);
        var transform = new ScaleTransform(scale, scale); transform.Freeze(); resources["UiTransform"] = transform;
        Tr.SetLanguage(settings.Language);
        Changed?.Invoke();
    }
}
