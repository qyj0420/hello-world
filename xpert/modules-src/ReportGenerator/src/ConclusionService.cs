using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HVX.ReportGenerator
{
    /// <summary>Bridge to the same source-embedded Evidence V3 core as the analysis page.</summary>
    public sealed class ConclusionService
    {
        // Kept for binary/source API compatibility; never loads an external DLL.
        public string RuntimeDllPath { get; set; }
        public string LastError { get; private set; }
        public string LastDiagnostic { get; private set; }
        public Action<string> DiagnosticSink { get; set; }

        public ConclusionService()
        {
            RuntimeDllPath = string.Empty;
            LastError = string.Empty;
            LastDiagnostic = string.Empty;
        }

        public bool TryGenerate(double[,] locationMatrix, double[] jointsA, double[] jointsB, double[] jointsC,
            double operatingPhaseVoltageKv, bool englishOutput, out string conclusion)
        {
            return TryGenerate(locationMatrix, jointsA, jointsB, jointsC, operatingPhaseVoltageKv, 2.0, englishOutput, out conclusion);
        }

        public bool TryGenerate(double[,] locationMatrix, double[] jointsA, double[] jointsB, double[] jointsC,
            double operatingPhaseVoltageKv, double cableServiceYears, bool englishOutput, out string conclusion)
        {
            return TryGenerate(locationMatrix, jointsA, jointsB, jointsC, operatingPhaseVoltageKv,
                cableServiceYears, englishOutput, 1, out conclusion);
        }

        public bool TryGenerate(double[,] locationMatrix, double[] jointsA, double[] jointsB, double[] jointsC,
            double operatingPhaseVoltageKv, double cableServiceYears, bool englishOutput,
            int conclusionAudienceType, out string conclusion)
        {
            conclusion = string.Empty;
            LastError = string.Empty;
            LastDiagnostic = string.Empty;
            try
            {
                if (locationMatrix == null || locationMatrix.GetLength(0) == 0 || locationMatrix.GetLength(1) < 36)
                {
                    RecordDiagnostic(new ArgumentException("A non-empty location matrix with at least 36 columns is required."));
                    LastError = englishOutput ? "No valid location results are available. Analyse the data and save the results before generating a conclusion."
                        : "没有可用于生成结论的有效定位结果。请先完成数据分析并保存结果。";
                    return false;
                }
                if (double.IsNaN(operatingPhaseVoltageKv) || double.IsInfinity(operatingPhaseVoltageKv) || operatingPhaseVoltageKv < 0.0)
                {
                    RecordDiagnostic(new ArgumentOutOfRangeException("operatingPhaseVoltageKv"));
                    LastError = englishOutput ? "The operating phase voltage is invalid. Check the project voltage setting before generating a conclusion."
                        : "运行相电压无效，请核对项目电压设置后重新生成结论。";
                    return false;
                }

                var result = HVX.Location.EmbeddedConclusions.AnalyzeForReport(locationMatrix,
                    jointsA, jointsB, jointsC, operatingPhaseVoltageKv, cableServiceYears,
                    englishOutput, conclusionAudienceType);
                if (!result.Ok) throw new InvalidOperationException(result.Error);
                conclusion = result.SummaryText;
                if (string.IsNullOrWhiteSpace(conclusion)) throw new InvalidOperationException("The conclusion engine returned an empty result.");
                conclusion = NormalizeU0Ratios(conclusion.Trim());
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (IsFatal(ex.InnerException ?? ex)) throw;
                RecordDiagnostic(ex.InnerException ?? ex);
                LastError = GenerationFailureText(englishOutput);
                conclusion = string.Empty;
                return false;
            }
            catch (Exception ex)
            {
                if (IsFatal(ex)) throw;
                RecordDiagnostic(ex);
                LastError = GenerationFailureText(englishOutput);
                conclusion = string.Empty;
                return false;
            }
        }

        internal bool TryRenderSavedPresentation(ReportAnalysisPackage package, bool english, int audience, out string text)
        {
            text=string.Empty;LastError=string.Empty;LastDiagnostic=string.Empty;
            try
            {
                if(package==null)throw new ArgumentNullException("package");
                // Saved Api also contains semicolon-separated host provenance.
                // Only the exact engine-version token permits presentation-only
                // regeneration; never silently re-assess historical conclusions.
                string savedApi=(package.ConclusionApi??string.Empty).Split(';')[0].Trim();
                if(!string.Equals(savedApi,HVX.Location.LocatorEngine.ApiVersion,StringComparison.Ordinal))
                    throw new InvalidOperationException(english
                        ? "The saved conclusion was generated by a different or unknown algorithm version."
                        : "已保存结论的算法版本不同或未记录。");
                var joints=package.ConclusionJoints;
                var result=HVX.Location.EmbeddedConclusions.AnalyzeForReport(package.ConclusionInput,joints[0],joints[1],joints[2],package.ConclusionOperatingVoltage,0,english,audience);
                if(!result.Ok)throw new InvalidOperationException(result.Error);
                var saved=package.ConclusionRegions;var actual=result.LocationMatrix;
                if(saved==null||result.LocationRows!=saved.GetLength(0)||actual.Length!=result.LocationRows*result.LocationColumns)
                    throw new InvalidOperationException("Saved/current conclusion regions differ.");
                for(int r=0;r<saved.GetLength(0);r++)
                    if(actual[r*result.LocationColumns]!=saved[r,0]||Math.Abs(actual[r*result.LocationColumns+1]-saved[r,1])>0.000001||actual[r*result.LocationColumns+8]!=saved[r,2])
                        throw new InvalidOperationException("Saved/current conclusion phase, position or grade differs.");
                text=NormalizeU0Ratios(result.SummaryText);
                return !string.IsNullOrWhiteSpace(text);
            }
            catch(Exception ex)
            {
                if (IsFatal(ex)) throw;
                RecordDiagnostic(ex);
                LastError=english?"The saved conclusion cannot be reformatted without changing its evidence result. Its original text is retained; regenerate and save in Data analysis if required.":"已保存结论与当前算法的证据结果不一致，不能仅按文字切换改写等级。已保留原文；如需更新，请在数据分析重新生成并保存。";
                return false;
            }
        }

        static string GenerationFailureText(bool english)
        {
            return english ? "The conclusion could not be generated. Check the analysis results and project settings, then try again. Existing results have not been changed."
                : "无法生成结论。请核对分析结果和项目参数后重试，原有结果未更改。";
        }
        static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException || error is System.Threading.ThreadAbortException;
        }
        void RecordDiagnostic(Exception error)
        {
            RecordDiagnosticMessage(error.ToString());
        }
        internal void RecordDiagnosticMessage(string message)
        {
            LastDiagnostic = message ?? string.Empty;
            try { if (DiagnosticSink != null) DiagnosticSink(LastDiagnostic); }
            catch (Exception loggingError) { if (IsFatal(loggingError)) throw; }
        }

        static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(item => string.Equals(item.Name, name, StringComparison.Ordinal))
                .FirstOrDefault(item => item.GetParameters().Length == parameterCount);
        }

        static string NormalizeU0Ratios(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            return Regex.Replace(text, @"(?<![\d.])(\d+(?:\.\d+)?)\s*U0", delegate(Match match)
            {
                double value;
                return double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value)
                    ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "U0"
                    : match.Value;
            }, RegexOptions.IgnoreCase);
        }

        internal static string FormatForReport(string text, string systemName, bool chinese)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            string formatted = text;
            if (!string.IsNullOrWhiteSpace(systemName))
            {
                string name = systemName.Trim();
                formatted = Regex.Replace(formatted,
                    @"(?m)^(?<indent>[ \t]*)(?:稳定算法\+多因素融合模式|综合多因素模式)(?=共评估)",
                    match => match.Groups["indent"].Value + name);
                formatted = Regex.Replace(formatted,
                    @"(?m)^(?<indent>[ \t]*)(?:Stable/multi-factor hybrid mode|Multi-factor mode)(?=\s+evaluated)",
                    match => match.Groups["indent"].Value + name, RegexOptions.IgnoreCase);
            }
            if (!chinese)
            {
                formatted = Regex.Replace(formatted, @"\bA/B/C\b", "L1/L2/L3", RegexOptions.IgnoreCase);
                formatted = Regex.Replace(formatted, @"\b([ABC])\s+phase\b", match =>
                    "L" + ("ABC".IndexOf(match.Groups[1].Value.ToUpperInvariant(), StringComparison.Ordinal) + 1) + " phase",
                    RegexOptions.IgnoreCase);
            }
            return formatted;
        }

        public string GetIeeeRecommendation(int languageIndex, string conclusion)
        {
            ReportLanguages.Validate(languageIndex);
            if (ReportLanguages.IsChinese(languageIndex) || ContainsChinese(conclusion)) return string.Empty;
            string[] recommendations = {
                "",
                "Use this result as condition-assessment evidence: verify repeatability, compare it with prior measurements, inspect accessories near repeated locations, and let a qualified engineer set the maintenance or retest interval in accordance with the applicable IEEE cable-diagnostic practice.",
                "Използвайте резултата като доказателство за оценка на състоянието: потвърдете повторяемостта, сравнете с предишни измервания, проверете аксесоарите около повтарящите се места и възложете на квалифициран инженер да определи интервала за поддръжка или повторно изпитване съгласно приложимата практика на IEEE.",
                "Rezultat koristite kao dokaz procjene stanja: provjerite ponovljivost, usporedite ga s ranijim mjerenjima, pregledajte pribor uz ponavljajuća mjesta i prepustite kvalificiranom inženjeru određivanje održavanja ili ponovnog ispitivanja prema primjenjivoj IEEE praksi.",
                "Výsledek použijte jako podklad pro posouzení stavu: ověřte opakovatelnost, porovnejte jej s dřívějšími měřeními, zkontrolujte příslušenství u opakovaných míst a interval údržby či opakované zkoušky stanovte kvalifikovaným technikem podle příslušné praxe IEEE.",
                "Utilisez ce résultat comme élément d’évaluation de l’état : vérifiez la répétabilité, comparez avec les mesures antérieures, inspectez les accessoires proches des positions récurrentes et faites définir l’intervalle de maintenance ou de nouvel essai par un ingénieur qualifié selon la pratique IEEE applicable.",
                "Verwenden Sie das Ergebnis als Zustandsnachweis: Wiederholbarkeit prüfen, mit früheren Messungen vergleichen, Garnituren an wiederkehrenden Positionen untersuchen und Wartungs- oder Wiederholungsprüffristen durch eine qualifizierte Fachkraft nach der anwendbaren IEEE-Praxis festlegen lassen.",
                "Az eredményt állapotértékelési bizonyítékként használja: ellenőrizze az ismételhetőséget, hasonlítsa össze a korábbi mérésekkel, vizsgálja meg a visszatérő helyek körüli szerelvényeket, és az alkalmazandó IEEE gyakorlat szerint képesített mérnök határozza meg a karbantartás vagy ismételt vizsgálat idejét.",
                "Usare il risultato come evidenza per la valutazione dello stato: verificare la ripetibilità, confrontare le misure precedenti, ispezionare gli accessori presso le posizioni ricorrenti e far definire a un tecnico qualificato l’intervallo di manutenzione o riprova secondo la pratica IEEE applicabile.",
                "Wynik należy traktować jako materiał do oceny stanu: potwierdzić powtarzalność, porównać z wcześniejszymi pomiarami, skontrolować osprzęt w pobliżu powtarzających się lokalizacji oraz zlecić wykwalifikowanemu inżynierowi ustalenie terminu konserwacji lub ponownej próby zgodnie z właściwą praktyką IEEE.",
                "Utilizați rezultatul ca dovadă pentru evaluarea stării: verificați repetabilitatea, comparați cu măsurătorile anterioare, inspectați accesoriile din apropierea pozițiilor recurente și solicitați unui inginer calificat să stabilească intervalul de mentenanță sau retestare conform practicii IEEE aplicabile.",
                "Используйте результат как данные для оценки состояния: проверьте повторяемость, сравните с предыдущими измерениями, осмотрите арматуру у повторяющихся мест и поручите квалифицированному инженеру назначить обслуживание или повторное испытание по применимой практике IEEE.",
                "Koristite rezultat kao dokaz za procenu stanja: proverite ponovljivost, uporedite sa ranijim merenjima, pregledajte pribor kod ponavljajućih lokacija i prepustite kvalifikovanom inženjeru da odredi održavanje ili ponovno ispitivanje prema primenljivoj IEEE praksi.",
                "Výsledok použite ako podklad na posúdenie stavu: overte opakovateľnosť, porovnajte ho s predchádzajúcimi meraniami, skontrolujte príslušenstvo pri opakovaných miestach a interval údržby alebo opakovanej skúšky nech určí kvalifikovaný technik podľa príslušnej praxe IEEE.",
                "Rezultat uporabite kot dokaz za oceno stanja: preverite ponovljivost, primerjajte s prejšnjimi meritvami, preglejte pribor pri ponavljajočih se mestih in naj usposobljen inženir določi vzdrževanje ali ponovni preskus skladno z veljavno prakso IEEE.",
                "Use el resultado como evidencia para evaluar el estado: confirme la repetibilidad, compárelo con mediciones anteriores, inspeccione los accesorios próximos a ubicaciones recurrentes y haga que un ingeniero cualificado defina el mantenimiento o la repetición del ensayo conforme a la práctica IEEE aplicable.",
                "Sonucu durum değerlendirme kanıtı olarak kullanın: tekrarlanabilirliği doğrulayın, önceki ölçümlerle karşılaştırın, tekrarlanan konumların yakınındaki aksesuarları inceleyin ve bakım ya da yeniden test aralığını ilgili IEEE uygulamasına göre yetkin bir mühendise belirletin.",
                "Sử dụng kết quả làm bằng chứng đánh giá tình trạng: xác minh tính lặp lại, so sánh với các phép đo trước, kiểm tra phụ kiện gần các vị trí lặp lại và để kỹ sư đủ năng lực xác định chu kỳ bảo trì hoặc thử lại theo thực hành IEEE áp dụng."
            };
            return recommendations[Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(languageIndex)];
        }

        static double[] SafeJoints(double[] values)
        {
            return values == null ? new double[0] : values.Where(IsFinite).OrderBy(value => value).Distinct().ToArray();
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static bool ContainsChinese(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (char value in text) if (value >= '\u4e00' && value <= '\u9fff') return true;
            return false;
        }
    }
}

