using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class WindowTests
{
    [STATestMethod]
    public void SocketDraftSurvivesTabsAndMatchingSearchButResetsWithSelectionOrSaveChanges()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES for socket draft parity.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        string original = document.BlobSha256;
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        var grid = (DataGrid)window.FindName("InventoryGrid");
        model.PageIndex = 2;
        var row = grid.Items.Cast<ItemRow>().First(r => r.Item.Source == ItemSource.Inventory && r.Item.Sockets.Unlocked > 0 && SocketState.Limit(r.Item.Key) > 0);
        grid.SelectedItem = row;
        uint saved = model.SocketRows[0].SelectedKey;
        uint draft = saved == 1002785 ? 1002786u : 1002785u;
        model.SocketRows[0].SelectedKey = draft;
        model.UnlockedSockets = 0;
        var rows = model.SocketRows;
        model.PageIndex = 1;
        grid.SelectedItem = grid.Items[0];
        Assert.IsFalse(model.SocketsCommand.CanExecute(null));
        model.PageIndex = 2;
        Assert.AreSame(rows, model.SocketRows);
        Assert.AreEqual(draft, model.SocketRows[0].SelectedKey);
        Assert.AreEqual(0, model.UnlockedSockets);
        model.Search = row.Item.Key.ToString();
        Assert.AreSame(rows, model.SocketRows);
        Assert.AreEqual(draft, model.SocketRows[0].SelectedKey);
        Assert.AreEqual(original, document.BlobSha256, "Drafts must not change save bytes.");
        model.Search = "unlikely_missing_item_12345";
        Assert.AreEqual(0, model.SocketRows.Length);
        Assert.IsFalse(model.SocketsCommand.CanExecute(null));
        model.Search = "";
        grid.SelectedItem = row;
        Assert.AreEqual(saved, model.SocketRows[0].SelectedKey);
        Assert.AreEqual(row.Item.Sockets.Unlocked, model.UnlockedSockets);
        model.SocketRows[0].SelectedKey = draft;
        Pump(() => model.SocketsCommand.ExecuteAsync(null));
        Assert.AreEqual(draft, model.SocketRows[0].SelectedKey);
        Assert.AreEqual(draft, document.SocketTarget(model.Selected!.Item).Sockets.Slots[0].Key);
        Pump(() => model.UndoCommand.ExecuteAsync(null));
        Assert.AreEqual(saved, model.SocketRows[0].SelectedKey);
        Assert.AreEqual(original, document.BlobSha256);
        model.SocketRows[0].SelectedKey = draft;
        Pump(() => model.LoadFileAsync(document.SourcePath));
        Assert.AreEqual(0, model.SocketRows.Length);
        Assert.IsFalse(model.SocketsCommand.CanExecute(null));
        window.Close();
    }

    [STATestMethod]
    public void SelectionAndSortSurviveTabsEditsAndUndoButNotAnotherFile()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES for selection parity.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        string path = Path.Combine(fixtures, "save_v2.03.02.save");
        var document = SaveDocument.Open(path);
        string hash = document.BlobSha256;
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        var grid = (DataGrid)window.FindName("InventoryGrid");
        var sort = new System.ComponentModel.SortDescription("Item.Number", System.ComponentModel.ListSortDirection.Descending);
        model.ItemsView.SortDescriptions.Add(sort);
        var selected = grid.Items.Cast<ItemRow>().Where(r => r.Item.Source == ItemSource.Inventory).Take(2).ToArray();
        foreach (var row in selected) grid.SelectedItems.Add(row);
        model.PageIndex = 1;
        Assert.AreEqual(0, grid.SelectedItems.Count);
        Assert.AreEqual(0, model.ItemsView.SortDescriptions.Count);
        grid.SelectedItem = grid.Items[0];
        var vendor = (ItemRow)grid.SelectedItem;
        model.PageIndex = 0;
        Assert.AreEqual(sort, model.ItemsView.SortDescriptions.Single());
        CollectionAssert.AreEquivalent(selected.Select(r => r.Identity).ToArray(), grid.SelectedItems.Cast<ItemRow>().Select(r => r.Identity).ToArray());
        model.QuantityText = "123";
        Pump(() => model.ApplyCommand.ExecuteAsync(null));
        Assert.AreEqual(2, grid.SelectedItems.Count);
        Assert.IsTrue(grid.SelectedItems.Cast<ItemRow>().All(r => r.Item.Quantity == 123 && document.Items.Contains(r.Item)));
        Assert.AreEqual(sort, model.ItemsView.SortDescriptions.Single());
        Pump(() => model.UndoCommand.ExecuteAsync(null));
        Assert.AreEqual(hash, document.BlobSha256);
        CollectionAssert.AreEquivalent(selected.Select(r => r.Identity).ToArray(), grid.SelectedItems.Cast<ItemRow>().Select(r => r.Identity).ToArray());
        model.PageIndex = 1;
        Assert.AreEqual(vendor.Identity, ((ItemRow)grid.SelectedItem).Identity);
        Assert.IsTrue(document.Items.Contains(((ItemRow)grid.SelectedItem).Item));
        model.Search = "unlikely_missing_item_12345";
        Assert.AreEqual(0, grid.SelectedItems.Count);
        Assert.IsFalse(model.SwapCommand.CanExecute(null));
        Pump(() => model.LoadFileAsync(path));
        Assert.AreEqual(0, grid.SelectedItems.Count);
        model.PageIndex = 0;
        Assert.AreEqual(0, grid.SelectedItems.Count);
        Assert.AreEqual(0, model.ItemsView.SortDescriptions.Count);
        window.Close();
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

    [STATestMethod]
    public void WindowLoadsAndCommandsRespectSelectionAndBusyState()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        var document = fixtures is null ? null : SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        Assert.AreEqual(4, window.InputBindings.Count);
        window.Show(); window.UpdateLayout();
        var grid = (DataGrid)window.FindName("InventoryGrid");
        var tabs = (TabControl)window.FindName("ItemTabs");
        CollectionAssert.AreEqual(new[] { "인벤토리", "아이템 교환(재구매)", "장비", "아이템 데이터베이스" }, tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
        var navigation = (ListBox)window.FindName("PrimaryNavigation");
        model.PageIndex = 1;
        navigation.SelectedIndex = 5;
        Assert.IsTrue(model.IsBackups);
        Assert.AreEqual(Visibility.Collapsed, tabs.Visibility);
        navigation.SelectedIndex = 0;
        Assert.AreEqual(1, model.PageIndex, "Returning to Items preserves its sub-tab.");
        window.Width = 1300; window.UpdateLayout();
        // Hosted CI displays are 1024px wide, which caps the window below the auto-collapse width.
        if (window.ActualWidth >= 1300) Assert.AreEqual(212d, ((Border)window.FindName("NavigationRail")).ActualWidth);
        Capture(window, "navigation-expanded");
        ((Button)window.FindName("NavigationToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Width = 1400; window.UpdateLayout();
        Assert.AreEqual(68d, ((Border)window.FindName("NavigationRail")).ActualWidth, "Manual collapse survives resize.");
        model.PageIndex = 0;
        Assert.IsTrue(grid.EnableRowVirtualization);
        Assert.IsTrue(grid.IsReadOnly);
        Assert.IsFalse(model.SwapCommand.CanExecute(null));
        if (document is not null)
        {
            grid.SelectedItem = grid.Items[0];
            Assert.IsTrue(model.ApplyCommand.CanExecute(null));
            Assert.IsTrue(model.DeleteCommand.CanExecute(null));
            Assert.IsFalse(model.EnchantCommand.CanExecute(null));
            Assert.IsFalse(model.SocketsCommand.CanExecute(null));
            model.Search = "unlikely_missing_item_12345";
            Assert.IsTrue(model.ItemsView.IsEmpty);
            model.PageIndex = 1;
            Assert.AreEqual("", model.Search);
            Assert.AreEqual(document.Items.Count(i => i.Source == ItemSource.Vendor), grid.Items.Count);
            grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
            Assert.IsTrue(model.SwapCommand.CanExecute(null), "Original repurchase uses the first selected item.");
            Assert.IsTrue(model.ApplyCommand.CanExecute(null));
            Assert.IsFalse(model.DeleteCommand.CanExecute(null));
            Assert.IsFalse(model.EnchantCommand.CanExecute(null));
            model.Busy = true;
            Assert.IsFalse(model.SwapCommand.CanExecute(null));
            Assert.IsFalse(model.ApplyCommand.CanExecute(null));
            model.Busy = false;
            Capture(window, "repurchase");
            model.PageIndex = 2;
            Assert.AreEqual(document.Items.Count(i => i.EnchantLevel.HasValue || SocketState.Limit(i.Key) > 0), grid.Items.Count);
            grid.SelectedItem = grid.Items.Cast<ItemRow>().First(r => r.Item.Sockets.Unlocked > 0 && SocketState.Limit(r.Item.Key) > 0);
            Assert.IsTrue(model.EnchantCommand.CanExecute(null));
            Assert.IsTrue(model.SocketsCommand.CanExecute(null));
            Assert.IsFalse(model.SwapCommand.CanExecute(null));
            Assert.IsFalse(model.ApplyCommand.CanExecute(null));
            Assert.AreEqual(model.Selected!.Item.Endurance & 0xff, model.Selected.ActualEndurance);
            Assert.AreEqual($"{model.Selected.Item.Sockets.Slots.Count(s => s.Key != 0)} / {model.Selected.Item.Sockets.Unlocked} / {SocketState.Limit(model.Selected.Item.Key)}", model.Selected.SocketText);
            Capture(window, "equipment");
            model.PageIndex = 0;
            Assert.AreEqual("unlikely_missing_item_12345", model.Search);
            model.Search = "";
            Capture(window, "inventory");
        }
        window.Close();
    }

    [STATestMethod]
    public void RepurchaseSearchDefaultsToTemplatesAndClearsSelectionOnFilter()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var window = new ItemSearchWindow() { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        var results = (DataGrid)window.FindName("Results");
        var templates = (CheckBox)window.FindName("TemplatesOnly");
        var search = (TextBox)window.FindName("SearchBox");
        Assert.AreEqual(true, templates.IsChecked);
        Assert.IsTrue(results.Items.Count is > 0 and <= 100);
        Assert.IsTrue(results.Items.Cast<ItemSearchWindow.Choice>().All(c => c.HasTemplate));
        results.SelectedItem = results.Items[0];
        Assert.IsNotNull(window.SelectedKey);
        search.Text = "unlikely_missing_item_12345";
        Assert.AreEqual(0, results.Items.Count);
        Assert.IsNull(window.SelectedKey);
        Assert.IsFalse(((Button)window.FindName("Accept")).IsEnabled);
        templates.IsChecked = false;
        search.Text = "1002785";
        Assert.IsTrue(results.Items.Cast<ItemSearchWindow.Choice>().Any(c => c.Key == 1002785));
        Capture(window, "search");
        window.Close();
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
}
