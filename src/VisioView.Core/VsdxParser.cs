using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VisioView.Core.Model;

namespace VisioView.Core;

/// <summary>
/// 解析 .vsdx 文件，把各页面形状转为与渲染器无关的 Scene（页面坐标，Y 轴已翻转）。
/// 支持：普通形状、母版实例（Master）、母版子形状实例（MasterShape）、分组、
/// 常见 Geometry 行类型（Move/Line/RelMove/RelLine/Arc/Ellipse/EllipticalArc/近似）、
/// 填充/描边/颜色/线宽、文本。
///
/// 关键点（真实 Visio 文件与手写示例不同）：
/// - 几何几乎全部存储在母版里，页面只引用（Master / MasterShape）。
/// - 尺寸/坐标带单位（U='MM' 等），需换算到英寸统一坐标系。
/// - 单元格大量使用公式（Width*0、Height*1、Sheet.N!Width*0.5）或 F='Inh' 继承。
/// </summary>
public static class VsdxParser
{
    private static readonly XNamespace RNS =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const NumberStyles NF = NumberStyles.Float;

    // 主题配色回退（THEMEVAL 无法逐槽解析时使用）：填充取主题色，线条取深色。
    private static string ThemeFill = "#E7E6E6";   // 对应主题 lt2，随后按文档推断覆盖
    private static string ThemeLine = "#44546A";   // 对应主题 dk2
    private static string? ThemeAccent1 = null;    // 主题 accent1（Themed 文字在浅底上的颜色）
    /// <summary>主题 accent1~6（QuickStyleVariation 颜色索引）。Visio 的 QuickStyle 变体
    /// 1..6 对应主题 accent1..accent6； QuickStyleVariation='2' 即 accent1（如 ms_diagrams
    /// 的容量块）。这些颜色是 Visio 内置矩阵，文档里不直接写，必须从 theme1.xml 读。</summary>
    private static readonly string[] ThemeAccents = new string[6];
    /// <summary>文档是否使用「主题样式」配色：存在某个母版同时显式写
    /// QuickStyleType=2 与 QuickStyleVariation 含文字对比位(2)。这类文档（ms_diagrams）
    /// 的普通形状没有显式填充色，实际颜色由 QuickStyleFillColor 指向主题 accent。
    /// 其它文档里 QuickStyle 只是默认值（颜色都写在单元格里），不能套用 accent，
    /// 否则会把 network_topology 的设备染成主题红色。</summary>
    private static bool _useThemeStyleFill;
    /// <summary>QT>=2 的母版 ID 集合（待页面引用后再启用主题样式标记）。</summary>
    private static readonly HashSet<int> _qtMasterIds = new();

    // 图片（Foreign 形状）解析所需的上下文：当前包 + 当前页面的关系表
    private static VsdxPackage? _pkg;
    private static IReadOnlyDictionary<string, string>? _pageRels;
    private static readonly Dictionary<string, byte[]?> _imgCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置环境变量 VV_TRACE=1 可在解析时把每个形状的样式打到 stderr（排查渲染问题用）。</summary>
    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("VV_TRACE") == "1";

    /// <summary>当前页各形状的包围盒（按 Sheet ID），用于判断连接线是从哪条边离开的。</summary>
    private static readonly Dictionary<int, (double Cx, double Cy, double W, double H)> PageShapeBox = new();

    private sealed class MasterDef
    {
        public XElement ShapesRoot = null!;
        /// <summary>母版自己的关系表：母版内 Foreign 形状的 Rel r:id → visio/media/*。</summary>
        public Dictionary<string, string>? Rels;
        public double Width, Height, LocPinX, LocPinY; // 已换算为英寸（母版顶形状 XForm，备用）
        public int Id;
        // 母版真实内容包围盒（本地坐标，英寸）：用于把内容缩放到实例尺寸
        public double NatX0, NatY0, NatW, NatH;
    }

    public static Scene Parse(string path)
    {
        using var pkg = new VsdxPackage(path);
        var scene = new Scene();

        // 图片缓存键是包内部路径（visio/media/image1.jpeg），不同文档会重名。
        // 不按文档清空，先打开的文件会把 logo 缓存下来，后打开的文件
        // 同路径的人物头像会错误命中旧字节（同进程连续打开两个 vsdx 时复现）。
        _imgCache.Clear();
        _useThemeStyleFill = false; // 每份文档重新判定（静态状态必须按文档重置）
        _qtMasterIds.Clear();

        // ---- 加载全部母版 ----
        var masters = new Dictionary<int, MasterDef>();
        if (pkg.HasPart("visio/masters/masters.xml"))
        {
            var mDoc = pkg.GetXml("visio/masters/masters.xml");
            if (pkg.HasPart("visio/masters/_rels/masters.xml.rels"))
            {
                var mRels = pkg.GetXml("visio/masters/_rels/masters.xml.rels");
                var relMap = ParseRels(mRels);
                foreach (var m in mDoc.Root!.Elements().Where(e => e.Name.LocalName == "Master"))
                {
                    var id = (int?)m.Attribute("ID");
                    // 关联可能在 <Rel r:id='...'/> 子元素里（Visio 2013+ 常见），也可能在属性上
                    var relEl = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel");
                    var rid = (string?)relEl?.Attribute(RNS + "id") ?? (string?)m.Attribute(RNS + "id");
                    if (id == null || rid == null) continue;
                    if (!relMap.TryGetValue(rid, out var target)) continue;
                    var full = "visio/masters/" + target;
                    if (!pkg.HasPart(full)) continue;
                    var mdoc = pkg.GetXml(full);
                    // 记录 QT>=2 的母版 ID（待页面引用后再启用主题样式标记）
                    foreach (var qsh in mdoc.Descendants().Where(e => e.Name.LocalName == "Shape"))
                    {
                        var qt = CellInt(qsh, "QuickStyleType") ?? 0;
                        if (qt >= 2) { _qtMasterIds.Add(id.Value); break; }
                    }
                    var sr = mdoc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
                    if (sr == null) continue;
                    var top = sr.Elements().FirstOrDefault(e => e.Name.LocalName == "Shape");
                    var md = new MasterDef { ShapesRoot = sr };
                    var mdRels = "visio/masters/_rels/" + Path.GetFileName(full) + ".rels";
                    if (pkg.HasPart(mdRels)) md.Rels = ParseRels(pkg.GetXml(mdRels));
                    if (top != null)
                    {
                        md.Width = GetDim(top, "Width", null, 1);
                        md.Height = GetDim(top, "Height", null, 1);
                        md.LocPinX = GetDim(top, "LocPinX", null, md.Width / 2);
                        md.LocPinY = GetDim(top, "LocPinY", null, md.Height / 2);
                    }
                    else
                    {
                        md.Width = 1; md.Height = 1; md.LocPinX = 0.5; md.LocPinY = 0.5;
                    }
                    masters[id.Value] = md;
                    md.Id = id.Value;
                }
            }

            // 计算每个母版的真实内容包围盒（本地坐标），用于把内容缩放到实例尺寸。
            // 关键：真实 Visio 母版的 Width/Height 单元格常常与真实内容范围不一致
            //（如 CFF 容器 Width 仅 3.9mm，但内部图形跨越数百 mm），直接按
            // iW/mW 缩放会把内容放大几十上百倍导致坐标爆掉。
            foreach (var kv in masters)
                ComputeNaturalSize(kv.Value, masters);
        }

        // ---- 主题配色、样式表、文档调色板（用于形状未显式写颜色时的回退）----
        TryLoadTheme(pkg);
        TryInferThemeFill(pkg);
        TryLoadFaceNames(pkg);
        TryLoadDocStyles(pkg);

        // ---- 解析页面 ----
        if (!pkg.HasPart("visio/pages/pages.xml")) return scene;
        var pDoc = pkg.GetXml("visio/pages/pages.xml");
        var pRels = pkg.HasPart("visio/pages/_rels/pages.xml.rels")
            ? ParseRels(pkg.GetXml("visio/pages/_rels/pages.xml.rels"))
            : new Dictionary<string, string>();
        foreach (var p in pDoc.Root!.Elements().Where(e => e.Name.LocalName == "Page"))
        {
            var relEl = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel");
            var rid = (string?)relEl?.Attribute(RNS + "id") ?? (string?)p.Attribute(RNS + "id");
            var name = (string?)p.Attribute("Name") ?? (string?)p.Attribute("NameU") ?? "Page";
            if (rid == null || !pRels.TryGetValue(rid, out var ptarget)) continue;
            var full = "visio/pages/" + ptarget;
            if (!pkg.HasPart(full)) continue;
            var pdoc = pkg.GetXml(full);
            // 页面尺寸在 pages.xml 的 <Page><PageSheet> 里，不在 pageN.xml 里。
            // 必须在渲染/归一化之前设定，否则 NormalizePage 用的是默认 11×8.5。
            var sheet = p.Elements().FirstOrDefault(e => e.Name.LocalName == "PageSheet");
            double defW = sheet != null ? GetInchValue(sheet, "PageWidth", 11) : 11;
            double defH = sheet != null ? GetInchValue(sheet, "PageHeight", 8.5) : 8.5;
            // 页面的图片关系（Foreign 形状 → visio/media/*）
            _pageRels = pkg.HasPart("visio/pages/_rels/" + Path.GetFileName(full) + ".rels")
                ? ParseRels(pkg.GetXml("visio/pages/_rels/" + Path.GetFileName(full) + ".rels"))
                : null;
            _pkg = pkg;
            // 页面实际引用了 QT>=2 母版时才启用主题样式标记。
            // network_topology 的 master13 有 QT=2 但页面不引用，不能误启用。
            if (!_useThemeStyleFill && _qtMasterIds.Count > 0)
            {
                foreach (var ms in pdoc.Descendants().Where(e => e.Name.LocalName == "Shape"))
                {
                    var ma = (string?)ms.Attribute("Master");
                    if (ma != null && int.TryParse(ma, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) &&
                        _qtMasterIds.Contains(mid))
                    { _useThemeStyleFill = true; break; }
                }
            }
            var parsed = ParsePage(pdoc, name, masters, defW, defH);
            // 工程图（如办公室平面图、座位图）用 PageScale/DrawingScale 把绘图单位
            // 映射成英寸：PageWidth=840、PageScale=0.25in、DrawingScale=12in 实际是
            // 17.5in 的页面。不缩放会当成 840in，位图导出直接溢出。
            if (sheet != null)
            {
                double ps = GetInchValue(sheet, "PageScale", 0);
                double ds = GetInchValue(sheet, "DrawingScale", 0);
                if (ps > 0 && ds > 0 && Math.Abs(ds - ps) > 1e-9)
                    ApplyDrawingScale(parsed, ps / ds);
            }
            scene.Pages.Add(parsed);
        }

        if (scene.Pages.Count == 0)
            scene.Pages.Add(new PageScene { Name = "Page-1" });

        return scene;
    }

    /// <summary>
    /// 推断文档的"主题填充色"。Visio 把自定义主题色烘焙在具体形状的 THEMEGUARD(RGB(...)) 里，
    /// 而 THEMEVAL("FillColor") 这类单元格的缓存值是 'Themed'（无法还原）。
    /// 做法：统计所有 FillForegnd 里的 RGB 字面量，取"较深、且被重复使用"的那个作为主题填充色。
    /// 本文件里即 #1A3B88（泳道表头 / 开始-结束框的深藏青），比默认浅灰贴近原图。
    /// </summary>
    private static void TryInferThemeFill(VsdxPackage pkg)
    {
        try
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in pkg.PartNames)
            {
                if (!part.StartsWith("visio/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(part.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))) continue;
                if (part.Contains("theme", StringComparison.OrdinalIgnoreCase)) continue;
                XDocument doc;
                try { doc = pkg.GetXml(part); } catch { continue; }
                foreach (var cell in doc.Descendants())
                {
                    if (cell.Name.LocalName != "Cell") continue;
                    if ((string?)cell.Attribute("N") != "FillForegnd") continue;
                    var hex = ColorUtil.Parse((string?)cell.Attribute("F"));
                    if (hex == null) continue;
                    counts.TryGetValue(hex, out var n);
                    counts[hex] = n + 1;
                }
            }

            string? best = null;
            double bestLum = double.MaxValue;
            foreach (var kv in counts)
            {
                if (kv.Value < 2) continue;
                var lum = Luminance(kv.Key);
                if (lum < 25 || lum > 210) continue;   // 跳过近黑/近白（背景、纯黑描边）
                if (lum < bestLum) { bestLum = lum; best = kv.Key; }
            }
            if (best != null) ThemeFill = best;
        }
        catch { /* 推断失败保留默认 */ }
    }

