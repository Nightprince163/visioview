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

        foreach (var d in page.Drawables)
        {
            if (d is PathDrawable p)
                sb.AppendLine(RenderPath(p));
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

    private static string RenderPath(PathDrawable p)
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
                case _Ellipse e:
                    d.Append($"M{e.X - e.Rx:0.###} {e.Y:0.###} ");
                    d.Append($"A{e.Rx:0.###} {e.Ry:0.###} 0 1 1 {e.X + e.Rx:0.###} {e.Y:0.###} ");
                    d.Append($"A{e.Rx:0.###} {e.Ry:0.###} 0 1 1 {e.X - e.Rx:0.###} {e.Y:0.###} Z ");
                    break;
                case _Close: d.Append("Z "); break;
            }
        }
        string fill = (p.NoFill || p.Fill == null) ? "none" : p.Fill;
        string stroke = (p.NoLine || p.Stroke == null) ? "none" : p.Stroke;
        // viewBox/路径坐标都以「英寸」为用户单位，故 stroke-width 也应直接用英寸，
        // 若再乘 PX_PER_IN 会放大 96 倍，导致粗达 1 英寸、整张图被描边糊成一片黑。
        string sw = p.StrokeWidth.ToString("0.####", CultureInfo.InvariantCulture);
        return $"  <path d=\"{d.ToString().Trim()}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{sw}\"/>";
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
