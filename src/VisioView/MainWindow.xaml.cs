using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VisioView.Core;
using VisioView.Core.Model;

namespace VisioView;

public partial class MainWindow : Window
{
    private Scene? _scene;
    private int _currentPage = 0;
    private double _zoom = 1.0;       // 1 = 实际大小（1 英寸 = 96px）
    private double _tx = 0, _ty = 0;

    private bool _panning;
    private Point _panStart;
    private double _tx0, _ty0;

    public MainWindow()
    {
        InitializeComponent();
        Viewport.MouseWheel += Viewport_MouseWheel;
        Viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        Viewport.MouseMove += Viewport_MouseMove;
        Viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        Viewport.MouseLeave += (_, _) => { if (_panning) EndPan(); };
    }

    // ---------------- 文件加载 ----------------

    public void LoadFile(string path)
    {
        try
        {
            _scene = VsdxParser.Parse(path);
            if (_scene.Pages.Count == 0)
            {
                MessageBox.Show("未能从文件中解析出任何页面。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PageCombo.ItemsSource = _scene.Pages;
            PageCombo.SelectedIndex = 0;
            _currentPage = 0;
            RenderPage();
            // 标题：未打开文件时为 "VisioView"，打开后为 "文件名 - VisioView"
            var fileName = System.IO.Path.GetFileName(path);
            Title = string.IsNullOrEmpty(fileName) ? "VisioView" : $"{fileName} - VisioView";
            StatusText.Text = $"已打开：{fileName}（{_scene.Pages.Count} 页）";
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"打开失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderPage()
    {
        if (_scene == null) return;
        var page = _scene.Pages[_currentPage];
        SceneCanvas.Children.Clear();
        var cv = WpfRenderer.Build(page);
        SceneCanvas.Children.Add(cv);
        Fit();
    }

    // ---------------- 视图变换 ----------------

    private void ApplyTransform()
    {
        double s = 96 * _zoom;
        ScaleT.ScaleX = s;
        ScaleT.ScaleY = s;
        TransT.X = _tx;
        TransT.Y = _ty;
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
        if (_scene != null)
        {
            var p = _scene.Pages[_currentPage];
            InfoLabel.Text = $"页面 {_currentPage + 1}/{_scene.Pages.Count}  ·  {p.Width:0.##} × {p.Height:0.##} in";
        }
    }

    private void Fit()
    {
        if (_scene == null) return;
        var p = _scene.Pages[_currentPage];
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw <= 0 || vh <= 0) { vw = 1000; vh = 700; }
        double z = Math.Min(vw / (p.Width * 96), vh / (p.Height * 96)) * 0.96;
        z = Math.Max(0.02, Math.Min(z, 50));
        _zoom = z;
        // 居中
        _tx = (vw - p.Width * 96 * z) / 2;
        _ty = (vh - p.Height * 96 * z) / 2;
        ApplyTransform();
    }

    private void ActualSize()
    {
        if (_scene == null) return;
        var p = _scene.Pages[_currentPage];
        _zoom = 1.0;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        _tx = (vw - p.Width * 96) / 2;
        _ty = (vh - p.Height * 96) / 2;
        ApplyTransform();
    }

    private void ZoomAround(Point center, double newZoom)
    {
        newZoom = Math.Max(0.02, Math.Min(newZoom, 50));
        double oldZ = _zoom;
        double lx = (center.X - _tx) / (96 * oldZ);
        double ly = (center.Y - _ty) / (96 * oldZ);
        _zoom = newZoom;
        _tx = center.X - 96 * newZoom * lx;
        _ty = center.Y - 96 * newZoom * ly;
        ApplyTransform();
    }

    // ---------------- 鼠标交互 ----------------

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scene == null) return;
        var p = e.GetPosition(Viewport);
        ZoomAround(p, _zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_scene == null) return;
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _tx0 = _tx; _ty0 = _ty;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.Hand;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Viewport);
        _tx = _tx0 + (p.X - _panStart.X);
        _ty = _ty0 + (p.Y - _panStart.Y);
        ApplyTransform();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Cross;
    }

    // ---------------- 菜单 / 工具栏 ----------------

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Visio 文件 (*.vsdx)|*.vsdx|所有文件 (*.*)|*.*",
            Title = "打开 Visio 文件",
        };
        if (dlg.ShowDialog() == true) LoadFile(dlg.FileName);
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register();
            MessageBox.Show("已注册 .vsdx 文件关联（当前用户）。\n现在可以双击 .vsdx 文件直接用本程序打开。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"注册失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateRegisterMenuState();
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        FileAssociation.Unregister();
        MessageBox.Show("已取消 .vsdx 文件关联。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateRegisterMenuState();
    }

    private void UpdateRegisterMenuState()
    {
        // 关联状态以弹窗反馈为准（HKCU 注册无需管理员）
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "VisioView — .vsdx 查看器\n\n" +
            "· 双击 .vsdx 直接打开（需先「设为默认打开程序」）\n" +
            "· 滚轮缩放，拖拽平移，工具栏可适应窗口 / 实际大小\n" +
            "· 支持多页切换、形状/连线/文本渲染", "关于 VisioView",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void Actual_Click(object sender, RoutedEventArgs e) => ActualSize();
    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ZoomAround(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), _zoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ZoomAround(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), _zoom / 1.2);

    private void PageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageCombo.SelectedIndex >= 0 && _scene != null)
        {
            _currentPage = PageCombo.SelectedIndex;
            RenderPage();
        }
    }

    // ---------------- 拖放 ----------------

    private void Viewport_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Viewport_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (files[0].EndsWith(".vsdx", System.StringComparison.OrdinalIgnoreCase))
                LoadFile(files[0]);
        }
    }
}
