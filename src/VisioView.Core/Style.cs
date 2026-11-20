using VisioView.Core.Model;

namespace VisioView.Core;

/// <summary>形状样式（填充/描边/线宽/文本），支持从父级继承缺失项。</summary>
public class Style
{
    public string? FillForegnd { get; set; }
    /// <summary>图案填充的背景色（FillBkgnd），FillPattern≥2 时作为 hatch 底色。</summary>
    public string? FillBkgnd { get; set; }
    public string? LineColor { get; set; }
    public string? TextColor { get; set; }
    /// <summary>
    /// Visio FillPattern 图案索引：0=无填充，1=纯色（FillForegnd），
    /// 2-24=内置图案填充（Fill 对话框顺序：2=深色上行斜线、5=深色下行斜线、
    /// 6=深色横线、7=深色竖线、3/23=网格、4/24=菱形、17=棋盘、18=格栅、
    /// 8-12=点阵密度、13-16/19-22=宽窄线变体），图案线条用 FillForegnd、
    /// 底色用 FillBkgnd；25-40=渐变族（本实现按纯色回退，真渐变走 FillGradient 段）；
    /// 254=USE(...) 自定义图案（不支持，回退纯色）。
    /// </summary>
    public int? FillPattern { get; set; }
    /// <summary>线性渐变色标（来自 FillGradient 段）。</summary>
    public IReadOnlyList<GradientStopDef>? GradientStops { get; set; }
    /// <summary>渐变方向角（弧度，Visio 内部坐标）。</summary>
    public double GradientAngle { get; set; }
    public int? LinePattern { get; set; }
    public double? LineWeight { get; set; }
    public double? FillTrans { get; set; }
    /// <summary>线条透明度（LineColorTrans），0=不透明，1=全透明。</summary>
    public double? LineTrans { get; set; }
    public double? CharSize { get; set; }
    /// <summary>加粗（Character.Style 的 bit0）。</summary>
    public bool Bold { get; set; }
    /// <summary>斜体（Character.Style 的 bit1）。</summary>
    public bool Italic { get; set; }
    /// <summary>字体名（Character.Font）。</summary>
    public string? FontFamily { get; set; }

    /// <summary>只合并「文字」相关字段（字体/字号/颜色/粗斜体），不动填充与描边。</summary>
    public Style MergeText(Style? other)
    {
        if (other == null) return this;
        return new Style
        {
            FillForegnd = FillForegnd,
            LineColor = LineColor,
            FillPattern = FillPattern,
            FillBkgnd = FillBkgnd,
            GradientStops = GradientStops,
            GradientAngle = GradientAngle,
            LinePattern = LinePattern,
            LineWeight = LineWeight,
            FillTrans = FillTrans,
            LineTrans = LineTrans,
            TextColor = TextColor ?? other.TextColor,
            CharSize = CharSize ?? other.CharSize,
            Bold = Bold || other.Bold,
            Italic = Italic || other.Italic,
            FontFamily = FontFamily ?? other.FontFamily,
        };
    }

    /// <summary>复制本样式，只替换非 null 的填充/线条颜色。</summary>
    public Style WithColors(string? fill, string? line) => new()
    {
        FillForegnd = fill ?? FillForegnd,
        LineColor = line ?? LineColor,
        TextColor = TextColor,
        FillPattern = FillPattern,
        FillBkgnd = FillBkgnd,
        GradientStops = GradientStops,
        GradientAngle = GradientAngle,
        LinePattern = LinePattern,
        LineWeight = LineWeight,
        FillTrans = FillTrans,
        LineTrans = LineTrans,
        CharSize = CharSize,
        Bold = Bold,
        Italic = Italic,
        FontFamily = FontFamily,
    };

    /// <summary>复制本样式，但填充/线条颜色改用 source 的值（实例缓存颜色覆盖母版固定色）。</summary>
    public Style WithColorFrom(Style source) => WithColors(source.FillForegnd, source.LineColor);

    /// <summary>合并回退：本样式优先，缺失项取 other。</summary>
    public Style Merge(Style? other)
    {
        if (other == null) return this;
        return new Style
        {
            FillForegnd = FillForegnd ?? other.FillForegnd,
            FillBkgnd = FillBkgnd ?? other.FillBkgnd,
            LineColor = LineColor ?? other.LineColor,
            TextColor = TextColor ?? other.TextColor,
            FillPattern = FillPattern ?? other.FillPattern,
            GradientStops = GradientStops ?? other.GradientStops,
            GradientAngle = GradientStops != null ? GradientAngle : other.GradientAngle,
            LinePattern = LinePattern ?? other.LinePattern,
            LineWeight = LineWeight ?? other.LineWeight,
            FillTrans = FillTrans ?? other.FillTrans,
            LineTrans = LineTrans ?? other.LineTrans,
            CharSize = CharSize ?? other.CharSize,
            Bold = Bold || other.Bold,
            Italic = Italic || other.Italic,
            FontFamily = FontFamily ?? other.FontFamily,
        };
    }
}
