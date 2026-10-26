using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisioView.Core.Model;

namespace VisioView;

public enum ExportFormat { Png, Jpeg }

/// <summary>导出范围：整页画布，或仅有效元素占据的区域。</summary>
public enum ExportRange { FullPage, Content }

/// <summary>把 PageScene 导出为位图（PNG / JPEG）。</summary>
public static class ImageExporter
{
    /// <summary>「仅内容区域」时四周额外留白（英寸）。</summary>
    private const double ContentPadding = 0.05;

    /// <summary>像素总数上限，防止误选大倍数把内存打爆。</summary>
    public const long MaxPixels = 80_000_000;

    /// <summary>
    /// 按页面尺寸构建并完成布局的画布。
    /// 必须先布局：文字块的位置是靠实测 DesiredSize 反推的，不布局包围盒会不准。
    /// </summary>
    public static Canvas BuildArranged(PageScene page)
    {
        var canvas = WpfRenderer.Build(page);
        canvas.Measure(new Size(page.Width, page.Height));
        canvas.Arrange(new Rect(0, 0, page.Width, page.Height));
        canvas.UpdateLayout();
        return canvas;
    }

    /// <summary>
    /// 取有效元素真正占据的区域（页面坐标，英寸，原点可能为负）。
    /// 用视觉树包围盒而不是逐个 Drawable 累加，好处是文字/旋转/描边这些
    /// 「实际占多大得量了才知道」的东西都由 WPF 自己算准。
    /// 注意不能对 canvas 调 GetDescendantBounds —— 它会把画布自身的白底（整页大小）
    /// 一起并进来，结果永远等于页面尺寸。必须逐个取子元素再变换到画布坐标。
    /// </summary>
    public static Rect GetContentBounds(Canvas arranged, PageScene page)
    {
        Rect? acc = null;
        foreach (UIElement child in arranged.Children)
        {
            var b = VisualTreeHelper.GetDescendantBounds(child);
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;

            var rb = child.TransformToAncestor(arranged).TransformBounds(b);
            acc = acc.HasValue ? Rect.Union(acc.Value, rb) : rb;
        }

        if (acc == null || acc.Value.Width <= 0 || acc.Value.Height <= 0)
            return new Rect(0, 0, page.Width, page.Height);

        var result = acc.Value;
        result.Inflate(ContentPadding, ContentPadding);
        return result;
    }

    /// <summary>计算导出后的像素尺寸（供界面实时预览）。</summary>
    public static (int W, int H) PredictSize(PageScene page, ExportRange range, double scale, Canvas? arranged = null)
    {
        var src = new Rect(0, 0, page.Width, page.Height);
        if (range == ExportRange.Content && arranged != null)
            src = GetContentBounds(arranged, page);
        double ppi = 96 * scale;   // 画布单位是英寸，1 英寸 = 96 像素
        return (Math.Max(1, (int)Math.Ceiling(src.Width * ppi)),
                Math.Max(1, (int)Math.Ceiling(src.Height * ppi)));
    }

    /// <summary>
    /// 导出。scale = 1 时 1 英寸 = 96 像素，2 即 192 DPI，以此类推。
    /// transparent 仅对 PNG 生效（JPEG 没有 alpha 通道）。
    /// </summary>
    public static void Export(PageScene page, Canvas arranged, string path,
                              ExportFormat format, ExportRange range, double scale, bool transparent)
    {
        var src = new Rect(0, 0, page.Width, page.Height);
        if (range == ExportRange.Content) src = GetContentBounds(arranged, page);

        double ppi = 96 * scale;   // 目标分辨率：每英寸多少个像素
        const double DipPerInch = 96;

        int pw = Math.Max(1, (int)Math.Ceiling(src.Width * ppi));
        int ph = Math.Max(1, (int)Math.Ceiling(src.Height * ppi));

        bool alpha = transparent && format == ExportFormat.Png;

        // 画布单位是英寸 → 先固定按 96 DIP/英寸转到 DIP，再平移到裁剪区左上角。
        // 真正的放大交给 RenderTargetBitmap 的 dpi 参数：它会把 1 DIP 渲染成 dpi/96 个像素。
        // 这里若再乘一次缩放倍数，就会变成「倍数 × 倍数」（2x 实际渲染成 4x，内容溢出被裁）。
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(DipPerInch, DipPerInch));
        tg.Children.Add(new TranslateTransform(-src.X * DipPerInch, -src.Y * DipPerInch));
        arranged.RenderTransform = tg;

        // host 的尺寸用 DIP 表示，与 dpi 相乘后正好等于目标像素数
        double hostW = src.Width * DipPerInch;
        double hostH = src.Height * DipPerInch;

        var host = new Canvas
        {
            Width = hostW,
            Height = hostH,
            Background = alpha ? null : Brushes.White,
        };
        host.Children.Add(arranged);
        host.Measure(new Size(hostW, hostH));
        host.Arrange(new Rect(0, 0, hostW, hostH));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(pw, ph, ppi, ppi, PixelFormats.Pbgra32);
        bmp.Render(host);

        BitmapEncoder enc = format == ExportFormat.Png
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 92 };
        enc.Frames.Add(BitmapFrame.Create(bmp));

        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
