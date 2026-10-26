using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VisioView;

/// <summary>一个可用的新版本信息（来自 GitHub Releases）。</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string Tag { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string? DownloadUrl { get; init; }
    public long Size { get; init; }
    /// <summary>小写十六进制的 SHA256；GitHub 未提供时为 null（此时只校验文件大小）。</summary>
    public string? Sha256 { get; init; }
    public string? HtmlUrl { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// 零服务器的在线升级：向 GitHub Releases 查询最新版本，下载并替换自身。
/// 更新源可用环境变量 VV_UPDATE_FEED 覆盖（换镜像 / 自建静态接口时不用改代码）。
/// </summary>
public static class UpdateChecker
{
    private const string DefaultFeed =
        "https://api.github.com/repos/Nightprince163/visioview/releases/latest";

    private static string FeedUrl
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("VV_UPDATE_FEED");
            return string.IsNullOrWhiteSpace(custom) ? DefaultFeed : custom.Trim();
        }
    }

    /// <summary>当前运行的程序版本。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    private static HttpClient? _http;

    private static HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API 强制要求 User-Agent，缺了会直接 403
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VisioView-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>比较版本时只比 Major.Minor.Build（tag 里常写成 1.1.0，缺 Revision）。</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(Version candidate) => Normalize(candidate) > Normalize(CurrentVersion);

    /// <summary>查询最新版本。网络不可用时抛异常，由调用方决定是否提示。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // 全部 ConfigureAwait(false)：这是基础服务代码，不依赖 UI 上下文，
        // 否则在 UI 线程上同步等待结果会死锁。
        using var resp = await Http.GetAsync(FeedUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        string? url = null, name = null, sha = null;
        long size = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var an = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                name = an;
                url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0;

                // GitHub 较新的接口会给出 digest，形如 "sha256:xxxx"
                if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    var dv = dg.GetString();
                    if (!string.IsNullOrEmpty(dv) && dv.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = dv["sha256:".Length..].ToLowerInvariant();
                }
                break;
            }
        }

        return new UpdateInfo
        {
            Version = ver,
            Tag = tag,
            AssetName = name ?? "VisioView.exe",
            DownloadUrl = url,
            Size = size,
            Sha256 = sha,
            HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null,
        };
    }

    /// <summary>下载到临时目录并校验完整性，返回本地文件路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null,
                                                   CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
            throw new InvalidOperationException("该版本没有可下载的 .exe 附件。");

        var dir = Path.Combine(Path.GetTempPath(), "VisioView-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, info.AssetName);
        if (File.Exists(target)) File.Delete(target);

        using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? info.Size;

        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(target))
        {
            var buf = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.TransformBlock(buf, 0, read, null, 0);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        var actual = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();

        if (!string.IsNullOrEmpty(info.Sha256) &&
            !string.Equals(info.Sha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            throw new InvalidOperationException("下载文件校验失败（SHA256 不匹配），可能是网络中断或文件被篡改。");
        }

        if (info.Size > 0 && new FileInfo(target).Length != info.Size)
        {
            TryDelete(target);
            throw new InvalidOperationException("下载文件大小与预期不符，请重试。");
        }

        return target;
    }

    /// <summary>
    /// 用下载好的文件替换当前 exe。
    /// Windows 不允许覆盖或删除正在运行的可执行文件，但**允许重命名**——
    /// 所以先把自身改名成 .old，再把新文件写到原位置，最后启动新进程。
    /// </summary>
    public static bool Apply(string downloadedPath, string? reopenFile, out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            error = "无法定位当前程序文件的位置。";
            return false;
        }

        var old = exe + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);

            try
            {
                File.Copy(downloadedPath, exe, overwrite: true);
            }
            catch
            {
                File.Move(old, exe);   // 写回失败就回滚，别把用户程序弄没了
                throw;
            }

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(reopenFile)) psi.Arguments = $"\"{reopenFile}\"";
            Process.Start(psi);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "当前目录没有写入权限，无法自动替换。\n" +
                    "请把 VisioView.exe 放到桌面或用户目录下再试，" +
                    "或者手动到 Release 页面下载新版本。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>清理上次升级留下的 .old 文件与临时下载包（上次进程已退出，此时可以删）。</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
        }
        catch { /* 删不掉就留着，不影响使用 */ }

        // 一个安装包 60 MB+，升级完不再需要，别一直堆在临时目录里
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "VisioView-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
