namespace VisioView.Core.Model;

/// <summary>整份文档解析后的场景（页面列表）。</summary>
public class Scene
{
    public List<PageScene> Pages { get; } = new();
}

public class PageScene
{
    public string Name { get; set; } = "";
    /// <summary>页面宽度/高度（Visio 页单位，通常为英寸）。</summary>
    public double Width { get; set; } = 11;
    public double Height { get; set; } = 8.5;
    public List<Drawable> Drawables { get; } = new();
}

public abstract class Drawable
{
    public double MinX = double.MaxValue, MinY = double.MaxValue;
    public double MaxX = double.MinValue, MaxY = double.MinValue;

    public void Include(double x, double y)
    {
        if (x < MinX) MinX = x;
        if (y < MinY) MinY = y;
        if (x > MaxX) MaxX = x;
        if (y > MaxY) MaxY = y;
    }

    public bool HasBounds => MaxX >= MinX;
}

public class PathDrawable : Drawable
{
    /// <summary>已变换到页面坐标（Y 已翻转）的可渲染指令。</summary>
    public List<ILowCmd> Commands { get; } = new();
    /// <summary>填充颜色 #RRGGBB，为 null 表示无填充。</summary>
    public string? Fill { get; set; }
    /// <summary>描边颜色 #RRGGBB，为 null 表示无描边。</summary>
    public string? Stroke { get; set; }
    public double StrokeWidth { get; set; } = 1;
    public bool NoFill { get; set; }
    public bool NoLine { get; set; }
}

public class TextDrawable : Drawable
{
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#000000";
    /// <summary>字号（已换算为页单位的高度）。</summary>
    public double FontSize { get; set; } = 0.1667;
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    /// <summary>可用文本框宽度（英寸，对应 TxtWidth）。为 null 表示不换行。</summary>
    public double? MaxWidth { get; set; }
    /// <summary>加粗（Character.Style bit0）。</summary>
    public bool Bold { get; set; }
    /// <summary>斜体（Character.Style bit1）。</summary>
    public bool Italic { get; set; }
    /// <summary>字体名（Character.Font），为 null 表示用渲染器默认字体。</summary>
    public string? FontFamily { get; set; }
    /// <summary>
    /// 文字旋转角（度）。正值 = 在 Visio 坐标里逆时针（Y 轴向上）；
    /// 渲染到屏幕坐标（Y 轴向下）时取反。由形状 Angle 与 TxtAngle 叠加而来。
    /// </summary>
    public double RotationDeg { get; set; }
}

/// <summary>嵌入的图片（Visio 的 Foreign 形状），Data 为原始字节，按轴对齐框绘制。</summary>
public class ImageDrawable : Drawable
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Mime { get; set; } = "image/png";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

// 低层渲染指令（页面坐标，Y 轴向下）。
public interface ILowCmd { }

public class _Move : ILowCmd
{
    public double X, Y;
    public _Move(double x, double y) { X = x; Y = y; }
}

public class _Line : ILowCmd
{
    public double X, Y;
    public _Line(double x, double y) { X = x; Y = y; }
}

public class _Arc : ILowCmd
{
    public double X, Y, Rx, Ry, Rotation;
    public bool LargeArc, Sweep;
    public _Arc(double x, double y, double rx, double ry, double rot, bool la, bool sw)
    {
        X = x; Y = y; Rx = rx; Ry = ry; Rotation = rot; LargeArc = la; Sweep = sw;
    }
}

/// <summary>二次贝塞尔：用于 Visio 的 EllipticalArcTo（A/B 为两端切线交点，即控制点）。</summary>
public class _Quad : ILowCmd
{
    public double Cx, Cy, X, Y;
    public _Quad(double cx, double cy, double x, double y) { Cx = cx; Cy = cy; X = x; Y = y; }
}

public class _Ellipse : ILowCmd
{
    public double X, Y, Rx, Ry;
    public _Ellipse(double x, double y, double rx, double ry) { X = x; Y = y; Rx = rx; Ry = ry; }
}

public class _Close : ILowCmd { }
