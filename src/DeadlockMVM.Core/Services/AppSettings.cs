using System.Text.Json;
using DeadlockMVM.Core.Contracts;
using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native.InProcess;
namespace DeadlockMVM.Core.Services;

/// <summary>JSON-backed settings store for user configurable options.</summary>
public sealed class AppSettings : IAppSettings
{
    private sealed class SettingsDocument
    {
        public string DeadlockPath { get; set; } = string.Empty;

        public string ExtraLaunchArguments { get; set; } = string.Empty;

        public string SelectedReplayPath { get; set; } = string.Empty;
        public string SelectedCampathPath { get; set; } = string.Empty;
        public bool RestoreLastWorkspace { get; set; }

        public int VConsolePort { get; set; } = DeadlockConstants.DefaultVConsolePort;

        public string SmvmMenuHotkey { get; set; } = "Tab";
        public string SmvmAddHotkey { get; set; } = "Mouse3";
        public string SmvmDeleteHotkey { get; set; } = "L";
        public string SmvmCleanViewHotkey { get; set; } = "F10";
        public string SmvmRestoreUiHotkey { get; set; } = "F9";
        public bool SmvmCameraInputTakeover { get; set; } = true;
        public string SmvmForwardHotkey { get; set; } = "W";
        public string SmvmBackHotkey { get; set; } = "S";
        public string SmvmLeftHotkey { get; set; } = "A";
        public string SmvmRightHotkey { get; set; } = "D";
        public string SmvmUpHotkey { get; set; } = "Space";
        public string SmvmDownHotkey { get; set; } = "VK11";
        public string SmvmFastHotkey { get; set; } = "VK10";
        public string SmvmPrecisionHotkey { get; set; } = "VK12";
        public string SmvmRollLeftHotkey { get; set; } = "Q";
        public string SmvmRollRightHotkey { get; set; } = "E";
        public string SmvmRollResetHotkey { get; set; } = "R";
        public string SmvmPlayStartHotkey { get; set; } = string.Empty;
        public string SmvmPlayCurrentHotkey { get; set; } = string.Empty;
        public string SmvmStopHotkey { get; set; } = string.Empty;
        public string SmvmUndoHotkey { get; set; } = "Ctrl+Z";
        public string SmvmRedoHotkey { get; set; } = "Ctrl+Y";
        public string SmvmShowPathHotkey { get; set; } = string.Empty;
        public string SmvmShowCamerasHotkey { get; set; } = string.Empty;
        public double SmvmMovementSpeed { get; set; } = 600;
        public double SmvmMovementBoost { get; set; } = 4;
        public double SmvmMovementPrecision { get; set; } = 0.2;
        public double SmvmMouseSensitivity { get; set; } = 0.08;
        public double SmvmMouseSmoothing { get; set; }
        public bool SmvmMouseInvertY { get; set; }
        public double SmvmFovWheelStep { get; set; } = 1.0;
        public bool SmvmFovWheelInverted { get; set; }
        public bool SmvmShowMinimalPill { get; set; } = true;
        public bool SmvmShowToolbar { get; set; }
        public bool SmvmShowPath { get; set; } = true;
        public bool SmvmShowCameras { get; set; } = true;
        public bool SmvmShowLabels { get; set; } = true;
        public bool SmvmNotificationsEnabled { get; set; } = true;
        public double SmvmUiScale { get; set; } = 1;
        public double SmvmOpacity { get; set; } = 0.94;
        public SmvmMenuAnchor SmvmMenuAnchor { get; set; } = SmvmMenuAnchor.Left;
        public SmvmNotificationAnchor SmvmNotificationAnchor { get; set; } = SmvmNotificationAnchor.TopRight;
        public double SmvmPathLabelScale { get; set; } = 1;
        public bool SmvmHidePathWhilePlaying { get; set; } = true;
        public DeadlockUiMode SmvmDeadlockUiMode { get; set; } = DeadlockUiMode.DeadlockUi;
        public double SmvmReplayBarScale { get; set; } = 1;
        public double SmvmReplayBarOpacity { get; set; } = 0.92;
        public SmvmReplayBarAnchor SmvmReplayBarAnchor { get; set; } = SmvmReplayBarAnchor.Bottom;
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

