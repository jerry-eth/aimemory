using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiMemoryManager.Services;

/// <summary>
/// 托盘动态图标渲染:32x32 圆环 + 百分比数字,绿/橙/红分级。
/// Icon.FromHandle(GetHicon) 不拥有句柄,替换图标时必须调用 <see cref="Destroy"/> 释放旧句柄防泄漏。
/// </summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(double percent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var color = percent >= 85 ? Color.OrangeRed : percent >= 60 ? Color.Orange : Color.ForestGreen;
            using var trackPen = new Pen(Color.Gray, 4);
            using var arcPen = new Pen(color, 4);
            g.DrawEllipse(trackPen, 3, 3, 26, 26);
            if (percent > 0)
                g.DrawArc(arcPen, 3, 3, 26, 26, -90, (float)(Math.Min(percent, 100) / 100 * 360));
            using var font = new Font("Segoe UI", percent >= 100 ? 9f : 10f, FontStyle.Bold);
            var text = ((int)Math.Round(percent)).ToString();
            var size = g.MeasureString(text, font);
            using var brush = new SolidBrush(Color.White);
            // 深色/浅色托盘兼容:白字 + 黑色错位阴影描边
            g.DrawString(text, font, Brushes.Black, 16 - size.Width / 2 + 1, 16 - size.Height / 2 + 1);
            g.DrawString(text, font, brush, 16 - size.Width / 2, 16 - size.Height / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>销毁 Render 返回的图标(释放 GDI 句柄并 Dispose)。</summary>
    public static void Destroy(Icon? icon)
    {
        if (icon is null) return;
        DestroyIcon(icon.Handle);
        icon.Dispose();
    }
}
