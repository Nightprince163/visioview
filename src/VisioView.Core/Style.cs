using VisioView.Core.Model;

namespace VisioView.Core;

/// <summary>形状样式（填充/描边/线宽/文本），支持从父级继承缺失项。</summary>
public class Style
{
    public string? FillForegnd { get; set; }
    public string? LineColor { get; set; }
    public string? TextColor { get; set; }
    public int? FillPattern { get; set; }
    /// <summary>内置纹理名（如 greenDiagonal），用于 Visio 房间底板的绿色斜纹。</summary>
    public string? FillTexture { get; set; }
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
            FillTexture = FillTexture,
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
        FillTexture = FillTexture,
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
            LineColor = LineColor ?? other.LineColor,
            TextColor = TextColor ?? other.TextColor,
            FillPattern = FillPattern ?? other.FillPattern,
            FillTexture = FillTexture ?? other.FillTexture,
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
