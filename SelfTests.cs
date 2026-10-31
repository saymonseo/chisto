using System.Diagnostics;
using System.IO;

namespace Chisto;
public sealed record TestOutcome(string Test, bool Passed, string Detail);
public static class SelfTests
{
    public static List<TestOutcome> Run(string work)
    {
        var outcomes = new List<TestOutcome>();
        string root = Path.Combine(work, "chisto-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Test(string name, Action action)
        {
            try { action(); outcomes.Add(new TestOutcome(name, true, "OK")); }
            catch (Exception ex) { outcomes.Add(new TestOutcome(name, false, ex.Message)); }
        }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var cache = Path.Combine(root, "cache"); var outside = Path.Combine(root, "personal"); Directory.CreateDirectory(cache); Directory.CreateDirectory(outside);
        string OldFile(string name, string content = "old temporary content") { string p = Path.Combine(cache, name); File.WriteAllText(p, content); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-7)); return p; }
        var cleaner = new CacheCleaner([new CacheRule("test", "Fixture", [cache])]);
        CacheFile Snapshot(string p, string? allowedRoot = null) { var f = new FileInfo(p); return new CacheFile(f.FullName, allowedRoot ?? cache, "test", f.Length, f.LastWriteTimeUtc); }
        Test("Scanner aggregates folders and largest files", () =>
        {
            string scanRoot = Path.Combine(root, "scan"); Directory.CreateDirectory(Path.Combine(scanRoot, "nested"));
            File.WriteAllBytes(Path.Combine(scanRoot, "a.bin"), new byte[100]); File.WriteAllBytes(Path.Combine(scanRoot, "nested", "b.bin"), new byte[350]);
            var result = DiskScanner.Scan(scanRoot, null, CancellationToken.None);
            Assert(result.Root.Bytes == 450 && result.FileCount == 2 && result.Root.Children.Single().Bytes == 350 && result.Largest[0].Bytes == 350, "Wrong aggregated sizes");
        });
        Test("Scanner cancellation", () => { using var cts = new CancellationTokenSource(); cts.Cancel(); bool cancelled = false; try { DiskScanner.Scan(root, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; } Assert(cancelled, "Did not cancel"); });
        Test("Progress counts real files, stays bounded, and ends at 100", () =>
        {
            var values = new List<ScanProgress>(); var result = DiskScanner.Scan(Path.Combine(root, "scan"), new InlineProgress<ScanProgress>(values.Add), CancellationToken.None);
            Assert(values.Any(x => x.Percent == null), "Unknown inventory incorrectly presented as a percentage");
            var known = values.Where(x => x.Percent.HasValue).ToList();
            Assert(known.Count > 0 && known.Last().Percent == 100 && known.Last().TotalFiles == 2 && known.Last().Files == 2, "Wrong final total");
            Assert(known.All(x => x.Percent >= 0 && x.Percent <= 100) && known.Zip(known.Skip(1), (a, b) => b.Percent >= a.Percent).All(x => x), "Non-monotonic percentage");
            Assert(known.Where(x => x.Stage != "Готово").All(x => x.Percent < 100), "Premature 100 percent");
        });
        Test("Cleanup removes only old eligible files", () =>
        {
            string old = OldFile("old.tmp"); string fresh = Path.Combine(cache, "fresh.tmp"); File.WriteAllText(fresh, "fresh");
            var preview = cleaner.Preview(CancellationToken.None).Single(); Assert(preview.Files.Any(x => x.Path == old) && !preview.Files.Any(x => x.Path == fresh), "Age filter failed");
            var cleaned = cleaner.Clean(preview.Files, null, CancellationToken.None);
            Assert(cleaned.Deleted == 1 && !File.Exists(old) && File.Exists(fresh), "Unexpected deletion result");
        });
        Test("Changed snapshot is never deleted", () => { string p = OldFile("changed.tmp"); var snap = Snapshot(p); File.AppendAllText(p, "modified"); var result = cleaner.Clean([snap], null, CancellationToken.None); Assert(result.Deleted == 0 && File.Exists(p), "Changed file deleted"); });
        Test("Forged path outside cache is rejected", () => { string p = Path.Combine(outside, "important.txt"); File.WriteAllText(p, "keep me"); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-7)); var result = cleaner.Clean([Snapshot(p)], null, CancellationToken.None); Assert(result.Deleted == 0 && File.Exists(p), "Escaped whitelist"); });
        Test("Forged root and unknown category are rejected", () => { string p = Path.Combine(outside, "important.txt"); var fake = Snapshot(p, outside); var result = cleaner.Clean([fake, fake with { RuleId = "unknown" }], null, CancellationToken.None); Assert(result.Deleted == 0 && File.Exists(p), "Forged root allowed"); });
        Test("Read-only and locked files survive", () =>
        {
            string readOnly = OldFile("readonly.tmp"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            string locked = OldFile("locked.tmp"); var snapshot = Snapshot(locked);
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = cleaner.Clean([snapshot, Snapshot(readOnly)], null, CancellationToken.None); Assert(result.Deleted == 0 && File.Exists(locked) && File.Exists(readOnly), "Protected file deleted");
            }
            File.SetAttributes(readOnly, FileAttributes.Normal);
        });
        Test("Cleanup cancellation leaves remaining files", () => { string p = OldFile("cancel.tmp"); using var cts = new CancellationTokenSource(); cts.Cancel(); var result = cleaner.Clean([Snapshot(p)], null, cts.Token); Assert(result.Deleted == 0 && File.Exists(p), "Cancellation ignored"); });
        Test("Junctions cannot escape cache and are skipped by scanner", () =>
        {
            string link = Path.Combine(cache, "redirect"); string escaped = Path.Combine(link, "important.txt");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, outside }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; process.WaitForExit();
            Assert(process.ExitCode == 0 && Directory.Exists(link), "Test junction could not be created");
            try
            {
                Assert(!Format.NoLinks(escaped), "Junction ancestor missed");
                var result = cleaner.Clean([Snapshot(escaped)], null, CancellationToken.None); Assert(result.Deleted == 0 && File.Exists(Path.Combine(outside, "important.txt")), "Junction target was deleted");
                Assert(!cleaner.Preview(CancellationToken.None).Single().Files.Any(x => x.Path.Contains("redirect")), "Preview traversed junction");
                Assert(DiskScanner.Scan(cache, null, CancellationToken.None).Links == 1, "Scanner did not skip junction");
            }
            finally { Directory.Delete(link); }
        });
        Test("Duplicates use content, not size; hard links excluded", () =>
        {
            string dir = Path.Combine(root, "duplicates"); Directory.CreateDirectory(dir); byte[] a = new byte[1024 * 1024 + 1]; new Random(42).NextBytes(a);
            File.WriteAllBytes(Path.Combine(dir, "a.dat"), a); File.WriteAllBytes(Path.Combine(dir, "b.dat"), a); a[0] ^= 0xff; File.WriteAllBytes(Path.Combine(dir, "different.dat"), a);
            var fileA = Path.Combine(dir, "a.dat"); var hard = Path.Combine(dir, "hard.dat");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/d", "/c", "mklink", "/H", hard, fileA }) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start)!) { process.WaitForExit(); Assert(process.ExitCode == 0, "Hard-link fixture failed"); }
            var result = DuplicateFinder.Find(DiskScanner.Scan(dir, null, CancellationToken.None).DuplicateCandidates, null, CancellationToken.None);
            Assert(result.Count == 2 && result.All(x => x.Name != "different.dat") && result.Select(x => x.Group).Distinct().Count() == 1, "False duplicate or hard-link double count");
        });
        Test("AppData cleanup preserves settings, cookies, chats, and IndexedDB", () =>
        {
            var local = Path.Combine(root, "Local"); var roaming = Path.Combine(root, "Roaming");
            string code = Path.Combine(roaming, "Code"); string cachePath = Path.Combine(code, "Cache"); Directory.CreateDirectory(cachePath);
            var keep = new[] { Path.Combine(code, "User", "settings.json"), Path.Combine(code, "IndexedDB", "state.db"), Path.Combine(code, "Network", "Cookies"), Path.Combine(roaming, "Telegram Desktop", "tdata", "key_datas") };
            foreach (var path in keep.Append(Path.Combine(cachePath, "old-cache.bin"))) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8)); }
            var definitions = CacheCleaner.ForPaths(local, roaming).Definitions.Select(x => x with { ProcessName = null });
            var fixtureCleaner = new CacheCleaner(definitions);
            var candidates = fixtureCleaner.Preview(CancellationToken.None).SelectMany(x => x.Files).ToList();
            Assert(candidates.Count == 1 && candidates[0].Path.EndsWith("old-cache.bin"), "Personal state included in cleanup");
            var deleted = fixtureCleaner.Clean(candidates, null, CancellationToken.None);
            Assert(deleted.Deleted == 1 && keep.All(File.Exists), "Personal state was deleted");
        });
        Test("Crash dump cleanup respects extension and seven-day age", () =>
        {
            string local = Path.Combine(root, "CrashLocal"); string dumps = Path.Combine(local, "CrashDumps"); Directory.CreateDirectory(dumps);
            string oldDump = Path.Combine(dumps, "old.dmp"), fresh = Path.Combine(dumps, "recent.dmp"), personal = Path.Combine(dumps, "notes.txt");
            foreach (var path in new[] { oldDump, fresh, personal }) { File.WriteAllText(path, "fixture"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(path == fresh ? -2 : -8)); }
            var fixtureCleaner = CacheCleaner.ForPaths(local, Path.Combine(root, "NoRoaming"));
            var found = fixtureCleaner.Preview(CancellationToken.None).SelectMany(x => x.Files).ToList();
            Assert(found.Count == 1 && found[0].Path == oldDump, "Wrong crash-dump selection");
            var info = new FileInfo(personal); var forged = new CacheFile(personal, dumps, "crashdumps", info.Length, info.LastWriteTimeUtc);
            var deleted = fixtureCleaner.Clean(found.Append(forged), null, CancellationToken.None);
            Assert(deleted.Deleted == 1 && File.Exists(personal) && File.Exists(fresh), "Extension or age rule bypassed");
        });
        Test("Windows protected file labels apply only at drive root", () =>
        {
            Assert(new DiskFile("C:\\pagefile.sys", 0, default).Protected && new DiskFile("C:\\hiberfil.sys", 0, default).Protected, "System file unlabelled");
            Assert(!new DiskFile(Path.Combine(root, "pagefile.sys"), 0, default).Protected, "Ordinary names mislabelled");
        });
        Test("System reserve stays in totals but never enters file or duplicate lists", () =>
        {
            var node = new FolderNode("C:\\"); var largest = new PriorityQueue<DiskFile, long>(); var candidates = new List<DiskFile>();
            var reserve = new[] { "C:\\pagefile.sys", "C:\\hiberfil.sys", "C:\\swapfile.sys" };
            foreach (string path in reserve) DiskScanner.ObserveFile(node, new DiskFile(path, 2 * 1024 * 1024, default), 2 * 1024 * 1024, FileAttributes.Normal, largest, candidates);
            var normal = new DiskFile("C:\\Users\\example\\pagefile.sys", 3 * 1024 * 1024, default);
            DiskScanner.ObserveFile(node, normal, normal.Bytes, FileAttributes.Normal, largest, candidates);
            Assert(node.DirectBytes == 9 * 1024 * 1024 && node.SystemReserveBytes == 6 * 1024 * 1024 && node.Files == 4, "Reserve not reconciled with totals");
            Assert(largest.Count == 1 && largest.Peek() == normal && candidates.Count == 1 && candidates[0] == normal, "System files exposed or ordinary names hidden");
        });
        Test("Release parser requires correct repository and SHA-256", () =>
        {
            var updates = new UpdateClient("saymonseo", "chisto");
            var future = new Version(UpdateClient.Current.Major, UpdateClient.Current.Minor + 1, 0);
            string Json(string url, string hash) => System.Text.Json.JsonSerializer.Serialize(new { tag_name = "v" + future, draft = false, prerelease = false, assets = new[] { new { name = $"Chisto-Setup-{future}.exe", browser_download_url = url, digest = hash, size = 12345 } } });
            string valid = $"https://github.com/saymonseo/chisto/releases/download/v{future}/Chisto-Setup-{future}.exe"; string digest = "sha256:" + new string('a',64);
            Assert(updates.Parse(Json(valid,digest))?.Version == future, "Valid release rejected");
            foreach (string url in new[] { valid.Replace("https:","http:"),valid.Replace("/saymonseo/", "/other/"),valid.Replace("github.com/", "github.com.evil.example/") })
            { bool rejected = false; try { updates.Parse(Json(url,digest)); } catch (IOException) { rejected = true; } Assert(rejected,"Foreign download accepted"); }
            bool badHash = false; try { updates.Parse(Json(valid,"")); } catch (IOException) { badHash = true; } Assert(badHash,"Missing hash accepted");
        });
        Test("Theme preference survives reload and invalid settings fall back safely", () =>
        {
            string file = Path.Combine(root, "preferences", "preferences.json"); var store = new PreferencesStore(file);
            Assert(store.Load().Theme == "dark", "Missing settings must preserve default dark theme");
            store.Save(new UserPreferences("light")); Assert(new PreferencesStore(file).Load().Theme == "light", "Light preference lost on reload");
            store.Save(new UserPreferences("dark")); Assert(new PreferencesStore(file).Load().Theme == "dark", "Dark preference lost on reload");
            File.WriteAllText(file, "broken json"); Assert(store.Load().Theme == "dark", "Corrupt preferences crashed or had invalid fallback");
            File.WriteAllText(file, "{\"theme\":\"unknown\"}"); Assert(store.Load().Theme == "dark", "Invalid theme accepted");
        });
        Test("Settings migrate old preferences and preserve fields when theme changes", () =>
        {
            string file = Path.Combine(root,"settings.json"); var store = new PreferencesStore(file);
            File.WriteAllText(file,"{\"Theme\":\"light\"}"); var old = store.Load();
            Assert(old.Theme == "light" && old.CacheMinAgeHours == 24 && !old.AutoScanOnLaunch && !old.StartMinimized, "Old settings failed to migrate");
            var settings = new UserPreferences("light",root,true,true,168); store.Save(settings);
            store.Save(store.Load() with { Theme = "dark" }); var loaded = store.Load();
            Assert(loaded == settings with { Theme = "dark" }, "Changing theme lost another preference");
            store.Save(settings with { CacheMinAgeHours=0,DefaultScanPath="relative" }); loaded=store.Load();
            Assert(loaded.CacheMinAgeHours==24 && Path.IsPathFullyQualified(loaded.DefaultScanPath), "Unsafe settings not normalized");
        });
        Test("Startup registration changes only the owned value in an isolated registry fixture", () =>
        {
            string key = @"Software\Chisto.Tests\" + Guid.NewGuid().ToString("N") + @"\Run";
            string fixtureRoot = key[..key.LastIndexOf('\\')];
            try
            {
                using(var existing=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) existing.SetValue("OtherApp","keep");
                string exe=Path.Combine(root,"Startup App.exe"); File.WriteAllText(exe,"fixture; not executed");
                var startup=new WindowsStartup(key); Assert(!startup.Enabled,"Unexpected initial startup");
                startup.SetEnabled(true,exe); Assert(startup.Enabled && startup.Command()=="\""+exe+"\" --startup","Bad startup command quoting");
                startup.SetEnabled(false,exe); using var check=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
                Assert(!startup.Enabled && (string?)check?.GetValue("OtherApp")=="keep","Other startup data modified");
            }
            finally { Assert(fixtureRoot.StartsWith(@"Software\Chisto.Tests\",StringComparison.Ordinal),"Unsafe fixture cleanup"); Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(fixtureRoot,false); }
        });
        Test("Configured cache age is enforced again at deletion", () =>
        {
            string local=Path.Combine(root,"AgeLocal"), temp=Path.Combine(local,"Temp"); Directory.CreateDirectory(temp);
            string young=Path.Combine(temp,"two-days.tmp"),old=Path.Combine(temp,"four-days.tmp");
            foreach(string p in new[]{young,old}) { File.WriteAllText(p,"fixture"); File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddDays(p==young?-2:-4)); }
            var instance=CacheCleaner.ForPaths(local,Path.Combine(root,"AgeRoaming"),72); var files=instance.Preview(CancellationToken.None).SelectMany(x=>x.Files).ToList();
            Assert(files.Count==1&&files[0].Path==old,"Age setting not applied to preview");
            var info=new FileInfo(young); var forged=new CacheFile(young,temp,"temp",info.Length,info.LastWriteTimeUtc);
            var cleaned=instance.Clean(files.Append(forged),null,CancellationToken.None);
            Assert(cleaned.Deleted==1&&File.Exists(young),"Age setting bypassed during deletion");
        });
        Test("Installed update plan targets the current folder and preserves Windows launch checks", () =>
        {
            string setup=Path.Combine(root,"setup.exe"), target=Path.Combine(root,"Installed App");
            var plan=UpdateInstallation.Plan(setup,target,true);
            Assert(plan.UseShellExecute && plan.ArgumentList.Contains("/SILENT") && plan.ArgumentList.Contains("/NORESTART") && plan.ArgumentList.Contains("/CHISTOUPDATE=1") && plan.ArgumentList.Contains("/DIR="+target),"Incomplete upgrade plan");
            var portable=UpdateInstallation.Plan(setup,target,false);
            Assert(portable.ArgumentList.Count==0,"Portable copy silently converted to installed");
        });
        Test("Live theme change updates existing panels, text, buttons and grids", () =>
        {
            bool wasLight = ThemeManager.IsLight;
            System.Windows.Window? host = null;
            try
            {
                ThemeManager.Apply("dark");
                var panel = new System.Windows.Controls.Border { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("Panel") };
                var button = new System.Windows.Controls.Button { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("Primary") };
                var grid = new System.Windows.Controls.DataGrid { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.DataGrid)) };
                var text = new System.Windows.Controls.TextBlock(); text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Ink");
                var content = new System.Windows.Controls.StackPanel(); content.Children.Add(panel); content.Children.Add(button); content.Children.Add(grid); content.Children.Add(text);
                host = new System.Windows.Window { Content = content };
                static string Hex(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color.ToString();
                Assert(Hex(panel.Background) == "#FF121A28" && Hex(text.Foreground) == "#FFE8EEF9", "Wrong dark colors");
                ThemeManager.Apply("light");
                Assert(Hex(panel.Background) == "#FFFFFFFF" && Hex(grid.Background) == "#FFFFFFFF" && Hex(text.Foreground) == "#FF17263F", $"Existing views did not switch to light: panel={Hex(panel.Background)}, grid={Hex(grid.Background)}, text={Hex(text.Foreground)}");
                Assert(Hex(button.Background) == "#FF2F6FDA" && Hex(button.Foreground) == "#FFFFFFFF", "Light button contrast is incorrect");
                ThemeManager.Apply("dark"); Assert(Hex(panel.Background) == "#FF121A28" && Hex(text.Foreground) == "#FFE8EEF9", "Switching back failed");
            }
            finally { host?.Close(); ThemeManager.Apply(wasLight ? "light" : "dark"); }
        });
        try { Directory.Delete(root, true); } catch (Exception ex) { outcomes.Add(new TestOutcome("Fixture cleanup", false, ex.Message)); }
        return outcomes;
    }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}
