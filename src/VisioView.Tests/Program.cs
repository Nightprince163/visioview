using VisioView.Core;
using VisioView.Core.Model;
using VisioView.Render.Svg;

var input = args.Length > 0 ? args[0] : "testdata/sample.vsdx";
var outDir = args.Length > 1 ? args[1] : "testdata/out";

if (!File.Exists(input))
{
    Console.Error.WriteLine($"找不到输入文件: {input}");
    return 1;
}

var scene = VsdxParser.Parse(input);
Directory.CreateDirectory(outDir);

int i = 0;
foreach (var page in scene.Pages)
{
    var svg = SvgRenderer.RenderPage(page);
    var path = Path.Combine(outDir, $"page{i + 1}.svg");
    File.WriteAllText(path, svg);

    int paths = page.Drawables.OfType<PathDrawable>().Count();
    int texts = page.Drawables.OfType<TextDrawable>().Count();
    Console.WriteLine($"Page {i + 1}: '{page.Name}' {page.Width:0.##}x{page.Height:0.##}in " +
                      $"paths={paths} texts={texts} -> {path}");
    if (args.Length > 2 && args[2] == "debug")
    {
        foreach (var d in page.Drawables.Where(d => d.HasBounds))
            Console.WriteLine($"  {d.GetType().Name,-14} X[{d.MinX:0.##},{d.MaxX:0.##}] Y[{d.MinY:0.##},{d.MaxY:0.##}]" +
                              (d is TextDrawable td ? $" '{td.Text.Replace("\n", " ")[..Math.Min(18, td.Text.Replace("\n", " ").Length)]}'" : ""));
    }
    i++;
}

Console.WriteLine($"解析完成：共 {scene.Pages.Count} 页。");
return 0;
