using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisioView;

/// <summary>一个可用的新版本信息（来自 GitHub / Gitee Releases）。</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string Tag { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string? DownloadUrl { get; init; }
    public long Size { get; init; }
    /// <summary>小写十六进制的 SHA256。GitHub 取 digest，Gitee 取 SHA256.txt / 发布说明。</summary>
    public string? Sha256 { get; init; }
    /// <summary>校验值来自哪里，仅用于诊断输出。</summary>
    public string? Sha256Source { get; init; }
    /// <summary>提供本次版本信息的更新源名称。</summary>
    public string Source { get; init; } = "";
    public string? HtmlUrl { get; init; }
    public string? Notes { get; init; }
}

/// <summary>单个更新源的探测结果（--check-update 诊断用）。</summary>
public sealed class SourceProbe
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public UpdateInfo? Info { get; init; }
    public string? Error { get; init; }
    public long ElapsedMs { get; init; }
    public bool Ok => Info != null;
}

/// <summary>
/// 零服务器的在线升级：依次查询 GitHub Releases 与 Gitee Releases，哪个能用就用哪个。
/// GitHub 挂掉 / 国内访问不通时自动落到 Gitee；Gitee 的附件没有 digest，
/// 所以另传一份 SHA256.txt（或写在发布说明里）供下载后校验。
/// 环境变量 VV_UPDATE_FEED 可覆盖源列表，多个 URL 用分号分隔。
/// </summary>
public static class UpdateChecker
{
    private sealed record Source(string Name, string Url);

    private static readonly Source GitHub = new(
        "GitHub", "https://api.github.com/repos/Nightprince163/visioview/releases/latest");
    private static readonly Source Gitee = new(
        "Gitee", "https://gitee.com/api/v5/repos/nightprince_xm/visioview/releases/latest");

