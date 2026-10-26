using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisioView.Core.Model;

namespace VisioView;

public partial class ExportDialog : Window
{
    private readonly PageScene _page;
    private readonly Canvas _arranged;
    private bool _ready;   // XAML 里的 Checked 会在 InitializeComponent 期间就触发，先挡掉

    public ExportFormat Format { get; private set; } = ExportFormat.Png;
    public ExportRange Range { get; private set; } = ExportRange.FullPage;
    public double Scale { get; private set; } = 2;
    public bool Transparent { get; private set; }

    public ExportDialog(PageScene page, Canvas arranged)
    {
        _page = page;
        _arranged = arranged;
        InitializeComponent();
        _ready = true;
        UpdatePreview();
    }

    private double SelectedScale
    {
        get
        {
            if (ScaleCombo.SelectedItem is ComboBoxItem { Tag: string s } &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
            return 1;
        }
    }

    private ExportFormat CurrentFormat => JpgRb.IsChecked == true ? ExportFormat.Jpeg : ExportFormat.Png;
    private ExportRange CurrentRange => ContentRb.IsChecked == true ? ExportRange.Content : ExportRange.FullPage;

    private void UpdatePreview()
    {
        if (!_ready || SizeText == null) return;

        // JPEG 没有 alpha 通道，透明开关跟着失效
        bool isPng = CurrentFormat == ExportFormat.Png;
        AlphaCb.IsEnabled = isPng;
        if (!isPng) AlphaCb.IsChecked = false;

        RangeHint.Text = CurrentRange == ExportRange.Content
            ? "仅内容区域会把四周空白裁掉，导出到图形边界为止。"
            : "整页画布按 Visio 页面尺寸导出，四周空白一并保留。";

        var (w, h) = ImageExporter.PredictSize(_page, CurrentRange, SelectedScale, _arranged);
        SizeText.Text = $"输出尺寸：{w} × {h} px";

        if ((long)w * h > ImageExporter.MaxPixels || w > 16000 || h > 16000)
            SizeText.Text += "　（过大，请降低缩放倍数）";
    }

    private void Option_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Scale_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Format = CurrentFormat;
        Range = CurrentRange;
        Scale = SelectedScale;
        Transparent = AlphaCb.IsChecked == true && Format == ExportFormat.Png;

        var (w, h) = ImageExporter.PredictSize(_page, Range, Scale, _arranged);
        if ((long)w * h > ImageExporter.MaxPixels || w > 16000 || h > 16000)
        {
            MessageBox.Show(
                $"输出尺寸 {w} × {h} px 过大，渲染可能失败或耗尽内存。\n请降低缩放倍数，或改用「仅内容区域」。",
                "尺寸过大", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
