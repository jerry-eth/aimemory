using System.Windows;
using System.Windows.Media.Animation;

namespace AiMemoryManager.Services;

/// <summary>
/// 页面级淡入动效。只在用户开启“界面动效”时执行；页面卸载时恢复透明度，避免导航复用时闪烁。
/// </summary>
public static class AnimationBehavior
{
    public static readonly DependencyProperty EnablePageFadeProperty =
        DependencyProperty.RegisterAttached(
            "EnablePageFade",
            typeof(bool),
            typeof(AnimationBehavior),
            new PropertyMetadata(false, OnEnablePageFadeChanged));

    public static void SetEnablePageFade(DependencyObject element, bool value) =>
        element.SetValue(EnablePageFadeProperty, value);

    public static bool GetEnablePageFade(DependencyObject element) =>
        (bool)element.GetValue(EnablePageFadeProperty);

    private static void OnEnablePageFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
        }
        else
        {
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!IsAnimationsEnabled())
        {
            element.Opacity = 1;
            return;
        }

        element.Opacity = 0;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) => element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
    }

    private static bool IsAnimationsEnabled() =>
        Locator.Settings is null || Locator.Settings.Current.AnimationsEnabled;
}
