using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;

namespace HVX.ReportGenerator
{
    /// <summary>
    /// Read-only compatibility reader for the legacy Analysis_*.data + LabVIEW XML sidecar format and
    /// the HVX.NamedAnalysis directory format emitted by PdReview.
    /// </summary>
    public sealed class AnalysisPackageReader
    {
        private const long MaxJsonBytes = 16L * 1024 * 1024;
        private const long MaxXmlBytes = 256L * 1024 * 1024;
        private const long MaxPngBytes = 64L * 1024 * 1024;
        private const long MaxTotalPngBytes = 64L * 1024 * 1024;
        private const long MaxMatrixCells = 10000000L;
        private static readonly Regex LegacyName = new Regex(
            "^Analysis_(\\d{4}-\\d{2}-\\d{2}-\\d{2}'\\d{2}'\\d{2}\\.\\d{2})\\.(data|bin)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public ReportAnalysisPackage Load(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Analysis path is required.", "sourcePath");
            string full = Path.GetFullPath(sourcePath);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Analysis path does not exist.", full);

            string directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            string manifest = Path.Combine(directory, "analysis.json");
            if (File.Exists(manifest) || string.Equals(Path.GetFileName(full), "analysis.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(full), "selection.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(full), "session.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(full), "parameters.bin", StringComparison.OrdinalIgnoreCase))
                return LoadNamedAnalysis(directory, manifest);

            return LoadLegacy(full);
        }

        public bool TryLoad(string sourcePath, out ReportAnalysisPackage package, out string error)
        {
            package = null;
            error = string.Empty;
            try
            {
                package = Load(sourcePath);
                return true;
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                error = ex.Message;
                return false;
            }
        }

        public static ReportAnalysisPackage Read(string sourcePath)
        {
            return new AnalysisPackageReader().Load(sourcePath);
        }

        /// <summary>Finds loadable package entry paths without opening their large matrices.</summary>
        public string[] EnumeratePackagePaths(string projectOrAnalysisPath)
        {
            if (string.IsNullOrWhiteSpace(projectOrAnalysisPath)) return new string[0];
            string full;
            try { full = Path.GetFullPath(projectOrAnalysisPath); }
            catch { return new string[0]; }
            if (File.Exists(full)) return new[] { full };
            if (!Directory.Exists(full)) return new string[0];
            if (File.Exists(Path.Combine(full, "analysis.json"))) return new[] { full };

            string searchRoot = full;
            string analysisReport = Path.Combine(full, "Analysis report");
            if (Directory.Exists(analysisReport)) searchRoot = analysisReport;

            var found = new List<string>();
            foreach (string file in SafeEnumerateFiles(searchRoot))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "analysis.json", StringComparison.OrdinalIgnoreCase))
                    found.Add(Path.GetDirectoryName(file));
                else if (LegacyName.IsMatch(name) && string.Equals(Path.GetExtension(file), ".data", StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Path.Combine(Path.GetDirectoryName(file), "analysis.json")))
                    found.Add(file);
            }

            return found.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(GetLastWriteUtcSafe).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Locates the project root. Any descendant of a directory named "Analysis report" resolves to
        /// that directory's parent; otherwise ITEM.in/CABLE.in ancestors are preferred.
        /// </summary>
        public static string DetermineProjectRoot(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { return string.Empty; }
            string directory = File.Exists(full) ? Path.GetDirectoryName(full) : full;
            if (!Directory.Exists(directory) && Path.HasExtension(directory)) directory = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(directory)) return string.Empty;

