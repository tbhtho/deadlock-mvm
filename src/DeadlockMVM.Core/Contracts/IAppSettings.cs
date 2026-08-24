namespace DeadlockMVM.Core.Contracts;

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

    /// <summary>
    /// Opt-in: restore the previously saved Campath when a matching replay starts.
    /// Default OFF — a normal startup always opens a clean, empty workspace.
    /// </summary>
    bool RestoreLastWorkspace { get; set; }

    /// <summary>TCP port of Deadlock's VConsole2 command channel.</summary>
    int VConsolePort { get; set; }

    string SmvmMenuHotkey { get; set; }
    string SmvmAddHotkey { get; set; }
    string SmvmDeleteHotkey { get; set; }
    string SmvmCleanViewHotkey { get; set; }
    string SmvmRestoreUiHotkey { get; set; }
    string SmvmCycleUiHotkey { get; set; }
    string SmvmToggleFreeCameraHotkey { get; set; }
    string SmvmReplayPauseHotkey { get; set; }
    string SmvmStepBackHotkey { get; set; }
    string SmvmStepForwardHotkey { get; set; }
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
    string SmvmShowLabelsHotkey { get; set; }
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
    DeadlockUiMode SmvmDeadlockUiMode { get; set; }
    double SmvmReplayBarScale { get; set; }
    double SmvmReplayBarOpacity { get; set; }
    SmvmReplayBarAnchor SmvmReplayBarAnchor { get; set; }

    void Load();

    void Save();
}
