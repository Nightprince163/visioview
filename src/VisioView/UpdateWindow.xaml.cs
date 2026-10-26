using System.Windows;

namespace VisioView;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>下载成功后的本地文件路径；取消或失败时为 null。</summary>
    public string? DownloadedPath { get; private set; }

    public UpdateWindow(UpdateInfo info)
    {
        _info = info;
        InitializeComponent();

        TitleText.Text = $"正在下载 VisioView {info.Version}…";
        DetailText.Text = info.Size > 0 ? $"共 {info.Size / 1048576.0:0.0} MB" : "正在连接…";

        Loaded += OnLoaded;
        Closing += (_, _) => { if (!_cts.IsCancellationRequested) _cts.Cancel(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<double>(p =>
        {
            Bar.Value = p;
            var got = _info.Size * p / 1048576.0;
            DetailText.Text = _info.Size > 0
                ? $"{p * 100:0}%　{got:0.0} / {_info.Size / 1048576.0:0.0} MB"
                : $"{p * 100:0}%";
        });

        try
        {
            DownloadedPath = await UpdateChecker.DownloadAsync(_info, progress, _cts.Token);
            DetailText.Text = "下载完成，正在安装…";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"下载失败：\n{ex.Message}", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelBtn.IsEnabled = false;
        DetailText.Text = "正在取消…";
    }
}
