using System.Globalization;

namespace DeadlockMVM.Core.Services;

/// <summary>Pure command generation for the supported camera surface.</summary>
public static class CameraCommands
{
    public const string ReadActiveTransform = "getpos";
    public const string ReadSpectatorTransform = "spec_pos";
    public const string ReadBaseFov = "fov_desired";
    public const string ReadHeroFov = "citadel_camera_hero_fov";
    public const string ReadCameraHeight = "citadel_camera_height";
    public const string FreeRoam = "spec_mode 6";
    public const string InEye = "spec_in_eye";
    public const string Chase = "spec_chase";
    public const string NextPlayer = "spec_next";
    public const string PrevPlayer = "spec_prev";

    public static string SelectPlayer(string playerOrSlot)
    {
        if (string.IsNullOrWhiteSpace(playerOrSlot))
            throw new ArgumentException("A player name or slot is required.", nameof(playerOrSlot));

        var value = playerOrSlot.Trim();
        return int.TryParse(value, out _)
            ? $"spec_player {value}"
            : $"spec_player \"{value.Replace("\"", string.Empty)}\"";
    }

    /// <summary>
    /// Moves the spectator roam target. Deadlock currently preserves pitch/yaw
    /// for spec_goto, so rotation is intentionally not accepted here. The engine
    /// lands the camera citadel_camera_height units above the requested point.
    /// </summary>
    public static string MoveRoamTarget(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be finite numbers.");

        return string.Create(CultureInfo.InvariantCulture, $"spec_goto {x:0.######} {y:0.######} {z:0.######}");
    }

    public static string SetBaseFov(double fov)
    {
        ValidateFov(fov);
        return string.Create(CultureInfo.InvariantCulture, $"fov_desired {fov:0.##}");
    }

    public static void ValidateFov(double fov)
    {
        if (!double.IsFinite(fov) || fov is < 1 or > 179)
            throw new ArgumentOutOfRangeException(nameof(fov), fov, "FOV must be between 1 and 179 degrees.");
    }
}
