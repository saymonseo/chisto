using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Chisto;
public partial class MainWindow
{
    private UserPreferences preferences = new PreferencesStore().Load();
    private void SettingsPage_Click(object sender, RoutedEventArgs e) { LoadSettings(); ShowPage(SettingsPage, NavSettings); }
    private void LoadSettings()
    {
        preferences = new PreferencesStore().Load();
        DefaultPathBox.Text = preferences.DefaultScanPath; AutoScanCheck.IsChecked = preferences.AutoScanOnLaunch; MinimizedCheck.IsChecked = preferences.StartMinimized;
        try { StartupCheck.IsChecked = new WindowsStartup().Enabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { SettingsStatus.Text = "Автозапуск недоступен: " + ex.Message; StartupCheck.IsEnabled = false; }
        CacheAgePicker.SelectedItem = CacheAgePicker.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == preferences.CacheMinAgeHours.ToString());
        bool wasChanging = changingTheme; changingTheme = true;
        ThemeToggle.IsChecked = SettingsThemeToggle.IsChecked = ThemeManager.IsLight; changingTheme = wasChanging;
    }
    private void ChooseDefaultPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Начальная папка анализа", Multiselect = false };
        if (dialog.ShowDialog(this) == true) DefaultPathBox.Text = dialog.FolderName;
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (operation != null) return;
        try
        {
            string path = DefaultPathBox.Text.Trim();
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new IOException("Выберите существующий диск или папку полным путём.");
            var next = new UserPreferences(ThemeManager.IsLight ? "light" : "dark", Path.GetFullPath(path), AutoScanCheck.IsChecked == true, MinimizedCheck.IsChecked == true,
                int.Parse((string)((ComboBoxItem)CacheAgePicker.SelectedItem).Tag)).Normalized();
            var startup = new WindowsStartup();
            bool changeStartup = StartupCheck.IsEnabled;
            string? oldCommand = changeStartup ? startup.Command() : null;
            if (changeStartup) startup.SetEnabled(StartupCheck.IsChecked == true, Path.Combine(AppContext.BaseDirectory, "Chisto.exe"));
            try { new PreferencesStore().Save(next); }
            catch { if (changeStartup) startup.SetCommand(oldCommand); throw; }
            if (next.CacheMinAgeHours != preferences.CacheMinAgeHours)
            {
                cleaner = CacheCleaner.ForThisUser(next.CacheMinAgeHours); caches = []; CacheGrid.ItemsSource = null; CacheFilesGrid.ItemsSource = null; CleanButton.IsEnabled = false;
                CleanNote.Text = "Возраст файлов изменён. Повторите предварительную проверку.";
            }
            preferences = next; SettingsStatus.Text = "Настройки сохранены. Параметры запуска применятся при следующем открытии программы.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { SettingsStatus.Text = "Не удалось сохранить: " + ex.Message; }
    }
    internal void ShowSettingsPreview() { LoadSettings(); ShowPage(SettingsPage, NavSettings); }
    internal void ShowUpdatesPreview() => ShowPage(UpdatesPage, NavUpdates);
}
