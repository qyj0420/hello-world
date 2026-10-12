namespace HVX.PdReview
{
    // Keep metadata limits shared by the analysis commit and saved-session
    // readers/writers. These are UTF-8 byte quotas; serializer character
    // limits are additional upper bounds, not replacements for byte checks.
    internal static class ReviewAnalysisMetadataBudget
    {
        internal const int EncodedDiagnosticsBytes = 32 * 1024 * 1024;
        internal const int SessionMetadataBytes = 64 * 1024 * 1024;
    }
}
