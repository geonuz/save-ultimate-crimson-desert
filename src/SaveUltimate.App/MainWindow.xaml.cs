using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SaveUltimate.App;

public partial class MainWindow : Window
{
    private sealed record TableState(ItemRow[] Selection, ItemRow? Current, SortDescription[] Sorts,
        double Horizontal, double Vertical, double EditorOffset, DataGridLength[] Widths);
    private readonly TableState?[] tables = new TableState?[12];
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
            if (child is UserControl or SkillsView || child is Grid && Grid.GetRow(child) == 3 && child != ItemArea)
                ((FrameworkElement)child).Margin = new(20, 16, 20, 16);
        foreach (var (name, page) in new[] { ("tab.inventory", 0), ("tab.repurchase", 1), ("tab.equipment", 2), ("tab.knowledge", 5),
            ("Challenges", 6), ("tab.fishing", 7), ("skills.title", 4), ("map.title", 8), ("companion.title", 9), ("npc.title", 10), ("tab.backup", 3), ("tab.database", 11) })
        { var item = new MenuItem { Header = Tr.Text(name) }; item.Click += (_, _) => { if (!model.Busy) model.PageIndex = page; }; ViewMenu.Items.Add(item); }
        IconsToggle.IsChecked = SettingsStore.Current.ShowIcons;
        Appearance.Changed += ApplyAppearance;
        ApplyAppearance();
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

    private void CaptureTable()
    {
        var model = (MainViewModel)DataContext;
        var scroll = FindScroll(InventoryGrid);
        tables[model.PageIndex] = new(InventoryGrid.SelectedItems.Cast<ItemRow>().ToArray(),
            InventoryGrid.SelectedItem as ItemRow, model.ItemsView.SortDescriptions.ToArray(),
            scroll?.HorizontalOffset ?? 0, scroll?.VerticalOffset ?? 0, EditorScroll.VerticalOffset,
            InventoryGrid.Columns.Select(c => c.Width).ToArray());
    }

    private void ResetTables() => Array.Clear(tables);

