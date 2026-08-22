namespace DeadlockMVM.Core.Contracts;

/// <summary>Persists user-configurable launcher settings.</summary>
public interface IAppSettings
{
    /// <summary>Validated Deadlock executable path selected by the user.</summary>
    string DeadlockPath { get; set; }

    /// <summary>Raw additional launch arguments entered by the user.</summary>
    string ExtraLaunchArguments { get; set; }

    /// <summary>Most recently selected replay path.</summary>
    string SelectedReplayPath { get; set; }

    /// <summary>TCP port of Deadlock's VConsole2 command channel.</summary>
    int VConsolePort { get; set; }

    /// <summary>Global show/hide hotkey for the Director window (e.g. "Ctrl+Alt+M").</summary>
    string DirectorHotkey { get; set; }

    /// <summary>Foreground-scoped Campath Add binding (e.g. "Mouse3" or "Ctrl+F6").</summary>
    string CampathAddHotkey { get; set; }

    void Load();

    void Save();
}
