using System.Text.Json;

namespace GeminiBatch.WinForms;

/// <summary>
/// Operator choices remembered between launches (%LOCALAPPDATA%\GeminiBatch\ui-settings.json). Best effort: a missing
/// or unreadable file means "use the appsettings defaults".
/// </summary>
internal sealed class UiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiBatch", "ui-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The output folder picked with Browse…; null = Batch:OutputFolder.</summary>
    public string? OutputFolder { get; set; }

    public static UiSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new UiSettings()
                : new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    /// <summary>Returns false (nothing thrown) when the file could not be written.</summary>
    public bool TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