    private void RestoreTable()
    {
        var model = (MainViewModel)DataContext;
        var state = tables[model.PageIndex];
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
        bool below = e.NewSize.Width < 1800;
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
    private void OnQuantityStep(object sender, RoutedEventArgs e) => StepQuantity(int.Parse((string)((FrameworkElement)sender).Tag));
    private void OnQuantityKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) { StepQuantity(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PageIndex)) { UpdateColumns((MainViewModel)sender!); UpdateNavigation((MainViewModel)sender!); ((MainViewModel)sender!).RefreshAppearance(); if (HelpPanel.Visibility == Visibility.Visible) ShowHelp(CurrentTopic(), false); }
        if (e.PropertyName == nameof(MainViewModel.Detail)) ArrangeQuantityPanel();
    }

    private void UpdateNavigation(MainViewModel model)
    {
        updatingNavigation = true;
        PrimaryNavigation.SelectedIndex = model.IsItems ? 0 : model.IsSkills ? 2 : model.IsJournal ? 1 : model.IsMap ? 3 : model.IsNpc ? 4 : 5;
        updatingNavigation = false;
        SetNavigationCollapsed(navigationCollapsed);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNavigation || DataContext is not MainViewModel { Busy: false } model ||
            PrimaryNavigation.SelectedItem is not ListBoxItem { Tag: string tag }) return;
        int page = int.Parse(tag);
        model.PageIndex = page == 0 ? model.ItemPageIndex == 3 ? 11 : model.ItemPageIndex : page == 5 ? model.JournalPageIndex + 5 : page == 9 ? model.NpcPageIndex + 9 : page;
    }

    private void OnNavigationKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || DataContext is not MainViewModel { Busy: false }) return;
        if (e.Key == Key.Escape && HelpPanel.Visibility == Visibility.Visible) { HelpPanel.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        int index = e.Key switch { Key.F1 => 0, Key.F2 => 1, Key.F3 => 2, Key.F4 => 3, Key.F5 => 4, Key.F6 => 5, _ => -1 };
        if (index >= 0) { PrimaryNavigation.SelectedIndex = index; e.Handled = true; }
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
        string[] names = new[] { "tab.items", "tab.knowledge_quest", "skills.title", "tab.maps", "tab.mercenary", "tab.backup" }.Select(Tr.Text).ToArray();
        SettingsButton.Content = collapsed ? new ActionGlyph { Kind = "settings", Width = 18, Height = 18, Foreground = Appearance.Brush("text") } : Tr.Text("Settings");
        SettingsButton.ToolTip = Tr.Text("Settings");
        GameVersion.Text = Tr.Format("ui.game_version", ("version", typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "GameVersion").Value!));
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, Tr.Text("Settings"));
        string[] glyphs = ["items", "journal", "skills", "maps", "npcs", "backup"];
        for (int i = 0; i < names.Length; i++)
        {
            var item = (ListBoxItem)PrimaryNavigation.Items[i];
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new ActionGlyph { Kind = glyphs[i], Width = 20, Height = 20, Foreground = Appearance.Brush(item.IsSelected ? "accent" : "text_dim") });
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
        if (language != Tr.Language) { language = Tr.Language; ((MainViewModel)DataContext).RefreshLanguage(); }
        SetNavigationCollapsed(navigationCollapsed);
        InventoryGrid.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        DatabaseArea.Rows.Columns[0].Visibility = CompanionArea.Rows.Columns[0].Visibility = InventoryGrid.Columns[0].Visibility;
        ((MainViewModel)DataContext).RefreshAppearance(true);
    }
    private void OnIconsChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel || IconsToggle.IsChecked == SettingsStore.Current.ShowIcons) return;
        try { SettingsStore.Save(SettingsStore.Current with { ShowIcons = IconsToggle.IsChecked == true }); ApplyAppearance(); }
        catch (System.IO.IOException error) { ThemedMessageBox.Show(error.Message); }
    }
    public void ShowSettings(string page = "general")
    {
        if (DataContext is not MainViewModel { Busy: false } model) return;
        var dialog = new SettingsWindow(model.LoadedPath, page) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try { SettingsStore.Save(dialog.Result); Appearance.Apply(dialog.Result); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ThemedMessageBox.Show(error.Message, Tr.Text("Settings")); }
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
    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void OnThemeSettings(object sender, RoutedEventArgs e) => ShowSettings("theme");
    private void OnExit(object sender, RoutedEventArgs e) => Close();
    private void OnCloseHelp(object sender, RoutedEventArgs e) => HelpPanel.Visibility = Visibility.Collapsed;
    private string CurrentTopic() => ((MainViewModel)DataContext).PageIndex switch
    {
        0 => "tab.inventory", 1 => "tab.repurchase", 2 => "tab.equipment", 3 => "tab.backup", 4 => "skills.title",
        5 => "tab.knowledge", 6 => "Challenges", 7 => "tab.fishing", 8 => "map.title", 9 => "companion.title", 11 => "tab.database", _ => "npc.title"
    };
    public void ShowHelp(string topic, bool toggle = true)
    {
        if (toggle && HelpPanel.Visibility == Visibility.Visible && Equals(HelpPanel.Tag, topic)) { HelpPanel.Visibility = Visibility.Collapsed; return; }
        string name = topic switch { "tab.inventory" => "inventory", "tab.repurchase" => "repurchase", "tab.equipment" => "equipment", "tab.knowledge" => "knowledge",
            "Challenges" => "challenges", "tab.fishing" => "fishing", "map.title" => "maps", "companion.title" => "companions", "npc.title" => "npc", "skills.title" => "skills", "tab.database" => "items_db", _ => "backup" };
        HelpPanel.Tag = topic; HelpPanel.Visibility = Visibility.Visible; HelpTitle.Text = Tr.Text("ui.help") + " · " + Tr.Text(topic);
        HelpScroll.Content = RichTextBlock.Create(Tr.Text("help." + name)); HelpScroll.ScrollToTop();
    }

    private void UpdateColumns(MainViewModel model)
    {
        SourceColumn.Visibility = model.IsInventory ? Visibility.Visible : Visibility.Collapsed;
        OwnerColumn.Header = model.IsRepurchase ? Tr.Text("tab.stores") : Tr.Text("Owner");
        OwnerColumn.Binding = new System.Windows.Data.Binding(model.IsRepurchase ? nameof(ItemRow.VendorName) : nameof(ItemRow.OwnerText));
        EnduranceColumn.Visibility = SharpnessColumn.Visibility = model.IsEquipment ? Visibility.Visible : Visibility.Collapsed;
        ((DataGridTextColumn)InventoryGrid.Columns[9]).Binding = new System.Windows.Data.Binding(model.IsEquipment ? nameof(ItemRow.EnchantText) : nameof(ItemRow.EnchantValue));
        int[] order = model.IsRepurchase ? [0, 5, 1, 2, 3, 6, 7, 8, 9, 4, 10, 11]
            : model.IsEquipment ? [0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 4] : [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
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

