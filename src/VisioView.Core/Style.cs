namespace VisioView.Core;

/// <summary>形状样式（填充/描边/线宽/文本），支持从父级继承缺失项。</summary>
public class Style
{
    public string? FillForegnd { get; set; }
    public string? LineColor { get; set; }
    public string? TextColor { get; set; }
    public int? FillPattern { get; set; }
    public int? LinePattern { get; set; }
    public double? LineWeight { get; set; }
    public double? FillTrans { get; set; }
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
            LinePattern = LinePattern,
            LineWeight = LineWeight,
            FillTrans = FillTrans,
            TextColor = TextColor ?? other.TextColor,
            CharSize = CharSize ?? other.CharSize,
            Bold = Bold || other.Bold,
            Italic = Italic || other.Italic,
            FontFamily = FontFamily ?? other.FontFamily,
        };
    }

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
            LinePattern = LinePattern ?? other.LinePattern,
            LineWeight = LineWeight ?? other.LineWeight,
            FillTrans = FillTrans ?? other.FillTrans,
            CharSize = CharSize ?? other.CharSize,
            Bold = Bold || other.Bold,
            Italic = Italic || other.Italic,
            FontFamily = FontFamily ?? other.FontFamily,
        };
    }
}
