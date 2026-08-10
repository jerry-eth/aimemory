using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiMemoryManager.Controls;

public partial class PercentageRing : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(0.0, (d, e) => ((PercentageRing)d).OnPercentChanged((double)e.OldValue, (double)e.NewValue)));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(PercentageRing),
            new PropertyMetadata(10.0, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.DodgerBlue, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(PercentageRing),
            new PropertyMetadata(Brushes.Gray, (d, _) => ((PercentageRing)d).Redraw()));
    public static readonly DependencyProperty AnimationsEnabledProperty =
        DependencyProperty.Register(nameof(AnimationsEnabled), typeof(bool), typeof(PercentageRing),
            new PropertyMetadata(true, (d, e) => ((PercentageRing)d).OnAnimationsChanged((bool)e.NewValue)));

    private readonly DispatcherTimer _animationTimer;
    private double _renderedPercent;
    private double _animationStart;
    private double _animationTarget;
    private DateTime _animationStartedAt;
    private bool _hasRenderedValue;

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }

    public PercentageRing()
    {
        InitializeComponent();
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationTimer.Tick += OnAnimationTick;
        SizeChanged += (_, _) => Redraw();
        Unloaded += (_, _) => _animationTimer.Stop();
    }

    private void OnPercentChanged(double oldValue, double newValue)
    {
        newValue = Math.Clamp(newValue, 0, 100);
        if (!_hasRenderedValue || !AnimationsEnabled)
        {
            _animationTimer.Stop();
            _renderedPercent = newValue;
            _hasRenderedValue = true;
            Redraw();
            return;
        }

        _animationStart = _renderedPercent;
        _animationTarget = newValue;
        _animationStartedAt = DateTime.UtcNow;
        _animationTimer.Start();
    }

    private void OnAnimationsChanged(bool enabled)
    {
        if (enabled) return;
        _animationTimer.Stop();
        _renderedPercent = Math.Clamp(Percent, 0, 100);
        _hasRenderedValue = true;
        Redraw();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        const double durationMs = 240;
        var elapsed = (DateTime.UtcNow - _animationStartedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / durationMs, 0, 1);
        // SmoothStep: no abrupt speed change at either end.
        var eased = progress * progress * (3 - 2 * progress);
        _renderedPercent = _animationStart + (_animationTarget - _animationStart) * eased;
        Redraw();
        if (progress >= 1)
        {
            _renderedPercent = _animationTarget;
            _animationTimer.Stop();
        }
    }

    private void Redraw()
    {
        // 画刷/粗细不走 XAML 绑定(见 XAML 注释),每次重绘时同步一次
        Track.Stroke = TrackBrush;
        Track.StrokeThickness = StrokeThickness;
        Arc.Stroke = RingBrush;
        Arc.StrokeThickness = StrokeThickness;

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness) return;
        double percent = _hasRenderedValue ? _renderedPercent : Math.Clamp(Percent, 0, 100);
        double r = (size - StrokeThickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double angle = percent / 100.0 * 2 * Math.PI - Math.PI / 2; // 从 12 点方向起

        Track.Data = new EllipseGeometry(center, r, r);

        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        bool large = percent > 50;
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        if (percent >= 99.9)
            Arc.Data = new EllipseGeometry(center, r, r);
        else if (percent <= 0.01)
            Arc.Data = null;
        else
            Arc.Data = new PathGeometry(new[] { fig });
    }
}
