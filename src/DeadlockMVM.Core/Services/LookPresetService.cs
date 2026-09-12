using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeadlockMVM.Core.Models;
namespace DeadlockMVM.Core.Services;

public static class LookPresetService
{
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    public static readonly string[] FactoryNames = ["Neutral", "Clean Cinematic", "Warm Film", "Cool Night", "Moody Contrast", "Soft Dream"];
    public static LookSettings Factory(int index) => (index switch {
        0 => new LookSettings(),
        1 => new LookSettings { Enabled = true, Contrast = 1.06f, Saturation = 1.03f, Vibrance = .08f, Sharpen = .12f },
        2 => new LookSettings { Enabled = true, Temperature = .16f, Contrast = 1.04f, Saturation = .94f, GainR = 1.04f, LiftB = .01f, GrainStrength = .012f },
        3 => new LookSettings { Enabled = true, Temperature = -.18f, Exposure = -.15f, Shadows = .06f, Saturation = .9f },
        4 => new LookSettings { Enabled = true, Contrast = 1.18f, Shadows = .05f, Saturation = .9f, Vignette = .18f },
        5 => new LookSettings { Enabled = true, Contrast = .96f, BloomIntensity = .25f, BloomRadius = 2, BloomThreshold = .72f },
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    }) with { PresetName = FactoryNames[index] };
    public static LookSettings Read(string path) {
        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException("Preset exceeds 4 MiB.");
        var look = JsonSerializer.Deserialize<LookSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty preset.");
        if (!look.IsValid) throw new InvalidDataException("Unsupported schema or invalid Reshade parameters.");
        return look with { Modified = false, LutHash = look.LutRgb is null ? "" : Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(look.LutRgb.AsSpan()))) };
    }
    public static void Save(string path, LookSettings look) {
        if (!look.IsValid) throw new InvalidDataException("Invalid Reshade settings.");
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(look with { Modified = false }, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, absolute, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>3D CUBE 2..33, unit domain, RGB entries with red varying fastest. Trilinear GPU sampling in display-referred SDR.</summary>
    public static LookSettings LoadCube(string path, LookSettings previous) {
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("LUT exceeds 4 MiB.");
        return ParseCube(File.ReadAllText(path), previous);
    }
    public static LookSettings ParseCube(string text, LookSettings previous) {
        if (Encoding.UTF8.GetByteCount(text) > MaximumFileBytes) throw new InvalidDataException("LUT exceeds 4 MiB.");
        var values = new List<float>(); int size = 0; bool min = false, max = false;
        foreach (var raw in text.Split('\n')) {
            var line = raw.Split('#')[0].Trim(); if (line.Length == 0) continue;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens[0] == "TITLE") continue;
            if (tokens[0] == "LUT_3D_SIZE") {
                if (size != 0 || tokens.Length != 2 || !int.TryParse(tokens[1], out size) || size < 2 || size > 33) throw new InvalidDataException("Use a 3D CUBE LUT with size 2 through 33.");
                continue;
            }
            if (tokens[0] is "DOMAIN_MIN" or "DOMAIN_MAX") {
                var isMin = tokens[0] == "DOMAIN_MIN";
                if (tokens.Length != 4 || (isMin ? min : max)) throw new InvalidDataException("Duplicate or invalid LUT domain.");
                for (int i = 1; i < 4; i++) if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var domain) || domain != (isMin ? 0 : 1)) throw new InvalidDataException("Only DOMAIN_MIN 0 0 0 / DOMAIN_MAX 1 1 1 is supported.");
                if (isMin) min = true; else max = true; continue;
            }
            if (tokens.Length != 3 || size == 0) throw new InvalidDataException("Unsupported CUBE directive or row; declare LUT_3D_SIZE first.");
            foreach (var token in tokens) {
                if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !float.IsFinite(v) || v < 0 || v > 1) throw new InvalidDataException("LUT entries must be finite SDR values in [0,1].");
                values.Add(v);
            }
            if (values.Count > size * size * size * 3) throw new InvalidDataException("Too many LUT entries.");
        }
        if (size == 0 || values.Count != size * size * size * 3) throw new InvalidDataException("LUT entry count does not match size cubed.");
        var rgb = values.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.AsSpan())));
        return previous with { LutSize = (uint)size, LutRgb = rgb, LutHash = hash, LutIntensity = 1, Modified = true };
    }
}
