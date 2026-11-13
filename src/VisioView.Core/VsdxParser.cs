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
        public double Width, Height, LocPinX, LocPinY; // 已换算为英寸（母版顶形状 XForm，备用）
        public int Id;
        // 母版真实内容包围盒（本地坐标，英寸）：用于把内容缩放到实例尺寸
        public double NatX0, NatY0, NatW, NatH;
    }

    public static Scene Parse(string path)
    {
        using var pkg = new VsdxPackage(path);
        var scene = new Scene();

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
                    var sr = mdoc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
                    if (sr == null) continue;
                    var top = sr.Elements().FirstOrDefault(e => e.Name.LocalName == "Shape");
                    var md = new MasterDef { ShapesRoot = sr };
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

        // ---- 主题配色（用于 THEMEVAL 回退）----
        TryLoadTheme(pkg);
        TryInferThemeFill(pkg);
        TryLoadFaceNames(pkg);

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
            scene.Pages.Add(ParsePage(pdoc, name, masters, defW, defH));
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
            string? dk1 = null, lt2 = null, dk2 = null;
            foreach (var el in doc.Descendants())
            {
                var nm = el.Name.LocalName;
                if (nm != "dk1" && nm != "dk2" && nm != "lt2") continue;
                var val = el.Descendants().Select(x => (string?)x.Attribute("val") ?? (string?)x.Attribute("lastClr")).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (val == null) continue;
                var hex = "#" + val!.TrimStart('#');
                if (nm == "dk1") dk1 = hex; else if (nm == "dk2") dk2 = hex; else lt2 = hex;
            }
            if (lt2 != null) ThemeFill = lt2;
            else if (lt2 == null && dk1 != null) ThemeFill = "#E7E6E6";
            if (dk2 != null) ThemeLine = dk2;
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
        IReadOnlyDictionary<string, string>? textOverrides = null)
    {
        // ---- 嵌入图片（Foreign 形状，如左上角 LOGO）----
        if ((string?)shape.Attribute("Type") == "Foreign")
        {
            RenderForeignImage(shape, parentM, page);
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
            // 动态连接线（1-D 形状）：看到 Begin/End 就直接连两端点，别用母版里的 "⊔" 占位几何
            var cstyle0 = ReadStyle(shape, inherited, mrootEl);
            if (TryRenderConnector(shape, mrootEl, parentM, cstyle0, page)) return;

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
            var mInst = Affine.MakePlacement(pinX, pinY, mlx, mly, ang, sx, sy);
            var M = Affine.Compose(parentM, mInst);
            if (Trace)
                Console.Error.WriteLine($"  [inst] ID={shape.Attribute("ID")?.Value} master={mid} " +
                                        $"iW={iW:0.###} iH={iH:0.###} mW={mdef.Width:0.###} mH={mdef.Height:0.###} " +
                                        $"sx={sx:0.###} sy={sy:0.###}");

            var instStyle = ReadStyle(shape, inherited, null);
            // 实例没写的字体/字号/颜色要继承母版根形状的 Character 段，
            // 否则「流程动作」这类母版设定的 10pt 会被默认值顶掉，文字被撑大后换行溢出。
            if (mrootEl != null) instStyle = instStyle.MergeText(ReadStyle(mrootEl, null, null));
            // 实例可以把母版里的某个子形状"重写"（MasterShape=K）：几何通常沿用母版，
            // 变的多数是文字（例如泳道母版写的是"部门"占位，实例里换成"文件编制部门"）。
            // 这里只接管文字覆盖，几何仍走母版 —— 直接渲染实例子形状会因为它的 Pin 用的是
            // 实例本地坐标而与母版缩放叠加，整页坐标会炸到几十英寸外。
            var kidText = BuildTextOverrides(shape);

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
                var MNoScale = Affine.Compose(M, new Affine(1 / sx, 0, 0, 1 / sy, 0, 0));
                RenderGeometry(shape, MNoScale, instStyle, page, mrootEl);
                usedInstGeom = true;
                if (Trace)
                    Console.Error.WriteLine($"  [geom-src] ID={shape.Attribute("ID")?.Value} 用实例几何（抵消 sx/sy）");
            }
            if (!usedInstGeom)
            {
                foreach (var child in mdef.ShapesRoot.Elements().Where(e => e.Name.LocalName == "Shape"))
                    RenderMasterContent(child, M, masters, page, instStyle, mid, kidText);
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
            var mInst = Affine.MakePlacement(pinX, pinY, lkx, lky, ang, sx, sy);
            var M = Affine.Compose(parentM, mInst);

            var style = ReadStyle(shape, inherited, kEl);
            // 实例几何优先，但实例行里没写的单元格（如 EllipticalArcTo 的 B）要从 K 继承，
            // 否则会退化成默认值把弧画错。整段几何都没有时才完全用 K 的几何。
            RenderGeometry(shape, M, style, page, kEl);
            RenderText(shape, M, kEl, style, page);
            return;
        }

        // ---- 自身形状（页面直接定义，或分组） ----
        double w = GetDim(shape, "Width", null, 0);
        double h = GetDim(shape, "Height", null, 0);
        double lx = GetDim(shape, "LocPinX", null, w / 2);
        double ly = GetDim(shape, "LocPinY", null, h / 2);
        double px = GetDim(shape, "PinX", null, 0);
        double py = GetDim(shape, "PinY", null, 0);
        double a = GetDim(shape, "Angle", null, 0);
        var mLocal = Affine.MakePlacement(px, py, lx, ly, a, 1, 1);
        var M2 = Affine.Compose(parentM, mLocal);

        var childShapes = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (childShapes != null)
        {
            foreach (var ch in childShapes.Elements().Where(e => e.Name.LocalName == "Shape"))
                RenderShape(ch, M2, masters, page, inherited, masterId, textOverrides);
            return;
        }

        var style2 = ReadStyle(shape, inherited, null);
        if (TryRenderConnector(shape, null, parentM, style2, page)) return;

        // 容器/泳道的标题条：几何与标题文字都不做纵向拉伸，然后整体贴到容器顶边。
        bool heading = IsHeadingShape(shape);
        var Mh = heading && Math.Abs(M2.D) > 1e-9
            ? new Affine(M2.A, M2.B, M2.C, Math.Sign(M2.D), M2.E, M2.F)
            : M2;
        int startIdx = page.Drawables.Count;

        RenderGeometry(shape, Mh, style2, page);
        // 母版子形状：若实例重写了文字，这里换上实例的文字
        string? selfText = null;
        var selfId = (string?)shape.Attribute("ID");
        if (textOverrides != null && selfId != null) textOverrides.TryGetValue(selfId, out selfText);
        RenderText(shape, Mh, null, style2, page, selfText);

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

    /// <summary>嵌入图片形状：ForeignData/Rel → visio/media/*，按形状框（Pin/LocPin/Width/Height）放置。</summary>
    private static void RenderForeignImage(XElement shape, Affine parentM, PageScene page)
    {
        var fd = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "ForeignData");
        var rel = fd?.Elements().FirstOrDefault(e => e.Name.LocalName == "Rel");
        var rid = (string?)rel?.Attribute(RNS + "id");
        if (rid == null || _pageRels == null || !_pageRels.TryGetValue(rid, out var target)) return;

        var part = ResolvePartPath(target);
        if (part == null) return;
        if (!_imgCache.TryGetValue(part, out var data))
        {
            data = _pkg?.ReadBytes(part);
            _imgCache[part] = data;
        }
        if (data == null || data.Length == 0) return;

        double w = GetDim(shape, "Width", null, 0);
        double h = GetDim(shape, "Height", null, 0);
        double lx = GetDim(shape, "LocPinX", null, w / 2);
        double ly = GetDim(shape, "LocPinY", null, h / 2);
        double px = GetDim(shape, "PinX", null, 0);
        double py = GetDim(shape, "PinY", null, 0);
        double a = GetDim(shape, "Angle", null, 0);
        var M = Affine.Compose(parentM, Affine.MakePlacement(px, py, lx, ly, a, 1, 1));

        // 取四角包围盒（本场景图片均不旋转，等价于直接矩形）
        double minX = double.MaxValue, minY = double.MaxValue, maxX = -double.MaxValue, maxY = -double.MaxValue;
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (w, 0.0), (0.0, h), (w, h) })
        {
            var p = M.Apply(cx, cy);
            double sx = p.X, sy = page.Height - p.Y;
            if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
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
                var p2 = S(pts[pts.Count >= 2 ? pts.Count - 2 : 0].X,
                           pts[pts.Count >= 2 ? pts.Count - 2 : 0].Y);

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
        IReadOnlyDictionary<string, string>? textOverrides = null)
    {
        var style = ReadStyle(shape, inherited, null);
        bool heading = IsHeadingShape(shape);
        // 标题条本身以及它的标题文字都放在「未纵向拉伸」的坐标系里，
        // 之后整体平移到容器顶边（见下面的 _headingTopScreen 对齐）。
        var Mh = heading && Math.Abs(M.D) > 1e-9
            ? new Affine(M.A, M.B, M.C, Math.Sign(M.D), M.E, M.F)
            : M;

        int start = page.Drawables.Count;
        var sub = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Shapes");
        if (sub != null)
        {
            // 分组：先画自身几何（若有），再递归渲染子形状
            RenderGeometry(shape, Mh, style, page);
            foreach (var ch in sub.Elements().Where(e => e.Name.LocalName == "Shape"))
                RenderShape(ch, Mh, masters, page, style, masterId, textOverrides);
        }
        else
        {
            RenderGeometry(shape, Mh, style, page);
        }
        // 文字：若实例重写了这个子形状，用实例的文字（位置仍按母版文本框）
        string? tOv = null;
        var cid = (string?)shape.Attribute("ID");
        if (textOverrides != null && cid != null) textOverrides.TryGetValue(cid, out tOv);
        RenderText(shape, Mh, null, style, page, tOv);

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
            case TextDrawable t: t.CenterY += dy; break;
            case ImageDrawable im: im.Y += dy; break;
        }
        d.MinY += dy;
        d.MaxY += dy;
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
            bool noFill = style.FillPattern == 0 ||
                          (style.FillTrans.HasValue && style.FillTrans.Value >= 0.99);
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
                                        $"fill={fill ?? "-"} stroke={stroke ?? "-"} noFill={noFill} noLine={noLine} " +
                                        $"gw={gw.ToString("0.###", CultureInfo.InvariantCulture)} " +
                                        $"gh={gh.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            double lw = style.LineWeight.HasValue && style.LineWeight.Value > 0
                ? style.LineWeight.Value : 0.0104; // 缺省约 0.75pt
            double strokeW = lw; // LineWeight 的 V 已是英寸（0.01in ≈ 0.72pt），不要再 ÷72

            var path = new PathDrawable
            {
                Fill = fill,
                Stroke = stroke,
                StrokeWidth = strokeW,
                NoFill = noFill,
                NoLine = noLine,
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
                        curX = RowDim(row, "X", shape, 0, mrow); curY = RowDim(row, "Y", shape, 0, mrow);
                        ToScreen(curX, curY, out var mx, out var my);
                        path.Commands.Add(new _Move(mx, my));
                        break;
                    case "LineTo":
                        curX = RowDim(row, "X", shape, 0, mrow); curY = RowDim(row, "Y", shape, 0, mrow);
                        ToScreen(curX, curY, out var lx2, out var ly2);
                        path.Commands.Add(new _Line(lx2, ly2));
                        break;
                    case "RelMoveTo":
                        curX = RowDim(row, "X", shape, 0, mrow) * gw;
                        curY = RowDim(row, "Y", shape, 0, mrow) * gh;
                        ToScreen(curX, curY, out var rmx, out var rmy);
                        path.Commands.Add(new _Move(rmx, rmy));
                        break;
                    case "RelLineTo":
                        curX = RowDim(row, "X", shape, 0, mrow) * gw;
                        curY = RowDim(row, "Y", shape, 0, mrow) * gh;
                        ToScreen(curX, curY, out var rlx, out var rly);
                        path.Commands.Add(new _Line(rlx, rly));
                        break;
                    case "ArcTo":
                    {
                        double ex = RowDim(row, "X", shape, 0, mrow);
                        double ey = RowDim(row, "Y", shape, 0, mrow);
                        // Visio 的 A 是「弦高（矢高）」：弧中点到弦中点的距离。
                        // 圆角矩形的圆角半径 r 对应 A = r*(1-cos45°) = 0.2929r，
                        // 母版里正是 A=User.xxxOffset*0.2929 —— 所以它不是半径、更不是夹角。
                        double sag = RowDim(row, "A", shape, 0, mrow);
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
                        double ex = RowDim(row, "X", shape, curX, mrow);
                        double ey = RowDim(row, "Y", shape, curY, mrow);
                        double cx = RowDim(row, "A", shape, curX, mrow);
                        double cy = RowDim(row, "B", shape, curY, mrow);
                        ToScreen(cx, cy, out var qx, out var qy);
                        ToScreen(ex, ey, out var ax, out var ay);
                        path.Commands.Add(new _Quad(qx, qy, ax, ay));
                        curX = ex; curY = ey;
                        break;
                    }
                    case "Ellipse":
                    {
                        double cx = RowDim(row, "X", shape, 0, mrow), cy = RowDim(row, "Y", shape, 0, mrow);
                        double rx = RowDim(row, "A", shape, 0.5, mrow), ry = RowDim(row, "B", shape, 0.5, mrow);
                        ToScreen(cx, cy, out var ccx, out var ccy);
                        path.Commands.Add(new _Ellipse(ccx, ccy, rx * kx, ry * ky));
                        break;
                    }
                    case "NURBSTo":
                    {
                        // 非连接线形状上的 NURBS 曲线（连接线已由 TryRenderNurbsConnector 接管）。
                        double ex = RowDim(row, "X", shape, curX, mrow);
                        double ey = RowDim(row, "Y", shape, curY, mrow);
                        var pts = ParseNurbsPoints(row, gw, gh);
                        if (pts.Count > 0)
                        {
                            var q1 = pts[0];
                            var q2 = pts[pts.Count >= 2 ? pts.Count - 2 : 0];
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
                        double fx = RowDim(row, "X", shape, curX, mrow), fy = RowDim(row, "Y", shape, curY, mrow);
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
        string? textOverride = null)
    {
        string? text = textOverride;
        if (text == null)
        {
            var textEl = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
            if (textEl == null) return;
            text = textEl.Value.Trim();
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
            Color = style.TextColor ?? ContrastTextColor(style.FillForegnd),
            FontSize = style.CharSize.HasValue ? style.CharSize.Value : 0.1667,
            CenterX = sx,
            CenterY = sy,
            Bold = style.Bold,
            Italic = style.Italic,
            FontFamily = style.FontFamily,
            RotationDeg = rotDeg,
        };
        // 文本框宽度：用于换行，避免长标签横穿相邻图形
        double tw = GetDim(shape, "TxtWidth", inh, 0);
        if (tw > 0.15) td.MaxWidth = tw;
        td.Include(sx, sy);
        if (Trace)
            Console.Error.WriteLine($"  [text] ID={shape.Attribute("ID")?.Value} ov={textOverride != null} " +
                                    $"c=({sx:0.##},{sy:0.##}) '{text.Replace("\n", " ")}'");
        // 文字实际占位：按字数估算宽高，让页面包围盒把文字也算进去（否则贴边文字被裁掉）
        double estW = 0, estH = 0;
        EstimateTextSize(text, td.FontSize, td.MaxWidth, out estW, out estH);
        td.Include(sx - estW / 2, sy - estH / 2);
        td.Include(sx + estW / 2, sy + estH / 2);
        page.Drawables.Add(td);
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
                    case "Ellipse":
                        x = RowDim(row, "X", shape, 0); y = RowDim(row, "Y", shape, 0);
                        curX = x; curY = y; break;
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
    private static double RowDim(XElement row, string name, XElement owner, double def, XElement? altRow = null)
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
            var fd = EvalFormula(f, owner, null);
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

        // 形如 Width*0.5 / Sheet.5!Height*1
        var m = Regex.Match(f, @"(?:Sheet\.\d+!)?([A-Za-z_][A-Za-z0-9_]*)\s*\*\s*([0-9]*\.?[0-9]+)");
        if (m.Success)
        {
            if (!double.TryParse(m.Groups[2].Value, NF, CultureInfo.InvariantCulture, out var k)) return null;
            return ResolveName(owner, m.Groups[1].Value, inh) * k;
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

    private static double ResolveName(XElement owner, string nm, XElement? inh)
    {
        switch (nm.ToLowerInvariant())
        {
            case "width": return GetDim(owner, "Width", inh, 0);
            case "height": return GetDim(owner, "Height", inh, 0);
            case "locpinx": return GetDim(owner, "LocPinX", inh, 0);
            case "locpiny": return GetDim(owner, "LocPinY", inh, 0);
            case "pinx": return GetDim(owner, "PinX", inh, 0);
            case "piny": return GetDim(owner, "PinY", inh, 0);
            default: return 0;
        }
    }

    // ---------------- 样式 ----------------

    private static Style ReadStyle(XElement shape, Style? inherited, XElement? inh)
    {
        var st = new Style
        {
            FillForegnd = GetColor(shape, "FillForegnd", inh),
            LineColor = GetColor(shape, "LineColor", inh),
            FillPattern = CellInt(shape, "FillPattern"),
            LinePattern = CellInt(shape, "LinePattern"),
            LineWeight = GetDimNullable(shape, "LineWeight", inh),
            FillTrans = GetDimNullable(shape, "FillForegndTrans", inh),
        };

        var ch = shape.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "Section" &&
            ((string?)e.Attribute("N") == "Character" || (string?)e.Attribute("N") == "Character1"));
        if (ch != null)
        {
            var row = ch.Elements().FirstOrDefault(e => e.Name.LocalName == "Row");
            if (row != null)
            {
                st.TextColor = GetColor(row, "Color", null);
                var sz = CellRaw(row, "Size");
                if (sz != null && double.TryParse(sz, NF, CultureInfo.InvariantCulture, out var z))
                    st.CharSize = z; // Size 的内部单位即英寸（U 仅是显示单位，如 0.1944in=14pt）

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

    /// <summary>直接取单元格的原始 V 字符串（不做单位/颜色解释）。</summary>
    private static string? CellRaw(XElement el, string name)
        => (string?)el.Elements().FirstOrDefault(c =>
               c.Name.LocalName == "Cell" && (string?)c.Attribute("N") == name)?.Attribute("V");

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

            // 3) 真正的主题表达式 THEMEVAL(...)：填充取推断出的主题色。
            //    文字（Color）返回 null，交给渲染端按底色明暗自动取黑/白，
            //    这正是 Visio「Themed」文字在深色表头上的表现（白字）。
            if (f != null && f.IndexOf("THEMEVAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (name.StartsWith("Fill", StringComparison.OrdinalIgnoreCase)) return ThemeFill;
                if (name.StartsWith("Line", StringComparison.OrdinalIgnoreCase)) return ThemeLine;
                return null;
            }

            // 4) 缓存值（调色板索引）
            raw = v ?? f;
            if (raw == "Inh" || raw == "INH" || raw == "Themed")
                raw = inh != null ? GetColor(inh, name, null) : null;
        }
        else if (inh != null)
        {
            raw = GetColor(inh, name, null);
        }
        return raw == null ? null : ColorUtil.Parse(raw);
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
