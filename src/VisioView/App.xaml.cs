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
