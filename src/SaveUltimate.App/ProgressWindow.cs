using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SaveUltimate.App;

internal sealed class ProgressWindow : Window
{
    private bool finished;
    public ProgressWindow(Window owner, MainViewModel model)
    {
        Ui.Setup(this, "In Progress", 480, 150); Owner = owner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        var root = new StackPanel { Margin = new(20) };
        var stage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
        stage.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.Status)) { Source = model });
        root.Children.Add(stage); root.Children.Add(new ProgressBar { IsIndeterminate = true }); Content = root;
        Closing += (_, e) => e.Cancel = !finished;
    }
    public void Finish()
    {
        finished = true;
        // Hand activation back first; otherwise Windows may bring another application forward when this window closes.
        if (IsActive) Owner?.Activate();
        Close();
    }
}
