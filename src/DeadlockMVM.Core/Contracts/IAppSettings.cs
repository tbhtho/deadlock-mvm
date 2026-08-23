namespace DeadlockMVM.Core.Contracts;

using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;

/// <summary>Persists user-configurable launcher settings.</summary>
public interface IAppSettings
{
    /// <summary>Validated Deadlock executable path selected by the user.</summary>
    string DeadlockPath { get; set; }

    /// <summary>Raw additional launch arguments entered by the user.</summary>
    string ExtraLaunchArguments { get; set; }

    /// <summary>Most recently selected replay path.</summary>
    string SelectedReplayPath { get; set; }
    string SelectedCampathPath { get; set; }

    /// <summary>TCP port of Deadlock's VConsole2 command channel.</summary>
    int VConsolePort { get; set; }

    /// <summary>Global show/hide hotkey for the Director window (e.g. "Ctrl+Alt+M").</summary>
    string DirectorHotkey { get; set; }

    /// <summary>Foreground-scoped Campath Add binding (e.g. "Mouse3" or "Ctrl+F6").</summary>
    string CampathAddHotkey { get; set; }

    SmvmInterfaceMode InterfaceMode { get; set; }
    string SmvmMenuHotkey { get; set; }
    string SmvmAddHotkey { get; set; }
    string SmvmDeleteHotkey { get; set; }
    string SmvmCleanViewHotkey { get; set; }
    bool SmvmCameraInputTakeover { get; set; }
    string SmvmForwardHotkey { get; set; }
    string SmvmBackHotkey { get; set; }
    string SmvmLeftHotkey { get; set; }
    string SmvmRightHotkey { get; set; }
    string SmvmUpHotkey { get; set; }
    string SmvmDownHotkey { get; set; }
    string SmvmFastHotkey { get; set; }
    string SmvmPrecisionHotkey { get; set; }
    string SmvmRollLeftHotkey { get; set; }
    string SmvmRollRightHotkey { get; set; }
    string SmvmRollResetHotkey { get; set; }
    string SmvmPlayStartHotkey { get; set; }
    string SmvmPlayCurrentHotkey { get; set; }
    string SmvmStopHotkey { get; set; }
    string SmvmUndoHotkey { get; set; }
    string SmvmRedoHotkey { get; set; }
    string SmvmShowPathHotkey { get; set; }
    string SmvmShowCamerasHotkey { get; set; }
    double SmvmMovementSpeed { get; set; }
    double SmvmMovementBoost { get; set; }
    double SmvmMovementPrecision { get; set; }
    double SmvmMouseSensitivity { get; set; }
    double SmvmMouseSmoothing { get; set; }
    bool SmvmMouseInvertY { get; set; }
    double SmvmFovWheelStep { get; set; }
    bool SmvmFovWheelInverted { get; set; }
    bool SmvmShowMinimalPill { get; set; }
    bool SmvmShowToolbar { get; set; }
    bool SmvmShowPath { get; set; }
    bool SmvmShowCameras { get; set; }
    bool SmvmShowLabels { get; set; }
    bool SmvmNotificationsEnabled { get; set; }
    double SmvmUiScale { get; set; }
    double SmvmOpacity { get; set; }
    SmvmMenuAnchor SmvmMenuAnchor { get; set; }
    SmvmNotificationAnchor SmvmNotificationAnchor { get; set; }
    double SmvmPathLabelScale { get; set; }
    bool SmvmHidePathWhilePlaying { get; set; }

    void Load();

    void Save();
}
