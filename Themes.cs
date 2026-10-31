using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Chisto;
public sealed record UserPreferences(string Theme = "dark");
public sealed class PreferencesStore
{
    private readonly string path;
    public PreferencesStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chisto", "preferences.json")) { }
    internal PreferencesStore(string file) => path = Path.GetFullPath(file);
    public UserPreferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return new UserPreferences(value?.Theme == "light" ? "light" : "dark");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new UserPreferences(); }
    }
    public void Save(UserPreferences preferences)
    {
        string directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        if (!Format.NoLinks(directory)) throw new IOException("Папка настроек перенаправлена ссылкой.");
        string temporary = Path.Combine(directory, ".preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new UserPreferences(preferences.Theme == "light" ? "light" : "dark")));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public static class ThemeManager
{
    public static bool IsLight { get; private set; }
    // Each key is the original dark color. All WPF brushes reference these
    // resources dynamically, including styles, templates, and open tables.
    internal static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>
    {
        ["081426"]="FFFFFF", ["0C111B"]="F4F6FA", ["0D1420"]="F1F4F9", ["0E1521"]="EAF0F7",
        ["101724"]="EDF1F7", ["111824"]="FFFFFF", ["121A28"]="FFFFFF", ["132A28"]="E5F4EC",
        ["141B28"]="F5F7FB", ["151F30"]="E0E8F3", ["182131"]="EAF0F7", ["222D40"]="D6DFEB",
        ["223D62"]="DCEAFF", ["243249"]="DFE6F0", ["263247"]="D9E1ED", ["273245"]="DEE5EF",
        ["283347"]="CFD9E7", ["304767"]="BBCDE6", ["34435C"]="C4D1E3", ["354C71"]="B9CBE4",
        ["425572"]="A0B1C9", ["526D96"]="8AA2C3", ["7588A6"]="62738D", ["7E98BE"]="4D6C99",
        ["83B5FF"]="2F6FDA", ["86D5BF"]="257C61", ["8CDCC3"]="187D65", ["91A0B8"]="596C87",
        ["B9C7DE"]="455B78", ["E8EEF9"]="17263F"
    };
    public static void Apply(string theme)
    {
        IsLight = theme == "light";
        var resources = Application.Current.Resources;
        foreach (var pair in LightColors) resources["Theme_" + pair.Key] = Brush(IsLight ? pair.Value : pair.Key);
        resources["Ink"] = Get("E8EEF9"); resources["Muted"] = Get("91A0B8"); resources["Line"] = Get("283347"); resources["Lime"] = Get("83B5FF");
        resources["NavSelected"] = Brush(IsLight ? "DFEAFE" : "1E314D");
        resources["NavText"] = Brush(IsLight ? "455B78" : "9BAECB");
    }
    public static SolidColorBrush Get(string darkColor) => (SolidColorBrush)Application.Current.Resources["Theme_" + darkColor];
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString("#" + color));
}
