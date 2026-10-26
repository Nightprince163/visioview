using System.Windows;

namespace VisioView;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        // 静默注册模式（供安装脚本或手动调用）
        if (args.Length > 0 && args[0].Equals("--register", System.StringComparison.OrdinalIgnoreCase))
        {
            try { FileAssociation.Register(); } catch { }
            Shutdown(0);
            return;
        }

        // 诊断模式：--png <in.vsdx> <out.png> [页序号] —— 把页面渲染成 PNG（用于可视化比对）
        if (args.Length >= 3 && args[0].Equals("--png", System.StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var scene = VisioView.Core.VsdxParser.Parse(args[1]);
                int pi = args.Length >= 4 && int.TryParse(args[3], out var n) ? Math.Clamp(n, 1, scene.Pages.Count) - 1 : 0;
                if (scene.Pages.Count == 0) { Shutdown(1); return; }
                WpfRenderer.SavePng(scene.Pages[pi], args[2]);
                Shutdown(0);
            }
            catch (System.Exception ex)
            {
                System.IO.File.WriteAllText(args[2] + ".err.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }

        // 导出模式：--export <in.vsdx> <out.png|jpg> [页序号] [full|content] [缩放倍数]
        // 例：VisioView.exe --export in.vsdx out.png 1 content 2
        if (args.Length >= 3 && args[0].Equals("--export", System.StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var scene = VisioView.Core.VsdxParser.Parse(args[1]);
                if (scene.Pages.Count == 0) { Shutdown(1); return; }

                int pi = args.Length >= 4 && int.TryParse(args[3], out var n)
                    ? Math.Clamp(n, 1, scene.Pages.Count) - 1 : 0;
                var range = args.Length >= 5 && args[4].Equals("content", System.StringComparison.OrdinalIgnoreCase)
                    ? ExportRange.Content : ExportRange.FullPage;
                double scale = args.Length >= 6 && double.TryParse(args[5], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc > 0 ? sc : 1;
                bool alpha = args.Length >= 7 && args[6].Equals("alpha", System.StringComparison.OrdinalIgnoreCase);

                bool jpeg = args[2].EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
                         || args[2].EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase);
                var page = scene.Pages[pi];
                var arranged = ImageExporter.BuildArranged(page);
                ImageExporter.Export(page, arranged, args[2],
                    jpeg ? ExportFormat.Jpeg : ExportFormat.Png, range, scale, transparent: alpha);

                var (w, h) = ImageExporter.PredictSize(page, range, scale, arranged);
                System.IO.File.WriteAllText(args[2] + ".info.txt", $"{w} {h}");
                Shutdown(0);
            }
            catch (System.Exception ex)
            {
                System.IO.File.WriteAllText(args[2] + ".err.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }

        var win = new MainWindow();

        // 双击 .vsdx 打开：第一个参数是文件路径
        if (args.Length > 0)
        {
            var path = args[0];
            if (path.EndsWith(".vsdx", System.StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
            {
                win.LoadFile(path);
            }
        }

        win.Show();
    }
}