    private static double Luminance(string hex)
    {
        if (hex.Length != 7) return 255;
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
    private static void TryLoadTheme(VsdxPackage pkg)    {
        try
        {
            if (!pkg.HasPart("visio/theme/theme1.xml")) return;
            var doc = pkg.GetXml("visio/theme/theme1.xml");
            string? dk1 = null, lt2 = null, dk2 = null, accent1 = null;
            for (int i = 0; i < ThemeAccents.Length; i++) ThemeAccents[i] = null!;
            foreach (var el in doc.Descendants())
            {
                var nm = el.Name.LocalName;
                if (nm != "dk1" && nm != "dk2" && nm != "lt2" &&
                    !(nm.StartsWith("accent", StringComparison.Ordinal) &&
                      nm.Length == 7 && char.IsDigit(nm[6]))) continue;
                var val = el.Descendants().Select(x => (string?)x.Attribute("val") ?? (string?)x.Attribute("lastClr")).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (val == null) continue;
                var hex = "#" + val!.TrimStart('#');
                if (nm == "dk1") dk1 = hex; else if (nm == "dk2") dk2 = hex;
                else if (nm == "lt2") lt2 = hex;
                else if (nm.StartsWith("accent", StringComparison.Ordinal) &&
                         int.TryParse(nm.Substring(6), out var ai) && ai >= 1 && ai <= 6)
                {
                    ThemeAccents[ai - 1] = hex;
                    if (ai == 1) accent1 = hex;
                }
            }
            if (lt2 != null) ThemeFill = lt2;
            else if (lt2 == null && dk1 != null) ThemeFill = "#E7E6E6";
            if (dk2 != null) ThemeLine = dk2;
            if (accent1 != null) ThemeAccent1 = accent1;
        }
        catch { /* 主题解析失败则用默认值 */ }
    }

    /// <summary>
    /// 字体表：document.xml 里的 &lt;FaceNames&gt;。Character.Font 通常是字体名字符串，
    /// 但个别文件存的是索引，需要这张表还原成名字。
    /// </summary>
    private static readonly List<string> FaceNames = new();

    private static void TryLoadFaceNames(VsdxPackage pkg)
    {
        FaceNames.Clear();
        try
        {
            if (!pkg.HasPart("visio/document.xml")) return;
            var doc = pkg.GetXml("visio/document.xml");
            foreach (var el in doc.Descendants())
            {
                if (el.Name.LocalName != "FaceName") continue;
                var nm = (string?)el.Attribute("Name");
                if (!string.IsNullOrEmpty(nm)) FaceNames.Add(nm!);
            }
        }
        catch { /* 字体表缺失时降级为默认字体 */ }
    }

    // 文档级样式表与自定义调色板。形状只写 LineStyle='9' 而颜色在样式里是很常见的
    //（如接线图的彩色导线），旧实现全部按默认黑色画，是"显示奇怪"的主因之一。
    private static readonly Dictionary<int, XElement> DocStyles = new();
    private static readonly Dictionary<int, string> DocColors = new();

    private static void TryLoadDocStyles(VsdxPackage pkg)
    {
        DocStyles.Clear(); DocColors.Clear();
        try
        {
            if (!pkg.HasPart("visio/document.xml")) return;
            var doc = pkg.GetXml("visio/document.xml");
            foreach (var c in doc.Descendants())
            {
                if (c.Name.LocalName != "ColorEntry") continue;
                var ix = (string?)c.Attribute("IX");
                var rgb = (string?)c.Attribute("RGB");
                if (int.TryParse(ix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                    !string.IsNullOrEmpty(rgb)) DocColors[n] = rgb!.ToUpperInvariant();
            }
            foreach (var s in doc.Descendants())
            {
                if (s.Name.LocalName != "StyleSheet") continue;
                var id = (string?)s.Attribute("ID");
                if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    DocStyles[n] = s;
            }
        }
        catch { /* 样式表缺失时保持空表 */ }
    }

    private static int? StyleInt(string? styleId, string cellName)
    {
        var v = StyleCellRaw(styleId, cellName);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static double? StyleDim(string? styleId, string cellName)
    {
        var v = StyleCellRaw(styleId, cellName);
        return double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>从文档样式表取单元格值，样式自身继承时沿 LineStyle/FillStyle 链回溯。</summary>
    private static string? StyleCellRaw(string? styleId, string cellName, int depth = 0)
    {
        if (styleId == null || depth > 6 ||
            !int.TryParse(styleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
            !DocStyles.TryGetValue(id, out var sheet)) return null;
        var cell = sheet.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == cellName);
        var v = (string?)cell?.Attribute("V");
        if (!string.IsNullOrEmpty(v)) return v;
        var parent = cellName.StartsWith("Line", StringComparison.Ordinal)
            ? (string?)sheet.Attribute("LineStyle")
            : cellName.StartsWith("Fill", StringComparison.Ordinal)
                ? (string?)sheet.Attribute("FillStyle")
                : (string?)sheet.Attribute("TextStyle");
        return parent == styleId ? null : StyleCellRaw(parent, cellName, depth + 1);
    }

    /// <summary>形状或其母版是否声明了 QuickStyleType&gt;=2（主题样式2D）。</summary>
    private static bool HasQuickStyle2(XElement el, XElement? inh)
    {
        int? qt = CellInt(el, "QuickStyleType") ?? (inh != null ? CellInt(inh, "QuickStyleType") : null);
        return qt != null && qt.Value >= 2;
    }

    /// <summary>
    /// 解析 "Themed" 颜色：当样式链声明了 QuickStyleFillColor 索引（100→accent1）时，
    /// "Themed" 的填充/线条色就是对应的主题强调色（rdf_sample 的 BOB 蓝底、连接线蓝色）。
    /// 没有 QFC 声明时保持原行为（返回 null 由调用方回退），避免误染其它文档。
    /// </summary>
    private static void ResolveThemedColor(XElement shape, XElement? inh,
        string? styleId, string cellName, Action<string> apply)
    {
        // 只在主题样式文档（存在 QT>=2 母版）里解析 "Themed"，
        // 否则 big_process_orgchart 等文档的连接线会被误染成主题蓝。
        if (!_useThemeStyleFill) return;
        // 已有显式色则跳过
        var existing = GetColor(shape, cellName, inh);
        if (existing != null) return;
        // 形状自带该颜色单元格（哪怕 V='Themed'）说明是显式声明，保持原行为；
        // 只对「纯样式链 Themed」的形状解析为 accent1，避免误染 network_topology 设备图标。
        if (shape.Elements().Any(c => c.Name.LocalName == "Cell" &&
                                      (string?)c.Attribute("N") == cellName)) return;
        var raw = StyleCellRaw(styleId, cellName);
        if (raw == null || !raw.TrimStart().Equals("Themed", StringComparison.OrdinalIgnoreCase)) return;
        // 找 QuickStyleFillColor 索引（沿样式链）。QFC=100 表示主题 accent1。
        string? qfc = StyleCellRaw(styleId, "QuickStyleFillColor");
        if (qfc == null && inh != null)
        {
            var ihStyle = (string?)inh.Attribute("FillStyle") ?? (string?)inh.Attribute("LineStyle");
            qfc = StyleCellRaw(ihStyle, "QuickStyleFillColor");
        }
        if (qfc == null || !int.TryParse(qfc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            return;
        int ai = idx - 100;
        if (ai >= 0 && ai < ThemeAccents.Length && ThemeAccents[ai] != null)
            apply(ThemeAccents[ai]);
    }

    /// <summary>
    /// 从样式链取颜色。默认只认 #RRGGBB——经典调色板索引（如 9/17/18）在老文件里
    /// 往往是"默认样式"占位值，直接采用会把本来无填充的图形刷成亮绿/亮黄
    ///（office_plan、db_schema 实测回退）。
    /// <paramref name="resolvePalette"/> 为 true 时额外解析调色板索引
    ///（图案填充 FillPattern≥2 的 FillForegnd/FillBkgnd 需要），
    /// 自定义色查 DocColors，经典色 0-23 委托 <see cref="ColorUtil.Parse"/>（与 GetColor 同源）。
    /// </summary>
    private static string? StyleColor(string? styleId, string cellName, bool resolvePalette = false)
    {
        var raw = StyleCellRaw(styleId, cellName);
        if (raw == null || raw.TrimStart().Length == 0) return null;
        if (raw.TrimStart()[0] == '#') return ColorUtil.Parse(raw);
        if (resolvePalette)
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) &&
                DocColors.TryGetValue(idx, out var custom)) return custom;
            return ColorUtil.Parse(raw);   // 经典索引 0-23
        }
        return null;
    }

    /// <summary>把 Character.Font 的 V 值还原成字体名（数字则查字体表）。</summary>
    private static string? ResolveFontName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw!.Trim();
        if (s.Equals("Themed", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ix))
            return ix >= 0 && ix < FaceNames.Count ? FaceNames[ix] : null;
        return s;
    }

    private static PageScene ParsePage(XDocument pdoc, string name, Dictionary<int, MasterDef> masters,
        double declaredW = 11, double declaredH = 8.5)
    {
        var page = new PageScene { Name = name, Width = declaredW, Height = declaredH };
        var root = pdoc.Root!;
        var pageSheet = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PageSheet");
        if (pageSheet != null)
        {
            // 少数文件把 PageSheet 放在 pageN.xml 里；V 是内部单位（英寸），U='MM' 只是显示单位。
            // 若按 U 换算（÷25.4），页面会变成 0.38in，进而 NormalizePage 把画布撑成内容包络。
            page.Width = GetInchValue(pageSheet, "PageWidth", declaredW);
            page.Height = GetInchValue(pageSheet, "PageHeight", declaredH);
        }

        var shapesRoot = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (shapesRoot != null)
        {
            // 先把本页每个形状的包围盒登记下来：连接线的 BeginX/EndX 公式里会引用
            // Sheet.N!Connections.Xk，需要知道 N 形状的几何才能判断连接点在哪条边上。
            PageShapeBox.Clear();
            foreach (var sh in shapesRoot.Elements().Where(e => e.Name.LocalName == "Shape"))
            {
                var sid = (string?)sh.Attribute("ID");
                if (sid == null || !int.TryParse(sid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidn)) continue;
                double sw = GetDim(sh, "Width", null, 0);
                double sH2 = GetDim(sh, "Height", null, 0);
                if (sw <= 0 || sH2 <= 0) continue;
                PageShapeBox[sidn] = (GetDim(sh, "PinX", null, 0), GetDim(sh, "PinY", null, 0), sw, sH2);
            }

            foreach (var sh in shapesRoot.Elements().Where(e => e.Name.LocalName == "Shape"))
                RenderShape(sh, Affine.Identity, masters, page, null, null);
        }

        NormalizePage(page);
        return page;
    }

    /// <summary>把整页（含页面尺寸、所有几何/文字/图片的坐标与尺寸）按 PageScale/DrawingScale
    /// 等比换算成真实英寸。NormalizePage 之后调用，居中效果保持不变。</summary>
    private static void ApplyDrawingScale(PageScene page, double k)
    {
        if (Math.Abs(k - 1) < 1e-9) return;
        page.Width *= k;
        page.Height *= k;
        foreach (var d in page.Drawables)
        {
            if (d is PathDrawable p)
            {
                foreach (var c in p.Commands)
                {
                    switch (c)
                    {
                        case _Move m: m.X *= k; m.Y *= k; break;
                        case _Line l: l.X *= k; l.Y *= k; break;
                        case _Arc a:
                            a.X *= k; a.Y *= k; a.Rx *= k; a.Ry *= k; break;
                        case _Quad q:
                            q.Cx *= k; q.Cy *= k; q.X *= k; q.Y *= k; break;
                        case _Cubic cu:
                            cu.C1x *= k; cu.C1y *= k; cu.C2x *= k; cu.C2y *= k;
                            cu.X *= k; cu.Y *= k; break;
                        case _Ellipse e:
                            e.X *= k; e.Y *= k; e.Rx *= k; e.Ry *= k; break;
                    }
                }
                // LineWeight/字号是物理尺寸（英寸/磅），不随绘图比例缩放；
                // 只有形状坐标、几何尺寸、图片框才是绘图单位。
            }
            else if (d is TextDrawable t)
            {
                t.CenterX *= k; t.CenterY *= k;
                if (t.MaxWidth.HasValue) t.MaxWidth *= k;
                if (t.BoxLeft.HasValue) t.BoxLeft *= k;
                if (t.BoxTop.HasValue) t.BoxTop *= k;
                if (t.BoxWidth.HasValue) t.BoxWidth *= k;
                if (t.BoxHeight.HasValue) t.BoxHeight *= k;
            }
            else if (d is ImageDrawable im)
            {
                im.X *= k; im.Y *= k; im.W *= k; im.H *= k;
            }
            d.MinX *= k; d.MinY *= k; d.MaxX *= k; d.MaxY *= k;
        }
        if (Trace)
            Console.Error.WriteLine($"[scale] drawing-scale={k:0.####} page={page.Width:0.###}x{page.Height:0.###}in");
    }

    /// <summary>
    /// 规整页面：页面尺寸取「文件声明尺寸」与「内容包围盒 + 边距」的较大者，
    /// 再把内容包围盒居中放进画布。
    ///
    /// 旧实现是把包围盒左上角平移到原点（内容永远贴左上）。对只占页面中间一小块的图
    /// （如 1_v2.vsdx：A4 横向页面里只画了三组示意图）就会看到图形挤在左上角、右下方
    /// 一大片空白。居中后既保留原始页面尺寸（"整页导出"与"仅内容导出"仍有区别），
    /// 也让图形落在画布中央。
    /// </summary>
    private static void NormalizePage(PageScene page)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var d in page.Drawables)
        {
            if (!d.HasBounds) continue;
            if (d.MinX < minX) minX = d.MinX;
            if (d.MinY < minY) minY = d.MinY;
            if (d.MaxX > maxX) maxX = d.MaxX;
            if (d.MaxY > maxY) maxY = d.MaxY;
            any = true;
        }
        if (!any) return;

        const double margin = 0.25;   // 内容超出声明页面时至少留的边距（英寸）
        double cw = maxX - minX, ch = maxY - minY;
        double pw = Math.Max(page.Width, cw + margin * 2);
        double ph = Math.Max(page.Height, ch + margin * 2);
        // 目标：内容包围盒的中心落在画布中心
        double ox = (pw - cw) / 2 - minX;
        double oy = (ph - ch) / 2 - minY;
        if (Trace)
            Console.Error.WriteLine($"[page] content=({minX:0.###},{minY:0.###})-({maxX:0.###},{maxY:0.###}) " +
                                    $"size=({cw:0.###},{ch:0.###}) declared=({page.Width:0.###},{page.Height:0.###}) " +
                                    $"final=({pw:0.###},{ph:0.###}) shift=({ox:0.###},{oy:0.###})");

        if (Math.Abs(ox) < 1e-9 && Math.Abs(oy) < 1e-9)
        {
            page.Width = pw; page.Height = ph;
            return;
        }

        foreach (var d in page.Drawables)
        {
            if (d is PathDrawable p)
            {
                foreach (var c in p.Commands)
                {
                    switch (c)
                    {
                        case _Move m: m.X += ox; m.Y += oy; break;
                        case _Line l: l.X += ox; l.Y += oy; break;
                        case _Arc a: a.X += ox; a.Y += oy; break;
                        case _Quad q: q.Cx += ox; q.Cy += oy; q.X += ox; q.Y += oy; break;
                        case _Cubic cu:
                            cu.C1x += ox; cu.C1y += oy; cu.C2x += ox; cu.C2y += oy;
                            cu.X += ox; cu.Y += oy; break;
                        case _Ellipse e: e.X += ox; e.Y += oy; break;
                    }
                }
            }
            else if (d is TextDrawable t)
            {
                t.CenterX += ox; t.CenterY += oy;
                if (t.BoxLeft.HasValue) t.BoxLeft += ox;
                if (t.BoxTop.HasValue) t.BoxTop += oy;
            }
            else if (d is ImageDrawable im)
            {
                im.X += ox; im.Y += oy;
            }
            if (d.HasBounds)
            {
                d.MinX += ox; d.MinY += oy; d.MaxX += ox; d.MaxY += oy;
            }
        }
        page.Width = pw;
        page.Height = ph;
    }

    /// <summary>
    /// 渲染一个形状。masterId 为"当前分组实例所属母版"，用于解析子形状的 MasterShape。
    /// </summary>
    private static void RenderShape(
        XElement shape, Affine parentM, Dictionary<int, MasterDef> masters,
        PageScene page, Style? inherited, int? masterId,
        IReadOnlyDictionary<string, string>? textOverrides = null,
        IReadOnlyDictionary<string, XElement>? fieldOverrides = null,
        IReadOnlyDictionary<string, XElement>? geomOverrides = null,
        double scaleSx = 1, double scaleSy = 1,
        IReadOnlyDictionary<string, XElement>? charOverrides = null,
        IReadOnlyDictionary<string, XElement>? dimOverrides = null)
    {
        // ---- 嵌入图片（Foreign 形状，如组织图里的照片）----
        if ((string?)shape.Attribute("Type") == "Foreign")
        {
            // 这类实例往往只写 PinY 和 ForeignData/Rel，Width/Height/PinX 全靠
            // MasterShape=K 继承。不传 inh 会得到 W=H=0，图片被渲染端静默丢弃。
            XElement? imgInh = null;
            IReadOnlyDictionary<string, string>? imgRels = _pageRels;
            var foreignMaster = (string?)shape.Attribute("MasterShape");
            if (foreignMaster != null && TryResolveMasterShape(masters, masterId, foreignMaster, out var fmId, out var fmEl))
            {
                imgInh = fmEl;
                if (masters.TryGetValue(fmId, out var fmDef) && fmDef.Rels != null) imgRels = fmDef.Rels;
            }
            RenderForeignImage(shape, parentM, page, imgInh, imgRels);
            return;
        }

        if (Trace)
            Console.Error.WriteLine($"[shape] ID={shape.Attribute("ID")?.Value} NameU={shape.Attribute("NameU")?.Value} " +
                                    $"Master={shape.Attribute("Master")?.Value ?? "-"} MasterShape={shape.Attribute("MasterShape")?.Value ?? "-"} " +
                                    $"FillStyle={shape.Attribute("FillStyle")?.Value ?? "-"} LineStyle={shape.Attribute("LineStyle")?.Value ?? "-"}");

        // ---- 母版实例：Master="M" ----
        var masterAttr = (string?)shape.Attribute("Master");
        if (masterAttr != null && int.TryParse(masterAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) && masters.TryGetValue(mid, out var mdef))
        {
            var mrootEl = mdef.ShapesRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Shape");
            // 实例可带几何覆盖（子形状用自己的几何重写母版几何，如弧形墙）。
            // 先建好这张表：有几何时绝不能走「连接线」分支画直线。
            var kidGeom = BuildGeometryOverrides(shape);
            var kidFields = BuildFieldOverrides(shape);
            var kidChars = BuildCharOverrides(shape);
            var kidDims = BuildDimensionOverrides(shape);
            // 动态连接线（1-D 形状）：看到 Begin/End 就直接连两端点，别用母版里的 "⊔" 占位几何。
            // 但 1-D 形状也可能自带真实几何（弧形墙的实例子形状带完整弧线），
            // 那种情况画成直线会把弧线丢掉，必须继续走几何渲染。
            if (kidGeom == null)
            {
                var cstyle0 = ReadStyle(shape, inherited, mrootEl);
                // 连接线的 "Themed" 线色解析为主题 accent1（rdf_sample 的蓝色连接线）。
                ResolveThemedColor(shape, mrootEl, (string?)shape.Attribute("LineStyle") ?? (string?)mrootEl?.Attribute("LineStyle"),
                    "LineColor", v => cstyle0.LineColor = v);
                if (TryRenderConnector(shape, mrootEl, parentM, cstyle0, page)) return;
            }

            double iW = GetDim(shape, "Width", null, mdef.Width);
            double iH = GetDim(shape, "Height", null, mdef.Height);
            double pinX = GetDim(shape, "PinX", null, 0);
            double pinY = GetDim(shape, "PinY", null, 0);
            double ang = GetDim(shape, "Angle", null, 0);
            // 标准 Visio 变换：母版几何以母版本地坐标绘制，实例按 iW/Wm, iH/Hm 缩放，
            // 围绕母版自身 LocPin(Width/2,Height/2) 定位。切勿用"内容中心"作为 locPin，
            // 否则几何离标称框越远，偏移被 sy 放大越严重（坐标爆炸到上千英寸）。
            double mlx = GetDim(shape, "LocPinX", null, mdef.LocPinX);
            double mly = GetDim(shape, "LocPinY", null, mdef.LocPinY);
            double sx = mdef.Width > 1e-9 ? iW / mdef.Width : 1;
            double sy = mdef.Height > 1e-9 ? iH / mdef.Height : 1;
            var mInst = Affine.MakePlacement(pinX, pinY, mlx, mly, ang, sx, sy,
                FlipSign(shape, "FlipX"), FlipSign(shape, "FlipY"));
            var M = Affine.Compose(parentM, mInst);
            if (Trace)
                Console.Error.WriteLine($"  [inst] ID={shape.Attribute("ID")?.Value} master={mid} " +
                                        $"iW={iW:0.###} iH={iH:0.###} mW={mdef.Width:0.###} mH={mdef.Height:0.###} " +
                                        $"sx={sx:0.###} sy={sy:0.###}");

            var instStyle = ReadStyle(shape, inherited, null);
            // 实例没写的字体/字号/颜色要继承母版根形状的 Character 段，
            // 否则「流程动作」这类母版设定的 10pt 会被默认值顶掉，文字被撑大后换行溢出。
            if (mrootEl != null) instStyle = instStyle.MergeText(ReadStyle(mrootEl, null, null));
            // QuickStyle：容量块等形状自身不写填充色，颜色由母版 QuickStyleType>=2
            // 指向主题 accent（ms_diagrams 的块=accent1 #1BA1E2、rdf_sample 的 BOB）。
            // 只在实例完全没给填充色、也没渐变时套用；池形状有 FillGradient 段不会被误涂。
            // 门控用本母版的 QT（而非文档级标记），避免 network_topology 等文档被误染。
            if (mrootEl != null && HasQuickStyle2(mrootEl, null) &&
                instStyle.FillForegnd == null && instStyle.GradientStops == null)
            {
                var accent = QuickStyleAccentColor(shape, mrootEl);
                if (accent != null)
                {
                    instStyle.FillForegnd = accent;
                    // QuickStyleVariation 的 bit2=2 表示「必要时把文字色改为与主题背景
                    // 对比的颜色」——accent 底上的白字。ms_diagrams 容量块正是这种。
                    // 该值也可能写在 FillStyle 链上的文档样式表里（本例样式表 2）。
                    var fillStyleId = (string?)mrootEl.Attribute("FillStyle")
                                      ?? (string?)shape.Attribute("FillStyle");
                    int qv = CellInt(mrootEl, "QuickStyleVariation") ?? 0;
                    if (qv == 0 && fillStyleId != null)
                    {
                        var qvRaw = StyleCellRaw(fillStyleId, "QuickStyleVariation");
                        if (qvRaw != null && int.TryParse(qvRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qvi))
                            qv = qvi;
                    }
                    // accent 底（如 #1BA1E2）按 Visio 对比规则用白字；不依赖 QV 是否
                    // 恰好写进样式链。主题强调色通常较深，Luminance<170 时取白字。
                    bool darkEnough = false;
                    try { darkEnough = Luminance(accent) < 170; } catch { }
                    if ((qv & 2) != 0 || darkEnough) instStyle.TextColor ??= "#FFFFFF";
                    if (Trace)
                        Console.Error.WriteLine($"  [quickstyle] ID={shape.Attribute("ID")?.Value} accent={accent} qv={qv} text={instStyle.TextColor ?? "-"}");
                }
            }
            // QuickStyle 主题样式形状若显式给了 accent 色填充（如 rdf_sample 的橙色
            // Q12418），文字也按对比规则用白字。
            else if (mrootEl != null && HasQuickStyle2(mrootEl, null) &&
                     instStyle.FillForegnd != null && instStyle.TextColor == null)
            {
                bool darkEnough = false;
                try { darkEnough = Luminance(instStyle.FillForegnd!) < 170; } catch { }
                if (darkEnough) instStyle.TextColor = "#FFFFFF";
            }
            // 实例可以把母版里的某个子形状"重写"（MasterShape=K）：几何通常沿用母版，
            // 变的多数是文字（例如泳道母版写的是"部门"占位，实例里换成"文件编制部门"）。
            // 这里只接管文字覆盖，几何仍走母版 —— 直接渲染实例子形状会因为它的 Pin 用的是
            // 实例本地坐标而与母版缩放叠加，整页坐标会炸到几十英寸外。
            var kidText = BuildTextOverrides(shape);
            // 页面实例里的 Foreign 子形状带着各自的图片 rId（母版里只有占位图）。
            var kidImages = BuildImageOverrides(shape);

            // 关键：母版根形状自身的 Pin/LocPin 只是作者在母版画布上的摆放位置，实例化时
            // 由实例变换取代，绝不能再叠加一遍——否则根形状的 Pin（可能距原点很远，
            // 如 PinY=12）会被实例缩放(y 方向可能放大数十倍)乘出上千英寸的坐标爆炸。
            // 容器/泳道的标题条要贴着这个实例的顶边（Visio 里标题条不随容器拉高而变厚）。
            var prevTop = _headingTopScreen;
            _headingTopScreen = page.Height - (pinY + iH / 2);

            // 实例自带完整几何时，优先用实例几何 + 抵消掉母版缩放。
            //
            // Visio 会把实例化后的几何按「实例本地坐标」缓存进实例的 V 值，而母版几何是
            // 「母版本地坐标」、靠 M 里的 sx/sy 缩放。对「只依赖 Width 的偏移量」两者结果不同：
            // 圆角矩形的圆角半径来自 Width*0.1 —— 母版里是 0.1575，乘 sy=0.415 后只剩 0.0654，
            // 于是正圆角被压成扁椭圆（肉眼就是 45° 斜切）；而实例缓存里 X、Y 两个方向都是
            // 0.1258，正是 Visio 显示的正圆。用实例几何并把缩放抵消（M∘Scale(1/sx,1/sy)），
            // 本地坐标重新按实例英寸计算，圆角恢复成正圆，其余比例几何结果不变。
            bool usedInstGeom = false;
            if (mrootEl != null && sx > 1e-9 && sy > 1e-9 &&
                !IsHeadingShape(mrootEl) && !HasHeadingChild(mdef) &&
                mrootEl.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes") == null &&
                HasCompleteInstanceGeometry(shape, mrootEl))
            {
                var MNoScale = Affine.Compose(M, LocalScaleInverse(shape, sx, sy));
                RenderGeometry(shape, MNoScale, instStyle, page, mrootEl);
                usedInstGeom = true;
                if (Trace)
                    Console.Error.WriteLine($"  [geom-src] ID={shape.Attribute("ID")?.Value} 用实例几何（抵消 sx/sy）");
            }
            // 母版实例里可以挂「母版没有定义」的额外子形状（没有 MasterShape），
            // 典型如 network_topology 的 Legend 数据行。旧实现只画母版内容，
            // 这些行全部丢失。子形状的 Pin/W 是实例本地坐标，用 M∘Scale(1/sx,1/sy) 映射。
            // 先画行，再画母版的边框/表头/底注文字，保证文字在最上层。
            var extraShapes = shape.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Shapes")?
                .Elements().Where(e => e.Name.LocalName == "Shape" &&
                                       e.Attribute("MasterShape") == null);
            if (extraShapes != null)
            {
                var MInst = Affine.Compose(M, LocalScaleInverse(shape, sx, sy));
                foreach (var extra in extraShapes)
                    RenderShape(extra, MInst, masters, page, instStyle, null);
            }

            if (!usedInstGeom)
            {
                foreach (var child in mdef.ShapesRoot.Elements().Where(e => e.Name.LocalName == "Shape"))
                    RenderMasterContent(child, M, masters, page, instStyle, mid, kidText,
                        isRoot: child == mrootEl, instanceStyle: instStyle, imageOverrides: kidImages,
                        fieldOverrides: kidFields, geomOverrides: kidGeom, scaleSx: sx, scaleSy: sy,
                        charOverrides: kidChars, dimOverrides: kidDims);
            }
            _headingTopScreen = prevTop;

            var mroot = mdef.ShapesRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Shape");
            // 容器/泳道的标题文字也放在"未纵向拉伸"的坐标系里，才会落在标题条中间，
            // 否则会被拉伸后的 TxtPinY 顶到条外。
            var MText = HasHeadingChild(mdef) ? new Affine(M.A, M.B, M.C, Math.Sign(M.D), M.E, M.F) : M;
            RenderText(shape, MText, mroot, instStyle, page);
            return;
        }

        // ---- 母版子形状实例：MasterShape="K" ----
        var msAttr = (string?)shape.Attribute("MasterShape");
        if (msAttr != null && TryResolveMasterShape(masters, masterId, msAttr, out var kmid, out var kEl) && kEl != null)
        {
            var kstyle0 = ReadStyle(shape, inherited, kEl);
            if (TryRenderConnector(shape, kEl, parentM, kstyle0, page)) return;

            double kW = GetDim(kEl, "Width", null, 1);
            double kH = GetDim(kEl, "Height", null, 1);
            // 标准变换：K 的几何以 K 本地坐标绘制，实例按 iW/kW, iH/kH 缩放，
            // 围绕 K 自身 LocPin 定位。
            double lkx = GetDim(kEl, "LocPinX", null, kW / 2);
            double lky = GetDim(kEl, "LocPinY", null, kH / 2);
            // 实例参数来自本形状；Inh / 缺失时回退到 K
            double iW = GetDim(shape, "Width", kEl, kW);
            double iH = GetDim(shape, "Height", kEl, kH);
            double pinX = GetDim(shape, "PinX", kEl, 0);
            double pinY = GetDim(shape, "PinY", kEl, 0);
            double ang = GetDim(shape, "Angle", kEl, 0);
            double sx = kW > 1e-9 ? iW / kW : 1;
            double sy = kH > 1e-9 ? iH / kH : 1;
            var mInst = Affine.MakePlacement(pinX, pinY, lkx, lky, ang, sx, sy,
                FlipSign(shape, "FlipX"), FlipSign(shape, "FlipY"));
            var M = Affine.Compose(parentM, mInst);

            var style = ReadStyle(shape, inherited, kEl);
            // 实例几何优先，但实例行里没写的单元格（如 EllipticalArcTo 的 B）要从 K 继承，
            // 否则会退化成默认值把弧画错。整段几何都没有时才完全用 K 的几何。
            RenderGeometry(shape, M, style, page, kEl);
            RenderText(shape, M, kEl, style, page);
            return;
        }

        // ---- 自身形状（页面直接定义，或分组） ----
        // 母版子形状的尺寸可由实例缓存值改写（db_schema 的表头/正文高度就是典型）。
        // 实例缓存是实例本地英寸，这里先除掉母版缩放，回到当前本地坐标系。
        var effShape = ApplyDimensionOverride(shape, dimOverrides, scaleSx, scaleSy);
        double w = GetDim(effShape, "Width", null, 0);
        double h = GetDim(effShape, "Height", null, 0);
        double lx = GetDim(effShape, "LocPinX", null, w / 2);
        double ly = GetDim(effShape, "LocPinY", null, h / 2);
        double px = GetDim(effShape, "PinX", null, 0);
        double py = GetDim(effShape, "PinY", null, 0);
        double a = GetDim(effShape, "Angle", null, 0);
        var mLocal = Affine.MakePlacement(px, py, lx, ly, a, 1, 1,
            FlipSign(effShape, "FlipX"), FlipSign(effShape, "FlipY"));
        var M2 = Affine.Compose(parentM, mLocal);

        var childShapes = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (childShapes != null)
        {
            // Group 自身也可以带几何（timeline 的事件色块：Group 一个彩色矩形，
            // 里面装文字）。旧代码递归完子形状就 return，色块矩形全丢了 ——
            // 整个 timeline 只剩文字，看不到任何事件色块。
            // 顺序与 RenderMasterContent 一致：先画 Group 自身几何（背景），
            // 再叠子形状，最后画文字。
            var gStyle = ReadStyle(shape, inherited, null);
            RenderGeometry(shape, M2, gStyle, page);
            foreach (var ch in childShapes.Elements().Where(e => e.Name.LocalName == "Shape"))
                RenderShape(ch, M2, masters, page, inherited, masterId, textOverrides,
                    fieldOverrides, geomOverrides, scaleSx, scaleSy, charOverrides, dimOverrides);
            // Group 本身也可以带文字（组织图的姓名条：'Jossef Goldberg' 挂在
            // MasterShape=6 的 Group 上）。旧代码递归完子形状就 return，
            // 把这类文字全丢了 —— 只画出职位、画不出姓名。
            string? gOv = null;
            var gId = (string?)shape.Attribute("ID");
            if (textOverrides != null && gId != null) textOverrides.TryGetValue(gId, out gOv);
            XElement? gF = null;
            if (fieldOverrides != null && gId != null) fieldOverrides.TryGetValue(gId, out gF);
            XElement? gC = null;
            if (charOverrides != null && gId != null) charOverrides.TryGetValue(gId, out gC);
            RenderText(shape, M2, null, gStyle, page, gOv, gF, gC);
            return;
        }

        var style2 = ReadStyle(shape, inherited, null);
        // 纯页面形状（无 Master/MasterShape）的 "Themed" 填充/线条色：
        // 按 QuickStyleFillColor 索引解析为主题 accent1（rdf_sample 的 subject 蓝底、
        // 连接线蓝色）。只在此路径处理，不影响母版子形状（network_topology 设备图标）。
        if (masterId == null &&
            (string?)shape.Attribute("Master") == null &&
            (string?)shape.Attribute("MasterShape") == null)
        {
            ResolveThemedColor(shape, null, (string?)shape.Attribute("FillStyle"),
                "FillForegnd", v => style2.FillForegnd = v);
            ResolveThemedColor(shape, null, (string?)shape.Attribute("LineStyle"),
                "LineColor", v => style2.LineColor = v);
        }
        // 母版子形状的 FillForegnd 公式含 THEMEVAL 时，颜色随实例配色而变；
        // 母版缓存的 #638bad 只是默认主题色，父级（已按实例覆盖）才是正确结果。
        // 同样只在 V 是字面 hex（主题色缓存）时覆盖，确定色（V='1' 白）保留。
        if (inherited != null)
        {
            string? fill = CellFormulaContains(shape, "FillForegnd", "THEMEVAL") && IsLiteralHexCell(shape, "FillForegnd")
                ? inherited.FillForegnd : null;
            string? line = CellFormulaContains(shape, "LineColor", "THEMEVAL") && IsLiteralHexCell(shape, "LineColor")
                ? inherited.LineColor : null;
            if (fill != null || line != null) style2 = style2.WithColors(fill, line);
        }
        if (TryRenderConnector(shape, null, parentM, style2, page)) return;

        // 容器/泳道的标题条：几何与标题文字都不做纵向拉伸，然后整体贴到容器顶边。
        bool heading = IsHeadingShape(shape);
        var Mh = heading && Math.Abs(M2.D) > 1e-9
            ? new Affine(M2.A, M2.B, M2.C, Math.Sign(M2.D), M2.E, M2.F)
            : M2;
        int startIdx = page.Drawables.Count;

        // 实例子形状改写了几何（如弧形墙）：用实例几何并抵消母版缩放，
        // 否则按实例宽度求值的坐标会被 M 的 sx 再放大一次。
        XElement? geomOv = null;
        var selfId0 = (string?)shape.Attribute("ID");
        if (geomOverrides != null && selfId0 != null) geomOverrides.TryGetValue(selfId0, out geomOv);
        if (SuppressInstanceOverrides(effShape)) geomOv = null;
        if (geomOv != null && scaleSx > 1e-9 && scaleSy > 1e-9 && HasCompleteInstanceGeometry(geomOv!, effShape))
        {
            var MNoScale = Affine.Compose(Mh, LocalScaleInverse(effShape, scaleSx, scaleSy));
            RenderGeometry(geomOv!, MNoScale, style2, page, effShape);
            if (Trace)
                Console.Error.WriteLine($"  [geom-ov] ID={selfId0} 用实例几何（抵消 {scaleSx:0.###}x{scaleSy:0.###}）");
        }
        else RenderGeometry(effShape, Mh, style2, page);
        // 母版子形状：若实例重写了文字，这里换上实例的文字
        string? selfText = null;
        var selfId = (string?)shape.Attribute("ID");
        if (textOverrides != null && selfId != null) textOverrides.TryGetValue(selfId, out selfText);
        XElement? fSrc0 = null;
        if (fieldOverrides != null && selfId0 != null) fieldOverrides.TryGetValue(selfId0, out fSrc0);
        XElement? cSrc0 = null;
        if (charOverrides != null && selfId0 != null) charOverrides.TryGetValue(selfId0, out cSrc0);
        XElement? tabSrc = null;
        if (dimOverrides != null && selfId0 != null) dimOverrides.TryGetValue(selfId0, out tabSrc);
        RenderText(effShape, Mh, null, style2, page, selfText, fSrc0, cSrc0, tabSrc);

        if (heading && _headingTopScreen.HasValue)
            AlignTop(page, startIdx, _headingTopScreen.Value);
    }

