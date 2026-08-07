using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiMemoryManager.Controls;

public partial class PercentageRing : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(0.0, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(10.0, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.DodgerBlue, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.Gray, (d, _) => ((PercentageRing)d).Redraw()));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public PercentageRing() { InitializeComponent(); SizeChanged += (_, _) => Redraw(); }

    private void Redraw()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness) return;
        double r = (size - StrokeThickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double angle = Math.Clamp(Percent, 0, 100) / 100.0 * 2 * Math.PI - Math.PI / 2; // 从 12 点方向起

        Track.Data = new EllipseGeometry(center, r, r);

        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        bool large = Percent > 50;
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        if (Percent >= 99.9) // 整圆退化为椭圆
            Arc.Data = new EllipseGeometry(center, r, r);
        else if (Percent <= 0.01)
            Arc.Data = null;
        else
            Arc.Data = new PathGeometry(new[] { fig });
    }
}
