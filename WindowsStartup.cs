using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace Chisto;
public sealed class WindowsStartup
{
    private readonly string keyPath;
    public WindowsStartup() : this(@"Software\Microsoft\Windows\CurrentVersion\Run") { }
    internal WindowsStartup(string path) => keyPath = path;
    public string? Command()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue("Chisto") as string;
    }
    public bool Enabled => !string.IsNullOrWhiteSpace(Command());
    internal static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\r') || executable.Contains('\n') || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Неверный путь программы для автозапуска.");
        return "\"" + Path.GetFullPath(executable) + "\" --startup";
    }
    public void SetEnabled(bool enabled, string executable)
    {
        if (enabled && !File.Exists(executable)) throw new FileNotFoundException("Исполняемый файл программы не найден.", executable);
        SetCommand(enabled ? BuildCommand(executable) : null);
    }
    internal void SetCommand(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key == null) throw new IOException("Автозапуск недоступен для текущего пользователя.");
        if (command == null) key.DeleteValue("Chisto", false); else key.SetValue("Chisto", command, RegistryValueKind.String);
    }
}
internal static class UpdateInstallation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F59428C8-9792-4774-91DE-97E28CBB5472}_is1";
    public static bool IsInstalledHere(string directory)
    {
        foreach (var view in new[] {RegistryView.Registry64,RegistryView.Registry32})
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view); using var key = hive.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string path && SameDirectory(path, directory)) return true;
        }
        return false;
    }
    private static bool SameDirectory(string a, string b) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    internal static ProcessStartInfo Plan(string setup, string currentDirectory, bool installed)
    {
        if (!Path.IsPathFullyQualified(setup) || !Path.IsPathFullyQualified(currentDirectory)) throw new ArgumentException("Нужны полные пути обновления.");
        var plan = new ProcessStartInfo(setup) { UseShellExecute = true };
        if (installed)
        {
            foreach (string argument in new[] {"/SILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-","/CHISTOUPDATE=1","/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory))}) plan.ArgumentList.Add(argument);
        }
        // A portable copy gets the normal install dialog rather than being
        // silently converted into an installed application in its ZIP folder.
        return plan;
    }
}
