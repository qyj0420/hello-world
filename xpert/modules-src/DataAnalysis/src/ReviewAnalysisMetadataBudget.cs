namespace HVX.PdReview
{
    // Keep metadata limits shared by the analysis commit and saved-session
    // readers/writers. These are UTF-8 byte quotas; serializer character
    // limits are additional upper bounds, not replacements for byte checks.
    //
    // 2026-10-12 现场：多文件定位分析（含 11-12 / 462 LOC）在约 64 MiB
    // 诊断配额处整批丢结果。改为约 32 MiB 分段落盘；合并波形与 pdmap
    // 读档上限同步抬高，避免「有诊断配额但仍无定位结果」。
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
        /// <summary>Combined location-result waveform cells (3 blocks × rows × width) after a batch merge.</summary>
        internal const long MergedWaveformCells = 400L * 1000 * 1000;
        /// <summary>Archived pdmap.bin cell budget on 64-bit hosts (file ≈ cells×8 + header).</summary>
        internal const long ArchivedPdmapCellsX64 = 64L * 1000 * 1000;
        /// <summary>Archived pdmap.bin byte budget matching ArchivedPdmapCellsX64.</summary>
        internal const long ArchivedPdmapBytesX64 = ArchivedPdmapCellsX64 * 8 + 16;
        /// <summary>Named-analysis directory total byte budget (pdmap + diagnostics + charts).</summary>
        internal const long AnalysisArchiveTotalBytes = 1536L * 1024 * 1024;
        // Spill store (NOTES_ANALYSIS_MEMORY.md): per-file statuses live in
        // ReviewDiagnosticSpill and the session map holds short references, so
        // the two "referenced" totals below are ceilings on what one session
        // may name, not resident collections. At most one status (<=4 MiB)
        // and one ledger (<=16 MiB) are expanded at a time.
        /// <summary>Sum of decoded status bytes referenced by one session.</summary>
        internal const long DecodedDiagnosticsBytes = 2048L * 1024 * 1024;
        /// <summary>Cumulative decoded E/E+ ledger validation work for one session.</summary>
        internal const long LedgerValidationWorkBytes = 8192L * 1024 * 1024;
        /// <summary>Absolute ceiling on compressed bytes held by the process-wide spill store.</summary>
        internal const long DiagnosticsSpillBytes = 1024L * 1024 * 1024;
        /// <summary>Ceiling when no temporary folder is writable and the store falls back to memory.</summary>
        internal const long DiagnosticsSpillMemoryFallbackBytes = 256L * 1024 * 1024;
        /// <summary>diagnostics.bin inside a named analysis archive.</summary>
        internal const long DiagnosticsArchiveBytes = DiagnosticsSpillBytes;
    }
}
