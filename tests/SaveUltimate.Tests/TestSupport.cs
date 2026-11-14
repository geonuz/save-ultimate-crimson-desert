using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;

// WPF windows and DlcContent.Hidden are process-wide, so tests using them carry [DoNotParallelize].
[assembly: Parallelize(Workers = 12, Scope = ExecutionScope.ClassLevel)]

namespace SaveUltimate.Tests;

// Every run uses a throwaway settings file in Korean, so UI tests read Korean text and never touch the user's own settings.
[TestClass]
public sealed class TestRun
{
    private static string oldPath = "";
    private static EditorSettings old = new();

    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        old = SettingsStore.Current; oldPath = SettingsStore.FilePath;
        SettingsStore.FilePath = Path.Combine(Path.GetTempPath(), "save-tests-" + Guid.NewGuid().ToString("N") + ".json");
        SettingsStore.Save(old with { Language = "ko" });
        Tr.SetLanguage("ko");
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        string temporary = SettingsStore.FilePath;
        SettingsStore.Save(old); SettingsStore.FilePath = oldPath; File.Delete(temporary);
    }
}
// One folder per use, removed even when the test fails.
internal sealed class TempFolder : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("SaveUltimate-").FullName;
    public string Named(string name) => Path.Combine(Root, name);
    public void Dispose() => Directory.Delete(Root, true);
}

internal static class TestFiles
{
    // The checkout's resources folder, for comparing the shipped files with what the test output or a builder holds.
    internal static string Resources { get; } = Find(new DirectoryInfo(AppContext.BaseDirectory));
    private static string Find(DirectoryInfo folder) => File.Exists(Path.Combine(folder.FullName, "resources/data/knowledge_ui.json"))
        ? Path.Combine(folder.FullName, "resources")
        : Find(folder.Parent ?? throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertInconclusiveException("Repository resources not found."));
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

    internal static void Pump(Func<Task> action)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        try
        {
        var task = action();
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
    }

    internal static void Capture(Window window, string page)
    {
        string? output = Environment.GetEnvironmentVariable("SAVEULTIMATE_UI_PREVIEW");
        if (output is null) return;
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + page + ".png");
        using var stream = File.Create(path); encoder.Save(stream);
    }

    // Two tables side by side start and end at the same height.
    internal static void AssertSideBySide(FrameworkElement page, FrameworkElement left, FrameworkElement right)
    {
        page.UpdateLayout();
        double Top(FrameworkElement table) => table.TranslatePoint(new(), page).Y;
        Assert.AreEqual(Top(left), Top(right), 0.5, "Both tables start at the same height.");
        Assert.AreEqual(Top(left) + left.ActualHeight, Top(right) + right.ActualHeight, 0.5, "Both tables end at the same height.");
    }
}
