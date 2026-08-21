using System.Text.Json;
using DeadlockMVM.Core.Contracts;
namespace DeadlockMVM.Core.Services;

/// <summary>JSON-backed settings store for user configurable options.</summary>
public sealed class AppSettings : IAppSettings
{
    private sealed class SettingsDocument
    {
        public string DeadlockPath { get; set; } = string.Empty;

        public string ExtraLaunchArguments { get; set; } = string.Empty;

        public string SelectedReplayPath { get; set; } = string.Empty;

        public int VConsolePort { get; set; } = DeadlockConstants.DefaultVConsolePort;

        public string DirectorHotkey { get; set; } = "Ctrl+Alt+M";
    }

    private readonly string _filePath;
    private SettingsDocument _document = new();

    public AppSettings(string? filePath = null)
        => _filePath = filePath ?? AppPaths.SettingsFile;

    public string ExtraLaunchArguments
    {
        get => _document.ExtraLaunchArguments;
        set => _document.ExtraLaunchArguments = value ?? string.Empty;
    }

    public string DeadlockPath
    {
        get => _document.DeadlockPath;
        set => _document.DeadlockPath = value ?? string.Empty;
    }

    public string SelectedReplayPath
    {
        get => _document.SelectedReplayPath;
        set => _document.SelectedReplayPath = value ?? string.Empty;
    }

    public int VConsolePort
    {
        get => _document.VConsolePort;
        set => _document.VConsolePort = value > 0 ? value : DeadlockConstants.DefaultVConsolePort;
    }

    public string DirectorHotkey
    {
        get => _document.DirectorHotkey;
        set => _document.DirectorHotkey = string.IsNullOrWhiteSpace(value) ? "Ctrl+Alt+M" : value;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            _document = JsonSerializer.Deserialize<SettingsDocument>(json) ?? new SettingsDocument();
        }
        catch
        {
            _document = new SettingsDocument();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_document, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Intentionally ignored: settings persistence must not crash the launcher.
        }
    }
}
