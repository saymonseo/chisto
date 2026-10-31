using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace Chisto;
public static class Format
{
    internal static bool ReserveName(ReadOnlySpan<char> name) => name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase);
    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes; int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:N1} {units[i]}";
    }
    public static bool Below(string file, string root) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool NoLinks(string path)
    {
        try
        {
            for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)))
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch { return false; }
    }
}
public sealed class FolderNode(string path, FolderNode? parent = null)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public FolderNode? Parent { get; } = parent;
    public List<FolderNode> Children { get; } = [];
    public long DirectBytes { get; set; }
    public long SystemReserveBytes { get; set; }
    public long Bytes { get; set; }
    public string DisplaySize => Format.Size(Bytes);
    public long Files { get; set; }
    public long PlannedFiles { get; set; }
}
public sealed record DiskFile(string Path, long Bytes, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DisplaySize => Format.Size(Bytes);
    public string TypeLabel => !string.Equals(Folder, System.IO.Path.GetPathRoot(Path), StringComparison.OrdinalIgnoreCase) ? "Файл" : Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ? "Подкачка Windows" : Name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ? "Гибернация Windows" : Name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) ? "Память Windows" : "Файл";
    public bool Protected => TypeLabel != "Файл";
}
public sealed record ScanProgress(long Files, long Bytes, string Current, string Stage = "Подготовка", long TotalFiles = 0, int FoldersDone = 0, int TotalFolders = 0, double? Percent = null, TimeSpan Elapsed = default, TimeSpan? Remaining = null);
public sealed record ScanResult(FolderNode Root, List<DiskFile> Largest, List<DiskFile> DuplicateCandidates, long FileCount, int Inaccessible, int Links, TimeSpan Duration);
public static class DiskScanner
{
    public static ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException("Папка не найдена.");
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            rootPath = Directory.ResolveLinkTarget(rootPath, true)?.FullName ?? throw new IOException("Не удалось раскрыть ссылку на папку.");
        var timer = Stopwatch.StartNew();
        var root = new FolderNode(rootPath); var stack = new Stack<FolderNode>(); stack.Push(root);
        var allNodes = new List<FolderNode>(); var largest = new PriorityQueue<DiskFile, long>(); var candidates = new List<DiskFile>();
        long count = 0, total = 0, plannedFiles = 0, workDone = 0; int errors = 0, links = 0, foldersDone = 0; var next = TimeSpan.Zero;
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        // A directory/file-count pass establishes a real denominator. No invented
        // percentage is shown while this initial inventory is still unknown.
        while (stack.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            try { if ((File.GetAttributes(node.Path) & FileAttributes.ReparsePoint) != 0) { links++; continue; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (failedPaths.Add(node.Path)) errors++; continue; }
            allNodes.Add(node);
            try
            {
                foreach (var entry in new FileSystemEnumerable<PlanEntry>(node.Path, (ref FileSystemEntry e) => new PlanEntry(e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.IsDirectory ? e.ToFullPath() : null), options))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Link) { links++; continue; }
                    if (entry.Directory)
                    {
                        var child = new FolderNode(entry.Path!, node); node.Children.Add(child); stack.Push(child);
                    }
                    else { node.PlannedFiles++; plannedFiles++; }
                    if (timer.Elapsed >= next) { progress?.Report(new ScanProgress(plannedFiles, 0, node.Path, "Подсчёт файлов", Elapsed: timer.Elapsed, TotalFolders: allNodes.Count + stack.Count)); next = timer.Elapsed + TimeSpan.FromMilliseconds(200); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { if (failedPaths.Add(node.Path)) errors++; }
        }
        var analysisStart = timer.Elapsed; next = TimeSpan.Zero;
        foreach (var node in allNodes)
        {
            token.ThrowIfCancellationRequested(); long seen = 0;
            bool driveRoot = string.Equals(Path.TrimEndingDirectorySeparator(node.Path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(node.Path)!), StringComparison.OrdinalIgnoreCase);
            // A new junction inserted after the count pass must not be traversed.
            try { if ((File.GetAttributes(node.Path) & FileAttributes.ReparsePoint) != 0) { workDone += node.PlannedFiles; foldersDone++; continue; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (failedPaths.Add(node.Path)) errors++; workDone += node.PlannedFiles; foldersDone++; continue; }
            try
            {
                foreach (var file in new FileSystemEnumerable<ScanEntry>(node.Path, (ref FileSystemEntry e) =>
                {
                    bool take = !e.IsDirectory && (driveRoot && Format.ReserveName(e.FileName) || e.Length >= 1024 * 1024 || largest.Count < 1000 || (largest.TryPeek(out _, out var minimum) && e.Length > minimum));
                    return new ScanEntry(e.IsDirectory, e.Attributes, e.Length, e.LastWriteTimeUtc.UtcDateTime, take ? e.ToFullPath() : null);
                }, options))
                {
                    token.ThrowIfCancellationRequested();
                    if (file.Directory || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    seen++; count++; total += file.Bytes;
                    ObserveFile(node, file.Path == null ? null : new DiskFile(file.Path, file.Bytes, file.Modified), file.Bytes, file.Attributes, largest, candidates);
                    if (timer.Elapsed >= next)
                    {
                        Report(false); next = timer.Elapsed + TimeSpan.FromMilliseconds(200);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { if (failedPaths.Add(node.Path)) errors++; }
            workDone += node.PlannedFiles; foldersDone++;
            void Report(bool complete)
            {
                double fraction = plannedFiles > 0 ? (double)(workDone + Math.Min(seen, node.PlannedFiles)) / plannedFiles : (double)foldersDone / Math.Max(1, allNodes.Count);
                fraction = complete ? 1 : Math.Clamp(fraction, 0, 0.999);
                double seconds = (timer.Elapsed - analysisStart).TotalSeconds;
                TimeSpan? remaining = fraction > .001 && seconds >= 1 ? TimeSpan.FromSeconds(Math.Min(7 * 86400, seconds * (1 - fraction) / fraction)) : null;
                progress?.Report(new ScanProgress(count, total, node.Path, "Анализ размеров", plannedFiles, foldersDone, allNodes.Count, fraction * 100, timer.Elapsed, complete ? TimeSpan.Zero : remaining));
            }
        }
        for (int i = allNodes.Count - 1; i >= 0; i--)
        {
            var node = allNodes[i]; node.Bytes = node.DirectBytes + node.Children.Sum(x => x.Bytes);
            node.Files += node.Children.Sum(x => x.Files);
        }
        progress?.Report(new ScanProgress(count, total, rootPath, "Готово", plannedFiles, allNodes.Count, allNodes.Count, 100, timer.Elapsed, TimeSpan.Zero));
        return new ScanResult(root, largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToList(), candidates, count, errors, links, timer.Elapsed);
    }
    internal static void ObserveFile(FolderNode node, DiskFile? file, long bytes, FileAttributes attributes, PriorityQueue<DiskFile, long> largest, List<DiskFile> candidates)
    {
        node.DirectBytes += bytes; node.Files++;
        if (file == null) return;
        if (file.Protected) { node.SystemReserveBytes += bytes; return; }
        if (largest.Count < 1000 || (largest.TryPeek(out _, out var minimum) && bytes > minimum))
        { largest.Enqueue(file, bytes); if (largest.Count > 1000) largest.Dequeue(); }
        if (bytes >= 1024 * 1024 && (attributes & (FileAttributes.System | FileAttributes.Offline)) == 0) candidates.Add(file);
    }
    private readonly record struct PlanEntry(bool Directory, bool Link, string? Path);
    private readonly record struct ScanEntry(bool Directory, FileAttributes Attributes, long Bytes, DateTime Modified, string? Path);
}
public sealed record DuplicateFile(int Group, DiskFile File, string Hash)
{
    public string Name => File.Name;
    public string Path => File.Path;
    public long Bytes => File.Bytes;
    public string DisplaySize => File.DisplaySize;
}
public static class DuplicateFinder
{
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    public static List<DuplicateFile> Find(IEnumerable<DiskFile> files, IProgress<string>? progress, CancellationToken token)
    {
        var grouped = files.GroupBy(x => x.Bytes).Where(x => x.Count() > 1).OrderByDescending(x => x.Key).ToList();
        var result = new List<DuplicateFile>(); var identities = new HashSet<string>(); int group = 0, done = 0, total = grouped.Sum(x => x.Count());
        var buffer = new byte[1024 * 1024];
        foreach (var sizeGroup in grouped)
        {
            var hashes = new Dictionary<string, List<DiskFile>>();
            foreach (var file in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!Format.NoLinks(file.Path)) continue;
                    var before = new FileInfo(file.Path);
                    if (before.Length != file.Bytes || before.LastWriteTimeUtc != file.Modified) continue;
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) continue;
                    var identity = $"{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
                    if (!identities.Add(identity)) continue; // Hard links occupy one physical file.
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int length;
                    while ((length = stream.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hasher.AppendData(buffer, 0, length); }
                    var hash = Convert.ToHexString(hasher.GetHashAndReset());
                    var after = new FileInfo(file.Path);
                    if (after.Length != file.Bytes || after.LastWriteTimeUtc != file.Modified) continue;
                    if (!hashes.TryGetValue(hash, out var list)) hashes[hash] = list = [];
                    list.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                finally { done++; progress?.Report($"Проверка содержимого: {done:N0} из {total:N0} файлов · {file.Name}"); }
            }
            foreach (var match in hashes.Values.Where(x => x.Count > 1))
            {
                group++;
                result.AddRange(match.Select(x => new DuplicateFile(group, x, hashes.First(kv => ReferenceEquals(kv.Value, match)).Key)));
            }
        }
        return result;
    }
}
public sealed record CacheRule(string Id, string Title, List<string> Roots, string? ProcessName = null, string Description = "Восстанавливаемый кэш. Личные данные не входят в очистку.", int MinAgeHours = 24);
public sealed record CacheFile(string Path, string Root, string RuleId, long Bytes, DateTime Modified)
{
    public string DisplaySize => Format.Size(Bytes);
}
public sealed class CacheGroup(CacheRule rule, List<CacheFile> files, int skipped)
{
    public CacheRule Rule { get; } = rule;
    public List<CacheFile> Files { get; } = files;
    public bool Selected { get; set; }
    public string Title => Rule.Title;
    public string Description => Rule.Description;
    public long Bytes => Files.Sum(x => x.Bytes);
    public string DisplaySize => Format.Size(Bytes);
    public string Count => $"Файлов: {Files.Count:N0}";
    public string Note => Rule.ProcessName != null && CacheCleaner.Running(Rule.ProcessName) ? "Сначала закройте приложение" : skipped > 0 ? $"Пропущено: {skipped}" : $"Файлы старше {Rule.MinAgeHours} ч";
}
public sealed record CleanResult(int Deleted, long Bytes, int Skipped, List<string> Errors);
public sealed class CacheCleaner
{
    private readonly Dictionary<string, CacheRule> rules;
    internal CacheCleaner(IEnumerable<CacheRule> knownRules) => rules = knownRules.ToDictionary(x => x.Id);
    public IReadOnlyCollection<CacheRule> Definitions => rules.Values;
    public static CacheCleaner ForThisUser(int minimumAgeHours = 24)
    {
        return ForPaths(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), minimumAgeHours);
    }
    internal static CacheCleaner ForPaths(string local, string roaming, int minimumAgeHours = 24)
    {
        var rules = new List<CacheRule> { new("temp", "Временные файлы", [Path.Combine(local, "Temp")], Description: "Temp текущего пользователя. Занятые, новые и изменённые файлы пропускаются.") };
        foreach (var browser in new[] { ("chrome", "Google Chrome", "Google\\Chrome\\User Data", "chrome"), ("edge", "Microsoft Edge", "Microsoft\\Edge\\User Data", "msedge"), ("brave", "Brave", "BraveSoftware\\Brave-Browser\\User Data", "brave"), ("yandex", "Яндекс Браузер", "Yandex\\YandexBrowser\\User Data", "browser") })
        {
            var roots = new List<string>(); var parent = Path.Combine(local, browser.Item3);
            try
            {
                if (Directory.Exists(parent))
                    foreach (var profile in Directory.EnumerateDirectories(parent).Where(x => Path.GetFileName(x) == "Default" || Path.GetFileName(x).StartsWith("Profile ", StringComparison.Ordinal)))
                        foreach (var cache in new[] { "Cache\\Cache_Data", "Code Cache", "GPUCache" }) roots.Add(Path.Combine(profile, cache));
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (roots.Any(Directory.Exists)) rules.Add(new CacheRule(browser.Item1, browser.Item2, roots, browser.Item4, "Кэш страниц, скомпилированного кода и GPU. Закладки, пароли, cookies и история сохраняются."));
        }
        void Add(string id, string title, string parent, string? process, string description, params string[] folders)
        {
            var roots = folders.Select(x => Path.Combine(parent, x)).Where(Directory.Exists).ToList();
            if (roots.Count > 0) rules.Add(new CacheRule(id, title, roots, process, description));
        }
        foreach (var app in new[] { ("vscode", "Visual Studio Code", "Code", "Code"), ("discord", "Discord", "discord", "Discord"), ("codex", "Codex Desktop", "Codex", "ChatGPT"), ("slack", "Slack", "Slack", "slack"), ("obsidian", "Obsidian", "obsidian", "Obsidian"), ("element", "Element", "Element", "Element") })
            Add(app.Item1, app.Item2, Path.Combine(roaming, app.Item3), app.Item4, "Только Cache, Code Cache и GPUCache. Переписка, сессии, расширения, настройки и резервные копии не затрагиваются.", "Cache", "Code Cache", "GPUCache");
        string claude = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(claude) && Format.NoLinks(claude))
        {
            try { foreach (var profile in Directory.EnumerateDirectories(claude).Where(p => Path.GetFileName(p).StartsWith("claude_", StringComparison.OrdinalIgnoreCase))) Add("claude-" + Path.GetFileName(profile), "Claude Desktop", profile, "Claude", "Кэш интерфейса. Данные чатов и входа в аккаунт остаются на месте.", "Cache", "Code Cache", "GPUCache"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Add("pip", "Python · pip", Path.Combine(local, "pip", "Cache"), null, "Загруженные HTTP-ответы и wheels. Пакеты не удаляются; при следующей установке данные могут скачаться снова.", "http", "http-v2", "wheels");
        Add("crashdumps", "Дампы сбоев приложений", local, null, "Диагностические дампы CrashDumps старше 7 дней. Могут понадобиться для разбора сбоя.", "CrashDumps");
        if (rules.LastOrDefault()?.Id == "crashdumps") rules[^1] = rules[^1] with { MinAgeHours = 168 };
        int floor = Math.Max(24, minimumAgeHours);
        return new CacheCleaner(rules.Select(rule => rule with { MinAgeHours = Math.Max(rule.MinAgeHours, floor) }));
    }
    public static bool Running(string name) { var processes = Process.GetProcessesByName(name); try { return processes.Length > 0; } finally { foreach (var p in processes) p.Dispose(); } }
    public List<CacheGroup> Preview(CancellationToken token)
    {
        var result = new List<CacheGroup>();
        foreach (var rule in rules.Values)
        {
            var cutoff = DateTime.UtcNow.AddHours(-rule.MinAgeHours);
            var files = new List<CacheFile>(); int skipped = 0;
            foreach (var root in rule.Roots)
            {
                if (!Directory.Exists(root) || !Format.NoLinks(root)) continue;
                var stack = new Stack<string>(); stack.Push(root);
                while (stack.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) { skipped++; continue; }
                                if (entry is DirectoryInfo) stack.Push(entry.FullName);
                                else if (entry is FileInfo file && file.LastWriteTimeUtc < cutoff && (rule.Id != "crashdumps" || file.Extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase)))
                                    files.Add(new CacheFile(file.FullName, root, rule.Id, file.Length, file.LastWriteTimeUtc));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            result.Add(new CacheGroup(rule, files, skipped));
        }
        return result;
    }
    public CleanResult Clean(IEnumerable<CacheFile> selected, IProgress<string>? progress, CancellationToken token)
    {
        int count = 0, skipped = 0; long bytes = 0; var errors = new List<string>();
        foreach (var snapshot in selected.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!rules.TryGetValue(snapshot.RuleId, out var rule) || !rule.Roots.Any(root => string.Equals(Path.GetFullPath(root), Path.GetFullPath(snapshot.Root), StringComparison.OrdinalIgnoreCase)) || !Format.Below(snapshot.Path, snapshot.Root) || !Format.NoLinks(snapshot.Path)) { skipped++; continue; }
                var cutoff = DateTime.UtcNow.AddHours(-rule.MinAgeHours);
                if (rule.Id == "crashdumps" && !new[] { ".dmp", ".mdmp" }.Contains(Path.GetExtension(snapshot.Path), StringComparer.OrdinalIgnoreCase)) { skipped++; continue; }
                if (rule.ProcessName != null && Running(rule.ProcessName)) { skipped++; continue; }
                var file = new FileInfo(snapshot.Path);
                if (!file.Exists || file.Length != snapshot.Bytes || file.LastWriteTimeUtc != snapshot.Modified || file.LastWriteTimeUtc >= cutoff || (file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) { skipped++; continue; }
                if (!CheckedDelete.TryDelete(snapshot)) { skipped++; continue; }
                count++; bytes += snapshot.Bytes;
                progress?.Report($"Удалено {count:N0} файлов · {Format.Size(bytes)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; if (errors.Count < 100) errors.Add(snapshot.Path + " — " + ex.Message);
            }
        }
        return new CleanResult(count, bytes, skipped, errors);
    }
}

// Delete an already opened file handle, never a freshly resolved path. Holding
// this handle without write/delete sharing also prevents modification between
// checking the snapshot and deleting it.
internal static class CheckedDelete
{
    [StructLayout(LayoutKind.Sequential)] private struct Info
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, HighSize, LowSize, LinkCount, HighIndex, LowIndex;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int value, uint size);
    public static bool TryDelete(CacheFile snapshot)
    {
        string full = Path.GetFullPath(snapshot.Path);
        using var handle = CreateFileW(full, 0x10000 | 0x80, 1, IntPtr.Zero, 3, 0x200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
        if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return false;
        long size = ((long)info.HighSize << 32) | info.LowSize;
        long time = ((long)(uint)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime;
        if (size != snapshot.Bytes || DateTime.FromFileTimeUtc(time) != snapshot.Modified) return false;
        var buffer = new System.Text.StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        string resolved = buffer.ToString();
        if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(resolved, full, StringComparison.OrdinalIgnoreCase) || !Format.Below(resolved, snapshot.Root)) return false;
        int delete = 1;
        return SetFileInformationByHandle(handle, 4, ref delete, sizeof(int));
    }
}