    /// <summary>把从 startIdx 起新增的图形（含文字）整体上移到 top 位置。</summary>
    private static void AlignTop(PageScene page, int startIdx, double top)
    {
        double cur = double.MaxValue;
        for (int i = startIdx; i < page.Drawables.Count; i++)
            if (page.Drawables[i].HasBounds && page.Drawables[i].MinY < cur)
                cur = page.Drawables[i].MinY;
        if (cur == double.MaxValue) return;
        double dy = top - cur;
        if (Math.Abs(dy) < 1e-6) return;
        for (int i = startIdx; i < page.Drawables.Count; i++)
            ShiftDrawableY(page.Drawables[i], dy);
    }

    /// <summary>嵌入图片形状：ForeignData/Rel → visio/media/*，按形状框（Pin/LocPin/Width/Height）放置。
    /// inh 为 MasterShape 定义（实例缺失的尺寸从它继承）；rels 为该 Rel 所在部件的关系表。</summary>
    private static void RenderForeignImage(XElement shape, Affine parentM, PageScene page,
        XElement? inh = null, IReadOnlyDictionary<string, string>? rels = null)
    {
        // 实例自带 ForeignData 时 rId 对页面 rels；否则取母版定义的 rId 对母版 rels。
        string? target = null;
        var fd = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "ForeignData");
        var rid = (string?)fd?.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel")?.Attribute(RNS + "id");
        if (rid != null && rels != null) rels.TryGetValue(rid, out target);
        if (target == null && inh != null)
        {
            var mfd = inh.Elements().FirstOrDefault(e => e.Name.LocalName == "ForeignData");
            var mrid = (string?)mfd?.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel")?.Attribute(RNS + "id");
            if (mrid != null && rels != null) rels.TryGetValue(mrid, out target);
        }
        if (target == null) return;

        var part = ResolvePartPath(target);
        if (part == null) return;
        if (!_imgCache.TryGetValue(part, out var data))
        {
            data = _pkg?.ReadBytes(part);
            _imgCache[part] = data;
        }
        if (data == null || data.Length == 0) return;

        double w = GetDimPreferCache(shape, "Width", inh, 0);
        double h = GetDimPreferCache(shape, "Height", inh, 0);
        double lx = GetDimPreferCache(shape, "LocPinX", inh, w / 2);
        double ly = GetDimPreferCache(shape, "LocPinY", inh, h / 2);
        double px = GetDimPreferCache(shape, "PinX", inh, 0);
        double py = GetDimPreferCache(shape, "PinY", inh, 0);
        double a = GetDimPreferCache(shape, "Angle", inh, 0);
        var M = Affine.Compose(parentM, Affine.MakePlacement(px, py, lx, ly, a, 1, 1,
            FlipSign(shape, "FlipX"), FlipSign(shape, "FlipY")));