            DirectoryInfo current;
            try { current = new DirectoryInfo(directory); }
            catch { return string.Empty; }
            for (DirectoryInfo cursor = current; cursor != null; cursor = cursor.Parent)
            {
                if (string.Equals(cursor.Name, "Analysis report", StringComparison.OrdinalIgnoreCase))
                    return cursor.Parent == null ? cursor.FullName : TrimDirectory(cursor.Parent.FullName);
            }
            for (DirectoryInfo cursor = current; cursor != null; cursor = cursor.Parent)
            {
                try
                {
                    if (File.Exists(Path.Combine(cursor.FullName, "ITEM.in")) || File.Exists(Path.Combine(cursor.FullName, "CABLE.in")))
                        return TrimDirectory(cursor.FullName);
                }
                catch { }
            }
            return TrimDirectory(current.FullName);
        }

        private ReportAnalysisPackage LoadNamedAnalysis(string directory, string manifestPath)
        {
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("Named analysis is missing analysis.json.", manifestPath);
            var warnings = new List<string>();
            Dictionary<string, object> manifest = ReadJsonObject(manifestPath);
            string schema = GetString(manifest, "Schema", "schema");
            if (!string.IsNullOrEmpty(schema) && !string.Equals(schema, "HVX.NamedAnalysis", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported named-analysis schema: " + schema);

            var package = new ReportAnalysisPackage
            {
                Kind = AnalysisPackageKind.NamedAnalysis,
                SourcePath = Path.GetFullPath(manifestPath),
                PackageDirectory = TrimDirectory(Path.GetFullPath(directory)),
                CompanionPath = File.Exists(Path.Combine(directory, "session.json")) ? Path.Combine(directory, "session.json") : string.Empty,
                AnalysisId = GetString(manifest, "Id", "id", "AnalysisId", "analysis_id"),
                AnalysisName = GetString(manifest, "Name", "name", "AnalysisName", "analysis_name"),
                CreatedUtc = GetString(manifest, "CreatedUtc", "createdUtc", "created_utc")
            };
            if (string.IsNullOrWhiteSpace(package.AnalysisId)) package.AnalysisId = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(package.AnalysisName)) package.AnalysisName = package.AnalysisId;

            string physicalProject = DetermineProjectRoot(directory);
            string embeddedProject = FindStringRecursive(manifest, ProjectKeys, 0);
            package.ProjectPath = SelectProjectPath(physicalProject, embeddedProject, directory, warnings);

            Dictionary<string, object> session = null;
            string sessionPath = Path.Combine(directory, "session.json");
            if (File.Exists(sessionPath))
            {
                try { session = ReadJsonObject(sessionPath); }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    warnings.Add("session.json could not be read: " + ex.Message);
                }
            }
            if(session!=null){
                var settingsRows=GetValue(session,"settings") as object[];
                if(settingsRows!=null)foreach(object value in settingsRows){
                    var row=value as object[];
                    if(row!=null&&row.Length==2&&object.Equals(row[0],"AlgorithmDiagnostics"))
                        package.AlgorithmDiagnosticsJson=row[1] as string??"";
                }
            }

            bool structuredConclusion=GetValue(manifest,"Conclusion") is Dictionary<string,object>;
            if(structuredConclusion)
            {
                package.ConclusionText=ReadSavedConclusion(manifest,directory,package.AnalysisId,warnings);
                package.HasSavedConclusionSnapshot=!String.IsNullOrWhiteSpace(package.ConclusionText);
                if(package.HasSavedConclusionSnapshot)package.ConclusionApi=GetString((Dictionary<string,object>)GetValue(manifest,"Conclusion"),"Api");
            }
            else package.ConclusionText = FindStringRecursive(manifest, ConclusionKeys, 0);
            if (!structuredConclusion && string.IsNullOrWhiteSpace(package.ConclusionText) && session != null)
                package.ConclusionText = FindStringRecursive(session, ConclusionKeys, 0);

            double[,] matrix = null;
            string matrixPath = FindNamedMatrixPath(directory, manifest);
            if (!string.IsNullOrEmpty(matrixPath))
            {
                try { matrix = ReadHvxMatrix(matrixPath); }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    warnings.Add("Location matrix could not be read: " + ex.Message);
                }
            }
            if (matrix == null)
            {
                object embedded = FindValueRecursive(manifest, MatrixKeys, 0);
                if (embedded != null)
                {
                    try { matrix = ReadJsonMatrix(embedded); }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        warnings.Add("Embedded location matrix is invalid: " + ex.Message);
                    }
                }
            }
            package.SetLocationMatrix(matrix);
            ReportEffectiveSelection.Read(package,directory,manifest,session,matrix,warnings);
            if(!package.HasReportInputSnapshot&&package.HasSavedConclusionSnapshot&&package.UsesEvidenceV3&&package.AcquisitionReferenceOnlyRows.Length==0)ReadEvidenceConclusionInput(package,manifest,matrix,warnings);

            bool selectionAvailable;
            HashSet<int> selected = ReadSelection(directory, manifest, warnings, out selectionAvailable);
            var savedImages=ReadNamedCharts(directory, manifest, selected, selectionAvailable, warnings, matrix, session).ToArray();
            ReportEffectiveSelection.ValidateCharts(package,manifest,savedImages,warnings);
            package.SetReportImages(savedImages);
            if (!package.HasLocationMatrix) warnings.Add("No location-result matrix was available in this analysis package.");
            package.SetWarnings(warnings);
            return package;
        }

        static string ReadSavedConclusion(Dictionary<string,object> manifest,string directory,string id,List<string> warnings)
        {
            try
            {
                var value=GetValue(manifest,"Conclusion") as Dictionary<string,object>;
                string text=GetString(value,"SummaryText");DateTime generated,saved;
                if(GetString(manifest,"Schema")!="HVX.NamedAnalysis"||GetInt(manifest,0,"Version")!=1||
                    String.IsNullOrWhiteSpace(id)||id!=Path.GetFileName(id)||GetString(value,"Schema")!="HVX.AnalysisConclusion.1"||GetString(value,"AnalysisId")!=id||String.IsNullOrWhiteSpace(text)||
                    !DateTime.TryParse(GetString(value,"GeneratedUtc"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out generated)||
                    !DateTime.TryParse(GetString(manifest,"SavedUtc"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out saved)||generated.ToUniversalTime()>saved.ToUniversalTime())
                    throw new InvalidDataException("Saved conclusion identity or timestamp is invalid.");
                if(String.Equals(Path.GetFileName(Path.GetDirectoryName(directory)),"Analysis report",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(directory)!=id)
                    throw new InvalidDataException("Saved conclusion does not belong to this analysis directory.");
                object[] files=GetValue(manifest,"Files") as object[];
                if(files==null)throw new InvalidDataException("Saved conclusion has no file bindings.");
                foreach(string name in new[]{"parameters.bin","session.json"})
                {
                    var matches=files.OfType<Dictionary<string,object>>().Where(f=>GetString(f,"Path")==name).ToArray();
                    string expected=GetString(value,name=="parameters.bin"?"ParametersSha256":"SessionSha256");
                    if(matches.Length!=1||!Regex.IsMatch(expected,"^[0-9a-fA-F]{64}$")||!String.Equals(GetString(matches[0],"Sha256"),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Saved conclusion snapshot binding is invalid.");
                    string path=Path.Combine(directory,name);
                    long size=Convert.ToInt64(GetValue(matches[0],"Bytes"),CultureInfo.InvariantCulture);
                    if(!File.Exists(path)||new FileInfo(path).Length!=size||size<0||size>(name=="session.json"?MaxJsonBytes:16+MaxMatrixCells*8))throw new InvalidDataException("Saved conclusion source is missing or changed.");
                    for(string cursor=path;!String.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor))
                        if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Saved conclusion source links are not supported.");
                    using(var sha=SHA256.Create())using(var input=File.OpenRead(path))
                        if(!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Saved conclusion source hash has changed.");
                }
                return text;
            }
            catch(Exception error){if(IsFatal(error))throw;warnings.Add("Saved conclusion unavailable: "+error.Message);return String.Empty;}
        }

        static void ReadEvidenceConclusionInput(ReportAnalysisPackage package,Dictionary<string,object> manifest,double[,] matrix,List<string> warnings)
        {
            try
            {
                var c=(Dictionary<string,object>)GetValue(manifest,"Conclusion");
                var indices=GetValue(c,"SourceRowIndices") as object[];var allJoints=GetValue(c,"Joints") as object[];
                if(matrix==null||matrix.GetLength(1)<36||indices==null||indices.Length==0||indices.Length>200000||allJoints==null||allJoints.Length!=3)throw new InvalidDataException("Missing saved Evidence V3 input context; regenerate and save the analysis.");
                var seen=new HashSet<int>();var input=new double[indices.Length,matrix.GetLength(1)];
                for(int i=0;i<indices.Length;i++)
                {
                    double index=Convert.ToDouble(indices[i],CultureInfo.InvariantCulture);
                    if(Double.IsNaN(index)||Double.IsInfinity(index)||index<0||index>=matrix.GetLength(0)||index!=Math.Truncate(index)||!seen.Add((int)index))throw new InvalidDataException("Invalid saved conclusion source row.");
                    int r=(int)index;for(int col=0;col<matrix.GetLength(1);col++)input[i,col]=matrix[r,col];input[i,22]=Double.NaN;
                    if(input[i,35]!=1)throw new InvalidDataException("Saved conclusion refers to a non-accepted pulse.");
                }
                var joints=new double[3][];
                for(int p=0;p<3;p++)
                {
                    var values=allJoints[p] as object[];if(values==null||values.Length>10000)throw new InvalidDataException("Invalid conclusion joint context.");
                    joints[p]=values.Select(v=>Convert.ToDouble(v,CultureInfo.InvariantCulture)).ToArray();
                    if(joints[p].Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)||v<0)||joints[p].Skip(1).Where((v,i)=>v<=joints[p][i]).Any())throw new InvalidDataException("Unordered conclusion joint context.");
                }
                double voltage=Convert.ToDouble(GetValue(c,"OperatingVoltageKv"),CultureInfo.InvariantCulture);
                if(Double.IsNaN(voltage)||Double.IsInfinity(voltage)||voltage<0)throw new InvalidDataException("Invalid saved U0.");
                var savedRegions=GetValue(c,"Regions") as object[];
                if(savedRegions==null||savedRegions.Length==0||savedRegions.Length>100000)throw new InvalidDataException("Missing saved conclusion regions.");
                var regions=new double[savedRegions.Length,3];
                for(int i=0;i<savedRegions.Length;i++)
                {
                    var region=savedRegions[i] as Dictionary<string,object>;
                    if(region==null||!region.ContainsKey("PhaseMask")||!region.ContainsKey("PositionM")||!region.ContainsKey("ActionCode"))throw new InvalidDataException("Invalid saved conclusion region.");
                    double phase=Convert.ToDouble(GetValue(region,"PhaseMask"),CultureInfo.InvariantCulture),position=Convert.ToDouble(GetValue(region,"PositionM"),CultureInfo.InvariantCulture),action=Convert.ToDouble(GetValue(region,"ActionCode"),CultureInfo.InvariantCulture);
                    if(Double.IsNaN(phase)||Double.IsInfinity(phase)||phase<1||phase>7||phase!=Math.Truncate(phase)||Double.IsNaN(position)||Double.IsInfinity(position)||position<0||Double.IsNaN(action)||Double.IsInfinity(action)||action<0||action>3||action!=Math.Truncate(action))throw new InvalidDataException("Invalid saved conclusion region values.");
                    regions[i,0]=phase;regions[i,1]=position;regions[i,2]=action;
                }
                package.ConclusionInput=input;package.ConclusionJoints=joints;package.ConclusionOperatingVoltage=voltage;package.ConclusionRegions=regions;
            }
            catch(Exception ex){if(IsFatal(ex))throw;package.HasSavedConclusionSnapshot=false;package.ConclusionText=String.Empty;warnings.Add("Evidence V3 conclusion input unavailable: "+ex.Message);}
        }

        private ReportAnalysisPackage LoadLegacy(string input)
        {
            string dataPath;
            string xmlPath;
            ResolveLegacyPair(input, out dataPath, out xmlPath);
            if (!File.Exists(dataPath)) throw new FileNotFoundException("Legacy analysis .data file is missing.", dataPath);
            if (!File.Exists(xmlPath)) throw new FileNotFoundException("Legacy analysis LabVIEW XML .bin companion is missing.", xmlPath);
            EnsureReadable(dataPath);

            var warnings = new List<string>();
            XmlDocument document = ReadLabViewXml(xmlPath);
            string stem = Path.GetFileNameWithoutExtension(dataPath);
            var match = LegacyName.Match(Path.GetFileName(dataPath));
            string created = string.Empty;
            if (match.Success)
            {
                DateTime parsed;
                if (DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd-HH'mm'ss.ff", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed)) created = parsed.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            }

            var package = new ReportAnalysisPackage
            {
                Kind = AnalysisPackageKind.LegacyLabView,
                SourcePath = Path.GetFullPath(dataPath),
                LegacyDataPath = Path.GetFullPath(dataPath),
                CompanionPath = Path.GetFullPath(xmlPath),
                PackageDirectory = TrimDirectory(Path.GetDirectoryName(Path.GetFullPath(dataPath))),
                AnalysisId = stem,
                AnalysisName = stem,
                CreatedUtc = created,
                ConclusionText = ReadLegacyNamedText(document, ConclusionKeys)
            };

            string physicalProject = DetermineProjectRoot(dataPath);
            string embeddedProject = ReadLegacyNamedText(document, ProjectKeys);
            package.ProjectPath = SelectProjectPath(physicalProject, embeddedProject, package.PackageDirectory, warnings);
            try { package.SetLocationMatrix(ReadLegacyLocationMatrix(document)); }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                warnings.Add("Legacy location matrix could not be read: " + ex.Message);
            }
            package.SetReportImages(ReadLegacyImages(document, package.PackageDirectory, package.ProjectPath, warnings));
            if (!package.HasLocationMatrix) warnings.Add("The legacy companion did not contain a location-result parameter matrix.");
            package.SetWarnings(warnings);
            return package;
        }

        private static IEnumerable<ReportImageInfo> ReadNamedCharts(string directory, Dictionary<string, object> manifest,
            HashSet<int> selected, bool selectionAvailable, List<string> warnings, double[,] matrix,
            Dictionary<string, object> session)
        {
            object chartValue = GetValue(manifest, "Charts", "charts", "ReportCharts", "report_charts");
            object[] charts = chartValue as object[];
            if (charts == null) return new ReportImageInfo[0];
            var result = new List<ReportImageInfo>();
            Dictionary<int, string> pdmapNames = BuildPdmapNames(matrix, session);
            var regionIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var sequences = new HashSet<int>();
            long totalPngBytes = 0;
            for (int index = 0; index < charts.Length; index++)
            {
                var item = charts[index] as Dictionary<string, object>;
                if (item == null) continue;
                int sequence = GetInt(item, index + 1, "Sequence", "sequence", "Order", "order");
                while (sequence < 1 || !sequences.Add(sequence)) sequence++;
                string kind = GetString(item, "Kind", "kind", "ChartKind", "chart_kind", "type");
                string title = GetString(item, "Title", "title", "Name", "name");
                if (string.Equals(kind, "PDmap", StringComparison.OrdinalIgnoreCase))
                {
                    object[] rows = GetValue(item, "Rows", "rows") as object[];
                    int row;
                    string renamed;
                    if (rows != null && rows.Length > 0 && TryInt(rows[0], out row) && pdmapNames.TryGetValue(row, out renamed))
                    {
                        int suffix = title.IndexOf('·');
                        title = renamed + (suffix >= 0 ? " " + title.Substring(suffix).Trim() : string.Empty);
                    }
                }
                string region = GetString(item, "Region", "region", "RegionSummary", "region_summary");
                if (!string.IsNullOrWhiteSpace(region))
                {
                    int regionId;
                    if (!regionIds.TryGetValue(region, out regionId))
                    {
                        regionId = regionIds.Count + 1;
                        regionIds.Add(region, regionId);
                    }
                    string marker = "R" + regionId.ToString("00", CultureInfo.InvariantCulture);
                    title = marker + " · " + title;
                    region = marker + " · " + region;
                }
                bool inlineIncluded = GetBool(item, false, "IncludedInReport", "includedInReport", "included_in_report", "Selected", "selected");
                bool included = selectionAvailable ? selected.Contains(sequence) : inlineIncluded;
                string relative = GetString(item, "Image", "image", "ImagePath", "imagePath", "png", "png_path");
                string imagePath = ResolvePackageChild(directory, relative);
                byte[] png = null;
                if (included && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    try
                    {
                        png = ReadPng(imagePath);
                        totalPngBytes += png.LongLength;
                        if (totalPngBytes > MaxTotalPngBytes)
                            throw new InvalidDataException("Selected report PNG files exceed the total read limit.");
                    }
                    catch (Exception ex)
                    {
                        if (IsFatal(ex)) throw;
                        if (png != null) totalPngBytes -= png.LongLength;
                        png = null;
                        warnings.Add("Report image could not be read (sequence " + sequence + "): " + ex.Message);
                    }
                }
                else if (included && !string.IsNullOrEmpty(relative))
                    warnings.Add("Report image is missing (sequence " + sequence + "): " + relative);

                if (included && png == null)
                {
                    string base64 = GetString(item, "PngBase64", "pngBase64", "png_base64");
                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        try
                        {
                            byte[] decoded = Convert.FromBase64String(base64);
                            if (decoded.LongLength > MaxPngBytes || !IsPng(decoded)) throw new InvalidDataException("Invalid inline PNG.");
                            totalPngBytes += decoded.LongLength;
                            if (totalPngBytes > MaxTotalPngBytes)
                            {
                                totalPngBytes -= decoded.LongLength;
                                throw new InvalidDataException("Selected report PNG files exceed the total read limit.");
                            }
                            png = decoded;
                        }
                        catch (Exception ex)
                        {
                            if (IsFatal(ex)) throw;
                            warnings.Add("Inline report PNG could not be read (sequence " + sequence + "): " + ex.Message);
                        }
                    }
                }

                var image = new ReportImageInfo
                {
                    Sequence = sequence,
                    ChartKind = kind,
                    ChartMode = GetInt(item, -1, "Mode", "mode", "DisplayMode", "displayMode"),
                    Category = MapCategory(kind, title),
                    Title = title,
                    RegionSummary = region,
                    ImagePath = imagePath ?? string.Empty,
                    CreatedUtc = GetString(item, "CreatedUtc", "createdUtc", "created_utc"),
                    LanguageIndex = GetInt(item, 0, "Language", "language", "LanguageIndex", "language_index"),
                    Width = GetInt(item, 0, "Width", "width"),
                    Height = GetInt(item, 0, "Height", "height"),
                    IncludedInReport = included&&!GetBool(item,false,"AcquisitionReferenceOnly"),
                    AcquisitionReferenceOnly = GetBool(item,false,"AcquisitionReferenceOnly"),
                    IsHistoricalOrStale = GetBool(item, false, "Historical", "historical", "IsHistoricalOrStale")||GetBool(item,false,"AcquisitionReferenceOnly"),
                    StatusReason = GetBool(item,false,"AcquisitionReferenceOnly")?"Acquisition reference only; not a valid report attachment.":GetString(item, "StatusReason", "statusReason", "status_reason")
                };
                image.SetPngData(png);
                result.Add(image);
            }
            return result;
        }

        private static Dictionary<int, string> BuildPdmapNames(double[,] matrix, Dictionary<string, object> session)
        {
            var output = new Dictionary<int, string>();
            if (matrix == null || matrix.GetLength(1) < 10 || session == null) return output;
            object[] files = GetValue(session, "files", "Files") as object[];
            if (files == null) return output;
            var byFile = new Dictionary<int, List<KeyValuePair<int, int>>>();
            for (int row = 0; row < matrix.GetLength(0); row++)
            {
                double fileValue = matrix[row, 1], mapValue = matrix[row, 9];
                if (double.IsNaN(fileValue) || double.IsInfinity(fileValue) ||
                    double.IsNaN(mapValue) || double.IsInfinity(mapValue) ||
                    fileValue != Math.Truncate(fileValue) || mapValue != Math.Truncate(mapValue) ||
                    fileValue < 0 || fileValue >= files.Length || mapValue < 0 || mapValue > int.MaxValue) continue;
                int file = (int)fileValue;
                if (!byFile.ContainsKey(file)) byFile[file] = new List<KeyValuePair<int, int>>();
                byFile[file].Add(new KeyValuePair<int, int>(row, (int)mapValue));
            }
            foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> group in byFile)
            {
                object[] fileFields = files[group.Key] as object[];
                if (fileFields == null || fileFields.Length == 0) continue;
                string fileName = Convert.ToString(fileFields[0], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                string voltageName = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrWhiteSpace(voltageName)) continue;
                int ordinal = 0;
                foreach (IGrouping<int, KeyValuePair<int, int>> map in group.Value.GroupBy(item => item.Value).OrderBy(item => item.Key))
                {
                    ordinal++;
                    foreach (KeyValuePair<int, int> item in map)
                        output[item.Key] = voltageName + "_PDmap-" + ordinal.ToString(CultureInfo.InvariantCulture);
                }
            }
            return output;
        }

        private static HashSet<int> ReadSelection(string directory, Dictionary<string, object> manifest,
            List<string> warnings, out bool available)
        {
            available = false;
            Dictionary<string, object> selection = null;
            string path = Path.Combine(directory, "selection.json");
            if (File.Exists(path))
            {
                try
                {
                    selection = ReadJsonObject(path);
                    string schema = GetString(selection, "Schema", "schema");
                    if (!string.IsNullOrEmpty(schema) && !string.Equals(schema, "HVX.ReportSelection.1", StringComparison.Ordinal))
                        throw new InvalidDataException("Unsupported report-selection schema: " + schema);
                    available = true;
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    warnings.Add("selection.json could not be read; no chart is implicitly selected: " + ex.Message);
                }
            }
            if (selection == null)
            {
                object inline = GetValue(manifest, "Selection", "selection", "ReportSelection", "report_selection");
                selection = inline as Dictionary<string, object>;
                available = selection != null;
            }
            var output = new HashSet<int>();
            if (selection == null)
            {
                warnings.Add("selection.json is absent; no chart is implicitly selected for the report.");
                return output;
            }
            object values = GetValue(selection, "IncludedSequences", "includedSequences", "included_sequences", "Sequences", "sequences");
            object[] array = values as object[];
            if (array == null) return output;
            foreach (object value in array)
            {
                int sequence;
                if (TryInt(value, out sequence) && sequence > 0) output.Add(sequence);
            }
            return output;
        }

        private static IEnumerable<ReportImageInfo> ReadLegacyImages(XmlDocument document, string packageDirectory,
            string projectPath, List<string> warnings)
        {
            var output = new List<ReportImageInfo>();
            int sequence = 0;
            foreach (XmlNode nameNode in document.SelectNodes("//*[local-name()='Name']"))
            {
                string name = nameNode.InnerText ?? string.Empty;
                string lower = name.ToLowerInvariant();
                if (name.IndexOf("报告", StringComparison.Ordinal) < 0 && name.IndexOf("图片", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("report", StringComparison.Ordinal) < 0 && lower.IndexOf("image", StringComparison.Ordinal) < 0) continue;
                XmlElement parent = nameNode.ParentNode as XmlElement;
                XmlNode value = DirectChild(parent, "Val");
                if (value == null || !value.InnerText.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string path = ResolveLegacyImage(value.InnerText, packageDirectory, projectPath);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                byte[] png = null;
                try { png = ReadPng(path); }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    warnings.Add("Legacy report image could not be read: " + ex.Message);
                }
                var image = new ReportImageInfo
                {
                    Sequence = ++sequence,
                    ChartKind = name,
                    Category = MapCategory(name, name),
                    Title = name,
                    ImagePath = path,
                    IncludedInReport = true
                };
                image.SetPngData(png);
                output.Add(image);
            }
            return output;
        }

        private static double[,] ReadHvxMatrix(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < 16 || Encoding.ASCII.GetString(reader.ReadBytes(8)) != "HVXMAT01")
                    throw new InvalidDataException("Invalid HVX matrix signature.");
                int rows = reader.ReadInt32();
                int columns = reader.ReadInt32();
                long cells = (long)rows * columns;
                if (rows < 0 || columns < 0 || rows > 1000000 || columns > 4096 || cells < 0 || cells > MaxMatrixCells ||
                    stream.Length != 16L + cells * 8L) throw new InvalidDataException("Invalid HVX matrix dimensions or byte length.");
                var result = new double[rows, columns];
                for (int row = 0; row < rows; row++)
                    for (int column = 0; column < columns; column++) result[row, column] = reader.ReadDouble();
                return result;
            }
        }

        private static double[,] ReadJsonMatrix(object value)
        {
            var wrapper = value as Dictionary<string, object>;
            if (wrapper != null)
            {
                object data = GetValue(wrapper, "Data", "data", "Values", "values");
                int rows = GetInt(wrapper, -1, "Rows", "rows", "RowCount", "row_count");
                int columns = GetInt(wrapper, -1, "Columns", "columns", "ColumnCount", "column_count", "Cols", "cols");
                object[] flat = data as object[];
                if (flat != null && rows >= 0 && columns >= 0 && (long)rows * columns == flat.LongLength)
                {
                    if ((long)rows * columns > MaxMatrixCells) throw new InvalidDataException("Embedded matrix exceeds its cell limit.");
                    var matrix = new double[rows, columns];
                    for (int index = 0; index < flat.Length; index++) matrix[index / columns, index % columns] = JsonDouble(flat[index]);
                    return matrix;
                }
                value = data;
            }

            object[] rowValues = value as object[];
            if (rowValues == null) throw new InvalidDataException("Embedded matrix must be a rectangular JSON array.");
            if (rowValues.Length == 0) return new double[0, 0];
            object[] first = rowValues[0] as object[];
            if (first == null) throw new InvalidDataException("Embedded matrix rows must be arrays.");
            int columnCount = first.Length;
            if ((long)rowValues.Length * columnCount > MaxMatrixCells) throw new InvalidDataException("Embedded matrix exceeds its cell limit.");
            var result = new double[rowValues.Length, columnCount];
            for (int row = 0; row < rowValues.Length; row++)
            {
                object[] current = rowValues[row] as object[];
                if (current == null || current.Length != columnCount) throw new InvalidDataException("Embedded matrix is not rectangular.");
                for (int column = 0; column < columnCount; column++) result[row, column] = JsonDouble(current[column]);
            }
            return result;
        }

        private static double[,] ReadLegacyLocationMatrix(XmlDocument document)
        {
            XmlElement target = null;
            foreach (XmlElement array in document.SelectNodes("//*[local-name()='Array']"))
            {
                XmlNode name = DirectChild(array, "Name");
                if (name == null) continue;
                string value = name.InnerText.Trim();
                if (string.Equals(value, "定位结果参数矩阵", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "LocationMatrix", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "Location Result Parameter Matrix", StringComparison.OrdinalIgnoreCase))
                {
                    target = array;
                    break;
                }
            }
            if (target == null) return new double[0, 0];
            var dimensions = new List<int>();
            foreach (XmlNode child in target.ChildNodes)
            {
                if (child.LocalName == "Dimsize")
                {
                    int dimension;
                    if (!int.TryParse(child.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out dimension) || dimension < 0)
                        throw new InvalidDataException("Invalid legacy matrix dimension.");
                    dimensions.Add(dimension);
                }
            }
            if (dimensions.Count != 2) throw new InvalidDataException("Legacy location matrix must have two dimensions.");
            int rows = dimensions[0];
            int columns = dimensions[1];
            long cells = (long)rows * columns;
            if (rows > 1000000 || columns > 4096 || cells < 0 || cells > MaxMatrixCells)
                throw new InvalidDataException("Legacy location matrix dimensions exceed their limits.");
            var matrix = new double[rows, columns];
            int offset = 0;
            foreach (XmlNode child in target.ChildNodes)
            {
                if (!IsNumericLabViewElement(child.LocalName)) continue;
                if (offset >= cells) throw new InvalidDataException("Legacy location matrix contains too many values.");
                XmlNode val = DirectChild(child as XmlElement, "Val");
                double number;
                if (val == null || !double.TryParse(val.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new InvalidDataException("Invalid numeric value in the legacy matrix.");
                matrix[offset / columns, offset % columns] = number;
                offset++;
            }
            if (offset != cells) throw new InvalidDataException("Legacy location matrix dimensions do not match its values.");
            return matrix;
        }

        private static XmlDocument ReadLabViewXml(string path)
        {
            byte[] bytes = ReadLimited(path, MaxXmlBytes);
            Exception first = null;
            foreach (Encoding encoding in XmlEncodings(bytes))
            {
                try
                {
                    string text = encoding.GetString(bytes);
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaxXmlBytes,
                        IgnoreComments = true
                    };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var stringReader = new StringReader(text))
                    using (XmlReader reader = XmlReader.Create(stringReader, settings)) document.Load(reader);
                    if (document.DocumentElement == null || document.DocumentElement.LocalName != "LVData")
                        throw new InvalidDataException("The companion is not LabVIEW LVData XML.");
                    return document;
                }
                catch (Exception ex)
                {
                    if (IsFatal(ex)) throw;
                    if (first == null) first = ex;
                }
            }
            throw new InvalidDataException("The LabVIEW XML companion could not be decoded.", first);
        }

        private static IEnumerable<Encoding> XmlEncodings(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) yield return new UnicodeEncoding(false, true, true);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) yield return new UnicodeEncoding(true, true, true);
            else
            {
                yield return new UTF8Encoding(false, true);
                yield return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }

        private static string ReadLegacyNamedText(XmlDocument document, string[] names)
        {
            foreach (XmlNode node in document.SelectNodes("//*[local-name()='Name']"))
            {
                if (!Matches(node.InnerText, names)) continue;
                XmlNode value = DirectChild(node.ParentNode as XmlElement, "Val");
                if (value != null && !string.IsNullOrWhiteSpace(value.InnerText)) return value.InnerText.Trim();
            }
            return string.Empty;
        }

        private static string FindNamedMatrixPath(string directory, Dictionary<string, object> manifest)
        {
            string direct = Path.Combine(directory, "parameters.bin");
            if (File.Exists(direct)) return direct;
            object pathValue = FindValueRecursive(manifest, MatrixFileKeys, 0);
            string relative = pathValue as string;
            string resolved = ResolvePackageChild(directory, relative);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return resolved;
            object[] files = GetValue(manifest, "Files", "files") as object[];
            if (files != null)
                foreach (object value in files)
                {
                    var entry = value as Dictionary<string, object>;
                    string candidate = GetString(entry, "Path", "path", "File", "file");
                    string name = Path.GetFileName(candidate ?? string.Empty);
                    if (!string.Equals(name, "parameters.bin", StringComparison.OrdinalIgnoreCase) &&
                        name.IndexOf("parameter", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("location", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    resolved = ResolvePackageChild(directory, candidate);
                    if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved)) return resolved;
                }
            return string.Empty;
        }

        private static void ResolveLegacyPair(string input, out string dataPath, out string xmlPath)
        {
            if (Directory.Exists(input))
            {
                string[] candidates = Directory.GetFiles(input, "Analysis_*.data", SearchOption.TopDirectoryOnly)
                    .Where(path => LegacyName.IsMatch(Path.GetFileName(path))).OrderByDescending(GetLastWriteUtcSafe)
                    .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length == 0) throw new FileNotFoundException("No legacy Analysis_*.data file was found in the directory.", input);
                dataPath = candidates[0];
                xmlPath = Path.ChangeExtension(dataPath, ".bin");
                return;
            }
            string extension = Path.GetExtension(input);
            if (!LegacyName.IsMatch(Path.GetFileName(input))) throw new InvalidDataException("The file is not a recognized legacy Analysis_*.data/.bin file.");
            if (string.Equals(extension, ".data", StringComparison.OrdinalIgnoreCase))
            {
                dataPath = input;
                xmlPath = Path.ChangeExtension(input, ".bin");
            }
            else
            {
                xmlPath = input;
                dataPath = Path.ChangeExtension(input, ".data");
            }
        }

        private static Dictionary<string, object> ReadJsonObject(string path)
        {
            byte[] bytes = ReadLimited(path, MaxJsonBytes);
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var serializer = new JavaScriptSerializer { MaxJsonLength = (int)MaxJsonBytes, RecursionLimit = 64 };
            var result = serializer.DeserializeObject(text) as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("JSON root must be an object.");
            return result;
        }

        private static byte[] ReadLimited(string path, long maximumBytes)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("File not found.", path);
            if (info.Length < 0 || info.Length > maximumBytes) throw new InvalidDataException("File exceeds the read limit: " + path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > int.MaxValue) throw new InvalidDataException("File is too large to buffer.");
                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("Unexpected end of file: " + path);
                    offset += read;
                }
                return bytes;
            }
        }

        private static byte[] ReadPng(string path)
        {
            byte[] bytes = ReadLimited(path, MaxPngBytes);
            if (!IsPng(bytes)) throw new InvalidDataException("File is not a PNG image: " + path);
            return bytes;
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 &&
                bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
        }

        private static string ResolvePackageChild(string directory, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return string.Empty;
            try
            {
                string root = TrimDirectory(Path.GetFullPath(directory));
                string rootPrefix = root + Path.DirectorySeparatorChar;
                string candidate = Path.IsPathRooted(relative) ? Path.GetFullPath(relative) :
                    Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) &&
                    !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                return candidate;
            }
            catch { return string.Empty; }
        }

        private static string ResolveLegacyImage(string value, string packageDirectory, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                string candidate = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(packageDirectory, value));
                string packagePrefix = TrimDirectory(packageDirectory) + Path.DirectorySeparatorChar;
                string projectPrefix = string.IsNullOrEmpty(projectPath) ? string.Empty : TrimDirectory(projectPath) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(projectPrefix) && candidate.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))) return candidate;
            }
            catch { }
            return string.Empty;
        }

        private static string SelectProjectPath(string physical, string embedded, string packageDirectory, List<string> warnings)
        {
            string analysisDerived = IsBelowAnalysisReport(packageDirectory) ? physical : string.Empty;
            if (!string.IsNullOrEmpty(analysisDerived))
            {
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    try
                    {
                        string normalized = TrimDirectory(Path.GetFullPath(embedded));
                        if (!string.Equals(normalized, analysisDerived, StringComparison.OrdinalIgnoreCase))
                            warnings.Add("Embedded project path differs from the package location; the Analysis report parent was used.");
                    }
                    catch { warnings.Add("Embedded project path is invalid; the Analysis report parent was used."); }
                }
                return analysisDerived;
            }
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                try { return TrimDirectory(Path.GetFullPath(embedded)); }
                catch { warnings.Add("Embedded project path is invalid."); }
            }
            return physical ?? string.Empty;
        }

        private static bool IsBelowAnalysisReport(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                for (DirectoryInfo cursor = new DirectoryInfo(Path.GetFullPath(path)); cursor != null; cursor = cursor.Parent)
                    if (string.Equals(cursor.Name, "Analysis report", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static string FindStringRecursive(object value, string[] keys, int depth)
        {
            object found = FindValueRecursive(value, keys, depth);
            return found as string ?? string.Empty;
        }

        private static object FindValueRecursive(object value, string[] keys, int depth)
        {
            if (value == null || depth > 12) return null;
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                foreach (var item in dictionary)
                    if (Matches(item.Key, keys) && item.Value != null) return item.Value;
                foreach (var item in dictionary)
                {
                    object nested = FindValueRecursive(item.Value, keys, depth + 1);
                    if (nested != null) return nested;
                }
            }
            var array = value as object[];
            if (array != null)
                foreach (object item in array)
                {
                    object nested = FindValueRecursive(item, keys, depth + 1);
                    if (nested != null) return nested;
                }
            return null;
        }

        private static object GetValue(Dictionary<string, object> dictionary, params string[] names)
        {
            if (dictionary == null) return null;
            foreach (var item in dictionary)
                if (Matches(item.Key, names)) return item.Value;
            return null;
        }

        private static string GetString(Dictionary<string, object> dictionary, params string[] names)
        {
            object value = GetValue(dictionary, names);
            return value as string ?? string.Empty;
        }

        private static int GetInt(Dictionary<string, object> dictionary, int fallback, params string[] names)
        {
            int output;
            return TryInt(GetValue(dictionary, names), out output) ? output : fallback;
        }

        private static bool GetBool(Dictionary<string, object> dictionary, bool fallback, params string[] names)
        {
            object value = GetValue(dictionary, names);
            if (value is bool) return (bool)value;
            string text = value as string;
            bool parsed;
            if (text != null && bool.TryParse(text, out parsed)) return parsed;
            int number;
            return TryInt(value, out number) ? number != 0 : fallback;
        }

        private static bool TryInt(object value, out int output)
        {
            output = 0;
            if (value == null) return false;
            try
            {
                long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (number < int.MinValue || number > int.MaxValue) return false;
                output = (int)number;
                return true;
            }
            catch { return false; }
        }

        private static double JsonDouble(object value)
        {
            if (value == null) throw new InvalidDataException("Matrix value is null.");
            double number;
            try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) { throw new InvalidDataException("Matrix value is not numeric.", ex); }
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Matrix value must be finite.");
            return number;
        }

        private static bool Matches(string value, string[] candidates)
        {
            if (value == null || candidates == null) return false;
            foreach (string candidate in candidates)
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static XmlNode DirectChild(XmlElement element, string localName)
        {
            if (element == null) return null;
            foreach (XmlNode child in element.ChildNodes)
                if (string.Equals(child.LocalName, localName, StringComparison.Ordinal)) return child;
            return null;
        }

        private static bool IsNumericLabViewElement(string localName)
        {
            return localName == "DBL" || localName == "SGL" || localName == "I64" || localName == "I32" ||
                localName == "I16" || localName == "I8" || localName == "U64" || localName == "U32" ||
                localName == "U16" || localName == "U8";
        }

        private static ReportImageCategory MapCategory(string kind, string title)
        {
            string value = ((kind ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();
            if (value.Contains("pdmap") || value.Contains("pd map")) return ReportImageCategory.PdMap;
            if (value.Contains("q-l") || value.Contains("l-q") || value.Contains("charge-distance") || value.Contains("放电量"))
                return ReportImageCategory.LocationChargeDistance;
            if (value.Contains("n-l") || value.Contains("l-n") || value.Contains("count-distance") || value.Contains("放电次数"))
                return ReportImageCategory.LocationCountDistance;
            if (value.Contains("calibration") || value.Contains("校准")) return ReportImageCategory.Calibration;
            if (value.Contains("trend") || value.Contains("趋势")) return ReportImageCategory.TrendStatistics;
            if (value.Contains("region") || value.Contains("区域") || value.Contains("spectrum")) return ReportImageCategory.RegionStatistics;
            if (value.Contains("test") || value.Contains("测试")) return ReportImageCategory.TestData;
            return ReportImageCategory.AnalysisAttachment;
        }

        private static void EnsureReadable(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length < 1) throw new InvalidDataException("Analysis data file is empty.");
                stream.ReadByte();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(root);
            while (pending.Count > 0 && visited.Count < 10000)
            {
                string directory = pending.Pop();
                if (!visited.Add(directory)) continue;
                string[] files;
                try { files = Directory.GetFiles(directory); }
                catch { continue; }
                foreach (string file in files) yield return file;
                string[] children;
                try { children = Directory.GetDirectories(directory); }
                catch { continue; }
                foreach (string child in children)
                {
                    try
                    {
                        if (!Path.GetFileName(child).StartsWith(".",StringComparison.Ordinal) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    catch { }
                }
            }
        }

        private static DateTime GetLastWriteUtcSafe(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static string TrimDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return root;
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) && !string.IsNullOrEmpty(root) ? root : trimmed;
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException ||
                error is AppDomainUnloadedException || error is BadImageFormatException;
        }

        private static readonly string[] ProjectKeys =
        {
            "ProjectPath", "projectPath", "project_path", "项目路径"
        };

        private static readonly string[] ConclusionKeys =
        {
            "ConclusionText", "conclusionText", "conclusion_text", "Conclusion", "conclusion",
            "SummaryText", "summaryText", "summary_text", "OriginalSummaryText", "结论", "结论文本", "分析结论"
        };

        private static readonly string[] MatrixKeys =
        {
            "LocationMatrix", "locationMatrix", "location_matrix", "ParameterMatrix", "parameterMatrix",
            "locationResultMatrix", "定位结果参数矩阵"
        };

        private static readonly string[] MatrixFileKeys =
        {
            "LocationMatrixFile", "locationMatrixFile", "location_matrix_file", "ParameterMatrixFile",
            "parameterMatrixFile", "parameters_file"
        };
    }
}
