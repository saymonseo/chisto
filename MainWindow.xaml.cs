using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Chisto;
public partial class MainWindow : Window
{
    private CancellationTokenSource? operation;
    private ScanResult? scan;
    private FolderNode? currentFolder;
    private CacheCleaner cleaner = CacheCleaner.ForThisUser();
    private List<CacheGroup> caches = [];
    private List<DuplicateFile> duplicates = [];
    private readonly string[] palette = ["#416FBA", "#3A8E92", "#6A63A4", "#98764B", "#4A6F86", "#567F67", "#5A67A3", "#866D8C", "#556879", "#647899", "#4F7680", "#6C797B"];
    private readonly string[] lightPalette = ["#C8DCF8", "#BFE3E0", "#DCD3F1", "#EDDDC4", "#C8DEE9", "#D1E6D4", "#D4DAF4", "#E5D3E5", "#D5E0E9", "#D4DEEE", "#CAE4E7", "#DEE3DA"];
    private bool changingTheme = true;
    private Button? selectedNavigation;
    private readonly Stopwatch operationClock = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly UpdateClient updateClient = UpdateClient.Load();
    private ReleaseUpdate? availableUpdate;
    private List<AppDataEntry> appDataEntries = [];
    private TimeSpan? lastEta;
    private TimeSpan lastProgressElapsed;
    public MainWindow()
    {
        InitializeComponent();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width - 24); MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        SourceInitialized += (_, _) => ApplyWindowChrome();
        clock.Tick += (_, _) => { if (operation != null) UpdateClock(); };
        UpdateSourceLabel.Text = $"Источник: {updateClient.Owner}/{updateClient.Repository} · GitHub Releases";
        VersionLabel.Text = $"Чисто / {UpdateClient.Current}"; UpdateVersionLabel.Text = $"Чисто {UpdateClient.Current}";
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed)) DrivePicker.Items.Add(drive.Name);
        DrivePicker.SelectedItem = DrivePicker.Items.Contains("C:\\") ? "C:\\" : DrivePicker.Items.Cast<string>().FirstOrDefault();
        ShowPage(MapPage, NavMap);
        cleaner = CacheCleaner.ForThisUser(preferences.CacheMinAgeHours);
        LoadSettings(); changingTheme = false;
        if (Directory.Exists(preferences.DefaultScanPath))
        {
            string? drive = Path.GetPathRoot(preferences.DefaultScanPath); if (DrivePicker.Items.Contains(drive)) DrivePicker.SelectedItem = drive;
            ChangeRoot(preferences.DefaultScanPath);
        }
        Closed += (_, _) => operation?.Cancel();
    }
    private void ShowPage(Grid page, Button button)
    {
        foreach (var item in new[] { MapPage, FilesPage, DuplicatesPage, CleanPage, AppDataPage, UpdatesPage, SettingsPage }) item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        AnalysisToolbar.Visibility = page == SettingsPage || page == UpdatesPage ? Visibility.Collapsed : Visibility.Visible;
        selectedNavigation = button; UpdateNavigation();
        PageTitle.Text = page == SettingsPage ? "Настройки программы" : page == MapPage ? "Пространство под контролем" : page == FilesPage ? "Крупные файлы" : page == DuplicatesPage ? "Одинаковое содержимое" : page == AppDataPage ? "Что хранят приложения" : page == UpdatesPage ? "Всегда актуальная версия" : "Очистка приложений";
        PageIntro.Text = page == SettingsPage ? "Оформление, запуск и правила очистки." : page == MapPage ? "Карта папок и точный учёт объёма доступных файлов." : page == FilesPage ? "Самые объёмные файлы выбранной области и их расположение." : page == DuplicatesPage ? "Сначала размер, затем SHA-256 всего содержимого." : page == AppDataPage ? "Разберите AppData по приложениям, а не удаляйте её целиком." : page == UpdatesPage ? "Новые версии из вашего репозитория GitHub Releases." : "Предварительный список файлов и отдельное подтверждение удаления.";
        if (page == MapPage) Dispatcher.BeginInvoke(DrawMap);
        RefreshOperationPanel();
    }
    private void RefreshOperationPanel() => OperationPanel.Visibility = operation == null && (SettingsPage.Visibility == Visibility.Visible || UpdatesPage.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
    private void MapPage_Click(object sender, RoutedEventArgs e) => ShowPage(MapPage, NavMap);
    private void UpdateNavigation()
    {
        foreach (var item in new[] { NavMap, NavFiles, NavDuplicates, NavClean, NavAppData, NavUpdates, NavSettings })
        {
            item.Background = item == selectedNavigation ? (Brush)FindResource("NavSelected") : Brushes.Transparent;
            item.Foreground = (Brush)FindResource(item == selectedNavigation ? "Lime" : "NavText");
        }
    }
    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (changingTheme || sender is not System.Windows.Controls.Primitives.ToggleButton item) return;
        string theme = item.IsChecked == true ? "light" : "dark";
        ApplyTheme(theme);
        try { preferences = new PreferencesStore().Load() with { Theme = theme }; new PreferencesStore().Save(preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusLabel.Text = "Тема изменена, но сохранить выбор не удалось: " + ex.Message; }
    }
    internal void ApplyTheme(string theme)
    {
        ThemeManager.Apply(theme); UpdateNavigation(); DrawMap(); ApplyWindowChrome();
        changingTheme = true; ThemeToggle.IsChecked = SettingsThemeToggle.IsChecked = ThemeManager.IsLight; changingTheme = false;
        ThemeToggle.ToolTip = ThemeManager.IsLight ? "Светлая тема. Переключить на тёмную" : "Тёмная тема. Переключить на светлую";
    }
    private void ApplyWindowChrome()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = ThemeManager.IsLight ? 0 : 1; DwmSetWindowAttribute(handle, 20, ref dark, 4);
    }
    private void FilesPage_Click(object sender, RoutedEventArgs e) => ShowPage(FilesPage, NavFiles);
    private void DuplicatesPage_Click(object sender, RoutedEventArgs e) => ShowPage(DuplicatesPage, NavDuplicates);
    private void CleanPage_Click(object sender, RoutedEventArgs e) => ShowPage(CleanPage, NavClean);
    private void AppDataPage_Click(object sender, RoutedEventArgs e)
    {
        string path = Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!.FullName;
        var node = FindInLastScan(path);
        if (node != null) PopulateAppData(node, "Из последнего анализа");
        ShowPage(AppDataPage, NavAppData);
    }
    private void UpdatesPage_Click(object sender, RoutedEventArgs e) => ShowPage(UpdatesPage, NavUpdates);
    private void UpdateDrive(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            UsedLabel.Text = Format.Size(drive.TotalSize - drive.TotalFreeSpace); FreeLabel.Text = Format.Size(drive.AvailableFreeSpace);
            DriveTotal.Text = $"{drive.Name} · всего {Format.Size(drive.TotalSize)}"; DriveBar.Value = 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
        }
        catch { UsedLabel.Text = FreeLabel.Text = "—"; DriveTotal.Text = "Нет доступа к диску"; }
    }
    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RootBox != null && DrivePicker.SelectedItem is string drive) ChangeRoot(drive);
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку для анализа", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ChangeRoot(dialog.FolderName);
    }
    private void ChangeRoot(string path)
    {
        if (scan != null && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(scan.Root.Path), StringComparison.OrdinalIgnoreCase))
        {
            ResetDiskResults(); StatusLabel.Text = "Область изменена. Нажмите «Сканировать», чтобы получить новый результат.";
        }
        RootBox.Text = path; UpdateDrive(path);
    }
    private void ResetDiskResults()
    {
        scan = null; currentFolder = null; FolderGrid.ItemsSource = null; FilesGrid.ItemsSource = null;
        duplicates = []; DuplicatesGrid.ItemsSource = null; DuplicateSummary.Text = "Сначала просканируйте выбранную область.";
        MapCanvas.Children.Clear(); MapEmpty.Visibility = Visibility.Visible; UpButton.IsEnabled = false;
        Breadcrumb.Text = "Площадь каждого блока пропорциональна размеру папки."; RestoreScanLabels();
    }
    private bool BeginOperation()
    {
        if (operation != null) return false;
        operation = new CancellationTokenSource();
        RefreshOperationPanel();
        foreach (var button in new[] { ScanButton, FolderButton, FindDuplicatesButton, PreviewButton, CleanButton, AuditButton, CheckUpdateButton, InstallUpdateButton, SaveSettingsButton }) button.IsEnabled = false;
        DrivePicker.IsEnabled = false; WorkBar.Visibility = Visibility.Visible; WorkBar.IsIndeterminate = true; CancelButton.Visibility = Visibility.Visible;
        lastEta = null; lastProgressElapsed = TimeSpan.Zero; operationClock.Restart(); clock.Start(); OperationTitle.Text = "Подготовка"; PercentLabel.Text = "—"; RemainingLabel.Text = "Осталось —"; ProgressCount.Text = "Определяем объём работы…"; TimeLabel.Text = "Прошло 0 сек · Осталось —";
        return true;
    }
    private void EndOperation()
    {
        operation?.Dispose(); operation = null;
        foreach (var button in new[] { ScanButton, FolderButton, FindDuplicatesButton, PreviewButton, AuditButton, CheckUpdateButton, SaveSettingsButton }) button.IsEnabled = true;
        CleanButton.IsEnabled = caches.Any(x => x.Files.Count > 0); DrivePicker.IsEnabled = true;
        WorkBar.IsIndeterminate = false; CancelButton.Visibility = Visibility.Collapsed; clock.Stop(); operationClock.Stop();
        InstallUpdateButton.IsEnabled = availableUpdate != null;
        RefreshOperationPanel();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { operation?.Cancel(); StatusLabel.Text = "Останавливаем текущую операцию…"; }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await StartScan(RootBox.Text);
    public async Task StartScan(string path)
    {
        if (!BeginOperation()) return;
        ChangeRoot(path); ShowPage(MapPage, NavMap); StatusLabel.Text = "Читаем размеры папок…";
        var progress = new Progress<ScanProgress>(p =>
        {
            StatusLabel.Text = p.Current; OperationTitle.Text = p.Stage;
            WorkBar.IsIndeterminate = !p.Percent.HasValue;
            double shown = p.Stage == "Готово" ? 100 : Math.Floor(p.Percent ?? 0);
            PercentLabel.Text = p.Percent.HasValue ? $"{shown:N0}%" : "Учёт…";
            RemainingLabel.Text = p.Percent.HasValue ? $"Осталось {100 - shown:N0}%" : "Общее число файлов ещё определяется";
            WorkBar.Value = p.Percent ?? 0;
            ProgressCount.Text = p.Stage == "Готово" ? $"Обработано файлов: {p.Files:N0} · папок: {p.TotalFolders:N0}" : p.Percent.HasValue ? $"Файлы: {p.Files:N0} / {p.TotalFiles:N0} · папки: {p.FoldersDone:N0} / {p.TotalFolders:N0}" : $"Найдено файлов: {p.Files:N0} · папок: {p.TotalFolders:N0}";
            ProgressCount.ToolTip = $"В первичном учёте было {p.TotalFiles:N0} файлов. При изменении папок во время анализа итог может отличаться.";
            lastEta = p.Remaining; lastProgressElapsed = operationClock.Elapsed; UpdateClock();
            AnalyzedLabel.Text = p.Percent.HasValue ? Format.Size(p.Bytes) : "Учёт…"; FileCountLabel.Text = $"Файлов: {p.Files:N0}";
        });
        try
        {
            scan = await Task.Run(() => DiskScanner.Scan(path, progress, operation!.Token));
            AnalyzedLabel.Text = Format.Size(scan.Root.Bytes); FileCountLabel.Text = $"Файлов: {scan.FileCount:N0} · {scan.Duration.TotalSeconds:N0} сек.";
            duplicates = []; DuplicatesGrid.ItemsSource = null; DuplicateSummary.Text = "Нажмите «Найти одинаковые файлы» для новой области.";
            ApplyFileFilter(); ShowFolder(scan.Root);
            UpdateDrive(path);
            StatusLabel.Text = $"Готово · недоступных записей: {scan.Inaccessible:N0} · ссылок пропущено: {scan.Links:N0}. Размеры папок приблизительные: общие и сжатые файлы учитываются иначе.";
        }
        catch (OperationCanceledException) { RestoreScanLabels(); OperationTitle.Text = "Сканирование остановлено"; RemainingLabel.Text = "Результат не завершён"; TimeLabel.Text = "Прошло " + Duration(operationClock.Elapsed); StatusLabel.Text = "Сканирование остановлено. Предыдущий результат сохранён."; }
        catch (Exception ex) { RestoreScanLabels(); StatusLabel.Text = "Не удалось завершить анализ."; MessageBox.Show(this, ex.Message, "Ошибка анализа", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { EndOperation(); }
    }
    private void RestoreScanLabels() { AnalyzedLabel.Text = scan == null ? "—" : Format.Size(scan.Root.Bytes); FileCountLabel.Text = scan == null ? "Сканирование не завершено" : $"Последний результат: {scan.FileCount:N0} файлов"; }
    private void ShowFolder(FolderNode node)
    {
        currentFolder = node; Breadcrumb.Text = node.Path + "  ·  " + node.DisplaySize; UpButton.IsEnabled = node.Parent != null;
        FolderGrid.ItemsSource = node.Children.OrderByDescending(x => x.Bytes).ToList(); MapEmpty.Visibility = node.Bytes == 0 ? Visibility.Visible : Visibility.Collapsed; DrawMap();
    }
    private void Folder_DoubleClick(object sender, MouseButtonEventArgs e) { if (FolderGrid.SelectedItem is FolderNode node) ShowFolder(node); }
    private void Up_Click(object sender, RoutedEventArgs e) { if (currentFolder?.Parent is FolderNode parent) ShowFolder(parent); }
    private void Map_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMap();
    private sealed record Tile(string Name, long Bytes, FolderNode? Folder);
    private void DrawMap()
    {
        if (MapCanvas == null) return;
        MapCanvas.Children.Clear();
        if (currentFolder == null || currentFolder.Bytes == 0 || MapCanvas.ActualWidth < 5 || MapCanvas.ActualHeight < 5) return;
        var nodes = currentFolder.Children.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToList();
        var tiles = nodes.Take(10).Select(x => new Tile(x.Name, x.Bytes, x)).ToList();
        if (nodes.Count > 10) tiles.Add(new Tile($"Другие папки · {nodes.Count - 10}", nodes.Skip(10).Sum(x => x.Bytes), null));
        if (currentFolder.SystemReserveBytes > 0) tiles.Add(new Tile("Системный резерв", currentFolder.SystemReserveBytes, null));
        if (currentFolder.DirectBytes > currentFolder.SystemReserveBytes) tiles.Add(new Tile("Файлы в этой папке", currentFolder.DirectBytes - currentFolder.SystemReserveBytes, null));
        tiles = tiles.OrderByDescending(x => x.Bytes).ToList();
        DrawTiles(tiles, 0, 0, MapCanvas.ActualWidth, MapCanvas.ActualHeight, 0);
    }
    private void DrawTiles(List<Tile> tiles, double x, double y, double w, double h, int color)
    {
        if (tiles.Count == 0) return;
        if (tiles.Count == 1)
        {
            var tile = tiles[0]; if (w < 7 || h < 7) return;
            var colors = ThemeManager.IsLight ? lightPalette : palette;
            var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[color % colors.Length]));
            var box = new Border { Background = fill, CornerRadius = new CornerRadius(8), Width = Math.Max(0, w - 5), Height = Math.Max(0, h - 5), Padding = new Thickness(w > 150 ? 17 : 8), ClipToBounds = true, ToolTip = $"{tile.Folder?.Path ?? tile.Name}\n{Format.Size(tile.Bytes)}", Cursor = tile.Folder == null ? Cursors.Arrow : Cursors.Hand };
            if (w > 70 && h > 45)
            {
                var text = new StackPanel(); text.Children.Add(new TextBlock { Text = tile.Name, FontSize = w > 200 ? 16 : 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = Format.Size(tile.Bytes), FontFamily = new FontFamily("Bahnschrift"), FontSize = w > 200 && h > 95 ? 29 : 17, Margin = new Thickness(0, 7, 0, 0) });
                if (h > 128 && w > 180) text.Children.Add(new TextBlock { Text = $"{100.0 * tile.Bytes / currentFolder!.Bytes:N1}% этой папки", FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.65 });
                box.Child = text;
            }
            if (tile.Folder != null) box.MouseLeftButtonDown += (_, _) => ShowFolder(tile.Folder);
            Canvas.SetLeft(box, x + 2); Canvas.SetTop(box, y + 2); MapCanvas.Children.Add(box); return;
        }
        var total = tiles.Sum(t => t.Bytes); long partial = 0; int split = 0;
        do { partial += tiles[split++].Bytes; } while (split < tiles.Count - 1 && partial < total / 2);
        double ratio = (double)partial / total;
        if (w >= h) { DrawTiles(tiles.Take(split).ToList(), x, y, w * ratio, h, color); DrawTiles(tiles.Skip(split).ToList(), x + w * ratio, y, w * (1 - ratio), h, color + split); }
        else { DrawTiles(tiles.Take(split).ToList(), x, y, w, h * ratio, color); DrawTiles(tiles.Skip(split).ToList(), x, y + h * ratio, w, h * (1 - ratio), color + split); }
    }
    private void ApplyFileFilter() { if (FilesGrid != null) { var query = FileSearch.Text.Trim(); FilesGrid.ItemsSource = scan?.Largest.Where(x => x.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList(); } }
    private void FileSearch_Changed(object sender, TextChangedEventArgs e) => ApplyFileFilter();
    private void Reveal(string? path)
    {
        if (path == null) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show(this, "По этому пути файл уже недоступен. Он мог быть перемещён или удалён после сканирования. Повторите анализ.", "Файл недоступен"); return; }
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
                MessageBox.Show(this, $"{Path.GetFileName(path)} существует, но скрыт стандартными настройками Проводника как защищённый системный файл.\n\n" + (Path.GetFileName(path).Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ? "Это файл подкачки Windows." : Path.GetFileName(path).Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ? "Это файл гибернации Windows." : "Это служебный файл Windows.") + "\n\nСейчас откроется его папка. Файл может оставаться невидимым. Его размер уже учтён в анализе. Вручную удалять его не нужно.", "Защищённый файл Windows", MessageBoxButton.OK, MessageBoxImage.Information);
            ExplorerSelection.Show(path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть Проводник"); }
    }
    private void RevealFile_Click(object sender, RoutedEventArgs e) => Reveal((FilesGrid.SelectedItem as DiskFile)?.Path);
    private void OpenFile_DoubleClick(object sender, MouseButtonEventArgs e) => Reveal((FilesGrid.SelectedItem as DiskFile)?.Path);
    private void CopyFile_Click(object sender, RoutedEventArgs e) { if (FilesGrid.SelectedItem is DiskFile file) Clipboard.SetText(file.Path); }
    private void RevealDuplicate_Click(object sender, RoutedEventArgs e) => Reveal((DuplicatesGrid.SelectedItem as DuplicateFile)?.Path);
    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (scan == null) { MessageBox.Show(this, "Сначала просканируйте диск или папку.", "Поиск дубликатов"); return; }
        if (!BeginOperation()) return;
        StatusLabel.Text = "Ищем файлы одинакового размера…";
        var progress = new Progress<string>(text => StatusLabel.Text = text);
        try
        {
            var found = await Task.Run(() => DuplicateFinder.Find(scan.DuplicateCandidates, progress, operation!.Token));
            duplicates = found; DuplicatesGrid.ItemsSource = duplicates;
            long repeated = duplicates.GroupBy(x => x.Group).Sum(g => g.First().Bytes * (g.Count() - 1));
            DuplicateSummary.Text = $"Групп: {duplicates.Select(x => x.Group).Distinct().Count():N0} · повторные копии: {Format.Size(repeated)}";
            StatusLabel.Text = "Поиск завершён. Жёсткие ссылки не считаются отдельными копиями. Файлы не изменены.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Поиск дубликатов остановлен."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка поиска"); }
        finally { EndOperation(); }
    }
    private void ExportDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (duplicates.Count == 0) { MessageBox.Show(this, "Пока нет найденных дубликатов."); return; }
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Дубликаты.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        try { File.WriteAllLines(dialog.FileName, new[] { "Группа;Байт;SHA256;Путь" }.Concat(duplicates.Select(x => $"{x.Group};{x.Bytes};{x.Hash};{Csv(x.Path)}")), new UTF8Encoding(true)); StatusLabel.Text = "Отчёт сохранён: " + dialog.FileName; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка сохранения"); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await LoadPreview();
    private async Task LoadPreview()
    {
        if (!BeginOperation()) return; StatusLabel.Text = "Проверяем временные файлы и кэши…";
        try
        {
            caches = await Task.Run(() => cleaner.Preview(operation!.Token)); CacheGrid.ItemsSource = caches; CacheFilesGrid.ItemsSource = null;
            CleanNote.Text = $"Найдено {Format.Size(caches.Sum(x => x.Bytes))}. Отметьте нужные категории. Выберите строку, чтобы просмотреть список файлов.";
            if (caches.Count > 0) CacheGrid.SelectedIndex = 0;
            StatusLabel.Text = "Предварительная проверка завершена. Ничего не удалено.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Проверка кэшей остановлена."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка проверки"); }
        finally { EndOperation(); }
    }
    private void CacheSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CacheGrid.SelectedItem is CacheGroup group) { CacheFilesGrid.ItemsSource = group.Files; CacheDescription.Text = group.Title; CacheDetails.Text = group.Description; CleanNote.Text = $"Всего найдено: {Format.Size(caches.Sum(x => x.Bytes))} · " + group.Note + " · " + group.Count + " · " + group.DisplaySize; }
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        CacheGrid.CommitEdit(DataGridEditingUnit.Cell, true); CacheGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = caches.Where(x => x.Selected).SelectMany(x => x.Files).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Отметьте галочками категории, которые хотите очистить.", "Ничего не выбрано"); return; }
        var message = $"Удалить безвозвратно {selected.Count:N0} временных файлов ({Format.Size(selected.Sum(x => x.Bytes))})?\n\n" + string.Join("\n", caches.Where(x => x.Selected).Select(x => "• " + x.Title + ": " + x.DisplaySize)) + "\n\nИзменённые, новые и занятые файлы будут пропущены. При отмене уже удалённые файлы не восстанавливаются.";
        if (MessageBox.Show(this, message, "Подтвердите очистку выбранных кэшей", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!BeginOperation()) return;
        CleanResult? result = null;
        try
        {
            result = await Task.Run(() => cleaner.Clean(selected, new Progress<string>(text => Dispatcher.BeginInvoke(() => StatusLabel.Text = text)), operation!.Token));
            CleanNote.Text = $"Удалено файлов: {result.Deleted:N0} · {Format.Size(result.Bytes)}. Пропущено: {result.Skipped:N0}. Для нового списка повторите проверку.";
            StatusLabel.Text = CleanNote.Text;
            caches = []; CacheGrid.ItemsSource = null; CacheFilesGrid.ItemsSource = null; UpdateDrive(RootBox.Text);
            // Any cached disk analysis is stale after a successful cleanup.
            if (result.Deleted > 0) ResetDiskResults();
            if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(8)), "Некоторые файлы пропущены");
        }
        catch (Exception ex) { StatusLabel.Text = "Очистка прервана. Повторите проверку перед следующей попыткой."; caches = []; CacheGrid.ItemsSource = null; MessageBox.Show(this, ex.Message, "Очистка"); }
        finally { EndOperation(); }
    }
    private static string Duration(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} мин {span.Seconds} сек" : $"{Math.Max(0, (int)span.TotalSeconds)} сек";
    private void UpdateClock()
    {
        var remaining = lastEta.HasValue ? TimeSpan.FromSeconds(Math.Max(0, (lastEta.Value - (operationClock.Elapsed - lastProgressElapsed)).TotalSeconds)) : (TimeSpan?)null;
        TimeLabel.Text = $"Прошло {Duration(operationClock.Elapsed)} · осталось " + (remaining.HasValue ? $"≈ {Duration(remaining.Value)} · всего ≈ {Duration(operationClock.Elapsed + remaining.Value)}" : "—");
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private async void AuditAppData_Click(object sender, RoutedEventArgs e)
    {
        string path = Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!.FullName;
        var previous = scan;
        await StartScan(path);
        if (scan != null && !ReferenceEquals(previous, scan) && string.Equals(scan.Root.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            PopulateAppData(scan.Root, "Только что");
            ShowPage(AppDataPage, NavAppData);
        }
    }
    private FolderNode? FindInLastScan(string path)
    {
        if (scan == null || !Format.Below(path, scan.Root.Path) && !string.Equals(path, scan.Root.Path, StringComparison.OrdinalIgnoreCase)) return null;
        var node = scan.Root;
        string relative = Path.GetRelativePath(node.Path, path);
        if (relative == ".") return node;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        { node = node.Children.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase)); if (node == null) return null; }
        return node;
    }
    private void PopulateAppData(FolderNode node, string when)
    {
        appDataEntries = AppDataEntry.From(node, cleaner.Definitions); AppDataGrid.ItemsSource = appDataEntries;
        AppDataSummary.Text = $"{Format.Size(node.Bytes)} · папок: {appDataEntries.Count:N0} · {when}";
    }
    private async void AnalyzeApp_Click(object sender, RoutedEventArgs e) { if (AppDataGrid.SelectedItem is AppDataEntry app) await StartScan(app.Path); }
    private void AnalyzeApp_DoubleClick(object sender, MouseButtonEventArgs e) => AnalyzeApp_Click(sender, e);
    private void RevealApp_Click(object sender, RoutedEventArgs e) => Reveal((AppDataGrid.SelectedItem as AppDataEntry)?.Path);
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        OperationTitle.Text = "Проверка GitHub Releases"; UpdateStatus.Text = "Проверяем публичный релиз…";
        try
        {
            availableUpdate = await updateClient.Check(operation!.Token);
            UpdateStatus.Text = availableUpdate == null ? $"Установлена актуальная версия {UpdateClient.Current}." : $"Доступна версия {availableUpdate.Version} · {Format.Size(availableUpdate.Bytes)}. Нажмите «Обновить приложение».";
            OperationTitle.Text = "Проверка обновлений завершена"; PercentLabel.Text = "100%"; WorkBar.Value = 100; RemainingLabel.Text = "Готово";
        }
        catch (OperationCanceledException) { UpdateStatus.Text = operation!.IsCancellationRequested ? "Проверка остановлена." : "GitHub не ответил за отведённое время. Попробуйте позже."; }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось проверить обновления: " + ex.Message; }
        finally { EndOperation(); }
    }
    private async void UpdateApp_Click(object sender, RoutedEventArgs e)
    {
        if (availableUpdate == null || operation != null) return;
        var release = availableUpdate;
        bool installed;
        try { installed = UpdateInstallation.IsInstalledHere(AppContext.BaseDirectory); }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось определить папку обновления: " + ex.Message; return; }
        string details = installed ? "Программа скачает и проверит обновление, закроет текущее окно и автоматически перезапустится после установки. Сохранённые настройки останутся на месте." : "Сейчас запущена portable-копия. После проверки обновления откроется обычная установка новой версии. Настройки сохранятся.";
        if (MessageBox.Show(this, $"Обновить Чисто до версии {release.Version}?\n\n" + details + "\n\nWindows может дополнительно запросить подтверждение запуска.", "Обновить приложение", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!BeginOperation()) return;
        string? file = null; InstallUpdateButton.Content = "Обновление…"; OperationTitle.Text = "Загрузка обновления"; WorkBar.IsIndeterminate = false;
        var progress = new Progress<double>(percent => { PercentLabel.Text = $"{Math.Floor(percent):N0}%"; WorkBar.Value = percent; RemainingLabel.Text = $"Осталось {100 - Math.Floor(percent):N0}%"; ProgressCount.Text = $"Обновление {release.Version} · {Format.Size(release.Bytes)}"; });
        try { file = await updateClient.Download(release, progress, operation!.Token); UpdateStatus.Text = "Обновление проверено. Применяем новую версию…"; OperationTitle.Text = "Применение обновления"; }
        catch (OperationCanceledException) { UpdateStatus.Text = "Обновление остановлено."; }
        catch (Exception ex) { UpdateStatus.Text = "Обновление не завершено: " + ex.Message; }
        finally { EndOperation(); }
        InstallUpdateButton.Content = "Обновить приложение";
        if (file != null)
        {
            try
            {
                var setup = Process.Start(UpdateInstallation.Plan(file, AppContext.BaseDirectory, installed));
                if (setup == null) throw new IOException("Windows не запустила обновление.");
                Close();
            }
            catch (Exception ex) { UpdateStatus.Text = "Обновление не запущено: " + ex.Message; }
        }
    }
}

internal static class ExplorerSelection
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr item);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] children, uint flags);
    public static void Show(string path)
    {
        string full = Path.GetFullPath(path); string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) ?? full;
        IntPtr item = IntPtr.Zero, folder = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(full, IntPtr.Zero, out item, 0, out _));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(parent, IntPtr.Zero, out folder, 0, out _));
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, 1, [ILFindLastID(item)], 0));
        }
        catch
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(parent); Process.Start(start);
        }
        finally { if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item); if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder); }
    }
}