    private static readonly Regex ShaInText =
        new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);
    private static readonly Regex ShaInNotes =
        new(@"SHA256\s*[:：]\s*([0-9a-fA-F]{64})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>当前运行的程序版本。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    private static HttpClient? _metaHttp;
    private static HttpClient? _downloadHttp;

    /// <summary>元数据请求要快，单个源卡住不能拖住启动：10 秒超时。</summary>
    private static HttpClient MetaHttp => _metaHttp ??= CreateClient(TimeSpan.FromSeconds(10));

    /// <summary>62 MB 的 exe 经国内网络常常要几十秒，下载必须用独立的长超时客户端。</summary>
    private static HttpClient DownloadHttp => _downloadHttp ??= CreateClient(TimeSpan.FromMinutes(15));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VisioView-Updater/1.4");
        // GitHub API 强制要求 User-Agent；Gitee 忽略这个 Accept，不影响解析。
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>比较版本时只比 Major.Minor.Build（tag 里常写成 1.1.0，缺 Revision）。</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(Version candidate) => Normalize(candidate) > Normalize(CurrentVersion);

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisioView", "update-source.txt");

    private static string? CachedUrl
    {
        get
        {
            try { return File.ReadAllText(CachePath).Trim(); }
            catch { return null; }
        }
    }

    private static void RememberSource(string url)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CachePath, url);
        }
        catch { /* 缓存失败不影响本次更新 */ }
    }

    private static string NameOf(string url, int index)
    {
        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) return "Gitee";
        return $"自定义{index + 1}";
    }

    private static Source[] BuildSources()
    {
        var custom = Environment.GetEnvironmentVariable("VV_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var urls = custom.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => u.Length > 0).ToArray();
            if (urls.Length > 0)
                return urls.Select((u, i) => new Source(NameOf(u, i), u)).ToArray();
        }

        var sources = new[] { GitHub, Gitee };
        var cached = CachedUrl;
        if (!string.IsNullOrEmpty(cached))
        {
            var i = Array.FindIndex(sources, s => string.Equals(s.Url, cached, StringComparison.OrdinalIgnoreCase));
            if (i > 0)
            {
                var first = sources[i];
                sources[i] = sources[0];
                sources[0] = first;
            }
        }
        return sources;
    }

    /// <summary>顺序尝试各更新源，返回第一个能用的；全部失败返回 null。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        foreach (var source in BuildSources())
        {
            try
            {
                var info = await CheckOneAsync(source, ct).ConfigureAwait(false);
                RememberSource(source.Url);
                return info;
            }
            catch { /* 换下一个源 */ }
        }
        return null;
    }

    /// <summary>探测全部更新源（不提前退出），用于 --check-update 诊断。</summary>
    public static async Task<IReadOnlyList<SourceProbe>> CheckAllAsync(CancellationToken ct = default)
    {
        var probes = new List<SourceProbe>();
        foreach (var source in BuildSources())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var info = await CheckOneAsync(source, ct).ConfigureAwait(false);
                probes.Add(new SourceProbe
                {
                    Name = source.Name, Url = source.Url, Info = info, ElapsedMs = sw.ElapsedMilliseconds
                });
                if (probes.Count == 1) RememberSource(source.Url);
            }
            catch (Exception ex)
            {
                probes.Add(new SourceProbe
                {
                    Name = source.Name, Url = source.Url, Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds
                });
            }
        }
        return probes;
    }

    /// <summary>从单个源读取最新版本信息；网络或格式错误直接抛异常，由调用方切换源。</summary>
    private static async Task<UpdateInfo> CheckOneAsync(Source source, CancellationToken ct)
    {
        using var resp = await MetaHttp.GetAsync(source.Url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
            throw new FormatException($"无法解析版本号：{tag}");

        string? url = null, name = null, sha = null, shaSource = null;
        long size = 0;
        string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var an = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                name = an;
                url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0;

                // GitHub 的 Release asset 带 digest；Gitee 没有，留到下面从 SHA256.txt 补。
                if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    var dv = dg.GetString();
                    if (!string.IsNullOrEmpty(dv) && dv.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        sha = dv["sha256:".Length..].ToLowerInvariant();
                        shaSource = "digest";
                    }
                }
                break;
            }
        }

        if (string.IsNullOrEmpty(url))
            throw new FormatException("该更新源的 Release 没有可下载的 .exe 附件。");

        if (string.IsNullOrEmpty(sha))
            (sha, shaSource) = await TryGetChecksumAsync(url, notes, ct).ConfigureAwait(false);

        return new UpdateInfo
        {
            Version = ver,
            Tag = tag,
            AssetName = name ?? "VisioView.exe",
            DownloadUrl = url,
            Size = size,
            Sha256 = sha,
            Sha256Source = shaSource,
            Source = source.Name,
            HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
            Notes = notes,
        };
    }

    /// <summary>Gitee 不提供 digest：优先读发布说明里的 SHA256: xxx，其次读同目录 SHA256.txt。</summary>
    private static async Task<(string?, string?)> TryGetChecksumAsync(
        string exeUrl, string? notes, CancellationToken ct)
    {
        var inNotes = ShaInNotes.Match(notes ?? "");
        if (inNotes.Success)
            return (inNotes.Groups[1].Value.ToLowerInvariant(), "发布说明");

        var slash = exeUrl.LastIndexOf('/');
        if (slash < 0) return (null, null);
        var shaUrl = exeUrl[..(slash + 1)] + "SHA256.txt";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var resp = await MetaHttp.GetAsync(shaUrl, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var m = ShaInText.Match(text.Length > 4096 ? text[..4096] : text);
            if (m.Success) return (m.Value.ToLowerInvariant(), "SHA256.txt");
        }
        catch { /* 校验文件拿不到就只做大小校验，不阻断升级 */ }
        return (null, null);
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

        using var resp = await DownloadHttp.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Gitee 的 releases/latest 不返回 size，以响应头为准。
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

        if (total > 0 && new FileInfo(target).Length != total)
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
