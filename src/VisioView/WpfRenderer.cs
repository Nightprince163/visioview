using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisioView.Core.Model;

namespace VisioView;

/// <summary>把 PageScene 渲染为 WPF Canvas（坐标为英寸，Y 轴向下）。</summary>
public static class WpfRenderer
{
    /// <summary>缺省字体（含中文回退）。</summary>
    private const string DefaultFont = "Microsoft YaHei, Segoe UI, SimSun";

    public static Canvas Build(PageScene page)
    {
        var canvas = new Canvas
        {
            Width = page.Width,
            Height = page.Height,
            Background = Brushes.White,
        };

        foreach (var d in page.Drawables)
        {
            if (d is PathDrawable p)
                AddPath(canvas, p);
            else if (d is TextDrawable t)
                AddText(canvas, t);
            else if (d is ImageDrawable im)
                AddImage(canvas, im);
        }

        return canvas;
    }

    /// <summary>把页面渲染成 PNG（诊断/比对用）。1 英寸 = pixelsPerInch 像素。</summary>
    public static void SavePng(PageScene page, string path, double pixelsPerInch = 96)
    {
        var canvas = Build(page);
        // 画布坐标是英寸，靠 RenderTransform 放大到像素
        canvas.RenderTransform = new ScaleTransform(pixelsPerInch, pixelsPerInch);

        int pw = (int)Math.Ceiling(page.Width * pixelsPerInch);
        int ph = (int)Math.Ceiling(page.Height * pixelsPerInch);
        var host = new Canvas
        {
            Width = pw,
            Height = ph,
            Background = Brushes.White,
        };
        host.Children.Add(canvas);
        host.Measure(new Size(pw, ph));
        host.Arrange(new Rect(0, 0, pw, ph));
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void AddPath(Canvas canvas, PathDrawable p)
    {
        Brush? fill = (p.NoFill || p.Fill == null) ? null : new SolidColorBrush(ColorFromHex(p.Fill));
        Brush? stroke = (p.NoLine || p.Stroke == null) ? null : new SolidColorBrush(ColorFromHex(p.Stroke));

        // 主路径（move/line/arc/close）
        var pg = new PathGeometry();
        PathFigure? fig = null;
        var ellipses = new List<_Ellipse>();
        foreach (var c in p.Commands)
        {
            switch (c)
            {
                case _Move m:
                    fig = new PathFigure { StartPoint = new Point(m.X, m.Y), IsClosed = false };
                    pg.Figures.Add(fig);
                    break;
                case _Line l:
                    fig?.Segments.Add(new LineSegment(new Point(l.X, l.Y), true));
                    break;
                case _Arc a:
                    fig?.Segments.Add(new ArcSegment(
                        new Point(a.X, a.Y), new Size(a.Rx, a.Ry), a.Rotation,
                        a.LargeArc, a.Sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
                    break;
                case _Quad q:
                    fig?.Segments.Add(new QuadraticBezierSegment(
                        new Point(q.Cx, q.Cy), new Point(q.X, q.Y), true));
                    break;
                case _Cubic cb:
                    fig?.Segments.Add(new BezierSegment(
                        new Point(cb.C1x, cb.C1y), new Point(cb.C2x, cb.C2y),
                        new Point(cb.X, cb.Y), true));
                    break;
                case _Close:
                    if (fig != null) fig.IsClosed = true;
                    break;
                case _Ellipse e:
                    ellipses.Add(e);
                    break;
            }
        }

        if (pg.Figures.Count > 0)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = pg,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = p.StrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true,
            };
            canvas.Children.Add(path);
        }

        foreach (var e in ellipses)
        {
            var ep = new System.Windows.Shapes.Path
            {
                Data = new EllipseGeometry(new Point(e.X, e.Y), e.Rx, e.Ry),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = p.StrokeWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                SnapsToDevicePixels = true,
            };
            canvas.Children.Add(ep);
        }
    }

    private static void AddText(Canvas canvas, TextDrawable t)
    {
        var tb = new TextBlock
        {
            Text = t.Text,
            Foreground = new SolidColorBrush(ColorFromHex(t.Color)),
            FontSize = t.FontSize, // 模型 FontSize 为英寸；Canvas 已带 96× 缩放变换，故此处用英寸
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily(string.IsNullOrEmpty(t.FontFamily)
                ? DefaultFont
                : t.FontFamily + ", " + DefaultFont),
            FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal,
        };
        if (t.MaxWidth.HasValue)
        {
            tb.Width = t.MaxWidth.Value;
            tb.TextWrapping = TextWrapping.Wrap;
        }
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = tb.DesiredSize.Width;
        var h = tb.DesiredSize.Height;
        Canvas.SetLeft(tb, t.CenterX - w / 2);
        Canvas.SetTop(tb, t.CenterY - h / 2);

        // 绕文字中心旋转（WPF 的正角是屏幕顺时针，模型里是 Visio 的逆时针，故取反）
        if (Math.Abs(t.RotationDeg) > 0.05)
        {
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
            tb.RenderTransform = new RotateTransform(-t.RotationDeg);
        }
        canvas.Children.Add(tb);
    }

    private static void AddImage(Canvas canvas, ImageDrawable im)
    {
        if (im.Data.Length == 0 || im.W <= 0 || im.H <= 0) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 一次性读入，避免流关闭后失效
            bmp.StreamSource = new MemoryStream(im.Data);
            bmp.EndInit();
            bmp.Freeze();

            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = im.W,
                Height = im.H,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(img, im.X);
            Canvas.SetTop(img, im.Y);
            canvas.Children.Add(img);
        }
        catch { /* 图片格式不可解码时静默跳过，不影响其它图形 */ }
    }

    private static Color ColorFromHex(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            return Colors.Black;
        }
    }
}
