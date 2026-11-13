using System.Windows;
using System.Windows.Media;

namespace Musebase.Windows;

internal enum ClassicTransportIcon { Shuffle, Previous, Play, Pause, Next, Repeat, RepeatOne, Sync, Translate, Settings, Minimize, Fullscreen, ExitFullscreen, Close }

internal sealed class ClassicTransportGlyph : FrameworkElement
{
    private static readonly Geometry Play = Shape("M6 3 L19 12 6 21 Z");
    private static readonly Geometry Pause = Shape("M6 4 H10 V20 H6 Z M14 4 H18 V20 H14 Z");
    private static readonly Geometry Previous = Shape("M5 5 H7 V19 H5 Z M19 5 L8 12 19 19 Z");
    private static readonly Geometry Next = Shape("M19 5 H17 V19 H19 Z M5 5 L16 12 5 19 Z");
    private static readonly Geometry Shuffle = Shape("M3 6 H6 C9 6 10 9 12 12 C14 15 15 18 18 18 H21 M18 15 L21 18 18 21 M3 18 H6 C9 18 10 15 12 12 C14 9 15 6 18 6 H21 M18 3 L21 6 18 9");
    private static readonly Geometry Repeat = Shape("M18.5 5.5 H7 A4 4 0 0 0 3 9.5 V12 M16 2.5 L19 5.5 16 8.5 M5.5 18.5 H17 A4 4 0 0 0 21 14.5 V12 M8 15.5 L5 18.5 8 21.5");
    private static readonly Geometry One = Shape("M10.4 11 L12.3 9.8 V14.8");
    private static readonly Brush BadgeInk = new SolidColorBrush(Color.FromRgb(18, 18, 18));
    private static readonly Geometry Sync = Shape("M2 9 V15 M7 5 V19 M12 1.5 V22.5 M17 5 V19 M22 9 V15");
    private static readonly Geometry Translate = Shape("M9.2 4.5 V6.8 M4.8 6.8 H13.6 M11.8 6.8 C11 10.1 8.8 12.4 5 14 M6.8 8.2 C8.3 10.7 10.4 12.6 13.4 14 M12.4 19.2 L15.7 10.8 L19 19.2 M13.4 16.6 H18");
    private static readonly Geometry Settings = Shape("M5 8 H11 M15 8 H19 M13 6 A2 2 0 1 1 12.99 6 M5 16 H9 M13 16 H19 M11 14 A2 2 0 1 1 10.99 14");
    private static readonly Geometry Minimize = Shape("M6 12 H18");
    private static readonly Geometry Fullscreen = Shape("M14 5 H19 V10 M19 5 L13.5 10.5 M10 19 H5 V14 M5 19 L10.5 13.5");
    private static readonly Geometry ExitFullscreen = Shape("M5 14 H10 V19 M10 14 L4.5 19.5 M19 10 H14 V5 M14 10 L19.5 4.5");
    private static readonly Geometry Close = Shape("M6.5 6.5 L17.5 17.5 M17.5 6.5 L6.5 17.5");

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(ClassicTransportGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private ClassicTransportIcon _icon;
    private bool _active;

    public ClassicTransportGlyph(ClassicTransportIcon icon)
    {
        _icon = icon;
        Width = 23;
        Height = 32;
        IsHitTestVisible = false;
    }

    public Brush Tint
    {
        get => (Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public void SetState(ClassicTransportIcon icon, bool active = false)
    {
        if (_icon == icon && _active == active) return;
        _icon = icon;
        _active = active;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var ink = _active ? ClassicPlaybackSlider.Accent : Tint;
        if (_icon == ClassicTransportIcon.Sync)
        {
            drawing.PushTransform(new TranslateTransform((ActualWidth - 24) / 2, (ActualHeight - 24) / 2));
            drawing.DrawGeometry(null, Stroke(ink), Sync);
            drawing.Pop();
            return;
        }
        if (_icon == ClassicTransportIcon.Translate)
        {
            drawing.PushTransform(new TranslateTransform(2.3, 6.2));
            drawing.PushTransform(new ScaleTransform(0.77, 0.77));
            if (_active)
            {
                drawing.DrawRoundedRectangle(ClassicPlaybackSlider.Accent, null, new Rect(2.5, 2.5, 19, 19), 3.2, 3.2);
                drawing.DrawGeometry(null, Stroke(BadgeInk, 1.75), Translate);
            }
            else
            {
                drawing.DrawGeometry(null, Stroke(Tint, 1.75), Translate);
            }
            drawing.Pop();
            drawing.Pop();
            return;
        }
        drawing.PushTransform(new TranslateTransform(2.3, 6.2));
        drawing.PushTransform(new ScaleTransform(0.77, 0.77));
        switch (_icon)
        {
            case ClassicTransportIcon.Play: drawing.DrawGeometry(ink, null, Play); break;
            case ClassicTransportIcon.Pause: drawing.DrawGeometry(ink, null, Pause); break;
            case ClassicTransportIcon.Previous: drawing.DrawGeometry(ink, null, Previous); break;
            case ClassicTransportIcon.Next: drawing.DrawGeometry(ink, null, Next); break;
            case ClassicTransportIcon.Shuffle: drawing.DrawGeometry(null, Stroke(ink), Shuffle); break;
            case ClassicTransportIcon.Settings: drawing.DrawGeometry(null, Stroke(ink), Settings); break;
            case ClassicTransportIcon.Minimize: drawing.DrawGeometry(null, Stroke(ink), Minimize); break;
            case ClassicTransportIcon.Fullscreen: drawing.DrawGeometry(null, Stroke(ink), Fullscreen); break;
            case ClassicTransportIcon.ExitFullscreen: drawing.DrawGeometry(null, Stroke(ink), ExitFullscreen); break;
            case ClassicTransportIcon.Close: drawing.DrawGeometry(null, Stroke(ink), Close); break;
            default:
                drawing.DrawGeometry(null, Stroke(ink), Repeat);
                if (_icon == ClassicTransportIcon.RepeatOne) drawing.DrawGeometry(null, Stroke(ink), One);
                break;
        }
        drawing.Pop();
        drawing.Pop();
        if (_active) drawing.DrawEllipse(ink, null, new Point(11.5, 29), 2, 2);
    }

    private static Geometry Shape(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static Pen Stroke(Brush brush, double thickness = 2) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}
