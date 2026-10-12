namespace HVX.PdReview
{
    // Keep metadata limits shared by the analysis commit and saved-session
    // readers/writers. These are UTF-8 byte quotas; serializer character
    // limits are additional upper bounds, not replacements for byte checks.
    //
    // 2026-10-12 现场：多文件分析在约 64 MiB 被拦。改为约 32 MiB 即分段落盘，
    // 单段上限仍守 64 MiB 兼容旧 session.json；总工作集有更高硬顶。
    internal static class ReviewAnalysisMetadataBudget
    {
        /// <summary>Legacy / per-segment encoded diagnostics soft quota (also segment trigger).</summary>
        internal const int EncodedDiagnosticsBytes = 32 * 1024 * 1024;
        /// <summary>Legacy single session.json / MaxJsonLength ceiling (compat with old archives).</summary>
        internal const int SessionMetadataBytes = 64 * 1024 * 1024;
        /// <summary>Start writing additional segment files when encoded payload reaches this size.</summary>
        internal const int SegmentTriggerBytes = 32 * 1024 * 1024;
        /// <summary>Hard ceiling across all diagnostics segments (decoded working set when fully expanded).</summary>
        internal const int SegmentedDiagnosticsTotalBytes = 512 * 1024 * 1024;
        /// <summary>Maximum number of diagnostics segment part files in one analysis.</summary>
        internal const int MaxDiagnosticsSegments = 64;
    }
}
