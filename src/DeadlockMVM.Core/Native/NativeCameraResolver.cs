using System.Text;

namespace DeadlockMVM.Core.Native;

/// <summary>Outcome of one resolution step; a failure never discards sibling results.</summary>
public sealed record ResolveItemResult(string Name, bool Success, string? PatternUsed, int MatchCount, long Rva, string? Error);

/// <summary>Resolved RVAs (0 when unresolved) plus per-item diagnostics.</summary>
public sealed record ResolvedCameraAddresses(
    long SpectatorFovRefRva,
    long CameraManagerRva,
    long LocalControllersRva,
    long EntitySystemRva,
    IReadOnlyList<ResolveItemResult> Items);

/// <summary>
/// Resolves the spectator-FOV ConVar ref slot and the camera-manager global from an
/// on-disk <see cref="PeImage"/>, following the signature chains in
/// <c>tools/research/native_backend.md</c> §3. Strictly unique: 0 or &gt;1 matches is
/// a failure for that item only.
/// </summary>
public static class NativeCameraResolver
{
    private const string FovCvarName = "citadel_camera_spectator_fov";

    // §3a step 2: registrar's `lea rdx, name` (any disp; filtered by RIP target).
    private const string NameLeaPatternText = "48 8D 15 ?? ?? ?? ??";

    // §3a step 3: the next `lea rcx, [rip+disp]` within 0x20 bytes loads the ref slot.
    private const string RefLeaPatternText = "48 8D 0D ?? ?? ?? ??";

    // §3b: spectator update's cvar read site; leading LEA resolves directly to the ref.
    private const string FovRefFallbackPatternText =
        "48 8D 0D ?? ?? ?? ?? 48 8B F2 E8 ?? ?? ?? ?? F3 0F 11 43 50";

    // §3c: slot→manager getter; the LEA at pattern offset 9 targets the manager global.
    // (0x65 is the gs: segment prefix — `mov rax, gs:58h`.)
    private const string CameraManagerPatternText =
        "65 48 8B 04 25 58 00 00 00 48 8D 3D ?? ?? ?? ?? 8B D9 BA 68 00 00 00";

    // §3d: local-pawn getter, anchored on its prologue — the +0x6BC handle read is
    // the distinctive part (sibling entity getters share the walk but not this).
    // `lea r8, <controllers>` is at match+2; `mov r9, cs:<entity system>` follows.
    private const string LocalPawnGetterPatternText =
        "33 D2 4C 8D 05 ?? ?? ?? ?? 83 F9 FF 8B C2 0F 45 C1 48 98 4D 8B 04 C0 4D 85 C0 74 ?? 45 8B 80 BC 06 00 00";

    private const int ForwardScanWindow = 0x20;
    private const int RipLeaLength = 7;

