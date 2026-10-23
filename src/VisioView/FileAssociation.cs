using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace VisioView;

/// <summary>
/// 通过 HKCU\Software\Classes 注册 .vsdx 关联（当前用户，无需管理员权限）。
/// 双击 .vsdx 即会运行 "VisioView.exe "%1""。
/// </summary>
public static class FileAssociation
{
    private const string Ext = ".vsdx";
    private const string ProgId = "VisioView.vsdxfile";
    private const string Description = "Visio 绘图 (.vsdx)";

    public static string ExePath()
        => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
           ?? throw new System.InvalidOperationException("无法获取当前程序路径");

    public static void Register()
    {
        var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using (classes)
        {
            using (var k = classes.CreateSubKey(Ext))
                k.SetValue("", ProgId);
            using (var k = classes.CreateSubKey($@"{ProgId}"))
                k.SetValue("", Description);
            using (var k = classes.CreateSubKey($@"{ProgId}\DefaultIcon"))
                k.SetValue("", $"\"{ExePath()}\",0");
            using (var k = classes.CreateSubKey($@"{ProgId}\shell\open\command"))
                k.SetValue("", $"\"{ExePath()}\" \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
        if (classes == null) return;
        try { classes.DeleteSubKeyTree(ProgId, false); } catch { }
        try { classes.DeleteSubKeyTree(Ext, false); } catch { }
        classes.Dispose();
        NotifyShell();
    }

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        var v = k?.GetValue("") as string;
        return v != null && v.IndexOf("VisioView", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShell()
    {
        // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }
}
