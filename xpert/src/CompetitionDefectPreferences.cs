using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    // An independent program profile. Never import the main desktop's AI, model or language settings.
    internal sealed class CompetitionDefectPreferences
    {
        public int Schema { get; set; }
        public string LanguageCode { get; set; }
        public bool EnableAiFeatures { get; set; }
        public string ModelLibraryPath { get; set; }
        public string ModelKind { get; set; }
        public string CompetitionModelPath { get; set; }
        public CompetitionDefectPreferences() { Schema = 1; LanguageCode = "system"; EnableAiFeatures = true; ModelLibraryPath = String.Empty; }
        internal CompetitionDefectPreferences Clone() { return (CompetitionDefectPreferences)MemberwiseClone(); }
        internal int LanguageIndex { get { return AppLanguages.IndexOf(LanguageCode); } }
        internal static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XIONGXIN", "CompetitionDefectRecognition", "preferences.json"); }
        }
        internal void Validate()
        {
            if (Schema != 1) throw new InvalidDataException("Unsupported competition preferences version.");
            if (LanguageCode != "system" && !AppLanguages.All.Any(item => String.Equals(item.Code, LanguageCode, StringComparison.Ordinal)))
                throw new InvalidDataException("Unsupported competition language.");
            if (ModelLibraryPath == null) ModelLibraryPath = String.Empty;
            CompetitionModelPath=(CompetitionModelPath??String.Empty).Trim();
            if(CompetitionModelPath.Length>0){
                if(!Path.IsPathRooted(CompetitionModelPath))throw new InvalidDataException("The model path must be absolute.");
                CompetitionModelPath=Path.GetFullPath(CompetitionModelPath);
            }
            if (!String.IsNullOrWhiteSpace(ModelLibraryPath))
            {
                if (!Path.IsPathRooted(ModelLibraryPath)) throw new InvalidDataException("The independent model path must be absolute.");
                ModelLibraryPath = Path.GetFullPath(ModelLibraryPath);
            }
            else if(CompetitionModelPath.Length>0) ModelLibraryPath = CompetitionModelPath;
            else ModelLibraryPath = String.Empty;
            // Standard baseline only; competition is no longer a selectable mode.
            ModelKind=DefectModelCatalog.Standard;
            CompetitionModelPath=String.Empty;
        }
        private static string SafePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.Length >= 240) throw new PathTooLongException("The preferences path is too long for an atomic backup.");
            for (string current = full; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked preferences paths are not supported.");
            return full;
        }
        internal static CompetitionDefectPreferences Load(string path, out string warning)
        {
            warning = String.Empty;
            try
            {
                string full = SafePath(path);
                if (!File.Exists(full)) return new CompetitionDefectPreferences();
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 2 || stream.Length > 32768) throw new InvalidDataException("Invalid preferences file size.");
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    {
                        var value = new JavaScriptSerializer { MaxJsonLength = 32768, RecursionLimit = 5 }.Deserialize<CompetitionDefectPreferences>(reader.ReadToEnd());
                        if (value == null) throw new InvalidDataException("Missing preferences.");
                        value.Validate(); return value;
                    }
                }
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException || error is AccessViolationException) throw;
                warning = error.Message; return new CompetitionDefectPreferences();
            }
        }
        internal void Save(string path)
        {
            var copy = Clone(); copy.Validate(); string full = SafePath(path);
            string directory = Path.GetDirectoryName(full); Directory.CreateDirectory(directory); SafePath(full);
            string identity;
            using (var hash = SHA256.Create()) identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(full.ToUpperInvariant()))).Replace("-", String.Empty);
            using (var mutex = new Mutex(false, @"Local\HVX.CompetitionPreferences." + identity))
            {
                bool held = false; string pending = null;
                try
                {
                    try { held = mutex.WaitOne(5000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("The independent preferences are being saved by another window.");
                    pending = Path.Combine(directory, ".p-" + Guid.NewGuid().ToString("N") + ".tmp");
                    byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(copy));
                    using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                    SafePath(full); SafePath(full + ".bak");
                    if (File.Exists(full)) File.Replace(pending, full, full + ".bak", true); else File.Move(pending, full);
                    pending = null;
                }
                finally
                {
                    if (pending != null && File.Exists(pending)) File.Delete(pending);
                    if (held) mutex.ReleaseMutex();
                }
            }
        }
    }
}
