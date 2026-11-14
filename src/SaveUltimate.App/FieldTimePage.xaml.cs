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
        Ui.Follow(this, nameof(MainViewModel.Clock), Refresh);
    }
    // Days count from the clock's zero, which is the elapsed world time, not a calendar.
    public static string Describe(FieldClock clock) => Tr.Format("fieldtime.format", ("days", clock.Days.ToString("N0")), ("time", clock.TimeOfDay.ToString(@"hh\:mm")));
    private FieldClock? shown;
    private void Refresh()
    {
        var clock = Model.Clock;
        Current.Text = clock is null ? Tr.Text("ui.load_prompt") : Describe(clock);
        Editor.IsEnabled = clock is not null;
        // The inputs start at the current time; the player moves them to the day and time wanted. A language switch keeps them.
        if (clock is not null && clock != shown)
        {
            shown = clock;
            DayInput.Text = clock.Days.ToString(CultureInfo.InvariantCulture);
            HourInput.Text = clock.TimeOfDay.Hours.ToString(CultureInfo.InvariantCulture);
            MinuteInput.Text = clock.TimeOfDay.Minutes.ToString(CultureInfo.InvariantCulture);
        }
        UpdatePreview();
    }
    // A later day and time within 100 years; the clock never goes back.
    private FieldClock? Target()
    {
        static bool Read(TextBox box, ulong max, out ulong value) => ulong.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
        if (Model.Clock is not { } clock || !Read(DayInput, clock.Days + 36500, out ulong day) | !Read(HourInput, 23, out ulong hour) | !Read(MinuteInput, 59, out ulong minute)) return null;
        var target = clock.At(day, (uint)hour, (uint)minute);
        return target.Raw > clock.Raw ? target : null;
    }
    private void UpdatePreview()
    {
        if (Preview is null || Apply is null || MinuteInput is null) return;
        var target = Target();
        if (target is not null)
        {
            ulong minutes = (target.Seconds - Model.Clock!.Seconds) / 60;
            Preview.Text = Tr.Format("fieldtime.preview", ("days", (minutes / 1440).ToString("N0")), ("hours", minutes / 60 % 24), ("minutes", minutes % 60));
        }
        else Preview.Text = Model.Clock is null ? "" : Tr.Text("fieldtime.invalid");
        Apply.IsEnabled = target is not null;
    }
    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (Target() is { } target) await Model.AdvanceClockAsync(target);
    }
}
