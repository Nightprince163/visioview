using System.Globalization;

namespace VisioView.Core;

/// <summary>把 Visio 的颜色表达式解析为 #RRGGBB；无法识别时返回 null。</summary>
public static class ColorUtil
{
    // Visio 默认调色板（MS-VSDX 2.2.7.6 Color Table，索引 0-23）。
    // 注意：这不是 Windows 的 VGA 16 色顺序！旧实现用错顺序，导致索引 17 的
    // 外墙被画成 #CC99FF 淡紫，而规范/Visio 实际是 #B3B3B3 灰（实测参考图 (179,179,179)）。
    // 索引 >= 24 由 document.xml 的 <Colors><ColorEntry IX RGB> 定义。
    private static readonly string[] Palette =
    {
        "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
        "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#E6E6E6",
        "#CDCDCD", "#B3B3B3", "#9A9A9A", "#808080", "#666666", "#4D4D4D", "#333333", "#1A1A1A"
    };

    public static string? Parse(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var s = expr.Trim();

        // 公式里的 RGB(r,g,b) 字面量，如 THEMEGUARD(RGB(26,59,136)) / SHADE(RGB(1,2,3),50)
        int ri = s.IndexOf("RGB(", StringComparison.OrdinalIgnoreCase);
        if (ri >= 0)
        {
            int end = s.IndexOf(')', ri);
            if (end > ri)
            {
                var inner = s.Substring(ri + 4, end - ri - 4);
                var parts = inner.Split(',');
                if (parts.Length >= 3 &&
                    int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
                    int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
                    int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                {
                    return $"#{Clamp(r):X2}{Clamp(g):X2}{Clamp(b):X2}";
                }
            }
            return null;
        }

        // #RRGGBB
        if (s[0] == '#' && (s.Length == 7 || s.Length == 4)) return s.ToUpperInvariant();

        // 其它主题/函数表达式暂不解析
        if (s.IndexOf('(') >= 0 || s.IndexOf("THEME", StringComparison.OrdinalIgnoreCase) >= 0)
            return null;

        // 颜色索引整数
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
        {
            var i = ((idx % 24) + 24) % 24;
            return Palette[i];
        }

        return null;
    }

    /// <summary>只接受字面量 #RRGGBB（或 #RGB / #RRGGBBAA），不接受调色板索引。</summary>
    public static string? ParseLiteral(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var s = expr.Trim();
        if (s.Length == 0 || s[0] != '#') return null;
        if (s.Length == 9) s = s.Substring(0, 7);   // #RRGGBBAA → 丢掉 alpha
        if (s.Length == 7)
        {
            for (int i = 1; i < 7; i++)
                if (!Uri.IsHexDigit(s[i])) return null;
            return s.ToUpperInvariant();
        }
        if (s.Length == 4)
        {
            for (int i = 1; i < 4; i++)
                if (!Uri.IsHexDigit(s[i])) return null;
            return s.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>透明色（如 FillForegndTrans=1 或 RGB(...) 配合透明度）→ 返回表示透明的标记。</summary>
    public static bool IsTransparent(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var s = expr.Trim();
        // 形如 "1" 或 "0.5" 的透明度数值
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v >= 0.99;
        return false;
    }

    private static int Clamp(int v) => v<0?0:(v>255?255:v);
}
