using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CDSE.App;

public partial class MainWindow : Window
{
    private sealed record TableState(ItemRow[] Selection, ItemRow? Current, SortDescription[] Sorts,
        double Horizontal, double Vertical, double EditorOffset, DataGridLength[] Widths);
    private readonly TableState?[] tables = new TableState?[AppPages.All.Length];
    private bool restoringSelection;
    private bool? detailBelow;
    private bool? backupsBelow;
    private int scrollRevision;
    private bool navigationManual, navigationCollapsed, updatingNavigation;
    private string language = Tr.Language;
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        AddUpdateControls(model);
        AppVersion.Text = $"v{typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";
        foreach (var column in InventoryGrid.Columns.OfType<DataGridTextColumn>().Where(c => c != InventoryGrid.Columns[1]))
        {
            var style = new Style(typeof(TextBlock), column.ElementStyle);
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            column.ElementStyle = style;
        }
        model.PropertyChanged += OnModelChanged;
        model.ViewChanging += CaptureTable;
        model.ViewChanged += RestoreTable;
        model.ViewReset += ResetTables;
        UpdateColumns(model);
        UpdateNavigation(model);
        foreach (UIElement child in ContentArea.Children)
            if (child is UserControl || child is Grid && Grid.GetRow(child) == 3 && child != ItemArea)
                ((FrameworkElement)child).Margin = new(20, 16, 20, 16);
        foreach (var (page, name, _) in AppPages.All)
        { var item = new MenuItem(); Tr.Bind(item, HeaderedItemsControl.HeaderProperty, name); item.Click += (_, _) => { if (!model.Busy) model.CurrentPage = page; }; ViewMenu.Items.Add(item); }
        IconsToggle.IsChecked = SettingsStore.Current.ShowIcons;
        Appearance.Changed += ApplyAppearance;
        ApplyAppearance();
        // Open wide enough that two-table pages and the item editor keep their side panels, scaled with the UI but never past the screen.
        double uiScale = ((ScaleTransform)AppShell.LayoutTransform).ScaleX;
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width * uiScale, area.Width * 0.95));
        Height = Math.Max(MinHeight, Math.Min(Height * uiScale, area.Height * 0.95));
        PreviewKeyDown += OnNavigationKey;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelChanged;
            model.ViewChanging -= CaptureTable;
            model.ViewChanged -= RestoreTable;
            model.ViewReset -= ResetTables;
            Appearance.Changed -= ApplyAppearance;
        };
        Closing += OnClosing;
    }

    // Implemented in Updates/MainWindow.Updates.cs; without that file (OnlineUpdate=false) the compiler drops the calls.
    partial void AddUpdateControls(MainViewModel model);
    partial void LabelUpdateButton(bool collapsed);

    private void CaptureTable()
    {
        var model = (MainViewModel)DataContext;
        var scroll = FindScroll(InventoryGrid);
        tables[(int)model.CurrentPage] = new(InventoryGrid.SelectedItems.Cast<ItemRow>().ToArray(),
            InventoryGrid.SelectedItem as ItemRow, model.ItemsView.SortDescriptions.ToArray(),
            scroll?.HorizontalOffset ?? 0, scroll?.VerticalOffset ?? 0, EditorScroll.VerticalOffset,
            InventoryGrid.Columns.Select(c => c.Width).ToArray());
    }

    private void ResetTables() => Array.Clear(tables);

    private void RestoreTable()
    {
        var model = (MainViewModel)DataContext;
        var state = tables[(int)model.CurrentPage];
        restoringSelection = true;
        try
        {
            using (model.ItemsView.DeferRefresh())
            {
                model.ItemsView.SortDescriptions.Clear();
                foreach (var sort in state?.Sorts ?? []) model.ItemsView.SortDescriptions.Add(sort);
            }
            foreach (var column in InventoryGrid.Columns)
                column.SortDirection = state?.Sorts.Where(s => s.PropertyName == column.SortMemberPath).Select(s => (ListSortDirection?)s.Direction).FirstOrDefault();
            InventoryGrid.SelectedItems.Clear();
            var identities = state?.Selection.Select(r => r.Identity).ToHashSet();
            var rows = model.ItemsView.Cast<ItemRow>().Where(r => identities?.Contains(r.Identity) == true).ToArray();
            InventoryGrid.SelectedItem = rows.FirstOrDefault(r => r.Identity == state?.Current?.Identity);
            foreach (var row in rows)
                if (!InventoryGrid.SelectedItems.Contains(row)) InventoryGrid.SelectedItems.Add(row);
        }
        finally { restoringSelection = false; }
        model.SetSelection(InventoryGrid.SelectedItems.Cast<ItemRow>());
        if (state is not null)
            for (int i = 0; i < state.Widths.Length; i++) InventoryGrid.Columns[i].Width = state.Widths[i];
        int revision = ++scrollRevision;
        Dispatcher.InvokeAsync(() =>
        {
            if (revision != scrollRevision) return;
            var scroll = FindScroll(InventoryGrid);
            scroll?.ScrollToHorizontalOffset(state?.Horizontal ?? 0);
            scroll?.ScrollToVerticalOffset(state?.Vertical ?? 0);
            EditorScroll.ScrollToVerticalOffset(state?.EditorOffset ?? 0);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private void OnItemAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The detail panel stays at the side down to a narrow window; the table scrolls sideways before the panel drops below.
        bool below = e.NewSize.Width < 1100;
        if (detailBelow == below) return;
        detailBelow = below;
        ItemArea.ColumnDefinitions[1].Width = new(below ? 0 : 5);
        ItemArea.ColumnDefinitions[2].MinWidth = below ? 0 : 280;
        ItemArea.ColumnDefinitions[2].Width = new(below ? 0 : 340);
        ItemArea.RowDefinitions[0].Height = new(1, GridUnitType.Star);
        ItemArea.RowDefinitions[1].Height = new(below ? 5 : 0);
        ItemArea.RowDefinitions[2].Height = below ? new(1.45, GridUnitType.Star) : new(0);
        Grid.SetColumn(EditorScroll, below ? 0 : 2); Grid.SetRow(EditorScroll, below ? 2 : 0);
        Grid.SetColumn(DetailSplitter, below ? 0 : 1); Grid.SetRow(DetailSplitter, below ? 1 : 0);
        DetailSplitter.Width = below ? double.NaN : 5; DetailSplitter.Height = below ? 5 : double.NaN;
        DetailSplitter.ResizeDirection = below ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        DetailActions.ColumnDefinitions[1].Width = new(below ? 16 : 0);
        DetailActions.ColumnDefinitions[2].Width = below ? new(1, GridUnitType.Star) : new(0);
        foreach (var panel in new FrameworkElement[] { QuantityActions, SocketActions })
        {
            Grid.SetColumn(panel, below ? 2 : 0); Grid.SetRow(panel, below ? 0 : 2);
        }
        ArrangeQuantityPanel();
    }

    private void ArrangeQuantityPanel()
    {
        bool span = detailBelow == true && DataContext is MainViewModel { Detail: null };
        Grid.SetColumn(QuantityActions, detailBelow == true && !span ? 2 : 0);
        Grid.SetColumnSpan(QuantityActions, span ? 3 : 1);
    }

    private void OnBackupAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool below = e.NewSize.Width < 900;
        if (backupsBelow == below) return;
        backupsBelow = below;
        BackupArea.ColumnDefinitions[1].Width = new(below ? 0 : 5);
        BackupArea.ColumnDefinitions[2].Width = below ? new(0) : GridLength.Auto;
        BackupArea.RowDefinitions[1].Height = new(below ? 5 : 0);
        BackupArea.RowDefinitions[2].Height = below ? new(0.6, GridUnitType.Star) : new(0);
        Grid.SetColumn(BackupActions, below ? 0 : 2); Grid.SetRow(BackupActions, below ? 2 : 0);
        Grid.SetColumn(BackupSplitter, below ? 0 : 1); Grid.SetRow(BackupSplitter, below ? 1 : 0);
        BackupSplitter.Width = below ? double.NaN : 5; BackupSplitter.Height = below ? 5 : double.NaN;
        BackupSplitter.ResizeDirection = below ? GridResizeDirection.Rows : GridResizeDirection.Columns;
    }

    private void StepQuantity(int step)
    {
        var model = (MainViewModel)DataContext;
        if (long.TryParse(model.QuantityText, out long value)) model.QuantityText = Math.Clamp(Math.Clamp(value, 1, 999_999_999) + step, 1, 999_999_999).ToString();
    }
    // The pick is a draft like the unlocked count; Apply sockets writes every socket as one edit.
    private void OnSocketPick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SocketRow { IsEditable: true } row) return;
        var dialog = new ItemSearchWindow(row.Choices(), row.Label, Tr.Text("socket.pick_hint"), Tr.Text("Gear category"), "ItemKey") { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedKey is uint key) row.SelectedKey = key;
    }
    private void OnSocketClear(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is SocketRow { CanClear: true } row) row.SelectedKey = 0; }
    private void OnQuantityStep(object sender, RoutedEventArgs e) => StepQuantity(int.Parse((string)((FrameworkElement)sender).Tag));
    private void OnQuantityKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { StepQuantity(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) { UpdateColumns((MainViewModel)sender!); UpdateNavigation((MainViewModel)sender!); ((MainViewModel)sender!).RefreshAppearance(); if (HelpPanel.Visibility == Visibility.Visible) ShowHelp(CurrentTopic(), false); }
        if (e.PropertyName == nameof(MainViewModel.Detail)) ArrangeQuantityPanel();
        if (e.PropertyName == nameof(MainViewModel.HistoryRows)) FillHistory();
    }

    private void UpdateNavigation(MainViewModel model)
    {
        updatingNavigation = true;
        PrimaryNavigation.SelectedIndex = model.IsItems ? 0 : model.IsCharacter ? 1 : model.IsJournal ? 2 : model.IsMap ? 3 : model.IsNpc ? 4 : model.IsPets ? 5 : model.IsLife ? 6 : model.IsMisc ? 7 : 8;
        updatingNavigation = false;
        SetNavigationCollapsed(navigationCollapsed);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNavigation || DataContext is not MainViewModel { Busy: false } model ||
            PrimaryNavigation.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        model.CurrentPage = model.SectionPage(Enum.Parse<AppPage>(tag));
    }

    private void OnNavigationKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || DataContext is not MainViewModel { Busy: false }) return;
        if (e.Key == Key.Escape && HelpPanel.Visibility == Visibility.Visible) { HelpPanel.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (e.Key == Key.Escape && HistoryPanel.Visibility == Visibility.Visible) { ShowHistory(false); e.Handled = true; return; }
        int index = e.Key - Key.F1;
        if (index >= 0 && index < PrimaryNavigation.Items.Count) { PrimaryNavigation.SelectedIndex = index; e.Handled = true; }
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!navigationManual) SetNavigationCollapsed(e.NewSize.Width < 1180);
        HeaderActions.MaxWidth = Math.Max(300, e.NewSize.Width - NavigationRail.Width - 330);
    }

    private void OnNavigationToggle(object sender, RoutedEventArgs e)
    {
        navigationManual = true;
        SetNavigationCollapsed(!navigationCollapsed);
    }

    private void SetNavigationCollapsed(bool collapsed)
    {
        navigationCollapsed = collapsed;
        NavigationBrand.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavigationSubtitle.Visibility = VersionText.Visibility = NavigationBrand.Visibility;
        NavigationToggle.Content = new ActionGlyph { Kind = collapsed ? "next" : "previous", Width = 16, Height = 16, Foreground = Appearance.Brush("text_dim") };
        NavigationToggle.ToolTip = collapsed ? Tr.Text("Expand navigation") : Tr.Text("Collapse navigation");
        System.Windows.Automation.AutomationProperties.SetName(NavigationToggle, (string)NavigationToggle.ToolTip);
        string[] names = new[] { "tab.items", "tab.character", "tab.knowledge_quest", "tab.maps", "tab.mercenary", "companion.pets", "tab.life", "tab.misc", "tab.backup" }.Select(Tr.Text).ToArray();
        SettingsButton.Content = collapsed ? new ActionGlyph { Kind = "settings", Width = 18, Height = 18, Foreground = Appearance.Brush("text") } : Tr.Text("Settings");
        SettingsButton.ToolTip = Tr.Text("Settings");
        LabelUpdateButton(collapsed);
        GameVersion.Text = Tr.Format("ui.game_version", ("version", Ui.GameVersion));
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, Tr.Text("Settings"));
        string[] glyphs = ["items", "character", "journal", "maps", "npcs", "pets", "life", "misc", "backup"];
        for (int i = 0; i < names.Length; i++)
        {
            var item = (ListBoxItem)PrimaryNavigation.Items[i];
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new ActionGlyph { Kind = glyphs[i], Width = 20, Height = 20, Foreground = Appearance.Brush(item.IsSelected ? "selection_text" : "text_dim") });
            if (!collapsed) panel.Children.Add(new TextBlock { Text = names[i], Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Content = panel; item.ToolTip = names[i] + $" · F{i + 1}";
            System.Windows.Automation.AutomationProperties.SetName(item, names[i]);
            ToolTipService.SetShowOnDisabled(item, true);
        }
        // Like the original, widen past 212 for the longest translated label instead of clipping it.
        // Measure every label at the selected (SemiBold) weight so switching pages does not shift the width.
        var labels = PrimaryNavigation.Items.Cast<ListBoxItem>().Select(i => ((Panel)i.Content).Children.OfType<TextBlock>().FirstOrDefault()).OfType<TextBlock>().ToArray();
        foreach (var label in labels) label.FontWeight = FontWeights.SemiBold;
        NavigationRail.Width = double.NaN;
        NavigationRail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        NavigationRail.Width = collapsed ? 68 : Math.Max(212, Math.Ceiling(NavigationRail.DesiredSize.Width));
        foreach (var label in labels) label.ClearValue(TextBlock.FontWeightProperty);
        HeaderActions.MaxWidth = Math.Max(300, Shell.ActualWidth - NavigationRail.Width - 330);
    }

    private void ApplyAppearance()
    {
        double scale = ((ScaleTransform)AppShell.LayoutTransform).ScaleX;
        MinWidth = 920 * scale; MinHeight = 640 * scale;
        Chrome.CaptionHeight = TitleBar.Height * scale - Chrome.ResizeBorderThickness.Top;
        UpdateWindowState();
        if (language != Tr.Language) { language = Tr.Language; ((MainViewModel)DataContext).RefreshLanguage(); }
        SetNavigationCollapsed(navigationCollapsed);
        InventoryGrid.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        DatabaseArea.Rows.Columns[0].Visibility = CompanionArea.Rows.Columns[0].Visibility = PetArea.Rows.Columns[0].Visibility = DyeArea.PictureColumn.Visibility = FishingArea.PictureColumn.Visibility = InventoryGrid.Columns[0].Visibility;
        ((MainViewModel)DataContext).RefreshAppearance(true);
    }
    private void OnIconsChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel || IconsToggle.IsChecked == SettingsStore.Current.ShowIcons) return;
        try { SettingsStore.Save(SettingsStore.Current with { ShowIcons = IconsToggle.IsChecked == true }); ApplyAppearance(); }
        catch (System.IO.IOException error) { ThemedMessageBox.Show(ErrorCodes.Describe(error)); }
    }
    public void ShowSettings(string page = "general")
    {
        if (DataContext is not MainViewModel { Busy: false } model) return;
        var dialog = new SettingsWindow(model.LoadedPath, page) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try { SettingsStore.Save(dialog.Result); Appearance.Apply(dialog.Result); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ThemedMessageBox.Show(ErrorCodes.Describe(error), Tr.Text("Settings")); }
    }
    // Game images and names are not bundled; on first run point the user at the sync instead of leaving icons blank.
    public void RecommendSync()
    {
        var model = (MainViewModel)DataContext;
        // Writing the settings file marks the first run as done, so the prompt is shown once.
        try { SettingsStore.Save(SettingsStore.Current); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
        bool ready = model.CanSyncGameData();
        var labels = new Dictionary<MessageBoxResult, string> { [MessageBoxResult.OK] = Tr.Text(ready ? "Sync Items Local" : "Settings"), [MessageBoxResult.Cancel] = Tr.Text("ui.later") };
        if (ThemedMessageBox.Show(this, Tr.Text(ready ? "sync.first_run" : "sync.first_run_no_game"), Tr.Text("Sync Items Local"), MessageBoxButton.OKCancel,
            MessageBoxImage.Information, MessageBoxResult.OK, labels) != MessageBoxResult.OK) return;
        if (ready) model.SyncGameDataCommand.Execute(null); else ShowSettings();
    }
    private void OnDoctor(object sender, RoutedEventArgs e)
    {
        // Save Doctor is a page of the Misc section; the menu entry opens it.
        if (DataContext is MainViewModel { Busy: false } model) model.CurrentPage = AppPage.Doctor;
    }
    private void OnAddItems(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { Busy: false } model) return;
        model.CurrentPage = AppPage.Database;
        DatabaseArea.ShowAddable();
    }
    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void OnThemeSettings(object sender, RoutedEventArgs e) => ShowSettings("theme");
    private void OnExit(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    protected override void OnStateChanged(EventArgs e) { base.OnStateChanged(e); UpdateWindowState(); }
    private void UpdateWindowState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // Windows pushes a maximized window's frame past the screen edges.
        var frame = SystemParameters.WindowResizeBorderThickness;
        BorderThickness = maximized ? new(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new(0);
        MaximizeGlyph.Kind = maximized ? "window_restore" : "window_maximize";
        string caption = maximized ? "window.restore" : "window.maximize";
        Tr.Bind(MaximizeButton, ToolTipProperty, caption);
        Tr.Bind(MaximizeButton, System.Windows.Automation.AutomationProperties.NameProperty, caption);
    }
    private void OnCloseHelp(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
    // Help and history share the right side; opening one closes the other.
    private void OnHistory(object sender, RoutedEventArgs e) => ShowHistory(HistoryPanel.Visibility != Visibility.Visible);
    private void OnCloseHistory(object sender, RoutedEventArgs e) => ShowHistory(false);
    public void ShowHistory(bool show)
    {
        HistoryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { HelpPanel.Visibility = Visibility.Collapsed; FillHistory(); }
    }
    private void FillHistory()
    {
        // Wording every edit again is only worth it while the list is on screen.
        if (HistoryPanel.Visibility != Visibility.Visible) return;
        var rows = ((MainViewModel)DataContext).HistoryRows;
        HistoryList.ItemsSource = rows;
        HistoryList.SelectedItem = rows.FirstOrDefault(r => r.Current);
        if (HistoryList.SelectedItem is { } current) HistoryList.ScrollIntoView(current);
    }
    private async void OnHistoryClick(object sender, MouseButtonEventArgs e) => await GoToHistory((sender as FrameworkElement)?.DataContext as HistoryRow);
    private async void OnHistoryKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        await GoToHistory(HistoryList.SelectedItem as HistoryRow);
    }
    private Task GoToHistory(HistoryRow? row) => row is null ? Task.CompletedTask : ((MainViewModel)DataContext).GoToHistoryAsync(row.Position);
    private string CurrentTopic() => AppPages.All.First(p => p.Page == ((MainViewModel)DataContext).CurrentPage).Title;
    public void ShowHelp(string topic, bool toggle = true)
    {
        if (toggle && HelpPanel.Visibility == Visibility.Visible && Equals(HelpPanel.Tag, topic)) { HelpPanel.Visibility = Visibility.Collapsed; return; }
        string name = AppPages.All.FirstOrDefault(p => p.Title == topic).Help ?? "backup";
        HistoryPanel.Visibility = Visibility.Collapsed;
        HelpPanel.Tag = topic; HelpPanel.Visibility = Visibility.Visible; HelpTitle.Text = Tr.Text("ui.help") + " · " + Tr.Text(topic);
        HelpScroll.Content = RichTextBlock.Create(Tr.Text("help." + name)); HelpScroll.ScrollToTop();
    }

    private void UpdateColumns(MainViewModel model)
    {
        SourceColumn.Visibility = model.IsInventory ? Visibility.Visible : Visibility.Collapsed;
        OwnerColumn.Header = model.IsRepurchase ? Tr.Text("tab.stores") : Tr.Text("Owner");
        OwnerColumn.Binding = new System.Windows.Data.Binding(model.IsRepurchase ? nameof(ItemRow.VendorName) : nameof(ItemRow.OwnerText));
        ((DataGridTextColumn)InventoryGrid.Columns[8]).Binding = new System.Windows.Data.Binding(model.IsEquipment ? nameof(ItemRow.EnchantText) : nameof(ItemRow.EnchantValue));
        int[] order = model.IsRepurchase ? [0, 5, 1, 2, 3, 6, 7, 8, 4]
            : model.IsEquipment ? [0, 1, 2, 3, 5, 6, 7, 8, 4] : [0, 1, 2, 3, 4, 5, 6, 7, 8];
        for (int i = 0; i < order.Length; i++) InventoryGrid.Columns[order[i] + 1].DisplayIndex = i + 1;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!restoringSelection && DataContext is MainViewModel model)
            model.SetSelection(InventoryGrid.SelectedItems.Cast<ItemRow>());
    }

    private bool closeConfirmed, confirmingClose;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        var model = (MainViewModel)DataContext;
        if (closeConfirmed) return;
        e.Cancel = model.Busy || model.HasUnsavedChanges;
        if (!e.Cancel || model.Busy || confirmingClose) return;
        confirmingClose = true;
        try { if (await model.ConfirmLeaveAsync()) { closeConfirmed = true; await Dispatcher.InvokeAsync(Close); } }
        finally { confirmingClose = false; }
    }

    private void OnItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel { IsRepurchase: true } model &&
            ItemsControl.ContainerFromElement(InventoryGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
        {
            InventoryGrid.SelectedItems.Clear();
            InventoryGrid.SelectedItem = row.Item;
            if (model.SwapCommand.CanExecute(null)) model.SwapCommand.Execute(null);
        }
    }
}

