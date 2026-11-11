using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class FieldTimePage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    public FieldTimePage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Clock)) Refresh(); }
    // Days count from the clock's zero, which is the elapsed world time, not a calendar.
    public static string Describe(FieldClock clock) => Tr.Format("fieldtime.format", ("days", clock.Days.ToString("N0")), ("time", clock.TimeOfDay.ToString(@"hh\:mm")));
    private void Refresh()
    {
        var clock = Model.Clock;
        Current.Text = clock is null ? Tr.Text("ui.load_prompt") : Describe(clock);
        CurrentRaw.Text = clock is null ? "" : $"_fieldTimeRaw {clock.Raw:N0}";
        Editor.IsEnabled = clock is not null;
        UpdatePreview();
    }
    private bool Amount(out ulong days, out ulong hours, out ulong minutes)
    {
        static bool Read(TextBox box, ulong max, out ulong value) => ulong.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
        hours = minutes = 0;
        return Read(DaysInput, 36500, out days) & Read(HoursInput, 1000, out hours) & Read(MinutesInput, 100000, out minutes) && days + hours + minutes > 0;
    }
    private FieldClock? Target() => Model.Clock is { } clock && Amount(out ulong d, out ulong h, out ulong m) && d * 1440 + h * 60 + m <= 36500UL * 1440 ? clock.After(d, h, m) : null;
    private void UpdatePreview()
    {
        if (Preview is null || Apply is null) return;
        var target = Target();
        Preview.Text = target is null ? (Model.Clock is null ? "" : Tr.Text("fieldtime.invalid")) : Tr.Format("fieldtime.preview", ("time", Describe(target)));
        Apply.IsEnabled = target is not null;
    }
    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (Target() is { } target) await Model.AdvanceClockAsync(target);
    }
}
