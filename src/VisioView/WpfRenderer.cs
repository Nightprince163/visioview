using System.IO;
using System.Windows;
using System.Windows.Documents;
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
        Brush? fill = MakeFillBrush(p);
        Brush? stroke = (p.NoLine || p.Stroke == null) ? null : WithOpacity(ColorFromHex(p.Stroke), p.StrokeOpacity);

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

    private static TextBlock MakeTextBlock(TextDrawable t, string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(ColorFromHex(t.Color)),
        FontSize = t.FontSize, // 模型 FontSize 为英寸；Canvas 已带 96× 缩放变换，故此处用英寸
        TextAlignment = TextAlignment.Left,
        FontFamily = new FontFamily(string.IsNullOrEmpty(t.FontFamily)
            ? DefaultFont
            : t.FontFamily + ", " + DefaultFont),
        FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
        FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal,
        // Visio 的正文行距是 1.2em。WPF 默认行距略大，db_schema 表格最后一行
        // 会被框底裁掉；固定行高后行数和参考一致。
        LineHeight = t.FontSize * 1.2,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// <summary>按 Visio Tabs 制表位逐行分段绘制，比例字体下也能列对齐。</summary>
    private static void AddTabbedText(Canvas host, TextDrawable t)
    {
        double lineH = t.FontSize * 1.2;
        var stops = t.TabStops!;
        var lines = t.Text.Replace("\r", "").Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var parts = lines[li].Split('\t');
            double x = 0;
            for (int pi = 0; pi < parts.Length; pi++)
            {
                if (pi > 0)
                    x = pi - 1 < stops.Count ? stops[pi - 1] : x + t.FontSize * 0.5;
                if (parts[pi].Length == 0) continue;
                var seg = MakeTextBlock(t, parts[pi]);
                seg.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(seg, x);
                Canvas.SetTop(seg, li * lineH);
                host.Children.Add(seg);
            }
        }
    }

    private static void AddText(Canvas canvas, TextDrawable t)
    {
        var tb = MakeTextBlock(t, t.Text);
        var lines = t.Text.Replace("\r", "").Split('\n');
        if (t.LineStyles != null && t.LineStyles.Count == lines.Length)
        {
            tb.Text = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                var ls = t.LineStyles[i];
                tb.Inlines.Add(new Run(lines[i])
                {
                    FontSize = ls.FontSize,
                    Foreground = new SolidColorBrush(ColorFromHex(ls.Color ?? t.Color)),
                    FontFamily = new FontFamily(string.IsNullOrEmpty(ls.FontFamily)
                        ? DefaultFont
                        : ls.FontFamily + ", " + DefaultFont),
                    FontWeight = ls.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = ls.Italic ? FontStyles.Italic : FontStyles.Normal,
                });
            }
        }
        tb.TextAlignment = t.HorizontalAlignment switch
        {
            0 => TextAlignment.Left,
            2 => TextAlignment.Right,
            _ => TextAlignment.Center,
        };

        // 显式文字框 + 非居中对齐：放进与 Visio 文本框等大的容器里定位。
        // 旧实现一律把TextBlock中心对到TxtPin，表头/正文的左对齐和顶对齐全部丢失。
        bool useBox = t.BoxLeft.HasValue && t.BoxTop.HasValue &&
                      t.BoxWidth.HasValue && t.BoxHeight.HasValue &&
                      (t.HorizontalAlignment != 1 || t.VerticalAlignment != 1);
        if (useBox)
        {
            double bw = Math.Max(0.01, t.BoxWidth!.Value);
            double bh = Math.Max(0.01, t.BoxHeight!.Value);
            var host = new Canvas { Width = bw, Height = bh };
            Canvas.SetLeft(host, t.BoxLeft!.Value);
            Canvas.SetTop(host, t.BoxTop!.Value);

            if (t.TabStops is { Count: > 0 } && t.Text.IndexOf('\t') >= 0)
            {
                AddTabbedText(host, t);
                canvas.Children.Add(host);
                return;
            }

            tb.TextWrapping = TextWrapping.NoWrap; // 显式换行由 VSDX 给出，不让 WPF 再拦腰折断
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double naturalW = tb.DesiredSize.Width;
            double tx = 0;
            if (t.HorizontalAlignment != 0)
            {
                if (naturalW > bw)
                {
                    // 文字比 Visio 文本框宽时不裁切，按对齐方式溢出显示。
                    tx = t.HorizontalAlignment == 2 ? bw - naturalW : (bw - naturalW) / 2;
                }
                else tb.Width = bw;
            }
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double ty = t.VerticalAlignment switch
            {
                0 => 0,
                2 => Math.Max(0, bh - tb.DesiredSize.Height),
                _ => Math.Max(0, (bh - tb.DesiredSize.Height) / 2),
            };
            Canvas.SetLeft(tb, tx);
            Canvas.SetTop(tb, ty);
            host.Children.Add(tb);
            canvas.Children.Add(host);
            return;
        }

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

    /// <summary>
    /// 生成路径的填充画刷：渐变 &gt; 纹理 &gt; 纯色。
    /// 渐变用 LinearGradientBrush，StartPoint/EndPoint 按角度投影到单位方块。
    /// ms_diagrams 的池 GradientAngle=π/2（Visio 90°=自下而上），渲染成自上而下的
    /// 浅蓝/橙/红渐变，与 Visio 参考一致。
    /// </summary>
    private static Brush? MakeFillBrush(PathDrawable p)
    {
        if (p.GradientStops is { Count: >= 2 } stops)
        {
            // Visio 角度：0°=左→右，90°=下→上（内部 Y 轴向上）。
            // 屏幕 Y 向下，所以 90° 时渐变应从上(pos=0)到下(pos=1)。
            double ang = p.GradientAngle;
            double dx = Math.Cos(ang), dy = Math.Sin(ang);
            // 单位方块中把方向向量缩放到恰好覆盖 0..1 的投影区间。
            double ax = Math.Abs(dx), ay = Math.Abs(dy);
            double scale = ax > 1e-9 ? 1.0 / (2 * ax) : 1.0 / (2 * ay);
            double cx = 0.5, cy = 0.5;
            // 屏幕 Y 翻转：Visio 的正 sin（向上）对应屏幕向上（Y 减小）。
            var start = new Point(cx - dx * scale, cy + dy * scale);
            var end = new Point(cx + dx * scale, cy - dy * scale);
            var gb = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end,
                GradientStops = GradientStopOffsets(stops, p.FillOpacity),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            gb.Freeze();
            return gb;
        }
        if (p.NoFill || p.Fill == null) return null;
        if (p.FillTexture == "greenDiagonal") return GreenDiagonalBrush(p.FillOpacity);
        return WithOpacity(ColorFromHex(p.Fill), p.FillOpacity);
    }

    private static GradientStopCollection GradientStopOffsets(
        IReadOnlyList<GradientStopDef> stops, double opacity)
    {
        var coll = new GradientStopCollection();
        foreach (var s in stops)
        {
            var c = ColorFromHex(s.Color);
            if (opacity < 0.999) c.A = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
            coll.Add(new GradientStop(c, Math.Clamp(s.Position, 0, 1)));
        }
        return coll;
    }

    /// <summary>房间底板的绿色斜纹：白底 + 半透明绿色斜线，按 0.08in 平铺。</summary>
    private static Brush GreenDiagonalBrush(double opacity)
    {
        const double s = 0.05;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brushes.White, null,
            new RectangleGeometry(new Rect(0, 0, s, s))));
        double w = s * 0.18;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // 一条贯穿整块 tile 的斜带，平铺后形成连续的“\”斜纹。
            ctx.BeginFigure(new Point(0, 0), true, true);
            ctx.LineTo(new Point(w, 0), true, false);
            ctx.LineTo(new Point(s, s), true, false);
            ctx.LineTo(new Point(s - w, s), true, false);
        }
        geo.Freeze();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xA9, 0xD1, 0x8E)), null, geo));
        group.Freeze();
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, s, s),
            Viewbox = new Rect(0, 0, s, s),
            ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Opacity = Math.Clamp(opacity, 0, 1),
        };
        brush.Freeze();
        return brush;
    }

    private static Brush WithOpacity(Color color, double opacity)
    {
        if (opacity >= 0.999) return new SolidColorBrush(color);
        color.A = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        return new SolidColorBrush(color);
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
