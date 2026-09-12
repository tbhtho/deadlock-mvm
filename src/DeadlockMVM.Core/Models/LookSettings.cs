using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace DeadlockMVM.Core.Models;

/// <summary>Post-tonemap SDR look. Independent of Fog, camera, and capture passes.</summary>
public sealed record LookSettings
{
    public uint SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public ulong Revision { get; init; }
    public float Strength { get; init; } = 1.0f;
    public float Exposure { get; init; } = 0.0f;
    public float Contrast { get; init; } = 1.0f;
    public float Saturation { get; init; } = 1.0f;
    public float Temperature { get; init; } = 0.0f;
    public float Tint { get; init; } = 0.0f;
    public float LiftR { get; init; } = 0.0f;
    public float LiftG { get; init; } = 0.0f;
    public float LiftB { get; init; } = 0.0f;
    public float GammaR { get; init; } = 1.0f;
    public float GammaG { get; init; } = 1.0f;
    public float GammaB { get; init; } = 1.0f;
    public float GainR { get; init; } = 1.0f;
    public float GainG { get; init; } = 1.0f;
    public float GainB { get; init; } = 1.0f;
    public float Shadows { get; init; } = 0.0f;
    public float Highlights { get; init; } = 0.0f;
    public float Vibrance { get; init; } = 0.0f;
    public float BloomThreshold { get; init; } = 0.8f;
    public float BloomKnee { get; init; } = 0.5f;
    public float BloomIntensity { get; init; } = 0.0f;
    public float BloomRadius { get; init; } = 1.0f;
    public float Sharpen { get; init; } = 0.0f;
    public float Vignette { get; init; } = 0.0f;
    public float GrainStrength { get; init; } = 0.0f;
    public uint GrainSeed { get; init; } = 1;
    public uint BloomQuality { get; init; } = 1;
    public float LutIntensity { get; init; }
    public uint LutSize { get; init; }
    public ulong LutRevision { get; init; }
    public string PresetName { get; init; } = "Neutral";
    public bool Modified { get; init; }
    public float[]? LutRgb { get; init; }
    public string LutHash { get; init; } = "";

    [JsonIgnore]
    public bool IsValid =>
        SchemaVersion == 1 &&
        BloomQuality <= 2 &&
        HasValidLut() &&
        !string.IsNullOrWhiteSpace(PresetName) &&
        PresetName.Length <= 48 &&
        !PresetName.Any(char.IsControl) &&
        IsFiniteInRange(Strength, 0.0f, 1.0f) &&
        IsFiniteInRange(Exposure, -5.0f, 5.0f) &&
        IsFiniteInRange(Contrast, 0.0f, 2.0f) &&
        IsFiniteInRange(Saturation, 0.0f, 2.0f) &&
        IsFiniteInRange(Temperature, -1.0f, 1.0f) &&
        IsFiniteInRange(Tint, -1.0f, 1.0f) &&
        IsFiniteInRange(LiftR, -1.0f, 1.0f) &&
        IsFiniteInRange(LiftG, -1.0f, 1.0f) &&
        IsFiniteInRange(LiftB, -1.0f, 1.0f) &&
        IsFiniteInRange(GammaR, 0.1f, 4.0f) &&
        IsFiniteInRange(GammaG, 0.1f, 4.0f) &&
        IsFiniteInRange(GammaB, 0.1f, 4.0f) &&
        IsFiniteInRange(GainR, 0.0f, 4.0f) &&
        IsFiniteInRange(GainG, 0.0f, 4.0f) &&
        IsFiniteInRange(GainB, 0.0f, 4.0f) &&
        IsFiniteInRange(Shadows, -1.0f, 1.0f) &&
        IsFiniteInRange(Highlights, -1.0f, 1.0f) &&
        IsFiniteInRange(Vibrance, -1.0f, 1.0f) &&
        IsFiniteInRange(BloomThreshold, 0.0f, 1.0f) &&
        IsFiniteInRange(BloomKnee, 0.0f, 1.0f) &&
        IsFiniteInRange(BloomIntensity, 0.0f, 2.0f) &&
        IsFiniteInRange(BloomRadius, 0.25f, 4.0f) &&
        IsFiniteInRange(Sharpen, 0.0f, 1.0f) &&
        IsFiniteInRange(Vignette, 0.0f, 1.0f) &&
        IsFiniteInRange(GrainStrength, 0.0f, 0.2f) &&
        IsFiniteInRange(LutIntensity, 0.0f, 1.0f);

    private bool HasValidLut()
    {
        if (LutSize == 0)
            return LutRgb is null;

        return LutSize is >= 2 and <= 33 &&
            LutRgb?.Length == LutSize * LutSize * LutSize * 3 &&
            LutRgb.All(static value => IsFiniteInRange(value, 0.0f, 1.0f));
    }

    private static bool IsFiniteInRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    public LookSettings SetValue(int index, double value)
    {
        if (!double.IsFinite(value) ||
            (index is 26 or 27 && value != Math.Truncate(value)))
            throw new ArgumentOutOfRangeException(nameof(value));

        var v = (float)value;
        var next = index switch
        {
            0 => this with { Strength = v },
            1 => this with { Exposure = v },
            2 => this with { Contrast = v },
            3 => this with { Saturation = v },
            4 => this with { Temperature = v },
            5 => this with { Tint = v },
            6 => this with { LiftR = v },
            7 => this with { LiftG = v },
            8 => this with { LiftB = v },
            9 => this with { GammaR = v },
            10 => this with { GammaG = v },
            11 => this with { GammaB = v },
            12 => this with { GainR = v },
            13 => this with { GainG = v },
            14 => this with { GainB = v },
            15 => this with { Shadows = v },
            16 => this with { Highlights = v },
            17 => this with { Vibrance = v },
            18 => this with { BloomThreshold = v },
            19 => this with { BloomKnee = v },
            20 => this with { BloomIntensity = v },
            21 => this with { BloomRadius = v },
            22 => this with { Sharpen = v },
            23 => this with { Vignette = v },
            24 => this with { GrainStrength = v },
            25 => this with { LutIntensity = v },
            26 => this with { BloomQuality = checked((uint)value) },
            27 => this with { GrainSeed = checked((uint)value) },
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
        if (!next.IsValid)
            throw new ArgumentOutOfRangeException(nameof(value));

        return next with { Modified = true };
    }

    /// <summary>Writes the packed 140-byte native LookSettings protocol layout.</summary>
    public void Write(Span<byte> bytes)
    {
        if (!IsValid || bytes.Length < 140)
            throw new InvalidDataException("Invalid Reshade settings.");

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], Enabled ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Revision);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], Strength);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], Exposure);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[24..], Contrast);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], Saturation);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[32..], Temperature);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[36..], Tint);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[40..], LiftR);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[44..], LiftG);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[48..], LiftB);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[52..], GammaR);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[56..], GammaG);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[60..], GammaB);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[64..], GainR);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[68..], GainG);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[72..], GainB);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[76..], Shadows);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[80..], Highlights);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[84..], Vibrance);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[88..], BloomThreshold);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[92..], BloomKnee);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[96..], BloomIntensity);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[100..], BloomRadius);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[104..], Sharpen);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[108..], Vignette);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[112..], GrainStrength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[116..], GrainSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[120..], BloomQuality);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[124..], LutIntensity);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[128..], LutSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[132..], LutRevision);
    }
}
