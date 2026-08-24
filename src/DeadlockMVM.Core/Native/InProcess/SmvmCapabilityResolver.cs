namespace DeadlockMVM.Core.Native.InProcess;

/// <summary>
/// Computes the camera/editor capability bitmask published in the current SMVM snapshot.
/// Kept in Core (pure function) so capability gating is unit-testable; the
/// launcher host coordinator only supplies the live inputs.
/// </summary>
public static class SmvmCapabilityResolver
{
    public static SmvmCapabilities Resolve(
        bool internalEnabled,
        bool nativeConnected,
        InProcessCameraStatus? native,
        bool selfTestRunning)
    {
        if (!internalEnabled || !nativeConnected ||
            native?.RendererBackend != SmvmRendererBackend.D3D11 ||
            !native.OverlayFlags.HasFlag(SmvmOverlayFlags.Ready))
            return SmvmCapabilities.None;

        var capabilities = SmvmCapabilities.PathVisualization;
        if (native.Flags.HasFlag(InProcessStatusFlags.Resolved) &&
            native.Flags.HasFlag(InProcessStatusFlags.HookInstalled))
        {
            capabilities |= SmvmCapabilities.ManualCamera;
            // Rendered roll was visually proven on the published build
            // (2026-08-23: 0 -> +20.99 -> -20.07 -> 0 degrees). It uses the same
            // hooked camera channel as Manual Camera, so it is advertised under
            // the same Resolved + HookInstalled gate.
            capabilities |= SmvmCapabilities.RenderedRoll;
            if (!selfTestRunning)
                capabilities |= SmvmCapabilities.CameraSelfTest | SmvmCapabilities.CampathSelfTest;
        }

        return capabilities;
    }
}
