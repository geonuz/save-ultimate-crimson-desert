using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SaveUltimate.App;

namespace SaveUltimate.Tests;

// One folder per use, removed even when the test fails.
internal sealed class TempFolder : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("SaveUltimate-").FullName;
    public string Named(string name) => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, true);
}

internal static class TestUi
{
    // App.App would run OnStartup on the first dispatcher pump and open a real main window that outlives its test thread,
    // and it cannot be subclassed from here because its XAML only loads for its own assembly. So this mirrors App.xaml.
    internal static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SaveUltimate;component/Theme.xaml", UriKind.Relative) });
        app.Resources["BooleanVisibility"] = new BooleanToVisibilityConverter();
        Appearance.Apply(SettingsStore.Current);
    }

    internal static T Hidden<T>(T window) where T : Window
    { window.Left = -10000; window.Top = -10000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowActivated = false; window.ShowInTaskbar = false; return window; }

    internal static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
