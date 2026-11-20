using System.Globalization;
using System.Text;
using VisioView.Core.Model;

namespace VisioView.Render.Svg;

/// <summary>把 Scene 渲染为 SVG（用于测试/导出验证，坐标与 WPF 渲染一致）。</summary>
public static class SvgRenderer
{
    private const double PX_PER_IN = 96.0;

    public static string RenderPage(PageScene page)
    {
        var sb = new StringBuilder();
        double w = page.Width * PX_PER_IN;
        double h = page.Height * PX_PER_IN;
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w:0.0}\" height=\"{h:0.0}\" viewBox=\"0 0 {page.Width:0.#####} {page.Height:0.#####}\">");
        sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{page.Width:0.#####}\" height=\"{page.Height:0.#####}\" fill=\"#ffffff\"/>");

        // defs：渐变定义 + 图案填充定义，路径用 url(#gradN)/url(#patN) 引用。
        var grads = page.Drawables.OfType<PathDrawable>()
            .Where(p => p.GradientStops is { Count: >= 2 }).ToList();
        var pats = page.Drawables.OfType<PathDrawable>()
            .Where(p => p.GradientStops is not { Count: >= 2 }
                        && p.FillPatternIndex >= 2 && p.FillPatternIndex <= 24
                        && !p.NoFill && p.Fill != null).ToList();
        if (grads.Count > 0 || pats.Count > 0)
        {
            sb.AppendLine("  <defs>");
            for (int gi = 0; gi < grads.Count; gi++)
                sb.AppendLine(GradientDef(grads[gi], gi));
            for (int pi = 0; pi < pats.Count; pi++)
                sb.AppendLine(PatternDef(pats[pi], pi));
            sb.AppendLine("  </defs>");
        }

        foreach (var d in page.Drawables)
        {
            if (d is PathDrawable p)
            {
                int gi = grads.IndexOf(p);
                int pi = pats.IndexOf(p);
                string? fo2 = gi >= 0 ? $"url(#grad{gi})" : pi >= 0 ? $"url(#pat{pi})" : null;
                sb.AppendLine(RenderPath(p, fo2));
            }
            else if (d is TextDrawable t)
                sb.AppendLine(RenderText(t));
            else if (d is ImageDrawable im)
                sb.AppendLine(RenderImage(im));
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static string RenderScene(Scene scene)
        => string.Join("\n", scene.Pages.Select(RenderPage));

    /// <summary>生成 SVG linearGradient 定义。渐变方向与 WpfRenderer 一致。</summary>
    private static string GradientDef(PathDrawable p, int index)
    {
        double ang = p.GradientAngle;
        double dx = Math.Cos(ang), dy = Math.Sin(ang);
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        double scale = ax > 1e-9 ? 1.0 / (2 * ax) : 1.0 / (2 * ay);
        double x1 = 0.5 - dx * scale, y1 = 0.5 + dy * scale;
        double x2 = 0.5 + dx * scale, y2 = 0.5 - dy * scale;
        var sb = new StringBuilder();
        sb.Append($"    <linearGradient id=\"grad{index}\" gradientUnits=\"objectBoundingBox\" " +
                  $"x1=\"{x1:0.#####}\" y1=\"{y1:0.#####}\" x2=\"{x2:0.#####}\" y2=\"{y2:0.#####}\">");
        foreach (var s in p.GradientStops!)
        {
            string op = p.FillOpacity >= 0.999 ? "" : $" stop-opacity=\"{p.FillOpacity:0.###}\"";
            sb.Append($"<stop offset=\"{Math.Clamp(s.Position, 0, 1):0.#####}\" stop-color=\"{s.Color}\"{op}/>");
        }
        sb.Append("</linearGradient>");
        return sb.ToString();
    }

    /// <summary>
    /// Visio 内置填充图案（FillPattern 2-24）的 SVG pattern 定义。
    /// tile 尺寸/线宽与 WpfRenderer.PatternBrush 表一致，坐标单位为英寸。
    /// </summary>
    private static string PatternDef(PathDrawable p, int index)
    {
        int pattern = p.FillPatternIndex;
        // (tile 英寸, 线条宽英寸) —— 与 WpfRenderer.PatternBrush 同表
        var (s, w) = pattern switch
        {
            2 or 5 => (0.05, 0.009),
            3 => (0.12, 0.008),
            4 => (0.12, 0.008),
            6 => (0.05, 0.009),
            7 => (0.05, 0.009),
            13 => (0.12, 0.012),
            14 => (0.12, 0.012),
            15 or 16 => (0.05, 0.006),
            17 => (0.06, 0),
            18 => (0.08, 0.005),
            19 => (0.035, 0.007),
            20 => (0.035, 0.007),
            21 or 22 => (0.035, 0.007),
            23 => (0.05, 0.007),
            24 => (0.05, 0.007),
            _ => (0.05, 0.008),
        };
        string fg = p.Fill!;
        string? bg = p.FillBkgnd;
        double opacity = Math.Clamp(p.FillOpacity, 0, 1);

        var sb = new StringBuilder();
        sb.Append($"    <pattern id=\"pat{index}\" patternUnits=\"userSpaceOnUse\" " +
                  $"width=\"{s:0.#####}\" height=\"{s:0.#####}\">");
        bool wrapOp = opacity < 0.999;
        if (wrapOp) sb.Append($"<g opacity=\"{opacity:0.###}\">");

        // 底色
        if (bg != null)
            sb.Append($"<rect x=\"0\" y=\"0\" width=\"{s:0.#####}\" height=\"{s:0.#####}\" fill=\"{bg}\"/>");

        switch (pattern)
        {
            case 2: case 5: case 15: case 16: case 21: case 22:
            {
                bool up = pattern is 2 or 16 or 22;
                if (up)
                    sb.Append($"<polygon points=\"0,0 {w:0.#####},0 {s:0.#####},{s:0.#####} {s - w:0.#####},{s:0.#####}\" fill=\"{fg}\"/>");
                else
                    sb.Append($"<polygon points=\"0,{s:0.#####} {w:0.#####},{s:0.#####} {s:0.#####},0 {s - w:0.#####},0\" fill=\"{fg}\"/>");
                break;
            }
            case 6: case 7: case 13: case 14: case 19: case 20:
            {
                bool horiz = pattern is 6 or 13 or 19;
                if (horiz)
                    sb.Append($"<rect x=\"0\" y=\"{s / 2 - w / 2:0.#####}\" width=\"{s:0.#####}\" height=\"{w:0.#####}\" fill=\"{fg}\"/>");
                else
                    sb.Append($"<rect x=\"{s / 2 - w / 2:0.#####}\" y=\"0\" width=\"{w:0.#####}\" height=\"{s:0.#####}\" fill=\"{fg}\"/>");
                break;
            }
            case 3: case 23:
            {
                sb.Append($"<rect x=\"0\" y=\"{s / 2 - w / 2:0.#####}\" width=\"{s:0.#####}\" height=\"{w:0.#####}\" fill=\"{fg}\"/>");
                sb.Append($"<rect x=\"{s / 2 - w / 2:0.#####}\" y=\"0\" width=\"{w:0.#####}\" height=\"{s:0.#####}\" fill=\"{fg}\"/>");
                break;
            }
            case 4: case 24:   // 菱形描边
            {
                sb.Append($"<polygon points=\"{s / 2:0.#####},0 {s:0.#####},{s / 2:0.#####} {s / 2:0.#####},{s:0.#####} 0,{s / 2:0.#####}\" " +
                          $"fill=\"none\" stroke=\"{fg}\" stroke-width=\"{w:0.#####}\"/>");
                break;
            }
            case 17:   // 棋盘
            {
                double h = s / 2;
                sb.Append($"<rect x=\"0\" y=\"0\" width=\"{h:0.#####}\" height=\"{h:0.#####}\" fill=\"{fg}\"/>");
                sb.Append($"<rect x=\"{h:0.#####}\" y=\"{h:0.#####}\" width=\"{h:0.#####}\" height=\"{h:0.#####}\" fill=\"{fg}\"/>");
                break;
            }
            case 18:   // 格栅 "×"
            {
                sb.Append($"<line x1=\"0\" y1=\"0\" x2=\"{s:0.#####}\" y2=\"{s:0.#####}\" stroke=\"{fg}\" stroke-width=\"{w:0.#####}\"/>");
                sb.Append($"<line x1=\"0\" y1=\"{s:0.#####}\" x2=\"{s:0.#####}\" y2=\"0\" stroke=\"{fg}\" stroke-width=\"{w:0.#####}\"/>");
                break;
            }
            default:   // 8-12 点阵
            {
                double r = pattern switch
                {
                    8 => 0.006, 9 => 0.008, 10 => 0.010, 11 => 0.008, 12 => 0.005,
                    _ => 0.007,
                };
                sb.Append($"<circle cx=\"{s / 2:0.#####}\" cy=\"{s / 2:0.#####}\" r=\"{r:0.#####}\" fill=\"{fg}\"/>");
                break;
            }
        }

        if (wrapOp) sb.Append("</g>");
        sb.Append("</pattern>");
        return sb.ToString();
    }

    private static string RenderPath(PathDrawable p, string? fillOverride = null)
    {
        if (p.Commands.Count == 0) return "";
        var d = new StringBuilder();
        foreach (var c in p.Commands)
        {
            switch (c)
            {
                case _Move m: d.Append($"M{m.X:0.###} {m.Y:0.###} "); break;
                case _Line l: d.Append($"L{l.X:0.###} {l.Y:0.###} "); break;
                case _Arc a:
                    d.Append($"A{a.Rx:0.###} {a.Ry:0.###} {a.Rotation:0.###} {(a.LargeArc ? 1 : 0)} {(a.Sweep ? 1 : 0)} {a.X:0.###} {a.Y:0.###} ");
                    break;
                case _Quad q:
                    d.Append($"Q{q.Cx:0.####} {q.Cy:0.####} {q.X:0.####} {q.Y:0.####} ");
                    break;
                case _Cubic cb:
                    d.Append($"C{cb.C1x:0.####} {cb.C1y:0.####} {cb.C2x:0.####} {cb.C2y:0.####} {cb.X:0.####} {cb.Y:0.####} ");
                    break;
                case _Ellipse e:
                    d.Append($"M{e.X - e.Rx:0.###} {e.Y:0.###} ");
                    d.Append($"A{e.Rx:0.###} {e.Ry:0.###} 0 1 1 {e.X + e.Rx:0.###} {e.Y:0.###} ");
                    d.Append($"A{e.Rx:0.###} {e.Ry:0.###} 0 1 1 {e.X - e.Rx:0.###} {e.Y:0.###} Z ");
                    break;
                case _Close: d.Append("Z "); break;
            }
        }
        string fill = fillOverride ?? ((p.NoFill || p.Fill == null) ? "none" : p.Fill);
        string stroke = (p.NoLine || p.Stroke == null) ? "none" : p.Stroke;
        // viewBox/路径坐标都以「英寸」为用户单位，故 stroke-width 也应直接用英寸，
        // 若再乘 PX_PER_IN 会放大 96 倍，导致粗达 1 英寸、整张图被描边糊成一片黑。
        string sw = p.StrokeWidth.ToString("0.####", CultureInfo.InvariantCulture);
        string fo = p.FillOpacity >= 0.999 ? "" : $" fill-opacity=\"{p.FillOpacity:0.###}\"";
        string so = p.StrokeOpacity >= 0.999 ? "" : $" stroke-opacity=\"{p.StrokeOpacity:0.###}\"";
        return $"  <path d=\"{d.ToString().Trim()}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{sw}\"{fo}{so}/>";
    }

    private static string RenderImage(ImageDrawable im)
    {
        var b64 = Convert.ToBase64String(im.Data);
        return $"  <image x=\"{im.X:0.####}\" y=\"{im.Y:0.####}\" width=\"{im.W:0.####}\" height=\"{im.H:0.####}\" " +
               $"preserveAspectRatio=\"none\" href=\"data:{im.Mime};base64,{b64}\"/>";
    }

    private static string RenderText(TextDrawable t)
    {
        // 用户单位即英寸，FontSize 已是英寸，直接使用（乘 96 会放大成 16 英寸的巨型黑字）
        double fs = t.FontSize;
        string fx = FontAttrs(t);
        // 旋转中心就是文字锚点；SVG 的角度正方向是顺时针（Y 轴向下），
        // 而模型里正角是 Visio 的逆时针，所以取反。
        string rot = Math.Abs(t.RotationDeg) < 0.05
            ? ""
            : $" transform=\"rotate({-t.RotationDeg:0.###} {t.CenterX:0.###} {t.CenterY:0.###})\"";
        var lines = t.MaxWidth.HasValue ? WrapLines(t.Text, t.MaxWidth.Value, fs) : new List<string> { t.Text };
        if (lines.Count <= 1)
        {
            var esc0 = SecurityElementEscape(t.Text.Replace("\n", " "));
            return $"  <text x=\"{t.CenterX:0.###}\" y=\"{t.CenterY:0.###}\" font-size=\"{fs:0.###}\" fill=\"{t.Color}\"{fx}{rot} text-anchor=\"middle\" dominant-baseline=\"middle\">{esc0}</text>";
        }

        // 多行：第一行上移到整体垂直居中位置，其余行用 dy 递进
        double lh = fs * 1.3;
        double y0 = t.CenterY - lh * (lines.Count - 1) / 2.0;
        var sb = new StringBuilder();
        sb.Append($"  <text x=\"{t.CenterX:0.###}\" y=\"{y0:0.###}\" font-size=\"{fs:0.###}\" fill=\"{t.Color}\"{fx}{rot} text-anchor=\"middle\" dominant-baseline=\"middle\">");
        for (int i = 0; i < lines.Count; i++)
        {
            string dy = i == 0 ? "0" : lh.ToString("0.###", CultureInfo.InvariantCulture);
            sb.Append($"<tspan x=\"{t.CenterX:0.###}\" dy=\"{dy}\">{SecurityElementEscape(lines[i])}</tspan>");
        }
        sb.Append("</text>");
        return sb.ToString();
    }

    /// <summary>字体名/粗细/斜体的 SVG 属性串。</summary>
    private static string FontAttrs(TextDrawable t)
    {
        var sb = new StringBuilder();
        if (t.Bold) sb.Append(" font-weight=\"bold\"");
        if (t.Italic) sb.Append(" font-style=\"italic\"");
        if (!string.IsNullOrEmpty(t.FontFamily))
            sb.Append($" font-family=\"{SecurityElementEscape(t.FontFamily!)}\"");
        return sb.ToString();
    }

    /// <summary>按可用宽度粗略换行（中日韩字符按 1em、其余按 0.55em 估算）。</summary>
    private static List<string> WrapLines(string text, double maxWidth, double fontSize)
    {
        var result = new List<string>();
        if (maxWidth <= 0 || fontSize <= 0) { result.Add(text); return result; }
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            double cur = 0;
            var line = new StringBuilder();
            foreach (var ch in para)
            {
                double cw = ch >= 0x2E80 ? fontSize : fontSize * 0.55;
                if (cur + cw > maxWidth && line.Length > 0)
                {
                    result.Add(line.ToString());
                    line.Clear();
                    cur = 0;
                }
                line.Append(ch);
                cur += cw;
            }
            if (line.Length > 0) result.Add(line.ToString());
        }
        if (result.Count == 0) result.Add(text);
        return result;
    }

    private static string SecurityElementEscape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