    public string SelectedCampathPath
    {
        get => _document.SelectedCampathPath;
        set => _document.SelectedCampathPath = value ?? string.Empty;
    }

    public bool RestoreLastWorkspace
    {
        get => _document.RestoreLastWorkspace;
        set => _document.RestoreLastWorkspace = value;
    }

    public int VConsolePort
    {
        get => _document.VConsolePort;
        set => _document.VConsolePort = value > 0 ? value : DeadlockConstants.DefaultVConsolePort;
    }

    public string SmvmMenuHotkey
    {
        get => _document.SmvmMenuHotkey;
        set => _document.SmvmMenuHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmAddHotkey
    {
        get => _document.SmvmAddHotkey;
        set => _document.SmvmAddHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmDeleteHotkey
    {
        get => _document.SmvmDeleteHotkey;
        set => _document.SmvmDeleteHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmCleanViewHotkey
    {
        get => _document.SmvmCleanViewHotkey;
        set => _document.SmvmCleanViewHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmRestoreUiHotkey
    {
        get => _document.SmvmRestoreUiHotkey;
        set => _document.SmvmRestoreUiHotkey = NormalizeSlotBinding(122, value, "F9");
    }

    public bool SmvmCameraInputTakeover
    {
        get => _document.SmvmCameraInputTakeover;
        set => _document.SmvmCameraInputTakeover = value;
    }

    public string SmvmForwardHotkey
    {
        get => _document.SmvmForwardHotkey;
        set => _document.SmvmForwardHotkey = NormalizeSlotBinding(100, value, string.Empty);
    }

    public string SmvmBackHotkey
    {
        get => _document.SmvmBackHotkey;
        set => _document.SmvmBackHotkey = NormalizeSlotBinding(101, value, string.Empty);
    }

    public string SmvmLeftHotkey
    {
        get => _document.SmvmLeftHotkey;
        set => _document.SmvmLeftHotkey = NormalizeSlotBinding(102, value, string.Empty);
    }

    public string SmvmRightHotkey
    {
        get => _document.SmvmRightHotkey;
        set => _document.SmvmRightHotkey = NormalizeSlotBinding(103, value, string.Empty);
    }

    public string SmvmUpHotkey
    {
        get => _document.SmvmUpHotkey;
        set => _document.SmvmUpHotkey = NormalizeSlotBinding(104, value, string.Empty);
    }

    public string SmvmDownHotkey
    {
        get => _document.SmvmDownHotkey;
        set => _document.SmvmDownHotkey = NormalizeSlotBinding(105, value, string.Empty);
    }

    public string SmvmFastHotkey
    {
        get => _document.SmvmFastHotkey;
        set => _document.SmvmFastHotkey = NormalizeSlotBinding(106, value, string.Empty);
    }

    public string SmvmPrecisionHotkey
    {
        get => _document.SmvmPrecisionHotkey;
        set => _document.SmvmPrecisionHotkey = NormalizeSlotBinding(107, value, string.Empty);
    }

    public string SmvmRollLeftHotkey
    {
        get => _document.SmvmRollLeftHotkey;
        set => _document.SmvmRollLeftHotkey = NormalizeSlotBinding(108, value, string.Empty);
    }

    public string SmvmRollRightHotkey
    {
        get => _document.SmvmRollRightHotkey;
        set => _document.SmvmRollRightHotkey = NormalizeSlotBinding(109, value, string.Empty);
    }

    public string SmvmRollResetHotkey
    {
        get => _document.SmvmRollResetHotkey;
        set => _document.SmvmRollResetHotkey = NormalizeSlotBinding(110, value, string.Empty);
    }

    public string SmvmPlayStartHotkey
    {
        get => _document.SmvmPlayStartHotkey;
        set => _document.SmvmPlayStartHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmPlayCurrentHotkey
    {
        get => _document.SmvmPlayCurrentHotkey;
        set => _document.SmvmPlayCurrentHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmStopHotkey
    {
        get => _document.SmvmStopHotkey;
        set => _document.SmvmStopHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmUndoHotkey
    {
        get => _document.SmvmUndoHotkey;
        set => _document.SmvmUndoHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmRedoHotkey
    {
        get => _document.SmvmRedoHotkey;
        set => _document.SmvmRedoHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmShowPathHotkey
    {
        get => _document.SmvmShowPathHotkey;
        set => _document.SmvmShowPathHotkey = value?.Trim() ?? string.Empty;
    }

    public string SmvmShowCamerasHotkey
    {
        get => _document.SmvmShowCamerasHotkey;
        set => _document.SmvmShowCamerasHotkey = value?.Trim() ?? string.Empty;
    }

    public double SmvmMovementSpeed
    {
        get => ClampFinite(_document.SmvmMovementSpeed, 1, 10_000, 600);
        set => _document.SmvmMovementSpeed = ClampFinite(value, 1, 10_000, 600);
    }

    public double SmvmMovementBoost
    {
        get => ClampFinite(_document.SmvmMovementBoost, 1, 20, 4);
        set => _document.SmvmMovementBoost = ClampFinite(value, 1, 20, 4);
    }

    public double SmvmMovementPrecision
    {
        get => ClampFinite(_document.SmvmMovementPrecision, 0.01, 1, 0.2);
        set => _document.SmvmMovementPrecision = ClampFinite(value, 0.01, 1, 0.2);
    }

    public double SmvmMouseSensitivity
    {
        get => ClampFinite(_document.SmvmMouseSensitivity, 0.001, 5, 0.08);
        set => _document.SmvmMouseSensitivity = ClampFinite(value, 0.001, 5, 0.08);
    }

    public double SmvmMouseSmoothing
    {
        get => ClampFinite(_document.SmvmMouseSmoothing, 0, 0.95, 0);
        set => _document.SmvmMouseSmoothing = ClampFinite(value, 0, 0.95, 0);
    }

    public bool SmvmMouseInvertY
    {
        get => _document.SmvmMouseInvertY;
        set => _document.SmvmMouseInvertY = value;
    }

    public double SmvmFovWheelStep
    {
        get => ClampFinite(_document.SmvmFovWheelStep, 0.05, 30, 1);
        set => _document.SmvmFovWheelStep = ClampFinite(value, 0.05, 30, 1);
    }

    public bool SmvmFovWheelInverted
    {
        get => _document.SmvmFovWheelInverted;
        set => _document.SmvmFovWheelInverted = value;
    }

    public bool SmvmShowMinimalPill
    {
        get => _document.SmvmShowMinimalPill;
        set => _document.SmvmShowMinimalPill = value;
    }

    public bool SmvmShowToolbar
    {
        get => _document.SmvmShowToolbar;
        set => _document.SmvmShowToolbar = value;
    }

    public bool SmvmShowPath
    {
        get => _document.SmvmShowPath;
        set => _document.SmvmShowPath = value;
    }

    public bool SmvmShowCameras
    {
        get => _document.SmvmShowCameras;
        set => _document.SmvmShowCameras = value;
    }

    public bool SmvmShowLabels
    {
        get => _document.SmvmShowLabels;
        set => _document.SmvmShowLabels = value;
    }

    public bool SmvmNotificationsEnabled
    {
        get => _document.SmvmNotificationsEnabled;
        set => _document.SmvmNotificationsEnabled = value;
    }

    public double SmvmUiScale
    {
        get => ClampFinite(_document.SmvmUiScale, 0.75, 1.5, 1);
        set => _document.SmvmUiScale = ClampFinite(value, 0.75, 1.5, 1);
    }

    public double SmvmOpacity
    {
        get => ClampFinite(_document.SmvmOpacity, 0.65, 1, 0.94);
        set => _document.SmvmOpacity = ClampFinite(value, 0.65, 1, 0.94);
    }

    public SmvmMenuAnchor SmvmMenuAnchor
    {
        get => Enum.IsDefined(_document.SmvmMenuAnchor) ? _document.SmvmMenuAnchor : SmvmMenuAnchor.Left;
        set => _document.SmvmMenuAnchor = Enum.IsDefined(value) ? value : SmvmMenuAnchor.Left;
    }

    public SmvmNotificationAnchor SmvmNotificationAnchor
    {
        get => Enum.IsDefined(_document.SmvmNotificationAnchor)
            ? _document.SmvmNotificationAnchor
            : SmvmNotificationAnchor.TopRight;
        set => _document.SmvmNotificationAnchor = Enum.IsDefined(value)
            ? value
            : SmvmNotificationAnchor.TopRight;
    }

    public double SmvmPathLabelScale
    {
        get => ClampFinite(_document.SmvmPathLabelScale, 0.5, 2, 1);
        set => _document.SmvmPathLabelScale = ClampFinite(value, 0.5, 2, 1);
    }

    public bool SmvmHidePathWhilePlaying
    {
        get => _document.SmvmHidePathWhilePlaying;
        set => _document.SmvmHidePathWhilePlaying = value;
    }

    public DeadlockUiMode SmvmDeadlockUiMode
    {
        get => _document.SmvmDeadlockUiMode is DeadlockUiMode.DeadlockUi or DeadlockUiMode.SmvmReplayUi
            ? _document.SmvmDeadlockUiMode
            : DeadlockUiMode.DeadlockUi;
        set => _document.SmvmDeadlockUiMode = value is DeadlockUiMode.DeadlockUi or DeadlockUiMode.SmvmReplayUi
            ? value
            : DeadlockUiMode.DeadlockUi;
    }

    public double SmvmReplayBarScale
    {
        get => ClampFinite(_document.SmvmReplayBarScale, 0.75, 2, 1);
        set => _document.SmvmReplayBarScale = ClampFinite(value, 0.75, 2, 1);
    }

    public double SmvmReplayBarOpacity
    {
        get => ClampFinite(_document.SmvmReplayBarOpacity, 0.35, 1, 0.92);
        set => _document.SmvmReplayBarOpacity = ClampFinite(value, 0.35, 1, 0.92);
    }

    public SmvmReplayBarAnchor SmvmReplayBarAnchor
    {
        get => Enum.IsDefined(_document.SmvmReplayBarAnchor)
            ? _document.SmvmReplayBarAnchor
            : SmvmReplayBarAnchor.Bottom;
        set => _document.SmvmReplayBarAnchor = Enum.IsDefined(value)
            ? value
            : SmvmReplayBarAnchor.Bottom;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var json = File.ReadAllText(_filePath);
            _document = JsonSerializer.Deserialize<SettingsDocument>(json) ?? new SettingsDocument();
            NormalizeManualBindings();
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

    private static double ClampFinite(double value, double minimum, double maximum, double defaultValue) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : defaultValue;

    private void NormalizeManualBindings()
    {
        _document.SmvmForwardHotkey = NormalizeSlotBinding(100, _document.SmvmForwardHotkey, "W");
        _document.SmvmBackHotkey = NormalizeSlotBinding(101, _document.SmvmBackHotkey, "S");
        _document.SmvmLeftHotkey = NormalizeSlotBinding(102, _document.SmvmLeftHotkey, "A");
        _document.SmvmRightHotkey = NormalizeSlotBinding(103, _document.SmvmRightHotkey, "D");
        _document.SmvmUpHotkey = NormalizeSlotBinding(104, _document.SmvmUpHotkey, "Space");
        _document.SmvmDownHotkey = NormalizeSlotBinding(105, _document.SmvmDownHotkey, "VK11");
        _document.SmvmFastHotkey = NormalizeSlotBinding(106, _document.SmvmFastHotkey, "VK10");
        _document.SmvmPrecisionHotkey = NormalizeSlotBinding(107, _document.SmvmPrecisionHotkey, "VK12");
        _document.SmvmRollLeftHotkey = NormalizeSlotBinding(108, _document.SmvmRollLeftHotkey, "Q");
        _document.SmvmRollRightHotkey = NormalizeSlotBinding(109, _document.SmvmRollRightHotkey, "E");
        _document.SmvmRollResetHotkey = NormalizeSlotBinding(110, _document.SmvmRollResetHotkey, "R");
        _document.SmvmRestoreUiHotkey = NormalizeSlotBinding(122, _document.SmvmRestoreUiHotkey, "F9");
    }

    private static string NormalizeSlotBinding(int slot, string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return InputBinding.TryParse(value, out var binding) &&
               SmvmInputCode.IsBindingAllowedForSlot(slot, binding)
            ? binding.ToString()
            : fallback;
    }

}
