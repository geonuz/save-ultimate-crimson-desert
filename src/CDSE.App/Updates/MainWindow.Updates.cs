using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CDSE.App;

// Built in code rather than in MainWindow.xaml so that a build without Updates/ has no trace of them.
public partial class MainWindow
{
    private Button? updateButton;

    partial void AddUpdateControls(MainViewModel model)
    {
        var check = new MenuItem { Command = model.CheckForUpdateCommand, IsEnabled = model.CanUpdate };
        Tr.Bind(check, HeaderedItemsControl.HeaderProperty, "menu.check_update");
        FileMenu.Items.Insert(FileMenu.Items.IndexOf(DoctorMenuItem) + 1, check);
        updateButton = new Button { Command = model.InstallUpdateCommand, Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 0, 0, 8) };
        updateButton.SetBinding(VisibilityProperty, new Binding(nameof(MainViewModel.HasUpdate)) { Converter = (IValueConverter)FindResource("BooleanVisibility") });
        NavigationFooter.Children.Insert(0, updateButton);
        RegisterName("UpdateButton", updateButton); // Found by name, as when it was declared in XAML.
    }

    partial void LabelUpdateButton(bool collapsed)
    {
        if (updateButton is null) return;
        updateButton.Content = collapsed ? new ActionGlyph { Kind = "update", Width = 18, Height = 18, Foreground = Appearance.Brush("brand_text") } : Tr.Text("menu.update");
        updateButton.ToolTip = Tr.Text("menu.update");
        System.Windows.Automation.AutomationProperties.SetName(updateButton, Tr.Text("menu.update"));
    }
}
