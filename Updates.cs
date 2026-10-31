using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chisto;
public sealed record ReleaseUpdate(Version Version, string Page, string Download, string Sha256, long Bytes);
public sealed class UpdateClient
{
    public string Owner { get; }
    public string Repository { get; }
    public string Page => $"https://github.com/{Owner}/{Repository}/releases";
    public static Version Current { get; } = CurrentAssemblyVersion();
    private static Version CurrentAssemblyVersion()
    {
        var version = typeof(UpdateClient).Assembly.GetName().Version ?? new Version(0, 2, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }
    public UpdateClient(string owner, string repository)
    {
        if (!Regex.IsMatch(owner, "^[A-Za-z0-9-]{1,39}$") || !Regex.IsMatch(repository, "^[A-Za-z0-9_.-]{1,100}$")) throw new ArgumentException("Неверный адрес репозитория.");
        Owner = owner; Repository = repository;
    }
    public static UpdateClient Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
            return new UpdateClient(doc.RootElement.GetProperty("owner").GetString()!, doc.RootElement.GetProperty("repository").GetString()!);
        }
        catch { return new UpdateClient("saymonseo", "chisto"); }
    }
    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Chisto", Current.ToString()));
        return client;
    }
    public async Task<ReleaseUpdate?> Check(CancellationToken token)
    {
        using var client = Client(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await client.GetAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("Публичный релиз пока не опубликован или репозиторий недоступен.");
        response.EnsureSuccessStatusCode(); string json = await response.Content.ReadAsStringAsync(timeout.Token);
        return Parse(json);
    }
    internal ReleaseUpdate? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var release = doc.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() || release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var parsed)) throw new IOException("Непонятный номер версии в релизе.");
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        if (version <= Current) return null;
        string name = $"Chisto-Setup-{version}.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            string url = asset.GetProperty("browser_download_url").GetString()!;
            if (!ValidDownload(url)) throw new IOException("Адрес установщика не относится к настроенному репозиторию.");
            string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new IOException("У релиза отсутствует SHA-256 установщика. Автоматическая загрузка недоступна.");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > 512L * 1024 * 1024) throw new IOException("Неожиданный размер установщика.");
            return new ReleaseUpdate(version, $"{Page}/tag/v{version}", url, digest[7..], size);
        }
        throw new IOException("В релизе нет установщика Windows x64.");
    }
    internal bool ValidDownload(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo == "" && uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    public async Task<string> Download(ReleaseUpdate release, IProgress<double> progress, CancellationToken token)
    {
        if (!ValidDownload(release.Download)) throw new IOException("Неверный источник установщика.");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chisto", "Updates"); Directory.CreateDirectory(folder);
        if (!Format.NoLinks(folder)) throw new IOException("Папка обновлений перенаправлена ссылкой.");
        string file = Path.Combine(folder, $"Chisto-Setup-{release.Version}-{Guid.NewGuid():N}.exe");
        string temporary = file + ".download";
        try
        {
            using var client = Client(); using var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 262144, true))
            {
                byte[] buffer = new byte[262144]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count; if (total > release.Bytes) throw new IOException("Размер установщика изменился.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); progress.Report(100.0 * total / release.Bytes);
                }
                if (total != release.Bytes) throw new IOException("Установщик скачан не полностью.");
            }
            await using (var stream = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Контрольная сумма не совпала. Установщик не будет запущен.");
            File.Move(temporary, file);
            // Preserve the Internet origin so normal Windows checks can apply.
            File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + release.Download + "\r\n");
            return file;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
}
public sealed record AppDataEntry(string Name, string Location, long Bytes, string Path, string CacheHint)
{
    public string DisplaySize => Format.Size(Bytes);
    public static List<AppDataEntry> From(FolderNode root, IEnumerable<CacheRule> rules)
    {
        var known = rules.SelectMany(x => x.Roots).ToList();
        return root.Children.SelectMany(area => area.Children.Select(app => new AppDataEntry(app.Name, area.Name, app.Bytes, app.Path,
            known.Any(cache => Format.Below(cache, app.Path) || string.Equals(cache, app.Path, StringComparison.OrdinalIgnoreCase)) ? "Кэш распознан" : "Только просмотр"))).OrderByDescending(x => x.Bytes).ToList();
    }
}
