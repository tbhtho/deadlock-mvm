using System.Globalization;
using System.Text.RegularExpressions;
using DeadlockMVM.Core.Models;

namespace DeadlockMVM.Core.Services;

/// <summary>Parses camera and FOV readbacks from Source 2 console output.</summary>
public static partial class CameraStateParser
{
    [GeneratedRegex(@"setpos\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s*;\s*setang\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GetPosLine();

    [GeneratedRegex(@"^spec_goto\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)\s+([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SpecPosLine();

    [GeneratedRegex(@"^fov_desired\s*=\s*([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseFovLine();

    [GeneratedRegex(@"^citadel_camera_hero_fov\s*=\s*([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex HeroFovLine();

    public static CameraState Merge(IEnumerable<string> lines)
    {
        CameraTransform? transform = null;
        double? baseFov = null;
        double? heroFov = null;

        foreach (var line in lines)
        {
            var getPos = GetPosLine().Match(line);
            if (getPos.Success)
            {
                transform = new CameraTransform(
                    Number(getPos, 1), Number(getPos, 2), Number(getPos, 3),
                    Number(getPos, 4), Number(getPos, 5), Number(getPos, 6));
                continue;
            }

            var specPos = SpecPosLine().Match(line);
            if (specPos.Success && transform is null)
            {
                transform = new CameraTransform(
                    Number(specPos, 1), Number(specPos, 2), Number(specPos, 3),
                    Number(specPos, 4), Number(specPos, 5), 0);
                continue;
            }

            var baseFovMatch = BaseFovLine().Match(line);
            if (baseFovMatch.Success)
            {
                baseFov = Number(baseFovMatch, 1);
                continue;
            }

            var heroFovMatch = HeroFovLine().Match(line);
            if (heroFovMatch.Success)
                heroFov = Number(heroFovMatch, 1);
        }

        return new CameraState
        {
            ActiveTransform = transform,
            BaseFov = baseFov,
            HeroFov = heroFov,
            ActiveFov = null,
        };
    }

    private static double Number(Match match, int group)
        => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
}
