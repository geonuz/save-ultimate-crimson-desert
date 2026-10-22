using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SaveUltimate.App;

public sealed class ActionGlyph : FrameworkElement
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached("Show", typeof(bool), typeof(ActionGlyph), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetShow(DependencyObject target, bool value) => target.SetValue(ShowProperty, value);
    public static bool GetShow(DependencyObject target) => (bool)target.GetValue(ShowProperty);
    private static readonly Dictionary<string, Geometry> paths = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/data/ui_icons.json")))!
        .ToDictionary(p => p.Key, p => { var geometry = Geometry.Parse(p.Value); geometry.Freeze(); return geometry; });
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(ActionGlyph), new FrameworkPropertyMetadata("apply", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(ActionGlyph), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        if (!paths.TryGetValue(Kind ?? "", out var geometry)) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, new Pen(Foreground, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        dc.Pop();
    }
    public static IValueConverter Converter { get; } = new LabelConverter();
    private sealed class LabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = Tr.EnglishCaption(value as string ?? "").ToLowerInvariant();
            if (text.Length <= 1) return "";
            foreach (var (kind, words) in new (string, string[])[] {
                ("delete", ["delete","remove","삭제","제거"]), ("save", ["save","저장","백업"]),
                ("open", ["open","browse","folder","열기","찾아보기","폴더","슬롯 선택"]),
                ("undo", ["undo","cancel","취소","되돌리기"]), ("swap", ["swap","replace","교체","교환"]),
                ("refresh", ["load","refresh","sync","불러오기","새로고침","동기화"]), ("help", ["help","도움말"]),
                ("settings", ["settings","설정"]), ("next", ["next","다음"]), ("previous", ["previous","이전"]) })
                if (words.Any(text.Contains)) return kind;
            return "apply";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