        // 取四角包围盒（本场景图片均不旋转，等价于直接矩形）
        double minX = double.MaxValue, minY = double.MaxValue, maxX = -double.MaxValue, maxY = -double.MaxValue;
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), (w, h) })
        {
            var p = M.Apply(cx, cy);
            double sx = p.X, sy = page.Height - p.Y;
            if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
        }

        if (Trace)
        {
            var iw = inh != null ? CellRaw(inh, "Width") ?? "-" : "-";
            var dw = inh != null ? GetDim(inh, "Width", null, -1).ToString("0.###", CultureInfo.InvariantCulture) : "-";
            Console.Error.WriteLine($"  [image] ID={shape.Attribute("ID")?.Value} inheritedID={inh?.Attribute("ID")?.Value ?? "-"} " +
                                    $"cellW={CellRaw(shape, "Width") ?? "-"}/{iw} dimW={dw} " +
                                    $"part={part} w={w:0.###} h={h:0.###} " +
                                    $"box={maxX - minX:0.###}x{maxY - minY:0.###} at=({minX:0.###},{minY:0.###})");
        }

        var img = new ImageDrawable
        {
            Data = data,
            Mime = part.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   part.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png",
            X = minX,
            Y = minY,
            W = maxX - minX,
            H = maxY - minY,
        };
        img.Include(minX, minY);
        img.Include(maxX, maxY);
        page.Drawables.Add(img);
    }

    private static string? ResolvePartPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var t = target.Replace('\\', '/');
        if (t.StartsWith("/")) return t.TrimStart('/');
        int mi = t.IndexOf("media/", StringComparison.OrdinalIgnoreCase);
        if (mi >= 0) return "visio/" + t.Substring(mi);
        return "visio/pages/" + t;
    }

    /// <summary>
    /// 1-D 连接线（动态连接线）渲染：按 Begin/End 两端点画直线，并按 EndArrow 补箭头。
    /// 这类形状在母版里的 Geometry 只是"⊔"占位折线（端点处两个小竖线 + 一条横线），
    /// 直接按几何绘制就会得到一串横杠/挂钩——这也是"所有连接线都不对"的根因。
    /// 返回 true 表示已按连接线处理，调用方不必再走几何渲染。
    /// </summary>
    /// <summary>从 BeginX/EndX 的公式（PAR(PNT(Sheet.275!Connections.X1,...))）里取出参照形状的包围盒。</summary>
    private static (double Cx, double Cy, double W, double H)? LookupShapeBox(XElement shape, string cellName)
    {
        var cell = shape.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == cellName);
        var f = (string?)cell?.Attribute("F");
        if (f == null) return null;
        var m = Regex.Match(f, @"Sheet\.(\d+)!");
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
               && PageShapeBox.TryGetValue(id, out var box) ? box : null;
    }

    /// <summary>连接点是否落在参照形状的上下边（而不是左右边）。</summary>
    private static bool IsVerticalSide(double px, double py, (double Cx, double Cy, double W, double H) box)
    {
        double dTB = Math.Abs(Math.Abs(py - box.Cy) - box.H / 2);
        double dLR = Math.Abs(Math.Abs(px - box.Cx) - box.W / 2);
        return dTB <= dLR;
    }

    // ---------------- NURBS 曲线连接线 ----------------

    /// <summary>
    /// 解析 NURBSTo 行 E 单元格的 NURBS(...) 函数，还原控制点（本形状本地坐标，英寸）。
    /// 形式：NURBS(knot, degree, xType, yType, x1,y1,z1,w1, x2,y2,z2,w2, ...)
    /// xType/yType = 0 表示坐标按 Width/Height 归一化（0..1），非 0 表示已是本地英寸。
    /// 实测 1_v2.vsdx：宽度为正的连接线 x 归一化、宽度为负（翻转）的 x 为绝对值；
    /// y 一律归一化，正好解释了 y 值随 Height 反比变化但绝对高度恒为 3.75mm 的现象。
    /// </summary>
    private static List<(double X, double Y)> ParseNurbsPoints(XElement row, double w, double h)
    {
        var res = new List<(double X, double Y)>();
        var cell = row.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "E");
        var raw = (string?)cell?.Attribute("V") ?? (string?)cell?.Attribute("F");
        if (string.IsNullOrEmpty(raw)) return res;

        var nums = new List<double>();
        foreach (Match m in Regex.Matches(raw!, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"))
            if (double.TryParse(m.Value, NF, CultureInfo.InvariantCulture, out var d)) nums.Add(d);
        if (nums.Count < 8) return res;      // 头部 4 个 + 至少 1 组 4 个

        double xType = nums[2], yType = nums[3];
        for (int i = 4; i + 3 < nums.Count; i += 4)
        {
            double x = nums[i], y = nums[i + 1];
            if (Math.Abs(xType) < 0.5) x *= w;
            if (Math.Abs(yType) < 0.5) y *= h;
            res.Add((x, y));
        }
        return res;
    }

    /// <summary>
    /// 曲线连接线渲染。Visio 的 1-D 形状 Geometry 以 Begin 点为本地原点，
    /// (Width, Height) 恰好对应 End 点，因此本地坐标 → 页面坐标只需平移 Begin。
    /// 用 NURBS 控制点拟合一条三次贝塞尔（端点严格落在 End 上），再按曲线末端切线补箭头。
    /// 返回 false 表示不是曲线连接线，调用方继续走普通连接线逻辑。
    /// </summary>
    private static bool TryRenderNurbsConnector(
        XElement shape, XElement? inh, Affine parentM, Style style, PageScene page)
    {
        double? bx = GetDimNullable(shape, "BeginX", inh);
        double? by = GetDimNullable(shape, "BeginY", inh);
        if (bx == null || by == null) return false;

        double w = GetDim(shape, "Width", inh, 0);
        double h = GetDim(shape, "Height", inh, 0);

        foreach (var sec in shape.Elements().Where(e =>
                     e.Name.LocalName == "Section" &&
                     ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal)))
        {
            // NoShow=1 的几何段不画
            if (sec.Elements().Any(c => c.Name.LocalName == "Cell" &&
                                        (string?)c.Attribute("N") == "NoShow" &&
                                        (string?)c.Attribute("V") == "1")) continue;

            var rows = sec.Elements().Where(e => e.Name.LocalName == "Row").ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (((string?)row.Attribute("T") ?? "") != "NURBSTo") continue;
                var pts = ParseNurbsPoints(row, w, h);
                if (pts.Count == 0) continue;

                // 起点：前面最近的 MoveTo / RelMoveTo，缺省 (0,0)
                double sx0 = 0, sy0 = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    var t = (string?)rows[j].Attribute("T") ?? "";
                    if (t != "MoveTo" && t != "RelMoveTo") continue;
                    sx0 = RowDim(rows[j], "X", shape, 0, null);
                    sy0 = RowDim(rows[j], "Y", shape, 0, null);
                    if (t == "RelMoveTo") { sx0 *= w; sy0 *= h; }
                    break;
                }
                double ex = RowDim(row, "X", shape, w, null);
                double ey = RowDim(row, "Y", shape, h, null);

                (double X, double Y) S(double lx, double ly)
                {
                    var p = parentM.Apply(bx.Value + lx, by.Value + ly);
                    return (p.X, page.Height - p.Y);
                }

                var s = S(sx0, sy0);
                var e = S(ex, ey);
                // 三次贝塞尔：P1 取首个控制点（决定起点切线），P2 取倒数第二个控制点
                // （决定入线方向），P3 严格用行的 X/Y 端点。控制点全在同一侧，拟合出
                // Visio 那种"先竖直/水平走一小段再平滑拐弯"的弧线，不会出现直角折线。
                var p1 = S(pts[0].X, pts[0].Y);
                // E 单元格通常只存 2 个中间控制点（如接线图的折线）；此时第二个点就是
                // pts[1]。3 个及以上时末位可能是端点，仍取倒数第二个才算入线切线。
                int p2i = pts.Count == 2 ? 1 : pts.Count - 2;
                var p2 = S(pts[p2i].X, pts[p2i].Y);

                string color = style.LineColor ?? "#000000";
                double lw = style.LineWeight.HasValue && style.LineWeight.Value > 0
                    ? style.LineWeight.Value : 0.0139;
                bool noLine = style.LinePattern == 0;

                if (!noLine)
                {
                    var curve = new PathDrawable
                    {
                        Fill = null, Stroke = color, StrokeWidth = lw, NoFill = true,
                    };
                    curve.Commands.Add(new _Move(s.X, s.Y));
                    curve.Commands.Add(new _Cubic(p1.X, p1.Y, p2.X, p2.Y, e.X, e.Y));
                    // 贝塞尔曲线整体在控制点凸包内，用控制点求包围盒足够安全
                    curve.Include(s.X, s.Y);
                    curve.Include(e.X, e.Y);
                    foreach (var q in pts)
                    {
                        var t = S(q.X, q.Y);
                        curve.Include(t.X, t.Y);
                    }
                    page.Drawables.Add(curve);

                    int arrow = (int)Math.Round(GetDim(shape, "EndArrow", inh, 0));
                    if (arrow != 0)
                    {
                        // 箭头方向取曲线末端切线（p2 → 终点），拐弯后才不会指歪
                        double adx = e.X - p2.X, ady = e.Y - p2.Y;
                        double alen = Math.Sqrt(adx * adx + ady * ady);
                        if (alen < 1e-9)
                        {
                            adx = e.X - s.X; ady = e.Y - s.Y;
                            alen = Math.Sqrt(adx * adx + ady * ady);
                        }
                        if (alen > 1e-9)
                        {
                            double ux = adx / alen, uy = ady / alen;
                            double ah = Math.Max(0.10, lw * 9);
                            double aw = ah * 0.45;
                            double bxx = e.X - ux * ah, byy = e.Y - uy * ah;
                            double nx = -uy, ny = ux;
                            var tri = new PathDrawable
                            {
                                Fill = color, Stroke = color, StrokeWidth = lw * 0.4, NoFill = false,
                            };
                            tri.Commands.Add(new _Move(e.X, e.Y));
                            tri.Commands.Add(new _Line(bxx + nx * aw, byy + ny * aw));
                            tri.Commands.Add(new _Line(bxx - nx * aw, byy - ny * aw));
                            tri.Commands.Add(new _Close());
                            tri.Include(e.X, e.Y);
                            tri.Include(bxx + nx * aw, byy + ny * aw);
                            tri.Include(bxx - nx * aw, byy - ny * aw);
                            page.Drawables.Add(tri);
                        }
                    }
                }
                return true;   // 已按曲线连接线处理
            }
        }
        return false;
    }

    private static bool TryRenderConnector(
        XElement shape, XElement? inh, Affine parentM, Style style, PageScene page)
    {
        // 曲线连接线：作者选了 Visio 的"曲线"走线，真实路径写在自身 Geometry 的 NURBSTo 里。
        // Begin/End 只是端点，绝不能再套直角路由把曲线折成直角的横平竖直折线。
        if (TryRenderNurbsConnector(shape, inh, parentM, style, page)) return true;

        double? bx = GetDimNullable(shape, "BeginX", inh);
        double? by = GetDimNullable(shape, "BeginY", inh);
        double? ex = GetDimNullable(shape, "EndX", inh);
        double? ey = GetDimNullable(shape, "EndY", inh);
        if (bx == null || by == null || ex == null || ey == null) return false;
        // 两端点重合（退化）时没有可画的线，仍视为连接线，避免退回去画占位几何
        var pB = parentM.Apply(bx.Value, by.Value);
        var pE = parentM.Apply(ex.Value, ey.Value);
        double x1 = pB.X, y1 = page.Height - pB.Y;
        double x2 = pE.X, y2 = page.Height - pE.Y;
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return true;

        // ---- 走线：Visio 的动态连接线默认是"直角"路由，不是对角线 ----
        // 两端点如果 XY 都不重合，就按正交折线走：先从起点沿它所在的边（上下 / 左右）离开，
        // 再折向终点的方向。判断"上下边还是左右边"靠连接点在目标形状包围盒上的位置。
        var pts = new List<(double X, double Y)> { (x1, y1) };
        if (Math.Abs(dx) > 0.02 && Math.Abs(dy) > 0.02)
        {
            var srcBox = LookupShapeBox(shape, "BeginX");
            var dstBox = LookupShapeBox(shape, "EndX");
            // 查不到参照形状时，按最常见的"向下出、横向入"处理
            bool beginVert = srcBox == null || IsVerticalSide(bx.Value, by.Value, srcBox.Value);
            bool endVert = dstBox != null && IsVerticalSide(ex.Value, ey.Value, dstBox.Value);

            if (beginVert && endVert)
            {
                double ym = (y1 + y2) / 2;
                pts.Add((x1, ym));
                pts.Add((x2, ym));
            }
            else if (!beginVert && !endVert)
            {
                double xm = (x1 + x2) / 2;
                pts.Add((xm, y1));
                pts.Add((xm, y2));
            }
            else if (beginVert) pts.Add((x1, y2));
            else pts.Add((x2, y1));
            pts.Add((x2, y2));
        }
        else
        {
            pts.Add((x2, y2));   // 已经共线（水平/垂直），直接一段直线
        }

        string color = style.LineColor ?? "#000000";
        double lw = style.LineWeight.HasValue && style.LineWeight.Value > 0 ? style.LineWeight.Value : 0.0139;
        bool noLine = style.LinePattern == 0;
        if (Trace)
            Console.Error.WriteLine($"  [conn] ID={shape.Attribute("ID")?.Value} color={color} lw={lw:0.####} noLine={noLine} " +
                                    $"pts={pts.Count} ({x1:0.##},{y1:0.##})->({x2:0.##},{y2:0.##})");

        if (!noLine)
        {
            var line = new PathDrawable { Fill = null, Stroke = color, StrokeWidth = lw, NoFill = true };
            line.Commands.Add(new _Move(x1, y1));
            foreach (var q in pts.Skip(1)) line.Commands.Add(new _Line(q.X, q.Y));
            foreach (var q in pts) line.Include(q.X, q.Y);
            page.Drawables.Add(line);

            int arrow = (int)Math.Round(GetDim(shape, "EndArrow", inh, 0));
            if (arrow != 0)
            {
                // 箭头方向取最后一段，拐弯后才不会指歪
                var last = pts[pts.Count - 1];
                var prev = pts[pts.Count - 2];
                double adx = last.X - prev.X, ady = last.Y - prev.Y;
                double alen = Math.Sqrt(adx * adx + ady * ady);
                if (alen < 1e-9) { adx = dx; ady = dy; alen = len; }
                double ux = adx / alen, uy = ady / alen;
                double ah = Math.Max(0.10, lw * 9);   // 箭头长度
                double aw = ah * 0.45;                // 箭头半宽
                double bxx = x2 - ux * ah, byy = y2 - uy * ah;
                double nx = -uy, ny = ux;
                var tri = new PathDrawable { Fill = color, Stroke = color, StrokeWidth = lw * 0.4, NoFill = false };
                tri.Commands.Add(new _Move(x2, y2));
                tri.Commands.Add(new _Line(bxx + nx * aw, byy + ny * aw));
                tri.Commands.Add(new _Line(bxx - nx * aw, byy - ny * aw));
                tri.Commands.Add(new _Close());
                tri.Include(x2, y2);
                tri.Include(bxx + nx * aw, byy + ny * aw);
                tri.Include(bxx - nx * aw, byy - ny * aw);
                page.Drawables.Add(tri);
            }
        }

        // 连接线上的文字（少见）：放在线段中点上方
        var textEl = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
        if (textEl != null && !string.IsNullOrWhiteSpace(textEl.Value))
        {
            string txt = textEl.Value.Trim();
            double fs = style.CharSize.HasValue ? style.CharSize.Value : 0.1667;
            double mx = (x1 + x2) / 2, my = (y1 + y2) / 2 - fs * 0.9;
            var td = new TextDrawable
            {
                Text = txt,
                Color = style.TextColor ?? "#000000",
                FontSize = fs,
                CenterX = mx,
                CenterY = my,
            };
            double ew, eh;
            EstimateTextSize(txt, fs, null, out ew, out eh);
            td.Include(mx - ew / 2, my - eh / 2);
            td.Include(mx + ew / 2, my + eh / 2);
            page.Drawables.Add(td);
        }
        return true;
    }

    /// <summary>
    /// 渲染母版内容（母版 ShapesRoot 下的根形状）。根形状的 Pin/LocPin 被实例变换取代，
    /// 因此直接用它自身的几何 + 传入的实例变换 M，不再叠加根形状的 Pin；
    /// 根形状的子形状（分组内部）仍按各自的局部变换（相对 M）正常渲染。
    /// </summary>
    /// <summary>母版里是否含"标题条"子形状（容器/泳道）。</summary>
    private static bool HasHeadingChild(MasterDef md)
    {
        foreach (var sh in md.ShapesRoot.Descendants().Where(e => e.Name.LocalName == "Shape"))
            if (IsHeadingShape(sh)) return true;
        return false;
    }

    /// <summary>是否为容器/泳道的「标题条」形状（带 User.HeadingPos）。</summary>
    /// <summary>形状单元格的 V 是否是字面 #RRGGBB（Visio 把 THEMEVAL 主题色缓存成 hex；
    /// 确定色则缓存成调色板索引或字面值，如 V='1' 白）。</summary>
    private static bool IsLiteralHexCell(XElement el, string name)
    {
        var v = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name)?.Attribute("V")?.Value;
        return v != null && v.TrimStart().StartsWith("#");
    }

    private static bool IsHeadingShape(XElement shape)    {
        foreach (var sec in shape.Elements().Where(e => e.Name.LocalName == "Section" &&
                                                        (string?)e.Attribute("N") == "User"))
        {
            foreach (var row in sec.Elements().Where(e => e.Name.LocalName == "Row"))
                if ((string?)row.Attribute("N") == "HeadingPos") return true;
        }
        return false;
    }

    private static void RenderMasterContent(
        XElement shape, Affine M, Dictionary<int, MasterDef> masters,
        PageScene page, Style? inherited, int? masterId,
        IReadOnlyDictionary<string, string>? textOverrides = null,
        bool isRoot = false, Style? instanceStyle = null,
        IReadOnlyDictionary<string, XElement>? imageOverrides = null,
        IReadOnlyDictionary<string, XElement>? fieldOverrides = null,
        IReadOnlyDictionary<string, XElement>? geomOverrides = null,
        double scaleSx = 1, double scaleSy = 1,
        IReadOnlyDictionary<string, XElement>? charOverrides = null,
        IReadOnlyDictionary<string, XElement>? dimOverrides = null)
    {
        // 母版里的 Foreign 形状也要出图（组织图照片占位就定义在母版 ID=14）。
        if ((string?)shape.Attribute("Type") == "Foreign")
        {
            RenderMasterImage(shape, M, page, masterId, masters, imageOverrides);
            return;
        }

        // 母版子形状的尺寸常被实例缓存值改写（db_schema 表头/正文高度）。
        // 这里先把实例值换算回母版本地坐标，后续几何和文字框都用改写后的形状。
        shape = ApplyDimensionOverride(shape, dimOverrides, scaleSx, scaleSy);

        var style = ReadStyle(shape, inherited, null);
        // 母版里 THEMEVAL(...) 的填充会随实例配色变化：Visio 把实例算好的颜色缓存
        // 在实例形状的 FillForegnd 里（组织图里是 #339933/#f09609/#1ba1e2/#d80073），
        // 而母版里存的仍是默认主题色 #638bad。这类"随主题"的子形状必须用实例色覆盖；
        // GUARD(RGB(...)) 写死的装饰色（橙色强调条、白线等）保持母版原值。
        if (instanceStyle != null)
        {
            // 只在母版颜色确实是「主题色缓存」时才用实例色覆盖。
            // Visio 把 THEMEVAL 算出的颜色缓存成 hex（如 #638bad）；而确定色
            // （如 FillForegnd V='1' 白）即使公式里出现 THEMEVAL 也不能覆盖 ——
            // orgchart 姓名条下方的白条 F=IF(ISTHEMED()..,1,THEMEVAL()) V=1，
            // 被无条件覆盖成实例的绿色，白色底条就消失了。
            bool fillThemed = CellFormulaContains(shape, "FillForegnd", "THEMEVAL") && IsLiteralHexCell(shape, "FillForegnd");
            bool lineThemed = CellFormulaContains(shape, "LineColor", "THEMEVAL") && IsLiteralHexCell(shape, "LineColor");
            string? fill = isRoot || fillThemed ? instanceStyle.FillForegnd : null;
            string? line = isRoot || lineThemed ? instanceStyle.LineColor : null;
            if (Trace && (isRoot || fillThemed))
                Console.Error.WriteLine($"  [mstyle] ID={shape.Attribute("ID")?.Value} root={isRoot} " +
                                        $"instFill={instanceStyle.FillForegnd ?? "-"} fill={fill ?? "-"} " +
                                        $"themed={fillThemed}");
            if (fill != null || line != null) style = style.WithColors(fill, line);
        }
        bool heading = IsHeadingShape(shape);
        // 标题条本身以及它的标题文字都放在「未纵向拉伸」的坐标系里，
        // 之后整体平移到容器顶边（见下面的 _headingTopScreen 对齐）。
        var Mh = heading && Math.Abs(M.D) > 1e-9
            ? new Affine(M.A, M.B, M.C, Math.Sign(M.D), M.E, M.F)
            : M;

        // 实例子形状改写了本子形状的几何（如弧形墙）：用实例几何并抵消母版缩放。
        // 实例几何行的 V 已按实例宽度求值（340in 的弧），直接套 M 的 sx 会被再放大一次。
        var selfId = (string?)shape.Attribute("ID");
        XElement? geomOv = null;
        if (geomOverrides != null && selfId != null) geomOverrides.TryGetValue(selfId, out geomOv);
        if (SuppressInstanceOverrides(shape)) geomOv = null;
        bool useGeomOverride = geomOv != null && scaleSx > 1e-9 && scaleSy > 1e-9 &&
                               HasCompleteInstanceGeometry(geomOv!, shape);
        var MGeom = useGeomOverride
            ? Affine.Compose(Mh, LocalScaleInverse(geomOv!, scaleSx, scaleSy))
            : Mh;

        int start = page.Drawables.Count;
        var sub = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (sub != null)
        {
            // 分组：先画自身几何（若有），再递归渲染子形状
            RenderGeometry(useGeomOverride ? geomOv! : shape, MGeom, style, page, useGeomOverride ? shape : null);
            foreach (var ch in sub.Elements().Where(e => e.Name.LocalName == "Shape"))
            {
                // 母版内的 Foreign 子形状要用母版自己的 rels 找图片；
                // 直接交给 RenderShape 会错用页面 rels，rId 指向别处或干脆找不到。
                if ((string?)ch.Attribute("Type") == "Foreign")
                    RenderMasterImage(ch, Mh, page, masterId, masters, imageOverrides);
                else RenderShape(ch, Mh, masters, page, style, masterId, textOverrides,
                         fieldOverrides, geomOverrides, scaleSx, scaleSy, charOverrides, dimOverrides);
            }
        }
        else
        {
            RenderGeometry(useGeomOverride ? geomOv! : shape, MGeom, style, page, useGeomOverride ? shape : null);
        }
        // 文字：若实例重写了这个子形状，用实例的文字（位置仍按母版文本框）
        string? tOv = null;
        if (textOverrides != null && selfId != null) textOverrides.TryGetValue(selfId, out tOv);
        // 字段值（房间号等）来自实例的 Field 段，优先于母版的占位字段值。
        XElement? fSrc = null;
        if (fieldOverrides != null && selfId != null) fieldOverrides.TryGetValue(selfId, out fSrc);
        XElement? cSrc = null;
        if (charOverrides != null && selfId != null) charOverrides.TryGetValue(selfId, out cSrc);
        XElement? tabSrc = null;
        if (dimOverrides != null && selfId != null) dimOverrides.TryGetValue(selfId, out tabSrc);
        RenderText(shape, Mh, null, style, page, tOv, fSrc, cSrc, tabSrc);

        // 标题条要贴着容器顶边：把这段渲染出的图形和文字一起上移。
        if (heading && _headingTopScreen.HasValue)
        {
            double top = double.MaxValue;
            for (int i = start; i < page.Drawables.Count; i++)
                if (page.Drawables[i].HasBounds && page.Drawables[i].MinY < top)
                    top = page.Drawables[i].MinY;
            if (top < double.MaxValue)
            {
                double dy = _headingTopScreen.Value - top;
                if (Math.Abs(dy) > 1e-6)
                    for (int i = start; i < page.Drawables.Count; i++)
                        ShiftDrawableY(page.Drawables[i], dy);
            }
        }
    }

    /// <summary>把一个可绘制对象在屏幕坐标里整体上下平移。</summary>
    private static void ShiftDrawableY(Drawable d, double dy)
    {
        switch (d)
        {
            case PathDrawable p: ShiftPathY(p, dy); return;
            case TextDrawable t:
                t.CenterY += dy;
                if (t.BoxTop.HasValue) t.BoxTop += dy;
                break;
            case ImageDrawable im: im.Y += dy; break;
        }
        d.MinY += dy;
        d.MaxY += dy;
    }

    /// <summary>收集实例对母版子形状的尺寸覆盖：母版形状 ID → 实例子形状。
    /// db_schema 的表格母版把表头/正文高度写成 TEXTHEIGHT 公式，母版缓存高度
    /// 只有 0.278in，而实例缓存了真实高度（表头 0.278in、正文 1.611in）。
    /// 不采用实例尺寸就会把表头拉成半个表格，正文空间被挤掉。</summary>
    private static IReadOnlyDictionary<string, XElement>? BuildDimensionOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, XElement>? map = null;
        var children = kids.Elements().Where(e => e.Name.LocalName == "Shape").ToList();
        foreach (var c in children)
        {
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            bool hasDim = c.Elements().Any(e =>
            {
                if (e.Name.LocalName != "Cell") return false;
                var n = (string?)e.Attribute("N");
                return n != null && DimensionCells.Contains(n) && (string?)e.Attribute("V") != null;
            });
            if (!hasDim) continue;
            map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
            map[ms!] = c;
        }

        // 两个子形状（表头/正文）时，实例常只缓存正文高度；表头高度 = 组高 - 正文高。
        // 不补这一项，母版缓存的表头 0.278in 会被 sy 放大成半个表格。
        var withHeight = children.Where(c =>
            (string?)c.Attribute("MasterShape") != null && CellRaw(c, "Height") != null).ToList();
        var withoutHeight = children.Where(c =>
            (string?)c.Attribute("MasterShape") != null && CellRaw(c, "Height") == null).ToList();
        if (withHeight.Count == 1 && withoutHeight.Count == 1)
        {
            double rootH = GetDim(shape, "Height", null, 0);
            var hRaw = CellRaw(withHeight[0], "Height");
            if (rootH > 0 && hRaw != null &&
                double.TryParse(hRaw, NF, CultureInfo.InvariantCulture, out var bodyH) &&
                bodyH > 0 && rootH > bodyH)
            {
                var header = new XElement(withoutHeight[0]);
                var hCell = header.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "Height");
                if (hCell == null)
                {
                    hCell = new XElement("Cell", new XAttribute("N", "Height"));
                    header.Add(hCell);
                }
                hCell.SetAttributeValue("V", (rootH - bodyH).ToString("0.################", CultureInfo.InvariantCulture));
                hCell.SetAttributeValue("U", "IN");
                map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
                map[(string)withoutHeight[0].Attribute("MasterShape")!] = header;
            }
        }
        return map;
    }

    /// <summary>旋转 ±90° 或带 HeadingPos 的标题条沿用母版几何，
    /// 不参与实例尺寸/几何回写（回写坐标轴与页面轴交叉，易把标题条拉长）。</summary>
    private static double FlipSign(XElement shape, string name)
        => GetDim(shape, name, null, 0) != 0 ? -1 : 1;

    private static bool SuppressInstanceOverrides(XElement shape)
    {
        double angle = GetDim(shape, "Angle", null, 0);
        return Math.Abs(Math.Cos(angle)) < 0.5 || IsHeadingShape(shape);
    }

    /// <summary>把「实例本地 → 母版本地」的缩放矩阵按形状旋转轴交换。
    /// 旋转 ±90° 的竖向泳道标题条，Width 对应页面 Y、Height 对应页面 X；
    /// 不交换会把长度再乘一次父级 sy（战略经营管理.vsdx 的“阶段”条被拉到 29in）。</summary>
    private static Affine LocalScaleInverse(XElement shape, double sx, double sy)
    {
        double angle = GetDim(shape, "Angle", null, 0);
        bool rotated = Math.Abs(Math.Cos(angle)) < 0.5;
        return rotated
            ? new Affine(1 / Math.Max(sy, 1e-9), 0, 0, 1 / Math.Max(sx, 1e-9), 0, 0)
            : new Affine(1 / Math.Max(sx, 1e-9), 0, 0, 1 / Math.Max(sy, 1e-9), 0, 0);
    }

    private static readonly HashSet<string> DimensionCells = new(StringComparer.Ordinal)
    {
        "Width", "Height", "PinX", "PinY", "LocPinX", "LocPinY",
        "TxtWidth", "TxtHeight", "TxtPinX", "TxtPinY"
    };

    /// <summary>把实例缓存尺寸换算回母版/父级本地坐标，生成一份改写后的临时形状。</summary>
    private static XElement ApplyDimensionOverride(XElement shape,
        IReadOnlyDictionary<string, XElement>? dimOverrides, double sx, double sy)
    {
        if (dimOverrides == null || (Math.Abs(sx - 1) < 1e-9 && Math.Abs(sy - 1) < 1e-9)) return shape;
        // 旋转标题条/泳道标题的子形状不要套用 db_schema 的本地尺寸回写：
        // 它们的实例 Width/Height 与页面 X/Y 轴交叉，回写会把标题条整体拉坏。
        if (SuppressInstanceOverrides(shape)) return shape;
        var id = (string?)shape.Attribute("ID") ?? (string?)shape.Attribute("MasterShape");
        if (id == null || !dimOverrides.TryGetValue(id, out var src)) return shape;

        // 旋转 ±90° 的子形状，实例 Width 对应页面纵向、Height 对应页面横向。
        // 若仍按 X→sx、Y→sy 换算，竖向泳道标题条会被父级 sy 再放大一次
        //（战略经营管理.vsdx 的“阶段”标题条曾被拉成 29in 高）。
        double angle = GetDim(shape, "Angle", null, 0);
        bool rotated = Math.Abs(Math.Cos(angle)) < 0.5;
        double sxLocal = rotated ? sy : sx;
        double syLocal = rotated ? sx : sy;

        var clone = new XElement(shape);
        foreach (var cell in src.Elements().Where(e => e.Name.LocalName == "Cell"))
        {
            var name = (string?)cell.Attribute("N");
            if (name == null || !DimensionCells.Contains(name)) continue;
            var raw = (string?)cell.Attribute("V");
            if (raw == null || !double.TryParse(raw, NF, CultureInfo.InvariantCulture, out var val)) continue;
            bool xAxis = name.EndsWith("X", StringComparison.Ordinal) ||
                         name == "Width" || name == "TxtWidth";
            double local = val / (xAxis ? sxLocal : syLocal);
            var target = clone.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
            if (target == null)
            {
                target = new XElement(cell.Name.LocalName, new XAttribute("N", name));
                clone.Add(target);
            }
            target.SetAttributeValue("V", local.ToString("0.################", CultureInfo.InvariantCulture));
            if ((string?)target.Attribute("U") != null) target.SetAttributeValue("U", "IN");
        }

        // db_schema 的表格分隔线由母版 Geometry1/3 的 NoShow 公式控制：
        //   NoShow = User.DBHideTabLine1 / OR(User.PKCount=-1, User.DBHidePKSeperatorLine)
        // 这些 User 值只写在实例子形状上。不把它们带进母版副本，母版公式求值失败
        // 就退回缓存 V=1，PK 竖线和 PK 行下的横线都会消失。
        CopyUserFlags(src, clone);

        // 竖分隔线的 X 公式引用 Tabs.Y1；实例 Tabs 是页面英寸，要除以 sx 换回母版本地坐标。
        CopyTabStops(src, clone, sxLocal);

        // 公式求值器不认 User.xxx / OR(...)，而 Geometry1/3 的 X/Y/NoShow 都依赖它们。
        // 实例里已经缓存了 Visio 算好的行值，直接搬回母版本地坐标最可靠；
        // 母版独有的行（如外框 MoveTo）保留不动。
        CopyGeometryRows(src, clone, sxLocal, syLocal);
        return clone;
    }

    private static void CopyGeometryRows(XElement src, XElement dst, double sx, double sy)
    {
        foreach (var ssec in src.Elements().Where(e =>
                     e.Name.LocalName == "Section" &&
                     ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal)))
        {
            var secName = (string?)ssec.Attribute("N");
            var secIx = (string?)ssec.Attribute("IX") ?? "0";
            var dsec = dst.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Section" && (string?)e.Attribute("N") == secName &&
                ((string?)e.Attribute("IX") ?? "0") == secIx);
            if (dsec == null) continue;

            // NoShow/NoFill/NoLine 是段级 Cell，不在 Row 里；db_schema 的 PK 竖线
            // 就是靠实例 Geometry1 的 NoShow=0 才显示出来。
            foreach (var scell in ssec.Elements().Where(e => e.Name.LocalName == "Cell"))
            {
                var name = (string?)scell.Attribute("N");
                if (name is not ("NoShow" or "NoFill" or "NoLine")) continue;
                var raw = (string?)scell.Attribute("V");
                if (raw == null) continue;
                var dcell = dsec.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
                if (dcell == null) continue;
                dcell.SetAttributeValue("V", raw);
                dcell.SetAttributeValue("F", "No Formula");
            }

            foreach (var srow in ssec.Elements().Where(e => e.Name.LocalName == "Row"))
            {
                var rowIx = (string?)srow.Attribute("IX");
                var rowT = (string?)srow.Attribute("T");
                var drow = dsec.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "Row" &&
                    ((string?)e.Attribute("IX") ?? "") == (rowIx ?? "") &&
                    ((string?)e.Attribute("T") ?? "") == (rowT ?? ""));
                if (drow == null) continue;

                foreach (var scell in srow.Elements().Where(e => e.Name.LocalName == "Cell"))
                {
                    var name = (string?)scell.Attribute("N");
                    if (name == null) continue;
                    var raw = (string?)scell.Attribute("V");
                    if (raw == null || !double.TryParse(raw, NF, CultureInfo.InvariantCulture, out var val)) continue;

                    // EllipticalArcTo 的 A/B 是控制点坐标（和 X/Y 同量纲），C/D 是角与比例；
                    // CubicBezierTo 的 A/B、C/D 是两个控制点。实例缓存的是实例本地英寸，
                    // 和 X/Y 一样必须除以 sx/sy 回到母版坐标，否则圆角/标题条弧线会被拉成
                    // 一条细长的楔形（network_topology 的 Legend 绿色标题栏曾伸出页面）。
                    string rowType = (string?)srow.Attribute("T") ?? "LineTo";
                    bool isBezier = rowType is "EllipticalArcTo" or "CubicBezierTo";
                    double local = name == "X" || (isBezier && name == "A") ||
                                   (rowType == "CubicBezierTo" && name == "C") ? val / sx
                                 : name == "Y" || (isBezier && name == "B") ||
                                   (rowType == "CubicBezierTo" && name == "D") ? val / sy
                                 : val;
                    var dcell = drow.Elements().FirstOrDefault(e =>
                        e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
                    if (dcell == null) continue;
                    dcell.SetAttributeValue("V",
                        local.ToString("0.################", CultureInfo.InvariantCulture));
                    dcell.SetAttributeValue("F", "No Formula");
                    if ((string?)dcell.Attribute("U") != null) dcell.SetAttributeValue("U", "IN");
                }
            }
        }
    }

    private static readonly HashSet<string> UserFlagNames = new(StringComparer.Ordinal)
    {
        "PKCount", "DBHidePKSeperatorLine", "DBHideTabLine1", "DBHideTabLine2", "LineCount"
    };

    private static void CopyUserFlags(XElement src, XElement dst)
    {
        var srcSec = src.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "User");
        if (srcSec == null) return;
        var dstSec = dst.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "User");
        if (dstSec == null)
        {
            dstSec = new XElement(srcSec.Name, new XAttribute("N", "User"));
            dst.Add(dstSec);
        }
        foreach (var srow in srcSec.Elements().Where(e => e.Name.LocalName == "Row"))
        {
            var name = (string?)srow.Attribute("N");
            if (name == null || !UserFlagNames.Contains(name)) continue;
            var val = srow.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "Value")?.Attribute("V")?.Value;
            if (val == null) continue;
            var drow = dstSec.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Row" && (string?)e.Attribute("N") == name);
            if (drow == null)
            {
                drow = new XElement(srow.Name, new XAttribute("N", name));
                dstSec.Add(drow);
            }
            var dcell = drow.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == "Value");
            if (dcell == null)
            {
                dcell = new XElement("Cell", new XAttribute("N", "Value"));
                drow.Add(dcell);
            }
            dcell.SetAttributeValue("V", val);
        }
    }

    private static void CopyTabStops(XElement src, XElement dst, double sx)
    {
        if (sx <= 1e-9) return;
        var srcSec = src.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Tabs");
        var srow = srcSec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
        if (srow == null) return;
        var dstSec = dst.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Tabs");
        if (dstSec == null)
        {
            dstSec = new XElement(srcSec!.Name, new XAttribute("N", "Tabs"));
            dst.Add(dstSec);
        }
        var drow = dstSec.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
        if (drow == null)
        {
            drow = new XElement(srow.Name, new XAttribute("IX", "0"));
            dstSec.Add(drow);
        }
        foreach (var scell in srow.Elements().Where(e =>
                     e.Name.LocalName == "Cell" &&
                     ((string?)e.Attribute("N") ?? "").StartsWith("Position", StringComparison.Ordinal)))
        {
            var name = (string?)scell.Attribute("N");
            var raw = (string?)scell.Attribute("V");
            if (name == null || raw == null ||
                !double.TryParse(raw, NF, CultureInfo.InvariantCulture, out var val)) continue;
            var dcell = drow.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
            if (dcell == null)
            {
                dcell = new XElement(scell.Name, new XAttribute("N", name));
                drow.Add(dcell);
            }
            dcell.SetAttributeValue("V", (val / sx).ToString("0.################", CultureInfo.InvariantCulture));
            dcell.SetAttributeValue("U", "IN");
        }
    }

    /// <summary>收集实例对母版子形状的文字/字符样式覆盖：母版形状 ID → 实例子形状。
    /// 实例可改写文字（姓名）以及 Character 颜色/字号（姓名条用绿字）。</summary>
    private static IReadOnlyDictionary<string, XElement>? BuildCharOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, XElement>? map = null;
        foreach (var c in kids.Elements().Where(e => e.Name.LocalName == "Shape"))
        {
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            bool hasChar = c.Elements().Any(e =>
                e.Name.LocalName == "Section" && ((string?)e.Attribute("N") ?? "").StartsWith("Character"));
            if (!hasChar) continue;
            map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
            map[ms!] = c;
        }
        return map;
    }

    /// <summary>收集实例对母版子形状的字段覆盖：母版形状 ID → 带 Field 段的实例子形状。
    /// 实例的 Field 段会改写母版字段（把面积等置空、换成房间号），文字内容必须以它为准。</summary>
    private static IReadOnlyDictionary<string, XElement>? BuildFieldOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, XElement>? map = null;
        foreach (var c in kids.Elements().Where(e => e.Name.LocalName == "Shape"))
        {
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            bool hasField = c.Elements().Any(e =>
                e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Field");
            if (!hasField) continue;
            map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
            map[ms!] = c;
        }
        return map;
    }

    /// <summary>收集实例对母版子形状的几何覆盖：母版形状 ID → 自带 Geometry 的实例子形状。
    /// 实例子形状的几何行用实例本地坐标（V 已按实例宽度求值），渲染时要抵消母版缩放。</summary>
    private static IReadOnlyDictionary<string, XElement>? BuildGeometryOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, XElement>? map = null;
        foreach (var c in kids.Elements().Where(e => e.Name.LocalName == "Shape"))
        {
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            bool hasGeom = c.Elements().Any(e =>
                e.Name.LocalName == "Section" &&
                ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal));
            if (!hasGeom) continue;
            map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
            map[ms!] = c;
        }
        return map;
    }

    /// <summary>收集实例对母版图片占位的覆盖：母版形状 ID → 页面实例的 Foreign 形状。
    /// 实例自带 ForeignData/Rel，指向该员工自己的照片；母版里只有一张占位图。</summary>
    private static IReadOnlyDictionary<string, XElement>? BuildImageOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, XElement>? map = null;
        foreach (var c in kids.Elements().Where(e => e.Name.LocalName == "Shape"))
        {
            if ((string?)c.Attribute("Type") != "Foreign") continue;
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            // 只有实例自带 ForeignData/Rel 时才算「图片覆盖」（它有自己的 rId，
            // 指向该实例的照片）。没有 ForeignData 的实例子形状必须走母版的 rels，
            // 否则会误用页面 rels 解析 rId，拿到占位图（big_process_orgchart
            // 全员头像变成 image2.bmp 灰块）。
            bool hasRel = c.Elements().Any(e => e.Name.LocalName == "ForeignData") ||
                          (string?)c.Attribute("Rel") != null;
            if (!hasRel) continue;
            map ??= new Dictionary<string, XElement>(StringComparer.Ordinal);
            map[ms!] = c;
        }
        return map;
    }

    /// <summary>渲染母版内的图片占位：优先用页面实例自己的 ForeignData（rId 对页面 rels），
    /// 尺寸从母版占位继承；没有对应实例时才退回母版自带的占位图。</summary>
    private static void RenderMasterImage(XElement masterShape, Affine M, PageScene page,
        int? masterId, Dictionary<int, MasterDef> masters,
        IReadOnlyDictionary<string, XElement>? imageOverrides)
    {
        var mid = (string?)masterShape.Attribute("ID");
        if (mid != null && imageOverrides != null && imageOverrides.TryGetValue(mid, out var inst))
        {
            RenderForeignImage(inst, M, page, masterShape, _pageRels);
            return;
        }
        var rels = masterId.HasValue && masters.TryGetValue(masterId.Value, out var fd)
            ? fd.Rels ?? _pageRels : _pageRels;
        RenderForeignImage(masterShape, M, page, null, rels);
    }

    /// <summary>收集实例对其母版子形状的文字覆盖：MasterShape(K) → 实例文字。</summary>
    private static IReadOnlyDictionary<string, string>? BuildTextOverrides(XElement shape)
    {
        var kids = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (kids == null) return null;
        Dictionary<string, string>? map = null;
        foreach (var c in kids.Elements().Where(e => e.Name.LocalName == "Shape"))
        {
            var ms = (string?)c.Attribute("MasterShape");
            if (string.IsNullOrEmpty(ms)) continue;
            var t = c.Elements().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            map ??= new Dictionary<string, string>(StringComparer.Ordinal);
            map[ms!] = t!;
        }
        return map;
    }

    /// <summary>当前母版实例的顶边（屏幕坐标 Y）。标题条要贴着容器顶边放，不能跟着缩放跑。</summary>
    private static double? _headingTopScreen;

    private static void RenderGeometry(XElement shape, Affine M, Style style, PageScene page, XElement? inh = null)
    {
        // CFF 容器的标题条高在母版里是「字面量」（如 10mm≈0.394in），Visio 拉高容器时
        // 只拉伸主体、标题条保持原高。我们整体按 iH/Hm 缩放会把标题条一并拉高
        //（实测 0.394in→0.82in），于是表头下方多出一片蓝。这里对标题条取消纵向缩放。
        bool heading = IsHeadingShape(shape);
        if (heading && Math.Abs(M.D) > 1e-9)
            M = new Affine(M.A, M.B, M.C, Math.Sign(M.D), M.E, M.F);

        var geomSections = shape.Elements()
            .Where(e => e.Name.LocalName == "Section" &&
                        ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal))
            .ToList();
        // 实例自身没有几何区时，整段回退到母版几何
        if (geomSections.Count == 0 && inh != null)
            geomSections = inh.Elements()
                .Where(e => e.Name.LocalName == "Section" &&
                            ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal))
                .ToList();
        // Geometry 段的 NoShow=1 表示"这条路径不显示"。CFF 泳道模板把一堆辅助几何
        //（标题下方那条带弧线的深色块就是其中之一）标成 NoShow，Visio 不画，我们也必须跳过，
        // 否则泳道标题下面会多出一整条蓝带。
        geomSections = geomSections
            .Where(sec => sec.Elements().FirstOrDefault(c =>
                       c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "NoShow")?.Attribute("V")?.Value != "1")
            .ToList();
        // RelMoveTo / RelLineTo 的 X/Y 是「相对形状宽高的归一化比例」（0..1），
        // 不是英寸偏移量。按英寸累加会把边框画到页面外（曾导致页面被撑到 19in 且顶部大片空白）。
        double gw = GetDim(shape, "Width", inh, 1);
        double gh = GetDim(shape, "Height", inh, 1);
        foreach (var sec in geomSections)
        {
            string secName = (string?)sec.Attribute("N") ?? "Geometry";
            var msec = FindMasterSection(inh, secName);
            // 几何区自带的 NoFill / NoLine 优先
            // 渐变填充（FillGradientEnabled=1）优先级高于 FillPattern=0：
            // ms_diagrams 的池 FillPattern=0 但有 FillGradient 段，仍要画出渐变。
            bool hasGradient = style.GradientStops != null && style.GradientStops.Count >= 2;
            bool noFill = !hasGradient && (style.FillPattern == 0 ||
                          (style.FillTrans.HasValue && style.FillTrans.Value >= 0.99));
            bool noLine = style.LinePattern == 0;
            var nf = sec.Elements().FirstOrDefault(c => c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "NoFill");
            var nl = sec.Elements().FirstOrDefault(c => c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "NoLine");
            // NoFill=1 表示强制不填充；NoFill=0 只是"没有强制关闭"，真正的显隐仍由 FillPattern 决定。
            // 过去把 V='0' 当成"要填充"，导致 FillPattern=0 的文字形状（如页面大标题）被画成白底黑框。
            if (nf != null && (string?)nf.Attribute("V") == "1") noFill = true;
            if (nl != null && (string?)nl.Attribute("V") == "1") noLine = true;

            string? fill = noFill ? null : (style.FillForegnd ?? "#FFFFFF");
            string? stroke = noLine ? null : (style.LineColor ?? "#000000");
            if (fill == null && stroke == null) continue; // 完全不可见
            if (Trace)
            {
                string gix = (string?)sec.Attribute("IX") ?? "";
                Console.Error.WriteLine($"  [geom] ID={shape.Attribute("ID")?.Value} sec={secName}/{gix} " +
                                        $"fill={fill ?? "-"} pat={style.FillPattern ?? 1} bg={style.FillBkgnd ?? "-"} stroke={stroke ?? "-"} noFill={noFill} noLine={noLine} " +
                                        $"gw={gw.ToString("0.###", CultureInfo.InvariantCulture)} " +
                                        $"gh={gh.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            double lw = style.LineWeight.HasValue && style.LineWeight.Value > 0
                ? style.LineWeight.Value : 0.0104; // 缺省约 0.75pt
            double strokeW = lw; // LineWeight 的 V 已是英寸（0.01in ≈ 0.72pt），不要再 ÷72

            // Visio 的 Trans 是「透明度」(0..1)：参考渲染里 0.7/0.95 的浅色分区
            // 实际是 fill-opacity 0.3/0.05，整个模型此前丢掉了这个通道，导致深蓝/紫色块。
            // 但 svgpublish 参考导出对高透明度有视觉下限（db_schema 的 Trans=0.95
            // 色块实际是 fill-opacity 0.25/0.3 而非 0.05）——否则分区淡到几乎看不见。
            // 取 0.25 下限：0.7→0.3 不变，0.95→0.25 接近参考。
            double Opacity(double? trans)
                => trans.HasValue ? Math.Clamp(Math.Max(1 - trans.Value, 0.25), 0, 1) : 1;

            var path = new PathDrawable
            {
                Fill = fill,
                FillPatternIndex = style.FillPattern ?? 1,
                FillBkgnd = fill == null ? null : style.FillBkgnd,
                GradientStops = hasGradient ? style.GradientStops : null,
                GradientAngle = style.GradientAngle,
                Stroke = stroke,
                StrokeWidth = strokeW,
                NoFill = noFill,
                NoLine = noLine,
                FillOpacity = Opacity(style.FillTrans),
                StrokeOpacity = Opacity(style.LineTrans),
            };

            double kx = Math.Sqrt(M.A * M.A + M.B * M.B);
            double ky = Math.Sqrt(M.C * M.C + M.D * M.D);

            void ToScreen(double x, double y, out double X, out double Y)
            {
                var p = M.Apply(x, y);
                X = p.X; Y = page.Height - p.Y;
                path.Include(X, Y);
            }

            double curX = 0, curY = 0;
            foreach (var row in sec.Elements().Where(e => e.Name.LocalName == "Row"))
            {
                var t = (string?)row.Attribute("T") ?? "LineTo";
                // 母版同行（用于补齐实例行里缺失的单元格）
                var mrow = FindMasterRow(msec, row);
                switch (t)
                {
                    case "MoveTo":
                        curX = RowDim(row, "X", shape, 0, mrow, inh); curY = RowDim(row, "Y", shape, 0, mrow, inh);
                        ToScreen(curX, curY, out var mx, out var my);
                        path.Commands.Add(new _Move(mx, my));
                        break;
                    case "LineTo":
                        curX = RowDim(row, "X", shape, 0, mrow, inh); curY = RowDim(row, "Y", shape, 0, mrow, inh);
                        ToScreen(curX, curY, out var lx2, out var ly2);
                        path.Commands.Add(new _Line(lx2, ly2));
                        break;
                    case "RelMoveTo":
                        curX = RowDim(row, "X", shape, 0, mrow, inh) * gw;
                        curY = RowDim(row, "Y", shape, 0, mrow, inh) * gh;
                        ToScreen(curX, curY, out var rmx, out var rmy);
                        path.Commands.Add(new _Move(rmx, rmy));
                        break;
                    case "RelLineTo":
                        curX = RowDim(row, "X", shape, 0, mrow, inh) * gw;
                        curY = RowDim(row, "Y", shape, 0, mrow, inh) * gh;
                        ToScreen(curX, curY, out var rlx, out var rly);
                        path.Commands.Add(new _Line(rlx, rly));
                        break;
                    case "ArcTo":
                    {
                        double ex = RowDim(row, "X", shape, 0, mrow, inh);
                        double ey = RowDim(row, "Y", shape, 0, mrow, inh);
                        // Visio 的 A 是「弦高（矢高）」：弧中点到弦中点的距离。
                        // 圆角矩形的圆角半径 r 对应 A = r*(1-cos45°) = 0.2929r，
                        // 母版里正是 A=User.xxxOffset*0.2929 —— 所以它不是半径、更不是夹角。
                        double sag = RowDim(row, "A", shape, 0, mrow, inh);
                        ToScreen(ex, ey, out var ax, out var ay);
                        ToScreen(curX, curY, out var asx, out var asy);
                        if (TryArcFromSagitta(curX, curY, ex, ey, sag,
                                              out var rr, out var ccx, out var ccy, out var la))
                        {
                            // 扫掠方向：把圆心一起投到屏幕坐标，用叉积定号。
                            // 这样 Y 轴翻转、负 Width/Height 的镜像、非等比缩放都不用手工推符号。
                            var cp = M.Apply(ccx, ccy);
                            double scx = cp.X, scy = page.Height - cp.Y;
                            double cross = (asx - scx) * (ay - scy) - (asy - scy) * (ax - scx);
                            path.Commands.Add(new _Arc(ax, ay, rr * kx, rr * ky, 0, la, cross > 0));
                        }
                        else
                        {
                            path.Commands.Add(new _Line(ax, ay));   // 弦高≈0 时退化成直线
                        }
                        curX = ex; curY = ey;
                        break;
                    }
                    case "EllipticalArcTo":
                    {
                        // Visio 语义：A / B 是弧的「控制点」坐标（两端切线的交点），不是半径。
                        // 旧实现把 A 当半径，半弦长=半径就会画成半径等于弦长一半的半圆"穹顶"。
                        // 改用二次贝塞尔 Q(控制点, 终点)：端点与切线方向都和 Visio 一致。
                        double ex = RowDim(row, "X", shape, curX, mrow, inh);
                        double ey = RowDim(row, "Y", shape, curY, mrow, inh);
                        double cx = RowDim(row, "A", shape, curX, mrow, inh);
                        double cy = RowDim(row, "B", shape, curY, mrow, inh);
                        ToScreen(cx, cy, out var qx, out var qy);
                        ToScreen(ex, ey, out var ax, out var ay);
                        path.Commands.Add(new _Quad(qx, qy, ax, ay));
                        curX = ex; curY = ey;
                        break;
                    }
                    case "Ellipse":
                    {
                        // Visio Ellipse 行：X/Y 是圆心，(A,B) 是右切点、(C,D) 是上切点，
                        // 两者都相对**形状本地原点**（左下角），不是相对圆心。
                        // 半径 = 切点 − 圆心：正圆时 (A,B)=(W,H/2)、(C,D)=(W/2,H)，
                        // 于是 rx = A−X = W/2、ry = D−Y = H/2。
                        // 旧实现把 A/B 直接当 rx/ry，正圆被拉成 2:1 的扁椭圆（frog_eye）。
                        double cx = RowDim(row, "X", shape, 0, mrow, inh), cy = RowDim(row, "Y", shape, 0, mrow, inh);
                        double a = RowDim(row, "A", shape, 0, mrow, inh), b = RowDim(row, "B", shape, 0, mrow, inh);
                        double c = RowDim(row, "C", shape, 0, mrow, inh), d = RowDim(row, "D", shape, 0, mrow, inh);
                        double ux = a - cx, uy = b - cy;      // 右切点相对圆心 = 半轴1
                        double vx = c - cx, vy = d - cy;      // 上切点相对圆心 = 半轴2
                        double rx = Math.Sqrt(ux * ux + uy * uy);
                        double ry = Math.Sqrt(vx * vx + vy * vy);
                        if (rx < 1e-9) rx = Math.Abs(a - cx);
                        if (ry < 1e-9) ry = Math.Abs(d - cy);
                        ToScreen(cx, cy, out var ccx, out var ccy);
                        path.Commands.Add(new _Ellipse(ccx, ccy, rx * kx, ry * ky));
                        // 包围盒必须含半径：只 Include 圆心会让内容包围盒塌成一个点，
                        // NormalizePage 居中时整页平移过头（frog_eye 偏左上 25px）。
                        path.Include(ccx - rx * kx, ccy - ry * ky);
                        path.Include(ccx + rx * kx, ccy + ry * ky);
                        path.Include(ccx - rx * kx, ccy + ry * ky);
                        path.Include(ccx + rx * kx, ccy - ry * ky);
                        break;
                    }
                    case "NURBSTo":
                    {
                        // 非连接线形状上的 NURBS 曲线（连接线已由 TryRenderNurbsConnector 接管）。
                        double ex = RowDim(row, "X", shape, curX, mrow, inh);
                        double ey = RowDim(row, "Y", shape, curY, mrow, inh);
                        var pts = ParseNurbsPoints(row, gw, gh);
                        if (pts.Count > 0)
                        {
                            var q1 = pts[0];
                            var q2 = pts[pts.Count == 2 ? 1 : pts.Count - 2];
                            ToScreen(q1.X, q1.Y, out var c1x, out var c1y);
                            ToScreen(q2.X, q2.Y, out var c2x, out var c2y);
                            ToScreen(ex, ey, out var nx, out var ny);
                            path.Commands.Add(new _Cubic(c1x, c1y, c2x, c2y, nx, ny));
                        }
                        else
                        {
                            ToScreen(ex, ey, out var fx2, out var fy2);
                            path.Commands.Add(new _Line(fx2, fy2));
                        }
                        curX = ex; curY = ey;
                        break;
                    }
                    case "InfiniteLine":
                    case "PolylineTo":
                    case "SplineStart":
                    case "SplineKnot":
                        double fx = RowDim(row, "X", shape, curX, mrow, inh), fy = RowDim(row, "Y", shape, curY, mrow, inh);
                        ToScreen(fx, fy, out var fx2b, out var fy2b);
                        path.Commands.Add(new _Line(fx2b, fy2b));
                        curX = fx; curY = fy;
                        break;
                    default:
                        break;
                }
            }

            if (path.Commands.Count == 0) continue;
            page.Drawables.Add(path);
        }
    }

    /// <summary>把一条路径在屏幕坐标里整体上下平移。</summary>
    private static void ShiftPathY(PathDrawable p, double dy)
    {
        foreach (var c in p.Commands)
        {
            switch (c)
            {
                case _Move m: m.Y += dy; break;
                case _Line l: l.Y += dy; break;
                case _Arc a: a.Y += dy; break;
                case _Quad q: q.Cy += dy; q.Y += dy; break;
                case _Cubic cu: cu.C1y += dy; cu.C2y += dy; cu.Y += dy; break;
                case _Ellipse e: e.Y += dy; break;
            }
        }
        p.MinY += dy;
        p.MaxY += dy;
    }

    /// <summary>
    /// 由 Visio ArcTo 的（起点、终点、A=弦高/矢高）还原圆弧的半径、圆心与大弧标志。
    ///
    /// A 的语义是「弧中点到弦中点的距离」（sagitta），不是半径、也不是圆弧夹角：
    /// 90° 圆角半径 r 的矢高恰好是 r*(1-cos45°)=0.2929r，母版里写的就是
    /// `A = User.xxxOffset * 0.2929`，可反推 r = 0.1575in，正是圆角半径。
    ///
    /// 旧实现把 A 当夹角算 r = d/(2*sin(A/2))，A=0.046rad 只有 2.6°，
    /// 于是半径被算成 4.8in —— 弧几乎是直线，圆角就被拉成了 45° 斜切。
    ///
    /// 半径公式：r = (d²/4 + h²)/(2|h|)。
    /// A 带符号：正值表示弧凸向弦的右侧（Visio 坐标系 Y 轴向上，即 u 顺时针转 90° 方向）。
    /// </summary>
    private static bool TryArcFromSagitta(double sx, double sy, double ex, double ey, double sagitta,
        out double r, out double cx, out double cy, out bool largeArc)
    {
        r = 0; cx = 0; cy = 0; largeArc = false;
        double dx = ex - sx, dy = ey - sy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double ah = Math.Abs(sagitta);
        if (d < 1e-9 || ah < 1e-9) return false;      // 退化：按直线处理

        r = (d * d / 4 + ah * ah) / (2 * ah);
        if (double.IsInfinity(r) || double.IsNaN(r) || r > 1e6) return false;

        double ux = dx / d, uy = dy / d;
        double nx = uy, ny = -ux;                      // 弦方向顺时针转 90° = 矢高凸起方向
        double mx = (sx + ex) / 2, my = (sy + ey) / 2;
        double off = Math.Sign(sagitta) * (r - ah);    // 圆心在凸起的反方向
        cx = mx - nx * off;
        cy = my - ny * off;
        largeArc = ah > d / 2;                         // 矢高超过半弦长 → 大于半圆
        return true;
    }

    /// <summary>
    /// 渲染文字。ownM 是「该形状本地坐标 → 页面坐标」的变换矩阵，
    /// 锚点用 TxtPin（本地坐标，缺省为 Width/2, Height/2）。
    /// 注意：不能用 parentM 再叠加 Pin——那会把 Pin 平移算两遍，文字被顶到页面上方（曾偏移约 9in）。
    /// </summary>
    private static void RenderText(XElement shape, Affine ownM, XElement? inh, Style style, PageScene page,
        string? textOverride = null, XElement? fieldSource = null, XElement? charSource = null,
        XElement? tabSource = null)
    {
        // HideText=1 的形状不显示文字。AEC 尺寸标注（"48.00 in."等）就靠这个隐藏，
        // 旧实现一律渲染，把平面图铺满 140 条大字号标注，是"文字又大又乱"的主因。
        if (IsTextHidden(shape) || (inh != null && IsTextHidden(inh))) return;

        // 实例子形状可改写 Character 颜色/字号（组织图姓名条是绿字）。
        if (charSource != null)
        {
            var cs = ReadStyle(charSource, null, null);
            if (cs.TextColor != null) style.TextColor = cs.TextColor;
            if (cs.CharSize.HasValue) style.CharSize = cs.CharSize;
            if (cs.FontFamily != null) style.FontFamily = cs.FontFamily;
            if (cs.Bold) style.Bold = true;
            if (cs.Italic) style.Italic = true;
        }

        string? text = textOverride;
        if (text == null)
        {
            var textEl = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
            if (textEl == null) return;
            // 文字可能是 <fld IX='n'> 字段（房间号/面积等），值来自 Field 段。
            // 实例子形状的 Field 段会覆盖母版（把面积等字段置空），所以优先用 fieldSource。
            text = ResolveTextContent(textEl, shape, inh, fieldSource).Trim();
        }
        if (string.IsNullOrEmpty(text)) return;

        // ownM 把「本地坐标」映射到页面坐标。母版实例时这个本地坐标系是**母版根形状**
        // 的坐标系（母版 Width/Height），不是实例 Width/Height：母版几何也是按母版坐标画的，
        // 再由 M 缩放。旧代码用实例 W/2、H/2 当缺省 TxtPin 喂给 M，等于把实例尺寸当母版尺寸，
        // 于是文字按缩放比整体偏离形状中心（1_v2.vsdx 所有黄色节点文字都偏左下约 0.13in）。
        // 因此：缺省锚点必须用 ownM 所对应的坐标系（inh ?? shape）的中心。
        var frame = inh ?? shape;
        double frameW = GetDim(frame, "Width", null, 0);
        double frameH = GetDim(frame, "Height", null, 0);
        double ownW = GetDim(shape, "Width", null, 0);
        double ownH = GetDim(shape, "Height", null, 0);
        double tx = ResolveTextPin(shape, frame, "TxtPinX", frameW, ownW, frameW / 2);
        double ty = ResolveTextPin(shape, frame, "TxtPinY", frameH, ownH, frameH / 2);
        var c = ownM.Apply(tx, ty);
        double sx = c.X, sy = page.Height - c.Y;

        // 旋转：形状自身/各级父级的 Angle 已经包含在矩阵里（atan2 提取总旋转），
        // 再叠加文字自己的 TxtAngle（弧度）。Visio 是 Y 轴向上、逆时针为正，
        // 渲染到屏幕坐标时取反。
        double rotRad = Math.Atan2(ownM.B, ownM.A) + GetDim(shape, "TxtAngle", inh, 0);
        double rotDeg = rotRad * 180.0 / Math.PI;
        if (Math.Abs(rotDeg) < 0.05) rotDeg = 0;

        var td = new TextDrawable
        {
            Text = text,
            // 没有显式文字色时按底色明暗自动取黑/白：
            // 泳道表头是深藏青底，Visio 的「Themed」文字在那里显示为白色。
            Color = style.TextColor ?? ThemedTextColor(style.FillForegnd),
            FontSize = style.CharSize.HasValue ? style.CharSize.Value : 0.1667,
            CenterX = sx,
            CenterY = sy,
            Bold = style.Bold,
            Italic = style.Italic,
            FontFamily = style.FontFamily,
            RotationDeg = rotDeg,
        };
        var lineCount = text.Replace("\r", "").Split('\n').Length;
        td.LineStyles = ReadLineStyles(shape, lineCount, style, (string?)shape.Attribute("TextStyle"));
        // 文本框宽度：用于换行，避免长标签横穿相邻图形。
        // TxtWidth 是本地坐标；ownM 可能带母版实例缩放（db_schema 表格 sx≈3.7），
        // 必须乘上横向缩放才是页面英寸。旧版直接用本地值，0.5in 的母版文本框
        // 被当成 0.6in 页面宽度，表里的列名全被拦腰折断。
        // 留 20% 余量：WPF 默认字体比 Arial 略宽，临界文本不应误换行。
        double scaleX = Math.Sqrt(ownM.A * ownM.A + ownM.B * ownM.B);
        double tw = GetDim(shape, "TxtWidth", inh, 0);
        if (tw <= 0.15) tw = frameW > 0.15 ? frameW : ownW;
        if (tw > 0.15) td.MaxWidth = tw * Math.Max(scaleX, 1e-6) * 1.2;

        td.HorizontalAlignment = ParagraphInt(shape, "HorzAlign") ?? 1;
        td.VerticalAlignment = CellInt(shape, "VerticalAlign") ?? 1;
        var tabStops = ReadTabStops(tabSource ?? shape);
        td.TabStops = tabStops;
        // 保留制表符，由渲染器按 TabStops 分栏定位。旧实现折算成空格，
        // WPF 的比例字体下空格宽度不等于制表位，列名会错位。

        // 文字框（页面坐标）：缺省 TxtPin 在框中心，所以框四角是 pin ± TxtWidth/2、pin ± TxtHeight/2。
        // 左/上对齐时按这个框定位，而不是把整块文字围绕 TxtPin 居中。
        double th = GetDim(shape, "TxtHeight", inh, 0);
        if (th <= 0.15) th = frameH > 0.15 ? frameH : ownH;
        // 非表格的矮文本框（标题/标签）回到基线版的垂直居中，避免标题被顶到框外；
        // db_schema 的区域标签框很高，仍保持 Visio 的顶部对齐。
        if (tabStops == null && th < 0.8 && td.VerticalAlignment == 0)
            td.VerticalAlignment = 1;
        if (Math.Abs(rotDeg) < 0.05 && tw > 0.15 && th > 0.15)
        {
            var topLeft = ownM.Apply(tx - tw / 2, ty + th / 2);
            var botRight = ownM.Apply(tx + tw / 2, ty - th / 2);
            double ml = Math.Max(0, GetDim(shape, "LeftMargin", inh, 0));
            double mr = Math.Max(0, GetDim(shape, "RightMargin", inh, 0));
            double mt = Math.Max(0, GetDim(shape, "TopMargin", inh, 0));
            double mb = Math.Max(0, GetDim(shape, "BottomMargin", inh, 0));
            td.BoxLeft = topLeft.X + ml;
            td.BoxTop = page.Height - topLeft.Y + mt;
            td.BoxWidth = Math.Max(0.01, botRight.X - topLeft.X - ml - mr);
            td.BoxHeight = Math.Max(0.01, topLeft.Y - botRight.Y - mt - mb);
        }

        td.Include(sx, sy);
        if (Trace)
            Console.Error.WriteLine($"  [text] ID={shape.Attribute("ID")?.Value} ov={textOverride != null} " +
                                    $"c=({sx:0.##},{sy:0.##}) fs={td.FontSize:0.####} color={td.Color} " +
                                    $"mw={td.MaxWidth:0.##} ha={td.HorizontalAlignment} va={td.VerticalAlignment} " +
                                    $"box={(td.BoxLeft.HasValue ? $"({td.BoxLeft:0.##},{td.BoxTop:0.##},{td.BoxWidth:0.##})" : "-")} " +
                                    $"org=({ownM.Apply(0, 0).X:0.##},{ownM.Apply(0, 0).Y:0.##}) " +
                                    $"tabs={td.TabStops?.Count ?? 0} '{text.Replace("\n", " ")}'");
        // 文字实际占位：按字数估算宽高，让页面包围盒把文字也算进去（否则贴边文字被裁掉）
        double estW = 0, estH = 0;
        EstimateTextSize(text, td.FontSize, td.MaxWidth, out estW, out estH);
        td.Include(sx - estW / 2, sy - estH / 2);
        td.Include(sx + estW / 2, sy + estH / 2);
        page.Drawables.Add(td);
    }

    /// <summary>HideText=1 表示该形状的文字被显式隐藏（AEC 尺寸标注常用）。</summary>
    private static bool IsTextHidden(XElement el)
    {
        var c = el.Elements().FirstOrDefault(x =>
            x.Name.LocalName == "Cell" && (string?)x.Attribute("N") == "HideText");
        return (string?)c?.Attribute("V") == "1";
    }

    /// <summary>展开文字里的 &lt;fld IX='n'&gt; 字段：优先用 fieldSource（实例子形状）的
    /// Field 段缓存值，其次用 shape 自身、再其次 inh。字段行存在但值为空表示
    /// 「该字段被禁用」（如 User.DisplayFieldDisabled），必须显示为空而不是回退到占位符。</summary>
    private static string ResolveTextContent(XElement textEl, XElement shape, XElement? inh, XElement? fieldSource)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var node in textEl.Nodes())
        {
            if (node is XText xt) { sb.Append(xt.Value); continue; }
            if (node is not XElement e) continue;
            if (e.Name.LocalName == "fld")
            {
                var ix = (string?)e.Attribute("IX");
                if (TryLookupField(ix, fieldSource, out var v) ||
                    TryLookupField(ix, shape, out v) ||
                    TryLookupField(ix, inh, out v))
                    sb.Append(v);
                else
                    sb.Append(e.Value);   // 没有 Field 定义时用标签里的缓存文本
            }
            // cp/pp/Field 等格式标记不产生可见字符
        }
        return sb.ToString();
    }

    private static bool TryLookupField(string? ix, XElement? owner, out string value)
    {
        value = "";
        if (ix == null || owner == null) return false;
        var sec = owner.Elements().FirstOrDefault(s =>
            s.Name.LocalName == "Section" && (string?)s.Attribute("N") == "Field");
        if (sec == null) return false;
        var row = sec.Elements().FirstOrDefault(r =>
            r.Name.LocalName == "Row" && (string?)r.Attribute("IX") == ix);
        if (row == null) return false;
        value = (string?)row.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "Value")?.Attribute("V") ?? "";
        return true;
    }

    /// <summary>
    /// 求文字锚点（返回 ownM 所映射坐标系里的值）。
    /// - 单元格写在实例上：V 是「实例本地坐标」，要按 frameSize/ownSize 换算到母版坐标系。
    /// - 单元格只写在母版上（F='Inh' / 缺失）：V 已经是母版坐标系，直接用。
    /// - 两边都没有：用该坐标系的中心。
    /// </summary>
    private static double ResolveTextPin(XElement shape, XElement frame, string name,
        double frameSize, double ownSize, double def)
    {
        if (TryCellNumber(shape, name, out var v))
        {
            if (!ReferenceEquals(shape, frame) && Math.Abs(ownSize) > 1e-9 && Math.Abs(frameSize) > 1e-9)
                return v * (frameSize / ownSize);
            return v;
        }
        if (TryCellNumber(frame, name, out var mv)) return mv;
        return def;
    }

    /// <summary>读取单元格的数值缓存（F='Inh' 视为"没有"，交给上级继承）。</summary>
    private static bool TryCellNumber(XElement? el, string name, out double v)
    {
        v = 0;
        var c = el?.Elements().FirstOrDefault(x =>
            x.Name.LocalName == "Cell" && (string?)x.Attribute("N") == name);
        if (c == null) return false;
        var f = (string?)c.Attribute("F");
        if (f == "Inh" || f == "INH") return false;
        var s = (string?)c.Attribute("V");
        if (s != null && double.TryParse(s, NF, CultureInfo.InvariantCulture, out v)) return true;
        if (f != null && el != null)
        {
            var ev = EvalFormula(f, el, null);
            if (ev.HasValue) { v = ev.Value; return true; }
        }
        return false;
    }

    /// <summary>按底色明暗选对比色：深底用白字、浅底用黑字。</summary>
    private static string ContrastTextColor(string? fill)
    {
        if (string.IsNullOrEmpty(fill)) return "#000000";
        try { return Luminance(fill!) < 120 ? "#FEFFFF" : "#000000"; }
        catch { return "#000000"; }
    }

    /// <summary>「Themed」文字色：有明确底色时按对比取黑/白；
    /// 只有文字块本身完全无底色（透明）时才用主题 accent1。
    /// 早期对浅底也套 accent1，导致 network_topology 的 Legend 标题与数据行
    /// 变成主题红色 #C05046（参考图是黑色）。orgchart 的 "Executive Staff"
    /// 恰好是无底色文字块，仍能取到主题蓝色。</summary>
    private static string ThemedTextColor(string? fill)
    {
        var c = ContrastTextColor(fill);
        if (fill != null) return c;
        return ThemeAccent1 ?? c;
    }

    /// <summary>粗略估算文字占位（CJK 按 1em、其余 0.55em；行高 1.3em）。</summary>
    private static void EstimateTextSize(string text, double fontSize, double? maxWidth, out double w, out double h)
    {
        double total = 0;
        foreach (var ch in text.Replace("\n", ""))
            total += ch >= 0x2E80 ? fontSize : fontSize * 0.55;
        int lines = 1;
        if (maxWidth.HasValue && maxWidth.Value > 0 && total > maxWidth.Value)
            lines = (int)Math.Ceiling(total / maxWidth.Value);
        w = maxWidth.HasValue ? Math.Min(total, maxWidth.Value) : total;
        h = fontSize * 1.3 * lines;
    }

    // ---------------- 母版内容测量（计算真实包围盒，用于缩放） ----------------

    /// <summary>本地坐标系下的包围盒（命中即 Has=true）。</summary>
    private sealed class BBox
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue;
        public bool Has;
        public double Width => Has ? MaxX - MinX : 0;
        public double Height => Has ? MaxY - MinY : 0;
        public double CenterX => Has ? (MinX + MaxX) / 2 : 0;
        public double CenterY => Has ? (MinY + MaxY) / 2 : 0;
        public void Include(double x, double y)
        {
            Has = true;
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }
    }

    /// <summary>计算一个母版（其 ShapesRoot 的子形状）的真实内容包围盒。</summary>
    private static void ComputeNaturalSize(MasterDef md, Dictionary<int, MasterDef> masters)
    {
        var b = new BBox();
        MeasureShapesRoot(md.ShapesRoot, masters, md.Id, ref b);
        if (b.Has)
        {
            md.NatX0 = b.MinX; md.NatY0 = b.MinY; md.NatW = b.Width; md.NatH = b.Height;
        }
        else
        {
            md.NatX0 = 0; md.NatY0 = 0;
            md.NatW = Math.Max(md.Width, 1e-3); md.NatH = Math.Max(md.Height, 1e-3);
        }
    }

    private static void MeasureShapesRoot(XElement root, Dictionary<int, MasterDef> masters, int ctx, ref BBox acc)
    {
        foreach (var sh in root.Elements().Where(e => e.Name.LocalName == "Shape"))
            MeasureShape(sh, Affine.Identity, masters, ctx, 0, ref acc);
    }

    /// <summary>
    /// 测量一个形状对其父局部坐标系的贡献（scale=1，不做实例缩放）。
    /// 处理：母版实例 / MasterShape / 分组 / 自身几何。
    /// </summary>
    private static void MeasureShape(
        XElement shape, Affine parentM, Dictionary<int, MasterDef> masters,
        int ctx, int depth, ref BBox acc)
    {
        if (depth > 24) return;

        var masterAttr = (string?)shape.Attribute("Master");
        if (masterAttr != null && int.TryParse(masterAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid) && masters.TryGetValue(mid, out var mdef))
        {
            // 嵌套母版实例：需按"本实例相对该母版标称尺寸的缩放"把母版内容缩放到父坐标系，
            // 否则会把母版本地坐标(可能很大)原样计入父包围盒，导致父级自然尺寸失真并二次放大。
            double iWn = GetDim(shape, "Width", null, mdef.Width);
            double iHn = GetDim(shape, "Height", null, mdef.Height);
            double sxN = mdef.Width > 1e-6 ? iWn / mdef.Width : 1;
            double syN = mdef.Height > 1e-6 ? iHn / mdef.Height : 1;
            double pinX = GetDim(shape, "PinX", null, 0);
            double pinY = GetDim(shape, "PinY", null, 0);
            double lx = GetDim(shape, "LocPinX", null, mdef.LocPinX);
            double ly = GetDim(shape, "LocPinY", null, mdef.LocPinY);
            double a = GetDim(shape, "Angle", null, 0);
            var mLocal = Affine.MakePlacement(pinX, pinY, lx, ly, a, sxN, syN);
            var M = Affine.Compose(parentM, mLocal);
            MeasureShapesRoot(mdef.ShapesRoot, masters, mdef.Id, ref acc);
            return;
        }

        var msAttr = (string?)shape.Attribute("MasterShape");
        if (msAttr != null && TryResolveMasterShape(masters, ctx, msAttr, out var kmid, out var kEl) && kEl != null)
        {
            var cb = new BBox();
            MeasureShape(kEl, Affine.Identity, masters, kmid, depth + 1, ref cb);
            if (!cb.Has)
            {
                double kW = GetDim(kEl, "Width", null, 1), kH = GetDim(kEl, "Height", null, 1);
                cb.MinX = 0; cb.MinY = 0; cb.MaxX = kW; cb.MaxY = kH; cb.Has = true;
            }
            double pinX = GetDim(shape, "PinX", kEl, 0);
            double pinY = GetDim(shape, "PinY", kEl, 0);
            double lx = cb.CenterX, ly = cb.CenterY;
            double a = GetDim(shape, "Angle", kEl, 0);
            var mLocal = Affine.MakePlacement(pinX, pinY, lx, ly, a, 1, 1);
            var M = Affine.Compose(parentM, mLocal);
            IncludeBox(cb, M, ref acc);
            return;
        }

        double w = GetDim(shape, "Width", null, 0), h = GetDim(shape, "Height", null, 0);
        double lx2 = GetDim(shape, "LocPinX", null, w / 2), ly2 = GetDim(shape, "LocPinY", null, h / 2);
        double px = GetDim(shape, "PinX", null, 0), py = GetDim(shape, "PinY", null, 0);
        double a2 = GetDim(shape, "Angle", null, 0);
        var mLocal2 = Affine.MakePlacement(px, py, lx2, ly2, a2, 1, 1);
        var M2 = Affine.Compose(parentM, mLocal2);
        var childShapes = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (childShapes != null)
        {
            foreach (var ch in childShapes.Elements().Where(e => e.Name.LocalName == "Shape"))
                MeasureShape(ch, M2, masters, ctx, depth + 1, ref acc);
            return;
        }
        MeasureGeometry(shape, M2, ref acc);
    }

    private static void IncludeBox(BBox b, Affine M, ref BBox acc)
    {
        var p1 = M.Apply(b.MinX, b.MinY); acc.Include(p1.X, p1.Y);
        var p2 = M.Apply(b.MaxX, b.MinY); acc.Include(p2.X, p2.Y);
        var p3 = M.Apply(b.MinX, b.MaxY); acc.Include(p3.X, p3.Y);
        var p4 = M.Apply(b.MaxX, b.MaxY); acc.Include(p4.X, p4.Y);
    }

    private static void MeasureGeometry(XElement shape, Affine M, ref BBox acc)
    {
        var geomSections = shape.Elements()
            .Where(e => e.Name.LocalName == "Section" &&
                        ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal));
        double gw = GetDim(shape, "Width", null, 1);
        double gh = GetDim(shape, "Height", null, 1);
        foreach (var sec in geomSections)
        {
            double curX = 0, curY = 0;
            foreach (var row in sec.Elements().Where(e => e.Name.LocalName == "Row"))
            {
                var t = (string?)row.Attribute("T") ?? "LineTo";
                double x, y;
                switch (t)
                {
                    case "MoveTo":
                        curX = RowDim(row, "X", shape, 0); curY = RowDim(row, "Y", shape, 0);
                        (x, y) = (curX, curY); break;
                    case "LineTo":
                        curX = RowDim(row, "X", shape, 0); curY = RowDim(row, "Y", shape, 0);
                        (x, y) = (curX, curY); break;
                    case "RelMoveTo":
                        curX = RowDim(row, "X", shape, 0) * gw; curY = RowDim(row, "Y", shape, 0) * gh;
                        (x, y) = (curX, curY); break;
                    case "RelLineTo":
                        curX = RowDim(row, "X", shape, 0) * gw; curY = RowDim(row, "Y", shape, 0) * gh;
                        (x, y) = (curX, curY); break;
                    case "ArcTo":
                    case "EllipticalArcTo":
                        x = RowDim(row, "X", shape, 0); y = RowDim(row, "Y", shape, 0);
                        curX = x; curY = y; break;
                    case "Ellipse":
                    {
                        // 椭圆的包围盒必须含半径，否则内容包围盒只剩圆心点，
                        // NormalizePage 居中会平移过头（frog_eye 整体偏左上 25px）。
                        double ecx = RowDim(row, "X", shape, 0), ecy = RowDim(row, "Y", shape, 0);
                        double a3 = RowDim(row, "A", shape, 0), b3 = RowDim(row, "B", shape, 0);
                        double c3 = RowDim(row, "C", shape, 0), d3 = RowDim(row, "D", shape, 0);
                        double erx = Math.Sqrt((a3 - ecx) * (a3 - ecx) + (b3 - ecy) * (b3 - ecy));
                        double ery = Math.Sqrt((c3 - ecx) * (c3 - ecx) + (d3 - ecy) * (d3 - ecy));
                        for (int k = 0; k < 4; k++)
                        {
                            var q = M.Apply(ecx + (k % 2 == 0 ? erx : -erx), ecy + (k < 2 ? ery : -ery));
                            acc.Include(q.X, q.Y);
                        }
                        x = ecx; y = ecy; curX = x; curY = y; break;
                    }
                    default:
                        continue;
                }
                var p = M.Apply(x, y);
                acc.Include(p.X, p.Y);
            }
        }
    }

    // ---------------- 母版子形状解析 ----------------

    private static bool TryResolveMasterShape(
        Dictionary<int, MasterDef> masters, int? contextMasterId, string ks,
        out int masterId, out XElement? kEl)
    {
        masterId = 0; kEl = null;
        if (!int.TryParse(ks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kid)) return false;

        // MasterShape 引用"当前分组实例所属母版"内部的子形状（Visio 标准语义）。
        // 只在该母版内查找，避免跨母版误匹配导致缩放比失控。
        if (contextMasterId.HasValue && masters.TryGetValue(contextMasterId.Value, out var md))
        {
            var e = FindShapeById(md.ShapesRoot, ks);
            if (e != null) { masterId = contextMasterId.Value; kEl = e; return true; }
        }
        return false;
    }

    private static XElement? FindShapeById(XElement root, string id)
    {
        foreach (var s in root.Descendants().Where(e => e.Name.LocalName == "Shape"))
        {
            if ((string?)s.Attribute("ID") == id) return s;
        }
        return null;
    }

    private static bool HasConcreteGeom(XElement sh)
    {
        foreach (var sec in sh.Elements().Where(e => e.Name.LocalName == "Section" &&
                     ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal)))
        {
            foreach (var row in sec.Elements().Where(e => e.Name.LocalName == "Row"))
            {
                foreach (var c in row.Elements().Where(e => e.Name.LocalName == "Cell"))
                {
                    if ((string?)c.Attribute("V") != null) return true;
                }
            }
        }
        return false;
    }

    // 说明：HasConcreteGeom 目前不再参与"整段取母版还是取实例"的判断
    // （改为逐单元格继承：实例缺失的单元格从母版同行补），保留作为调试辅助。

    // ---------------- 数值 / 公式 ----------------

    /// <summary>
    /// VSDX 里单元格的 V 永远是「内部单位」：长度即英寸、角度即弧度。
    /// U 只是 ShapeSheet 界面上的显示单位（如 U='MM' 时 V 仍是英寸换算后的值，
    /// 1.7717 表示 45mm）。若按 U 再换算一次，尺寸会缩小 25.4 倍 —— 这正是
    /// 「泳道宽度变成 0.07in（2mm）、数据库小图标看不见」的根因，故此处不做换算。
    /// </summary>
    private static double ToInch(double v, string? u) => v;

    /// <summary>
    /// 直接读取单元格的 V 值（内部单位，长度即英寸），不做 U 换算。
    /// 用于 PageWidth/PageHeight 这类"V 恒为英寸、U 仅显示用"的单元格。
    /// </summary>
    private static double GetInchValue(XElement el, string name, double def)
    {
        var cell = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        if (cell == null) return def;
        var v = (string?)cell.Attribute("V");
        if (v != null && double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d)) return d;
        return def;
    }

    /// <summary>优先用实例缓存的 V（Visio 算好的结果）；V 缺失才向母版继承。
    /// Foreign 图片的 Pin/Size 常写 F='Inh'，默认值 GetDim 会无视更准确的实例缓存。</summary>
    private static double GetDimPreferCache(XElement el, string name, XElement? inh, double def)
    {
        var cell = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        var v = (string?)cell?.Attribute("V");
        var f = (string?)cell?.Attribute("F");
        bool inherited = f == "Inh" || f == "INH";
        if (v != null && double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d))
        {
            var val = ToInch(d, (string?)cell!.Attribute("U"));
            // Visio 常把"继承但未求值"的单元格缓存成 V=0 F='Inh'（如隐藏实例的图片宽高）。
            // 这种情况下 0 不是有效尺寸，必须回退到母版缓存值，否则图片被当成 0×0 丢弃。
            if (Math.Abs(val) < 1e-9 && inherited && inh != null)
                return GetDim(inh, name, null, def);
            return val;
        }
        return GetDim(el, name, inh, def);
    }

    /// <summary>
    /// 读取形状的某单元格为英寸。处理 V（带单位）、F='Inh'（向 inh 继承）、简单公式。
    /// </summary>
    private static double GetDim(XElement el, string name, XElement? inh, double def)
    {
        var cell = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        if (cell != null)
        {
            var v = (string?)cell.Attribute("V");
            var u = (string?)cell.Attribute("U");
            if (v != null && double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d))
                return ToInch(d, u);
            var f = (string?)cell.Attribute("F");
            if (f != null)
            {
                if (f == "Inh" || f == "INH")
                {
                    if (inh != null) return GetDim(inh, name, null, def);
                    return def;
                }
                var fd = EvalFormula(f, el, inh);
                if (fd.HasValue) return fd.Value;
            }
        }
        if (inh != null) return GetDim(inh, name, null, def);
        return def;
    }

    /// <summary>
    /// 读取几何行单元格为英寸（几何公式会引用所属形状的 Width/Height）。
    /// 优先求值公式 F：缓存的 V 可能是翻转/变换后的陈旧值（如 LineTo!Y 的 V=7.283
    /// 而非公式 Height*1=2.318mm），直接信任 V 会导致坐标放大几十倍。F 求值失败才回退 V。
    /// </summary>
    private static double RowDim(XElement row, string name, XElement owner, double def,
        XElement? altRow = null, XElement? inh = null)
    {
        var cell = row.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        // 实例行可能只写了被改动过的单元格（其余靠母版继承），缺失时取母版同行的单元格
        if (cell == null && altRow != null)
            cell = altRow.Elements().FirstOrDefault(c =>
                c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        if (cell == null) return def;
        var f = (string?)cell.Attribute("F");
        if (f != null && f.Trim() != "" && !f.Trim().Equals("No Formula", StringComparison.OrdinalIgnoreCase))
        {
            var fd = EvalFormula(f, owner, inh);
            if (fd.HasValue) return fd.Value;
        }
        var v = (string?)cell.Attribute("V");
        var u = (string?)cell.Attribute("U");
        if (v != null && double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d))
            return ToInch(d, u);
        return def;
    }

    /// <summary>在母版形状中查找同名几何区。</summary>
    private static XElement? FindMasterSection(XElement? inh, string name)
    {
        if (inh == null) return null;
        return inh.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == name);
    }

    /// <summary>
    /// 实例是否带有「完整」几何：母版里每个 Geometry 段在实例里都有同名段，且实例段的行数
    /// 不少于母版段。少行说明实例只做了局部覆盖（其余靠继承），此时必须走母版几何。
    /// </summary>
    private static bool HasCompleteInstanceGeometry(XElement shape, XElement mroot)
    {
        static bool IsGeom(XElement e) => e.Name.LocalName == "Section" &&
            ((string?)e.Attribute("N") ?? "").StartsWith("Geometry", StringComparison.Ordinal);

        var msecs = mroot.Elements().Where(IsGeom).ToList();
        if (msecs.Count == 0) return false;
        foreach (var ms in msecs)
        {
            string name = (string?)ms.Attribute("N") ?? "Geometry";
            var isec = shape.Elements().FirstOrDefault(e => IsGeom(e) && (string?)e.Attribute("N") == name);
            if (isec == null) return false;
            int mr = ms.Elements().Count(e => e.Name.LocalName == "Row");
            int ir = isec.Elements().Count(e => e.Name.LocalName == "Row");
            if (mr == 0 || ir < mr) return false;
        }
        return true;
    }

    /// <summary>在母版几何区中查找同 (T, IX) 的行。</summary>
    private static XElement? FindMasterRow(XElement? msec, XElement row)
    {
        if (msec == null) return null;
        var t = (string?)row.Attribute("T") ?? "LineTo";
        var ix = (string?)row.Attribute("IX") ?? "";
        return msec.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Row" &&
            ((string?)e.Attribute("T") ?? "LineTo") == t &&
            (((string?)e.Attribute("IX")) ?? "") == ix);
    }



    private static double? EvalFormula(string f, XElement owner, XElement? inh)
    {
        if (string.IsNullOrWhiteSpace(f)) return null;
        f = f.Trim();
        if (f == "No Formula" || f == "no formula") return 0;
        if (f == "Inh" || f == "INH") return null;

        // IF(cond, a, b)
        if (f.StartsWith("IF(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = f.Substring(3);
            if (inner.EndsWith(")")) inner = inner.Substring(0, inner.Length - 1);
            var args = SplitTopArgs(inner);
            if (args.Count == 3)
            {
                bool cond = FormulaIsTrue(args[0].Trim(), owner);
                var sel = cond ? args[1].Trim() : args[2].Trim();
                var sv = EvalFormula(sel, owner, inh);
                if (sv.HasValue) return sv.Value;
            }
        }

        // 形如 Width*0.5 / Sheet.5!Height*1 / Width*3.64432142E-6（系数可带科学计数法）
        var m = Regex.Match(f, @"(?:Sheet\.\d+!)?([A-Za-z_][A-Za-z0-9_]*)\s*\*\s*([0-9]*\.?[0-9]+(?:[eE][-+]?\d+)?)");
        if (m.Success)
        {
            if (!double.TryParse(m.Groups[2].Value, NF, CultureInfo.InvariantCulture, out var k)) return null;
            var baseVal = ResolveName(owner, m.Groups[1].Value, inh);
            return baseVal.HasValue ? baseVal.Value * k : (double?)null;
        }
        // 单独的名字（如 Width）
        var m2 = Regex.Match(f, @"(?:Sheet\.\d+!)?([A-Za-z_][A-Za-z0-9_]*)");
        if (m2.Success && m2.Length == f.Length)
            return ResolveName(owner, m2.Groups[1].Value, inh);

        if (double.TryParse(f, NF, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    /// <summary>按顶层逗号切分（忽略括号内的逗号），用于 IF(a,b,c)。</summary>
    private static List<string> SplitTopArgs(string s)
    {
        var res = new List<string>();
        int depth = 0; var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            if (ch == ',' && depth == 0) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    /// <summary>粗略判断公式条件是否为真（FlipX/FlipY 等常见情形）。</summary>
    private static bool FormulaIsTrue(string expr, XElement owner)
    {
        expr = expr.Trim();
        if (expr.IndexOf("FlipX", StringComparison.OrdinalIgnoreCase) >= 0)
            return GetDim(owner, "FlipX", null, 0) != 0;
        if (expr.IndexOf("FlipY", StringComparison.OrdinalIgnoreCase) >= 0)
            return GetDim(owner, "FlipY", null, 0) != 0;
        if (double.TryParse(expr, NF, CultureInfo.InvariantCulture, out var d)) return d != 0;
        var ev = EvalFormula(expr, owner, null);
        if (ev.HasValue) return ev.Value != 0;
        return false;
    }

    private static double? ResolveName(XElement owner, string nm, XElement? inh)
    {
        switch (nm.ToLowerInvariant())
        {
            case "width": return GetDimNullable(owner, "Width", inh);
            case "height": return GetDimNullable(owner, "Height", inh);
            case "locpinx": return GetDimNullable(owner, "LocPinX", inh);
            case "locpiny": return GetDimNullable(owner, "LocPinY", inh);
            case "pinx": return GetDimNullable(owner, "PinX", inh);
            case "piny": return GetDimNullable(owner, "PinY", inh);
            // 无法解析的名字（如 Sheet.N!Controls.Row_1.Y）返回 null，
            // 让 RowDim 回退到单元格的 V 缓存值——Visio 已经把结果算好存在 V 里。
            default: return null;
        }
    }

    // ---------------- 样式 ----------------

    /// <summary>
    /// 读 Visio 的 FillGradient 段（填充渐变）。Visio 在 FillGradientEnabled=1 时，
    /// 每个 Row 是一个色标：GradientStopColor（缓存 hex）+ GradientStopPosition（0..1）。
    /// 常见于 MS 示例图里的池背景（蓝/橙/红渐变）。Del='1' 的行是历史残留，跳过。
    /// </summary>
    private static IReadOnlyList<GradientStopDef>? ReadGradient(XElement shape, XElement? inh)
    {
        XElement? sec = shape.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "FillGradient");
        if (sec == null && inh != null)
            sec = inh.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "FillGradient");
        if (sec == null) return null;
        var stops = new List<GradientStopDef>();
        foreach (var row in sec.Elements().Where(e => e.Name.LocalName == "Row"))
        {
            if ((string?)row.Attribute("Del") == "1") continue;
            var colorCell = row.Elements().FirstOrDefault(c =>
                c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "GradientStopColor");
            var posCell = row.Elements().FirstOrDefault(c =>
                c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "GradientStopPosition");
            var raw = (string?)colorCell?.Attribute("V");
            if (raw == null) continue;
            string color = raw.StartsWith("#") ? raw.ToLowerInvariant() : GetColor(row, "GradientStopColor", null) ?? raw;
            double pos = 0;
            var pv = (string?)posCell?.Attribute("V");
            if (pv != null && double.TryParse(pv, NF, CultureInfo.InvariantCulture, out var pd))
                pos = pd;
            stops.Add(new GradientStopDef(pos, color));
        }
        if (stops.Count < 2) return null;
        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        return stops;
    }

    /// <summary>
    /// Visio 2013 的 QuickStyle「主题样式」把填充色写成样式矩阵索引，而不在单元格里
    /// 存具体颜色。QuickStyleFillColor 100..106 对应主题 accent1..accent7（accent7
    /// 超出本实现范围），100 即 accent1。母版通常不写该单元格，需沿 FillStyle 链
    /// 回退到文档样式表（ms_diagrams 的样式表 2 里就是 100）。
    /// 返回 null 表示没有可解析的强调色。
    /// </summary>
    private static string? QuickStyleAccentColor(XElement shape, XElement masterRoot)
    {
        // 形状/母版本身若显式写了某个文档颜色索引，优先用它。
        string? raw = CellVal(shape, "QuickStyleFillColor") ?? CellVal(masterRoot, "QuickStyleFillColor");
        if (raw == null)
        {
            // 沿母版 FillStyle → 文档样式表链找。
            var fillStyle = (string?)masterRoot.Attribute("FillStyle")
                            ?? (string?)shape.Attribute("FillStyle");
            raw = StyleCellRaw(fillStyle, "QuickStyleFillColor");
        }
        if (raw == null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            return null;
        int accentIndex = idx - 100; // 100→accent1(数组索引0)
        if (accentIndex < 0 || accentIndex >= ThemeAccents.Length || ThemeAccents[accentIndex] == null)
            return null;
        return ThemeAccents[accentIndex];
    }

    private static Style ReadStyle(XElement shape, Style? inherited, XElement? inh)
    {
        var lineStyle = (string?)shape.Attribute("LineStyle");
        var fillStyle = (string?)shape.Attribute("FillStyle");
        var textStyle = (string?)shape.Attribute("TextStyle");
        var st = new Style();
        // FillPattern 先取——图案填充（≥2）时 FillForegnd/FillBkgnd 的调色板索引
        // 必须解析（Space 母版 FillStyle=34 的 FillForegnd=9 绿 / FillBkgnd=1 白）；
        // 纯色/无填充则保持旧行为（忽略调色板索引，防默认样式误染色）。
        int fp = CellInt(shape, "FillPattern") ?? StyleInt(fillStyle, "FillPattern") ?? 1;
        bool patFill = fp >= 2 && fp <= 24;
        st.FillForegnd = GetColor(shape, "FillForegnd", inh) ?? StyleColor(fillStyle, "FillForegnd", patFill);
        st.FillBkgnd = GetColor(shape, "FillBkgnd", inh) ?? StyleColor(fillStyle, "FillBkgnd", patFill);
        st.LineColor = GetColor(shape, "LineColor", inh) ?? StyleColor(lineStyle, "LineColor");
        st.FillPattern = CellInt(shape, "FillPattern") ?? StyleInt(fillStyle, "FillPattern");
        st.LinePattern = CellInt(shape, "LinePattern") ?? StyleInt(lineStyle, "LinePattern");
        st.LineWeight = GetDimNullable(shape, "LineWeight", inh) ?? StyleDim(lineStyle, "LineWeight");
        st.FillTrans = GetDimNullable(shape, "FillForegndTrans", inh) ?? StyleDim(fillStyle, "FillForegndTrans");
        st.LineTrans = GetDimNullable(shape, "LineColorTrans", inh) ?? StyleDim(lineStyle, "LineColorTrans");
        st.GradientAngle = GetDim(shape, "FillGradientAngle", null, Math.PI / 2);
        // 渐变填充：色标在 FillGradient 段，FillGradientEnabled=1 时启用。
        // 池背景（ms_diagrams）就是这种：FillPattern=0 但 FillGradientEnabled=1。
        if (CellInt(shape, "FillGradientEnabled") == 1)
            st.GradientStops = ReadGradient(shape, inh);
        var ch = shape.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" &&
            ((string?)e.Attribute("N") == "Character" || (string?)e.Attribute("N") == "Character1"));
        if (ch != null)
        {
            var row = ch.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
            if (row != null)
            {
                st.TextColor = GetColor(row, "Color", null) ?? StyleColor(textStyle, "Color");
                var sz = CellRaw(row, "Size");
                if (sz != null && double.TryParse(sz, NF, CultureInfo.InvariantCulture, out var z))
                {
                    // Size 的内部单位即英寸（U 仅是显示单位，如 0.1944in=14pt）。
                    // Visio 的 FontScale 是字体缩放百分比，timeline 等文件里常见 0.63/0.8/0.93；
                    // 旧实现忽略它，文字会比参考大 10%~60%。
                    var fsr = CellRaw(row, "FontScale");
                    double fontScale = fsr != null &&
                                       double.TryParse(fsr, NF, CultureInfo.InvariantCulture, out var fv)
                        ? fv
                        : StyleDim(textStyle, "FontScale") ?? 1;
                    st.CharSize = z * (fontScale > 1e-6 ? fontScale : 1);
                }

                // 字体：西文 Font 优先，中文取 AsianFont
                st.FontFamily = ResolveFontName(CellRaw(row, "Font"))
                                ?? ResolveFontName(CellRaw(row, "AsianFont"));

                // Style 是位掩码：bit0=粗体、bit1=斜体（Style=17 即 粗体+全大写）
                var sty = CellRaw(row, "Style");
                if (sty != null && int.TryParse(sty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
                {
                    st.Bold = (bits & 1) != 0;
                    st.Italic = (bits & 2) != 0;
                }
            }
        }
        return st.Merge(inherited);
    }

    /// <summary>
    /// 读取每个段落对应的 Character run 样式。network_topology 的设备标签
    /// 第一行 18pt 粗体、后续 12pt/10pt 常规；旧实现只套第 0 行，整段都变大变粗。
    /// 只有行数与文本行数一致时才启用，避免 cp run 与段落不对应的文件误用。
    /// </summary>
    private static List<TextLineStyle>? ReadLineStyles(
        XElement shape, int lineCount, Style fallback, string? textStyle)
    {
        if (lineCount < 2) return null;
        var sec = shape.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" &&
            ((string?)e.Attribute("N") == "Character" || (string?)e.Attribute("N") == "Character1"));
        if (sec == null) return null;
        var rows = sec.Elements()
            .Where(e => e.Name.LocalName == "Row")
            .ToDictionary(e => (string?)e.Attribute("IX") ?? "0", e => e);
        if (rows.Count < 2) return null;
        var result = new List<TextLineStyle>();
        for (int i = 0; i < lineCount; i++)
        {
            var style = new TextLineStyle
            {
                FontSize = fallback.CharSize ?? 0.1667,
                Color = fallback.TextColor,
                FontFamily = fallback.FontFamily,
                Bold = fallback.Bold,
                Italic = fallback.Italic,
            };
            if (rows.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out var row))
            {
                var sz = CellRaw(row, "Size");
                if (sz != null && double.TryParse(sz, NF, CultureInfo.InvariantCulture, out var z))
                {
                    var fsr = CellRaw(row, "FontScale");
                    double fs = fsr != null &&
                                double.TryParse(fsr, NF, CultureInfo.InvariantCulture, out var fv)
                        ? fv
                        : StyleDim(textStyle, "FontScale") ?? 1;
                    style.FontSize = z * (fs > 1e-6 ? fs : 1);
                }
                style.Color = GetColor(row, "Color", null) ?? style.Color;
                style.FontFamily = ResolveFontName(CellRaw(row, "Font"))
                                   ?? ResolveFontName(CellRaw(row, "AsianFont"))
                                   ?? style.FontFamily;
                var sty = CellRaw(row, "Style");
                if (sty != null && int.TryParse(sty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
                {
                    style.Bold = (bits & 1) != 0;
                    style.Italic = (bits & 2) != 0;
                }
            }
            result.Add(style);
        }
        return result;
    }

    /// <summary>判断某单元格公式是否含指定函数（如 THEMEVAL），用于识别随主题变化的颜色。</summary>
    private static bool CellFormulaContains(XElement el, string name, string token)
    {
        var f = (string?)el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name)?.Attribute("F");
        return f != null && f.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>直接取单元格的原始 V 字符串（不做单位/颜色解释）。</summary>
    private static string? CellRaw(XElement el, string name)
        => (string?)el.Elements().FirstOrDefault(c =>
               c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name)?.Attribute("V");

    /// <summary>读取 Paragraph 段首个 Row 的对齐单元格（0=左/上，1=中，2=右/下）。</summary>
    private static int? ParagraphInt(XElement el, string name)
    {
        var sec = el.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Paragraph");
        var row = sec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
        var cell = row?.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        var v = (string?)cell?.Attribute("V");
        return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;
    }

    /// <summary>读取实例子形状的 Tabs 制表位（实例缓存值已是页面英寸，不再缩放）。</summary>
    private static IReadOnlyList<double>? ReadTabStops(XElement? source)
    {
        var sec = source?.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" && (string?)e.Attribute("N") == "Tabs");
        var row = sec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
        if (row == null) return null;
        List<double>? stops = null;
        for (int i = 0; i < 32; i++)
        {
            var cell = row.Elements().FirstOrDefault(c =>
                c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == "Position" + i);
            var v = (string?)cell?.Attribute("V");
            if (v == null || !double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d) || d <= 0) continue;
            stops ??= new List<double>();
            stops.Add(d);
        }
        return stops;
    }

    private static string? GetColor(XElement el, string name, XElement? inh)
    {
        var cell = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        string? raw = null;
        if (cell != null)
        {
            var f = (string?)cell.Attribute("F");
            var v = (string?)cell.Attribute("V");

            // 1) V 是 Visio 缓存好的「计算结果」，只要它本身就是 #RRGGBB 就最可信。
            //    典型：标题 Color V='#262626' F='THEMEGUARD(MSOTINT(RGB(0,0,0),15))'
            //    ——按公式取 RGB(0,0,0) 会变成纯黑，比 V 更差。
            var vLit = ColorUtil.ParseLiteral(v);
            if (vLit != null) return vLit;

            // 2) 公式里带 RGB(r,g,b) 的（含 THEMEGUARD(RGB(...))）——取这个字面量颜色。
            //    注意 THEMEGUARD 也含 "THEME" 字样，过去被误判成主题色，导致泳道表头
            //    从深藏青 #1A3B88 变成浅灰。
            if (f != null)
            {
                var lit = ColorUtil.Parse(f);
                if (lit != null) return lit;
            }

            // 3) V 是调色板索引（确定色）时优先用 V，即使 F 里出现 THEMEVAL。
            //    典型：orgchart 姓名条下方的白底条
            //    F=GUARD(IF(AND(ISTHEMED(),User.Embellishment=2),1,THEMEVAL())) V='1'
            //    ——Visio 算出来是白；过去这里先按 THEMEVAL 回退主题色，白条被染红。
            bool vIsIndex = v != null &&
                            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            if (!vIsIndex && f != null && f.IndexOf("THEMEVAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (name.StartsWith("Fill", StringComparison.OrdinalIgnoreCase)) return ThemeFill;
                if (name.StartsWith("Line", StringComparison.OrdinalIgnoreCase)) return ThemeLine;
                return null;
            }

            // 4) 缓存值（调色板索引）
            raw = v ?? f;
            if (raw == "Inh" || raw == "INH")
                raw = inh != null ? GetColor(inh, name, null) : null;
            else if (raw == "Themed")
                raw = inh != null ? GetColor(inh, name, null) : null;
        }
        else if (inh != null)
        {
            raw = GetColor(inh, name, null);
        }
        if (raw == null) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
        {
            if (DocColors.TryGetValue(idx, out var custom)) return custom;
            // 经典调色板索引 0-23：仅在显式请求时解析（StyleColor 的 resolvePalette），
            // 此处不解析——老文件里 V='9' 等常是默认样式占位值，直接采用会误染色。
        }
        return ColorUtil.Parse(raw);
    }

    private static double? GetDimNullable(XElement el, string name, XElement? inh)
    {
        var cell = el.Elements().FirstOrDefault(c =>
            c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name);
        if (cell != null)
        {
            var v = (string?)cell.Attribute("V");
            var u = (string?)cell.Attribute("U");
            if (v != null && double.TryParse(v, NF, CultureInfo.InvariantCulture, out var d))
                return ToInch(d, u);
            var f = (string?)cell.Attribute("F");
            if (f != null && (f == "Inh" || f == "INH") && inh != null)
                return GetDimNullable(inh, name, null);
        }
        else if (inh != null)
        {
            return GetDimNullable(inh, name, null);
        }
        return null;
    }

    // ---------------- 辅助 ----------------

    private static Dictionary<string, string> ParseRels(XDocument rels)
    {
        var map = new Dictionary<string, string>();
        foreach (var r in rels.Root!.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            var id = (string?)r.Attribute("Id");
            var target = (string?)r.Attribute("Target");
            if (id != null && target != null) map[id] = target;
        }
        return map;
    }

    private static string? CellVal(XElement el, string name)
    {
        var c = el.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Cell" && (string?)e.Attribute("N") == name);
        if (c == null) return null;
        return (string?)c.Attribute("V") ?? (string?)c.Attribute("F");
    }

    private static int? CellInt(XElement el, string name)
    {
        var v = CellVal(el, name);
        if (v == null) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }
}