    public static ResolvedCameraAddresses Resolve(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var items = new List<ResolveItemResult>();
        var code = image.FindCodeSection();

        // §3a.1: the cvar name string (NUL-terminated) must appear exactly once.
        var nameBytes = Encoding.ASCII.GetBytes(FovCvarName + '\0');
        var namePattern = new NativePattern(nameBytes, Enumerable.Repeat((byte)1, nameBytes.Length).ToArray());
        var nameHits = PatternScanner.FindAll(image.Bytes, 0, namePattern, maxResults: 2);

        long stringRva = 0;
        long? nameFileOffset = null;
        if (nameHits.Count == 1)
        {
            nameFileOffset = nameHits[0];
            var rva = FileOffsetToRva(image, nameFileOffset.Value);
            if (rva is null)
            {
                items.Add(new ResolveItemResult(
                    "SpectatorFovString", false, FovCvarName, 1, 0,
                    $"String found at file offset 0x{nameFileOffset.Value:X} but it maps to no section RVA."));
            }
            else
            {
                stringRva = rva.Value;
                items.Add(new ResolveItemResult("SpectatorFovString", true, FovCvarName, 1, stringRva, null));
            }
        }
        else
        {
            items.Add(new ResolveItemResult(
                "SpectatorFovString", false, FovCvarName, nameHits.Count, 0,
                $"Expected exactly 1 occurrence of the cvar name, found {(nameHits.Count == 0 ? "0" : "at least 2")}."));
        }

        // §3a.2: every `lea rdx, [rip+disp]` in .text whose target is the string; exactly 1.
        var codeBytes = code is null
            ? ReadOnlySpan<byte>.Empty
            : image.Bytes.AsSpan(checked((int)code.RawOffset), checked((int)code.RawSize));

        long? nameLeaIndex = null;
        if (stringRva == 0 || code is null)
        {
            items.Add(new ResolveItemResult(
                "SpectatorFovStringLea", false, null, 0, 0,
                stringRva == 0 ? "Skipped: cvar name string not resolved." : "Skipped: no code section found."));
        }
        else
        {
            var leaHits = PatternScanner.FindAll(
                codeBytes, code!.VirtualAddress, NativePattern.Parse(NameLeaPatternText), int.MaxValue);
            var targeted = new List<long>();
            foreach (var insnRva in leaHits)
            {
                var index = checked((int)(insnRva - code.VirtualAddress));
                var disp = BitConverter.ToInt32(codeBytes.Slice(index + 3, 4));
                if (insnRva + RipLeaLength + disp == stringRva)
                    targeted.Add(insnRva);
            }

            if (targeted.Count == 1)
            {
                nameLeaIndex = checked((int)(targeted[0] - code.VirtualAddress));
                items.Add(new ResolveItemResult(
                    "SpectatorFovStringLea", true, NameLeaPatternText, 1, targeted[0], null));
            }
            else
            {
                items.Add(new ResolveItemResult(
                    "SpectatorFovStringLea", false, NameLeaPatternText, targeted.Count, 0,
                    $"Expected exactly 1 lea rdx targeting the string, found {targeted.Count}."));
            }
        }

        // §3a.3: first `lea rcx, [rip+disp]` within 0x20 bytes after the registrar LEA.
        long fovRefRva = 0;
        if (nameLeaIndex is null)
        {
            items.Add(new ResolveItemResult(
                "SpectatorFovRef", false, null, 0, 0, "Skipped: registrar lea rdx not resolved."));
        }
        else
        {
            var start = checked((int)nameLeaIndex.Value) + RipLeaLength;
            var end = Math.Min(start + ForwardScanWindow, codeBytes.Length - RipLeaLength);
            for (var i = start; i <= end; i++)
            {
                if (codeBytes[i] == 0x48 && codeBytes[i + 1] == 0x8D && codeBytes[i + 2] == 0x0D)
                {
                    var disp = BitConverter.ToInt32(codeBytes.Slice(i + 3, 4));
                    fovRefRva = code!.VirtualAddress + i + RipLeaLength + disp;
                    items.Add(new ResolveItemResult(
                        "SpectatorFovRef", true, RefLeaPatternText, 1, fovRefRva, null));
                    break;
                }
            }

            if (fovRefRva == 0)
            {
                items.Add(new ResolveItemResult(
                    "SpectatorFovRef", false, RefLeaPatternText, 0, 0,
                    "No lea rcx within 0x20 bytes after the registrar lea rdx."));
            }
        }

        // §3b fallback, only when the primary chain above did not produce the ref.
        if (fovRefRva == 0)
        {
            if (code is null)
            {
                items.Add(new ResolveItemResult(
                    "SpectatorFovRefFallback", false, null, 0, 0, "Skipped: no code section found."));
            }
            else
            {
                try
                {
                    var matchRva = PatternScanner.FindUnique(
                        codeBytes, code.VirtualAddress, NativePattern.Parse(FovRefFallbackPatternText));
                    var index = checked((int)(matchRva - code.VirtualAddress));
                    var disp = BitConverter.ToInt32(codeBytes.Slice(index + 3, 4));
                    fovRefRva = matchRva + RipLeaLength + disp;
                    items.Add(new ResolveItemResult(
                        "SpectatorFovRefFallback", true, FovRefFallbackPatternText, 1, fovRefRva, null));
                }
                catch (SignatureResolutionException ex)
                {
                    items.Add(new ResolveItemResult(
                        "SpectatorFovRefFallback", false, FovRefFallbackPatternText, ex.MatchCount, 0, ex.Message));
                }
            }
        }

        // §3c: camera-manager global; LEA at match+9 (disp at match+12). On ambiguity,
        // report every candidate's resolved target — the diagnostics identify siblings.
        long managerRva = 0;
        if (code is null)
        {
            items.Add(new ResolveItemResult(
                "CameraManager", false, null, 0, 0, "Skipped: no code section found."));
        }
        else
        {
            var hits = PatternScanner.FindAll(
                codeBytes, code.VirtualAddress, NativePattern.Parse(CameraManagerPatternText), maxResults: 8);
            var targets = new List<long>(hits.Count);
            foreach (var hitRva in hits)
            {
                var index = checked((int)(hitRva - code.VirtualAddress));
                var disp = BitConverter.ToInt32(codeBytes.Slice(index + 12, 4));
                targets.Add(hitRva + 9 + RipLeaLength + disp);
            }

            // Multiple matches are acceptable only when every candidate resolves to the
            // same target (the getter shape is duplicated across TLS-init clones).
            var distinct = targets.Distinct().ToList();
            if (distinct.Count == 1)
            {
                managerRva = distinct[0];
                items.Add(new ResolveItemResult(
                    "CameraManager", true, CameraManagerPatternText, hits.Count, managerRva, null));
            }
            else
            {
                items.Add(new ResolveItemResult(
                    "CameraManager", false, CameraManagerPatternText, hits.Count, 0,
                    $"Ambiguous: {hits.Count} matches resolve to {distinct.Count} distinct targets " +
                    $"({string.Join(", ", distinct.Select(t => $"0x{t:X}"))})."));
            }
        }

        // §3d: local-pawn getter -> controllers array + entity system globals.
        long controllersRva = 0;
        long entitySystemRva = 0;
        if (code is null)
        {
            items.Add(new ResolveItemResult("LocalPawnGetter", false, null, 0, 0, "Skipped: no code section found."));
        }
        else
        {
            try
            {
                var matchRva = PatternScanner.FindUnique(
                    codeBytes, code.VirtualAddress, NativePattern.Parse(LocalPawnGetterPatternText));
                items.Add(new ResolveItemResult(
                    "LocalPawnGetter", true, LocalPawnGetterPatternText, 1, matchRva, null));

                // lea r8, controllers at match+2 (disp at match+5, insn length 7).
                var index = checked((int)(matchRva - code.VirtualAddress));
                var disp = BitConverter.ToInt32(codeBytes.Slice(index + 5, 4));
                controllersRva = matchRva + 2 + RipLeaLength + disp;
                items.Add(new ResolveItemResult("LocalControllers", true, "4C 8D 05", 1, controllersRva, null));

                entitySystemRva = ScanRipReference(
                    codeBytes, code.VirtualAddress, matchRva + 0x20, 0x30, 0x8B, 0x0D);
                items.Add(entitySystemRva != 0
                    ? new ResolveItemResult("EntitySystem", true, "4C 8B 0D", 1, entitySystemRva, null)
                    : new ResolveItemResult("EntitySystem", false, "4C 8B 0D", 0, 0,
                        "No mov r9, cs: in the local-pawn getter body."));
            }
            catch (SignatureResolutionException ex)
            {
                items.Add(new ResolveItemResult(
                    "LocalPawnGetter", false, LocalPawnGetterPatternText, ex.MatchCount, 0, ex.Message));
            }
        }

        return new ResolvedCameraAddresses(fovRefRva, managerRva, controllersRva, entitySystemRva, items);
    }

    /// <summary>Finds `REX.W &lt;opcode&gt; &lt;modrm-reg-rip&gt; &lt;disp32&gt;` in a window and
    /// resolves its RIP-relative target (0 when absent).</summary>
    private static long ScanRipReference(
        ReadOnlySpan<byte> codeBytes, long codeRvaBase, long instructionWindowStartRva, int windowLength, byte opcode, byte modrm)
    {
        var start = checked((int)(instructionWindowStartRva - codeRvaBase));
        var end = Math.Min(start + windowLength, codeBytes.Length - RipLeaLength);
        for (var i = start; i <= end; i++)
        {
            if (codeBytes[i] == 0x4C && codeBytes[i + 1] == opcode && codeBytes[i + 2] == modrm)
            {
                var disp = BitConverter.ToInt32(codeBytes.Slice(i + 3, 4));
                return codeRvaBase + i + RipLeaLength + disp;
            }
        }

        return 0;
    }

    private static long? FileOffsetToRva(PeImage image, long offset)
    {
        foreach (var section in image.Sections)
        {
            if (offset >= section.RawOffset && offset < section.RawOffset + section.RawSize)
                return section.VirtualAddress + (offset - section.RawOffset);
        }

        return null;
    }
}
