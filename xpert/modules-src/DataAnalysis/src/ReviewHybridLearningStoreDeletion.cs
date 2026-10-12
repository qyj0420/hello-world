using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal static partial class ReviewHybridLearningStore
    {
        // Removal is from the active retrieval/training set. Preserve the original
        // transaction and a deletion receipt; do not claim to untrain model weights.
        internal static string Delete(string root, string kind, string transactionId)
        {
            lock (gate)
            {
                ValidateRoot(root, false);
                if (string.IsNullOrWhiteSpace(transactionId) || transactionId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    transactionId != Path.GetFileName(transactionId)) throw new ArgumentException("Invalid learning transaction identifier.");
                string source = Path.Combine(Path.GetFullPath(root), "active", transactionId);
                RejectReparse(source);
                if (!Directory.Exists(source) || !Owned(source)) throw new InvalidOperationException("The active feedback record was not found.");
                // Keep the full transaction ID in the receipt and original manifest;
                // repeating it in this auxiliary name can exceed legacy path limits.
                string auditDirectory = Path.Combine(Path.GetFullPath(root), "history", "deleted_" + Guid.NewGuid().ToString("N"));
                string archivedSample = Path.Combine(auditDirectory, "sample");
                RequireDeletionPathLength(auditDirectory, true);
                RequireDeletionPathLength(Path.Combine(auditDirectory, "deletion.json"), false);
                ValidateDeletionTree(source, archivedSample);
                var entry = Read(root, kind).FirstOrDefault(item => Convert.ToString(item["transaction_id"], CultureInfo.InvariantCulture) == transactionId);
                if (entry == null) throw new InvalidOperationException("The feedback record belongs to another category.");
                RejectReparse(auditDirectory);
                Directory.CreateDirectory(auditDirectory);
                var receipt = new Dictionary<string, object> {
                    { "owner", Owner }, { "schema", 1 }, { "operation", "exclude_from_active_local_learning" },
                    { "transaction_id", transactionId }, { "kind", kind },
                    { "requested_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                    { "operator", Environment.UserName }, { "production_model_weights_modified", false },
                    { "completion_rule", "Removal completed only when the original sample directory exists beside this receipt." }
                };
                File.WriteAllText(Path.Combine(auditDirectory, "deletion.json"), new JavaScriptSerializer().Serialize(receipt), new UTF8Encoding(false));
                // A single same-volume rename is the commit. Failure leaves active data intact.
                Directory.Move(source, archivedSample);
                return auditDirectory;
            }
        }

        static void RequireDeletionPathLength(string path, bool directory)
        {
            // Paths are absolute here. Validate every destination before writing
            // a receipt, without relying on the machine's long-path policy.
            if (path.Length >= (directory ? 248 : 260))
                throw new InvalidOperationException("学习库路径过长，无法安全保留删除备份。请将学习库移至较短路径后重试；当前记录和原文件未修改。 / Learning path is too long to preserve a deletion backup. Use a shorter learning path; the active record and original files are unchanged.");
        }

        static void ValidateDeletionTree(string source, string destination)
        {
            RequireDeletionPathLength(destination, true);
            foreach (string entry in Directory.GetFileSystemEntries(source))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected feedback files cannot be removed.");
                string target = Path.Combine(destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) ValidateDeletionTree(entry, target);
                else RequireDeletionPathLength(target, false);
            }
        }
    }
}
