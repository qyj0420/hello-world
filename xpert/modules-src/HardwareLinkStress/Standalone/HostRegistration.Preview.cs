namespace HVX.HardwareLinkStress.Standalone
{
    internal static class HostRegistration
    {
        // Preview builds deliberately have no hardware host. A production host
        // must replace this source file only after the integration gates in
        // ../README.md and ../handoff.md have been implemented and reviewed.
        internal static IStressUiHost CreateVerifiedHost()
        {
            return null;
        }
    }
}
