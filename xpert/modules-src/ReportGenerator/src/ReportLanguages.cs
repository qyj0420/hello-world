using System;
using System.Collections.Generic;
using System.Globalization;

namespace HVX.ReportGenerator
{
    /// <summary>
    /// Stable language contract shared with the Item, PdReview and DataBrowser controls.
    /// This file owns text only. The host UI must reuse the DataBrowser BrowserTheme
    /// palette instead of introducing language-specific or report-specific colours.
    /// Chinese and English contain every UI key. Other languages translate the primary
    /// tree and command labels and deliberately fall back to English for missing text.
    /// </summary>
    internal static partial class ReportLanguages
    {
        internal static readonly string[] Names = Xiongxin.OWTS.Localization.UiLanguage.Names;

        internal static readonly string[] Codes = Xiongxin.OWTS.Localization.UiLanguage.Codes;

        internal static class UiKeys
        {
            internal const string Title = "Title";
            internal const string ProjectInformation = "ProjectInformation";
            internal const string LocationResults = "LocationResults";
            internal const string AllLocationResults = "AllLocationResults";
            internal const string AtOrBelowU0LocationResults = "AtOrBelowU0LocationResults";
            internal const string ServiceBandLocationResults = "ServiceBandLocationResults";
            internal const string AllPhases = "AllPhases";
            internal const string PhaseA = "PhaseA";
            internal const string PhaseB = "PhaseB";
            internal const string PhaseC = "PhaseC";
            internal const string ChargeLocation = "ChargeLocation";
            internal const string CountLocation = "CountLocation";
            internal const string CalibrationData = "CalibrationData";
            internal const string TestData = "TestData";
            internal const string RegionalStatisticsChart = "RegionalStatisticsChart";
            internal const string PdmapChart = "PdmapChart";
            internal const string TrendStatisticsChart = "TrendStatisticsChart";
            internal const string Conclusion = "Conclusion";
            internal const string SimpleConclusion = "SimpleConclusion";
            internal const string ProfessionalConclusion = "ProfessionalConclusion";
            internal const string GenerateConclusion = "GenerateConclusion";
            internal const string GenerateReport = "GenerateReport";
            internal const string OpenReport = "OpenReport";
            internal const string Clear = "Clear";
            internal const string LoadAnalysis = "LoadAnalysis";
            internal const string Refresh = "Refresh";
            internal const string AddVoltageRange = "AddVoltageRange";
            internal const string DeleteVoltageRange = "DeleteVoltageRange";
            internal const string AllRangeLocked = "AllRangeLocked";
            internal const string VoltageRange = "VoltageRange";
            internal const string SelectAnalysisPrompt = "SelectAnalysisPrompt";
            internal const string NoAnalysisFound = "NoAnalysisFound";
            internal const string AnalysisRequired = "AnalysisRequired";
            internal const string RestrictedBrowse = "RestrictedBrowse";
            internal const string SkipLoading = "SkipLoading";
            internal const string Loading = "Loading";
            internal const string Ready = "Ready";
            internal const string GeneratingReport = "GeneratingReport";
            internal const string ReportGenerated = "ReportGenerated";
            internal const string GenerateFailed = "GenerateFailed";
            internal const string InvalidProjectPath = "InvalidProjectPath";
            internal const string InvalidAnalysisArchive = "InvalidAnalysisArchive";
            internal const string OutputConflictTitle = "OutputConflictTitle";
            internal const string OutputExistsPrompt = "OutputExistsPrompt";
            internal const string SaveAs = "SaveAs";
            internal const string Overwrite = "Overwrite";
            internal const string Cancel = "Cancel";
            internal const string Browse = "Browse";
            internal const string SelectTemplate = "SelectTemplate";
            internal const string TemplateMissing = "TemplateMissing";
            internal const string OfficeFallbackWps = "OfficeFallbackWps";
            internal const string PdfConversionFailed = "PdfConversionFailed";
            internal const string ConfirmDeleteRange = "ConfirmDeleteRange";
            internal const string RangeOutsideBounds = "RangeOutsideBounds";
            internal const string RangeOverlap = "RangeOverlap";
            internal const string NoData = "NoData";
            internal const string CheckedItemsOnly = "CheckedItemsOnly";
            internal const string ReportOutputFolder = "ReportOutputFolder";
            internal const string OpenFolder = "OpenFolder";
            internal const string Close = "Close";
            internal const string ReportTitle = "ReportTitle";
            internal const string GeneratedBy = "GeneratedBy";
            internal const string CableInformation = "CableInformation";
            internal const string TestInformation = "TestInformation";
            internal const string ConclusionAndRecommendation = "ConclusionAndRecommendation";
            internal const string AppendixFigures = "AppendixFigures";
            internal const string Phase = "Phase";
            internal const string MaximumCharge = "MaximumCharge";
            internal const string AverageCharge = "AverageCharge";
            internal const string DischargeCount = "DischargeCount";
            internal const string Noise = "Noise";
            internal const string Frequency = "Frequency";
            internal const string Capacitance = "Capacitance";
            internal const string DielectricLoss = "DielectricLoss";
            internal const string InsulationBeforeTest = "InsulationBeforeTest";
            internal const string InsulationAfterTest = "InsulationAfterTest";
            internal const string RegionSelection = "RegionSelection";
            internal const string RegionRectangle = "RegionRectangle";
            internal const string RegionRecords = "RegionRecords";
            internal const string ReflectionUncertain = "ReflectionUncertain";
            internal const string AttachmentUnavailable = "AttachmentUnavailable";
            internal const string ArchivedConclusionUnavailable = "ArchivedConclusionUnavailable";
        }

        static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            {UiKeys.Title, "Report Generator"},
            {UiKeys.ProjectInformation, "Project information"},
            {UiKeys.LocationResults, "Location results"},
            {UiKeys.AllLocationResults, "All location results"},
            {UiKeys.AtOrBelowU0LocationResults, "Location results at U ≤ U0"},
            {UiKeys.ServiceBandLocationResults, "Location results at {0} ≤ U ≤ {1}"},
            {UiKeys.AllPhases, "ALL"},
            {UiKeys.PhaseA, "L1"},
            {UiKeys.PhaseB, "L2"},
            {UiKeys.PhaseC, "L3"},
            {UiKeys.ChargeLocation, "Q-L"},
            {UiKeys.CountLocation, "N-L"},
            {UiKeys.CalibrationData, "Calibration data"},
            {UiKeys.TestData, "Test data"},
            {UiKeys.RegionalStatisticsChart, "Regional statistics chart"},
            {UiKeys.PdmapChart, "PDmap chart"},
            {UiKeys.TrendStatisticsChart, "Trend statistics chart"},
            {UiKeys.Conclusion, "Conclusion"},
            {UiKeys.SimpleConclusion, "Brief conclusion"},
            {UiKeys.ProfessionalConclusion, "Professional conclusion"},
            {UiKeys.GenerateConclusion, "Generate conclusion"},
            {UiKeys.GenerateReport, "Generate report"},
            {UiKeys.OpenReport, "Open report"},
            {UiKeys.Clear, "Clear"},
            {UiKeys.LoadAnalysis, "Load analysis"},
            {UiKeys.Refresh, "Refresh"},
            {UiKeys.AddVoltageRange, "Add voltage range"},
            {UiKeys.DeleteVoltageRange, "Delete voltage range"},
            {UiKeys.AllRangeLocked, "All location results cannot be added or deleted."},
            {UiKeys.VoltageRange, "Voltage range"},
            {UiKeys.SelectAnalysisPrompt, "Select a saved analysis to load."},
            {UiKeys.NoAnalysisFound, "No saved analysis was found in this project."},
            {UiKeys.AnalysisRequired, "Load a valid project analysis before using this function."},
            {UiKeys.RestrictedBrowse, "No analysis is loaded. Only limited project data can be browsed."},
            {UiKeys.SkipLoading, "Skip loading"},
            {UiKeys.Loading, "Loading…"},
            {UiKeys.Ready, "Ready"},
            {UiKeys.GeneratingReport, "Generating report…"},
            {UiKeys.ReportGenerated, "Report generated: {0}"},
            {UiKeys.GenerateFailed, "Report generation failed: {0}"},
            {UiKeys.InvalidProjectPath, "Select a valid project folder."},
            {UiKeys.InvalidAnalysisArchive, "The selected analysis archive is incomplete or invalid."},
            {UiKeys.OutputConflictTitle, "Report already exists"},
            {UiKeys.OutputExistsPrompt, "A report with the same name already exists. Save as a new file, overwrite it, or cancel."},
            {UiKeys.SaveAs, "Save as"},
            {UiKeys.Overwrite, "Overwrite"},
            {UiKeys.Cancel, "Cancel"},
            {UiKeys.Browse, "Browse…"},
            {UiKeys.SelectTemplate, "Select report template"},
            {UiKeys.TemplateMissing, "Select a valid .docx report template before generating the report."},
            {UiKeys.OfficeFallbackWps, "Microsoft Office is unavailable. Trying WPS Office."},
            {UiKeys.PdfConversionFailed, "The Word report was created, but PDF conversion failed."},
            {UiKeys.ConfirmDeleteRange, "Delete the selected custom voltage range?"},
            {UiKeys.RangeOutsideBounds, "The voltage range must remain within 0–2U0."},
            {UiKeys.RangeOverlap, "This voltage range overlaps an existing custom range."},
            {UiKeys.NoData, "No data is available for this item."},
            {UiKeys.CheckedItemsOnly, "Only checked tree items are added to the report."},
            {UiKeys.ReportOutputFolder, "Reports are saved in the project folder."},
            {UiKeys.OpenFolder, "Open folder"},
            {UiKeys.Close, "Close"},
            {UiKeys.ReportTitle, "OWTS Cable Partial-Discharge Diagnostic Report"},
            {UiKeys.GeneratedBy, "Automatically generated report"},
            {UiKeys.CableInformation, "Cable information"},
            {UiKeys.TestInformation, "Test information"},
            {UiKeys.ConclusionAndRecommendation, "Conclusion and recommendation"},
            {UiKeys.AppendixFigures, "Appendix figures"},
            {UiKeys.Phase, "Phase"},
            {UiKeys.MaximumCharge, "Maximum charge"},
            {UiKeys.AverageCharge, "Average charge"},
            {UiKeys.DischargeCount, "Discharge count"},
            {UiKeys.Noise, "Noise"},
            {UiKeys.Frequency, "Frequency"},
            {UiKeys.Capacitance, "Capacitance"},
            {UiKeys.DielectricLoss, "Dielectric loss"},
            {UiKeys.InsulationBeforeTest, "First measurement"},
            {UiKeys.InsulationAfterTest, "Second measurement"},
            {UiKeys.RegionSelection, "Region selection"},
            {UiKeys.RegionRectangle, "rectangle"},
            {UiKeys.RegionRecords, "records"},
            {UiKeys.ReflectionUncertain, "Reflection pulse not identified"},
            {UiKeys.AttachmentUnavailable, "Some archived figures could not be localized from source data and were omitted."},
            {UiKeys.ArchivedConclusionUnavailable, "The archived conclusion is not available in this language. Generate a new conclusion."}
        };

        static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            {UiKeys.Title, "生成报告"},
            {UiKeys.ProjectInformation, "项目信息"},
            {UiKeys.LocationResults, "定位结果"},
            {UiKeys.AllLocationResults, "所有定位结果"},
            {UiKeys.AtOrBelowU0LocationResults, "≤U0 定位结果"},
            {UiKeys.ServiceBandLocationResults, "{0}≤U≤{1} 定位结果"},
            {UiKeys.AllPhases, "ALL"},
            {UiKeys.PhaseA, "A相"},
            {UiKeys.PhaseB, "B相"},
            {UiKeys.PhaseC, "C相"},
            {UiKeys.ChargeLocation, "Q-L"},
            {UiKeys.CountLocation, "N-L"},
            {UiKeys.CalibrationData, "校准数据"},
            {UiKeys.TestData, "测试数据"},
            {UiKeys.RegionalStatisticsChart, "区域统计图"},
            {UiKeys.PdmapChart, "PDmap图"},
            {UiKeys.TrendStatisticsChart, "趋势统计图"},
            {UiKeys.Conclusion, "结论"},
            {UiKeys.SimpleConclusion, "简明结论"},
            {UiKeys.ProfessionalConclusion, "专业结论"},
            {UiKeys.GenerateConclusion, "生成结论"},
            {UiKeys.GenerateReport, "生成报告"},
            {UiKeys.OpenReport, "打开报告"},
            {UiKeys.Clear, "清空"},
            {UiKeys.LoadAnalysis, "加载分析"},
            {UiKeys.Refresh, "刷新"},
            {UiKeys.AddVoltageRange, "增加电压区间"},
            {UiKeys.DeleteVoltageRange, "删除电压区间"},
            {UiKeys.AllRangeLocked, "所有定位结果不可添加或删除。"},
            {UiKeys.VoltageRange, "电压区间"},
            {UiKeys.SelectAnalysisPrompt, "请选择要加载的已保存分析。"},
            {UiKeys.NoAnalysisFound, "本项目未找到已保存的分析。"},
            {UiKeys.AnalysisRequired, "请先加载有效的项目分析，再使用此功能。"},
            {UiKeys.RestrictedBrowse, "尚未加载分析，当前只能浏览有限的项目数据。"},
            {UiKeys.SkipLoading, "跳过加载"},
            {UiKeys.Loading, "正在加载…"},
            {UiKeys.Ready, "就绪"},
            {UiKeys.GeneratingReport, "正在生成报告…"},
            {UiKeys.ReportGenerated, "报告已生成：{0}"},
            {UiKeys.GenerateFailed, "报告生成失败：{0}"},
            {UiKeys.InvalidProjectPath, "请选择有效的项目文件夹。"},
            {UiKeys.InvalidAnalysisArchive, "所选分析存档不完整或无效。"},
            {UiKeys.OutputConflictTitle, "报告已存在"},
            {UiKeys.OutputExistsPrompt, "同名报告已经存在，请选择另存为、覆盖或取消。"},
            {UiKeys.SaveAs, "另存为"},
            {UiKeys.Overwrite, "覆盖"},
            {UiKeys.Cancel, "取消"},
            {UiKeys.Browse, "浏览…"},
            {UiKeys.SelectTemplate, "选择报告模板"},
            {UiKeys.TemplateMissing, "生成报告前请选择有效的 .docx 报告模板。"},
            {UiKeys.OfficeFallbackWps, "Microsoft Office 不可用，正在尝试 WPS Office。"},
            {UiKeys.PdfConversionFailed, "Word 报告已生成，但 PDF 转换失败。"},
            {UiKeys.ConfirmDeleteRange, "确定删除选中的自定义电压区间吗？"},
            {UiKeys.RangeOutsideBounds, "电压区间必须位于 0–2U0 范围内。"},
            {UiKeys.RangeOverlap, "该电压区间与现有自定义区间重叠。"},
            {UiKeys.NoData, "此项没有可用数据。"},
            {UiKeys.CheckedItemsOnly, "仅将树形中勾选的内容添加到报告。"},
            {UiKeys.ReportOutputFolder, "报告保存在项目根目录。"},
            {UiKeys.OpenFolder, "打开文件夹"},
            {UiKeys.Close, "关闭"},
            {UiKeys.ReportTitle, "振荡波电缆局部放电诊断报告"},
            {UiKeys.GeneratedBy, "自动生成报告"},
            {UiKeys.CableInformation, "电缆信息"},
            {UiKeys.TestInformation, "测试信息"},
            {UiKeys.ConclusionAndRecommendation, "结论与建议"},
            {UiKeys.AppendixFigures, "附图"},
            {UiKeys.Phase, "相别"},
            {UiKeys.MaximumCharge, "最大放电量"},
            {UiKeys.AverageCharge, "平均放电量"},
            {UiKeys.DischargeCount, "放电次数"},
            {UiKeys.Noise, "噪声"},
            {UiKeys.Frequency, "频率"},
            {UiKeys.Capacitance, "电容量"},
            {UiKeys.DielectricLoss, "介损"},
            {UiKeys.InsulationBeforeTest, "首次测量"},
            {UiKeys.InsulationAfterTest, "二次测量"},
            {UiKeys.RegionSelection, "选区边界"},
            {UiKeys.RegionRectangle, "矩形"},
            {UiKeys.RegionRecords, "条数据"},
            {UiKeys.ReflectionUncertain, "反射波未确定"},
            {UiKeys.AttachmentUnavailable, "部分存档图无法从原始数据重建并翻译，已从报告中略去。"},
            {UiKeys.ArchivedConclusionUnavailable, "存档结论没有当前语言版本，请重新生成结论。"}
        };

        static readonly Dictionary<int, Dictionary<string, string>> Localized = BuildLocalized();

        static ReportLanguages()
        {
            if (Names.Length != Codes.Length || Codes.Length != Xiongxin.OWTS.Localization.UiLanguage.Codes.Length)
                throw new InvalidOperationException("The report language contract must match the shared language catalog.");
            foreach (string key in English.Keys)
                if (!Chinese.ContainsKey(key))
                    throw new InvalidOperationException("Missing Chinese report text: " + key);
            foreach (string key in Chinese.Keys)
                if (!English.ContainsKey(key))
                    throw new InvalidOperationException("Missing English report text: " + key);
        }

        static Dictionary<int, Dictionary<string, string>> BuildLocalized()
        {
            var result = new Dictionary<int, Dictionary<string, string>>();
            for (int i = 2; i < Codes.Length; i++)
                result.Add(i, new Dictionary<string, string>(StringComparer.Ordinal));

            AddCore(result, UiKeys.Title, "Генератор на отчети|Generator izvješća|Generátor zpráv|Générateur de rapports|Berichtsgenerator|Jelentéskészítő|Generatore di rapporti|Generator raportów|Generator de rapoarte|Генератор отчётов|Generator izveštaja|Generátor správ|Generator poročil|Generador de informes|Rapor oluşturucu|Trình tạo báo cáo");
            AddCore(result, UiKeys.ProjectInformation, "Информация за проекта|Podaci o projektu|Informace o projektu|Informations du projet|Projektinformationen|Projektadatok|Informazioni sul progetto|Informacje o projekcie|Informații despre proiect|Информация о проекте|Podaci o projektu|Informácie o projekte|Podatki o projektu|Información del proyecto|Proje bilgileri|Thông tin dự án");
            AddCore(result, UiKeys.LocationResults, "Резултати от локализацията|Rezultati lociranja|Výsledky lokalizace|Résultats de localisation|Ortungsergebnisse|Helymeghatározási eredmények|Risultati di localizzazione|Wyniki lokalizacji|Rezultate de localizare|Результаты локализации|Rezultati lociranja|Výsledky lokalizácie|Rezultati lokalizacije|Resultados de localización|Konum sonuçları|Kết quả định vị");
            AddCore(result, UiKeys.AllLocationResults, "Всички резултати от локализацията|Svi rezultati lociranja|Všechny výsledky lokalizace|Tous les résultats de localisation|Alle Ortungsergebnisse|Minden helymeghatározási eredmény|Tutti i risultati di localizzazione|Wszystkie wyniki lokalizacji|Toate rezultatele de localizare|Все результаты локализации|Svi rezultati lociranja|Všetky výsledky lokalizácie|Vsi rezultati lokalizacije|Todos los resultados de localización|Tüm konum sonuçları|Tất cả kết quả định vị");
            AddCore(result, UiKeys.CalibrationData, "Данни за калибриране|Kalibracijski podaci|Kalibrační data|Données d'étalonnage|Kalibrierdaten|Kalibrációs adatok|Dati di calibrazione|Dane kalibracyjne|Date de calibrare|Данные калибровки|Podaci o kalibraciji|Kalibračné údaje|Podatki umerjanja|Datos de calibración|Kalibrasyon verileri|Dữ liệu hiệu chuẩn");
            AddCore(result, UiKeys.TestData, "Данни от изпитването|Podaci ispitivanja|Zkušební data|Données d'essai|Prüfdaten|Vizsgálati adatok|Dati di prova|Dane pomiarowe|Date de testare|Данные испытаний|Podaci ispitivanja|Skúšobné údaje|Podatki preskusa|Datos de ensayo|Test verileri|Dữ liệu thử nghiệm");
            AddCore(result, UiKeys.RegionalStatisticsChart, "Диаграма на регионалната статистика|Graf regionalne statistike|Graf regionální statistiky|Graphique des statistiques régionales|Diagramm der Bereichsstatistik|Területi statisztikai diagram|Grafico delle statistiche regionali|Wykres statystyk regionalnych|Grafic statistici regionale|Диаграмма региональной статистики|Graf regionalne statistike|Graf regionálnej štatistiky|Graf regionalne statistike|Gráfico de estadísticas regionales|Bölgesel istatistik grafiği|Biểu đồ thống kê khu vực");
            AddCore(result, UiKeys.PdmapChart, "PDmap диаграма|PDmap graf|Graf PDmap|Graphique PDmap|PDmap-Diagramm|PDmap diagram|Grafico PDmap|Wykres PDmap|Grafic PDmap|Диаграмма PDmap|PDmap graf|Graf PDmap|Graf PDmap|Gráfico PDmap|PDmap grafiği|Biểu đồ PDmap");
            AddCore(result, UiKeys.TrendStatisticsChart, "Диаграма на статистическите тенденции|Graf statističkog trenda|Graf statistického trendu|Graphique des tendances statistiques|Statistisches Trenddiagramm|Statisztikai trenddiagram|Grafico delle tendenze statistiche|Wykres trendu statystycznego|Grafic tendințe statistice|Диаграмма статистического тренда|Graf statističkog trenda|Graf štatistického trendu|Graf statističnega trenda|Gráfico de tendencias estadísticas|İstatistik eğilim grafiği|Biểu đồ xu hướng thống kê");
            AddCore(result, UiKeys.Conclusion, "Заключение|Zaključak|Závěr|Conclusion|Schlussfolgerung|Következtetés|Conclusione|Wniosek|Concluzie|Заключение|Zaključak|Záver|Zaključek|Conclusión|Sonuç|Kết luận");
            AddCore(result, UiKeys.SimpleConclusion, "Кратко заключение|Kratak zaključak|Stručný závěr|Conclusion brève|Kurze Schlussfolgerung|Rövid következtetés|Conclusione breve|Krótkie podsumowanie|Concluzie scurtă|Краткое заключение|Kratak zaključak|Stručný záver|Kratek zaključek|Conclusión breve|Kısa sonuç|Kết luận ngắn gọn");
            AddCore(result, UiKeys.ProfessionalConclusion, "Професионално заключение|Stručni zaključak|Odborný závěr|Conclusion professionnelle|Fachliche Schlussfolgerung|Szakmai következtetés|Conclusione professionale|Wniosek specjalistyczny|Concluzie profesională|Профессиональное заключение|Stručni zaključak|Odborný záver|Strokovni zaključek|Conclusión profesional|Profesyonel sonuç|Kết luận chuyên môn");
            AddCore(result, UiKeys.GenerateConclusion, "Генерирай заключение|Generiraj zaključak|Vytvořit závěr|Générer la conclusion|Schlussfolgerung erstellen|Következtetés létrehozása|Genera conclusione|Generuj wniosek|Generează concluzia|Сформировать заключение|Generiši zaključak|Vytvoriť záver|Ustvari zaključek|Generar conclusión|Sonuç oluştur|Tạo kết luận");
            AddCore(result, UiKeys.GenerateReport, "Генерирай отчет|Generiraj izvješće|Vytvořit zprávu|Générer le rapport|Bericht erstellen|Jelentés készítése|Genera rapporto|Generuj raport|Generează raport|Создать отчёт|Generiši izveštaj|Vytvoriť správu|Ustvari poročilo|Generar informe|Rapor oluştur|Tạo báo cáo");
            AddCore(result, UiKeys.Clear, "Изчисти|Očisti|Vymazat|Effacer|Leeren|Törlés|Cancella|Wyczyść|Șterge|Очистить|Očisti|Vymazať|Počisti|Limpiar|Temizle|Xóa");
            AddCore(result, UiKeys.LoadAnalysis, "Зареди анализ|Učitaj analizu|Načíst analýzu|Charger l'analyse|Analyse laden|Elemzés betöltése|Carica analisi|Wczytaj analizę|Încarcă analiza|Загрузить анализ|Učitaj analizu|Načítať analýzu|Naloži analizo|Cargar análisis|Analizi yükle|Tải phân tích");
            AddCore(result, UiKeys.AddVoltageRange, "Добави диапазон на напрежение|Dodaj raspon napona|Přidat rozsah napětí|Ajouter une plage de tension|Spannungsbereich hinzufügen|Feszültségtartomány hozzáadása|Aggiungi intervallo di tensione|Dodaj zakres napięcia|Adaugă interval de tensiune|Добавить диапазон напряжения|Dodaj opseg napona|Pridať rozsah napätia|Dodaj napetostno območje|Añadir rango de tensión|Gerilim aralığı ekle|Thêm dải điện áp");
            AddCore(result, UiKeys.DeleteVoltageRange, "Изтрий диапазона на напрежение|Izbriši raspon napona|Odstranit rozsah napětí|Supprimer la plage de tension|Spannungsbereich löschen|Feszültségtartomány törlése|Elimina intervallo di tensione|Usuń zakres napięcia|Șterge intervalul de tensiune|Удалить диапазон напряжения|Izbriši opseg napona|Odstrániť rozsah napätia|Izbriši napetostno območje|Eliminar rango de tensión|Gerilim aralığını sil|Xóa dải điện áp");
            AddCore(result, UiKeys.SaveAs, "Запази като|Spremi kao|Uložit jako|Enregistrer sous|Speichern unter|Mentés másként|Salva con nome|Zapisz jako|Salvare ca|Сохранить как|Sačuvaj kao|Uložiť ako|Shrani kot|Guardar como|Farklı kaydet|Lưu dưới dạng");
            AddCore(result, UiKeys.Overwrite, "Презапиши|Prepiši|Přepsat|Écraser|Überschreiben|Felülírás|Sovrascrivi|Zastąp|Suprascrie|Перезаписать|Prepiši|Prepísať|Prepiši|Sobrescribir|Üzerine yaz|Ghi đè");
            AddCore(result, UiKeys.Cancel, "Отказ|Odustani|Zrušit|Annuler|Abbrechen|Mégse|Annulla|Anuluj|Anulează|Отмена|Otkaži|Zrušiť|Prekliči|Cancelar|İptal|Hủy");
            AddCore(result, UiKeys.ReportTitle, "Диагностичен доклад за частични разряди в кабели с OWTS|Dijagnostičko izvješće OWTS o parcijalnim izbijanjima u kabelu|Diagnostická zpráva OWTS o částečných výbojích v kabelu|Rapport de diagnostic OWTS des décharges partielles des câbles|OWTS-Diagnosebericht zu Teilentladungen in Kabeln|OWTS kábelrészleges-kisülési diagnosztikai jelentés|Rapporto diagnostico OWTS sulle scariche parziali nei cavi|Raport diagnostyczny OWTS wyładowań niezupełnych w kablach|Raport de diagnostic OWTS al descărcărilor parțiale în cabluri|Диагностический отчёт OWTS по частичным разрядам в кабеле|Dijagnostički izveštaj OWTS o parcijalnim pražnjenjima u kablu|Diagnostická správa OWTS o čiastočných výbojoch v kábli|Diagnostično poročilo OWTS o delnih razelektritvah v kablu|Informe de diagnóstico OWTS de descargas parciales en cables|OWTS kablo kısmi deşarj tanı raporu|Báo cáo chẩn đoán phóng điện cục bộ cáp OWTS");
            AddCore(result, UiKeys.GeneratedBy, "Автоматично генериран отчет|Automatizirano izvješće|Automaticky generovaná zpráva|Rapport généré automatiquement|Automatisch erstellter Bericht|Automatikusan létrehozott jelentés|Rapporto generato automaticamente|Automatycznie wygenerowany raport|Raport generat automat|Автоматически созданный отчёт|Automatski generisan izveštaj|Automaticky vygenerovaná správa|Samodejno ustvarjeno poročilo|Informe generado automáticamente|Otomatik oluşturulan rapor|Báo cáo được tạo tự động");
            AddCore(result, UiKeys.CableInformation, "Информация за кабела|Podaci o kabelu|Informace o kabelu|Informations sur le câble|Kabelinformationen|Kábeladatok|Informazioni sul cavo|Informacje o kablu|Informații despre cablu|Информация о кабеле|Podaci o kablu|Informácie o kábli|Podatki o kablu|Información del cable|Kablo bilgileri|Thông tin cáp");
            AddCore(result, UiKeys.TestInformation, "Информация за изпитването|Podaci o ispitivanju|Informace o zkoušce|Informations sur l’essai|Prüfinformationen|Vizsgálati adatok|Informazioni sulla prova|Informacje o badaniu|Informații despre testare|Информация об испытании|Podaci o ispitivanju|Informácie o skúške|Podatki o preskusu|Información del ensayo|Test bilgileri|Thông tin thử nghiệm");
            AddCore(result, UiKeys.ConclusionAndRecommendation, "Заключение и препоръки|Zaključak i preporuke|Závěr a doporučení|Conclusion et recommandations|Schlussfolgerung und Empfehlungen|Következtetés és ajánlások|Conclusioni e raccomandazioni|Wnioski i zalecenia|Concluzii și recomandări|Заключение и рекомендации|Zaključak i preporuke|Záver a odporúčania|Zaključek in priporočila|Conclusiones y recomendaciones|Sonuç ve öneriler|Kết luận và khuyến nghị");
            AddCore(result, UiKeys.AppendixFigures, "Допълнителни фигури|Dodatne slike|Obrázky v příloze|Figures en annexe|Abbildungen im Anhang|Melléklet ábrái|Figure in appendice|Rysunki w załączniku|Figuri anexate|Дополнительные иллюстрации|Dodatne slike|Obrázky v prílohe|Slike v prilogi|Figuras anexas|Ek şekiller|Hình ảnh phụ lục");
            AddCore(result, UiKeys.Phase, "Фаза|Faza|Fáze|Phase|Phase|Fázis|Fase|Faza|Fază|Фаза|Faza|Fáza|Faza|Fase|Faz|Pha");
            AddCore(result, UiKeys.MaximumCharge, "Максимален заряд|Maksimalni naboj|Maximální náboj|Charge maximale|Maximale Ladung|Maximális töltés|Carica massima|Ładunek maksymalny|Sarcină maximă|Максимальный заряд|Maksimalno naelektrisanje|Maximálny náboj|Največji naboj|Carga máxima|Maksimum yük|Điện lượng cực đại");
            AddCore(result, UiKeys.AverageCharge, "Среден заряд|Prosječni naboj|Průměrný náboj|Charge moyenne|Mittlere Ladung|Átlagos töltés|Carica media|Ładunek średni|Sarcină medie|Средний заряд|Prosečno naelektrisanje|Priemerný náboj|Povprečni naboj|Carga media|Ortalama yük|Điện lượng trung bình");
            AddCore(result, UiKeys.DischargeCount, "Брой разряди|Broj izbijanja|Počet výbojů|Nombre de décharges|Anzahl Entladungen|Kisülések száma|Numero di scariche|Liczba wyładowań|Număr de descărcări|Число разрядов|Broj pražnjenja|Počet výbojov|Število razelektritev|Número de descargas|Deşarj sayısı|Số lần phóng điện");
            AddCore(result, UiKeys.Noise, "Шум|Šum|Šum|Bruit|Rauschen|Zaj|Rumore|Szum|Zgomot|Шум|Šum|Šum|Šum|Ruido|Gürültü|Nhiễu");
            AddCore(result, UiKeys.Frequency, "Честота|Frekvencija|Frekvence|Fréquence|Frequenz|Frekvencia|Frequenza|Częstotliwość|Frecvență|Частота|Frekvencija|Frekvencia|Frekvenca|Frecuencia|Frekans|Tần số");
            AddCore(result, UiKeys.Capacitance, "Капацитет|Kapacitet|Kapacita|Capacité|Kapazität|Kapacitás|Capacità|Pojemność|Capacitanță|Ёмкость|Kapacitivnost|Kapacita|Kapacitivnost|Capacitancia|Kapasitans|Điện dung");
            AddCore(result, UiKeys.DielectricLoss, "Диелектрични загуби|Dielektrični gubici|Dielektrické ztráty|Pertes diélectriques|Dielektrischer Verlustfaktor|Dielektromos veszteség|Perdite dielettriche|Straty dielektryczne|Pierderi dielectrice|Диэлектрические потери|Dielektrični gubici|Dielektrické straty|Dielektrične izgube|Pérdidas dieléctricas|Dielektrik kayıp|Tổn hao điện môi");
            AddCore(result, UiKeys.InsulationBeforeTest, "Първо измерване|Prvo mjerenje|První měření|Première mesure|Erste Messung|Első mérés|Prima misura|Pierwszy pomiar|Prima măsurare|Первое измерение|Prvo merenje|Prvé meranie|Prva meritev|Primera medición|İlk ölçüm|Lần đo thứ nhất");
            AddCore(result, UiKeys.InsulationAfterTest, "Второ измерване|Drugo mjerenje|Druhé měření|Deuxième mesure|Zweite Messung|Második mérés|Seconda misura|Drugi pomiar|A doua măsurare|Второе измерение|Drugo merenje|Druhé meranie|Druga meritev|Segunda medición|İkinci ölçüm|Lần đo thứ hai");
            AddCore(result, UiKeys.RegionSelection, "Избрана област|Odabrano područje|Vybraná oblast|Zone sélectionnée|Ausgewählter Bereich|Kijelölt terület|Area selezionata|Wybrany obszar|Regiune selectată|Выбранная область|Izabrana oblast|Vybraná oblasť|Izbrano območje|Región seleccionada|Seçili bölge|Vùng đã chọn");
            AddCore(result, UiKeys.RegionRectangle, "правоъгълник|pravokutnik|obdélník|rectangle|Rechteck|téglalap|rettangolo|prostokąt|dreptunghi|прямоугольник|pravougaonik|obdĺžnik|pravokotnik|rectángulo|dikdörtgen|hình chữ nhật");
            AddCore(result, UiKeys.RegionRecords, "записа|zapisa|záznamů|enregistrements|Datensätze|rekord|record|rekordów|înregistrări|записей|zapisa|záznamov|zapisov|registros|kayıt|bản ghi");
            AddCore(result, UiKeys.ReflectionUncertain, "Отразеният импулс не е идентифициран|Reflektirani impuls nije identificiran|Odražený impulz nebyl určen|Impulsion réfléchie non identifiée|Reflexionsimpuls nicht identifiziert|A visszavert impulzus nem azonosított|Impulso riflesso non identificato|Nie zidentyfikowano impulsu odbitego|Impulsul reflectat nu a fost identificat|Отражённый импульс не определён|Odbijeni impuls nije identifikovan|Odrazený impulz nebol určený|Odbiti impulz ni prepoznan|Impulso reflejado no identificado|Yansıyan darbe tanımlanamadı|Chưa xác định xung phản xạ");
            AddCore(result, UiKeys.AnalysisRequired, "Заредете валиден анализ на проекта, преди да използвате тази функция.|Učitajte valjanu analizu projekta prije korištenja ove funkcije.|Před použitím této funkce načtěte platnou analýzu projektu.|Chargez une analyse de projet valide avant d’utiliser cette fonction.|Laden Sie eine gültige Projektanalyse, bevor Sie diese Funktion verwenden.|A funkció használata előtt töltsön be érvényes projektelemzést.|Caricare un'analisi di progetto valida prima di usare questa funzione.|Przed użyciem tej funkcji wczytaj prawidłową analizę projektu.|Încărcați o analiză validă a proiectului înainte de a utiliza această funcție.|Загрузите корректный анализ проекта перед использованием этой функции.|Učitajte važeću analizu projekta pre korišćenja ove funkcije.|Pred použitím tejto funkcie načítajte platnú analýzu projektu.|Pred uporabo te funkcije naložite veljavno analizo projekta.|Cargue un análisis de proyecto válido antes de usar esta función.|Bu işlevi kullanmadan önce geçerli bir proje analizi yükleyin.|Hãy tải phân tích dự án hợp lệ trước khi sử dụng chức năng này.");
            AddCore(result, UiKeys.RestrictedBrowse, "Няма зареден анализ. Могат да се преглеждат само ограничени данни за проекта.|Analiza nije učitana. Mogu se pregledavati samo ograničeni podaci projekta.|Není načtena žádná analýza. Lze procházet pouze omezená data projektu.|Aucune analyse n’est chargée. Seules des données de projet limitées peuvent être consultées.|Es ist keine Analyse geladen. Es können nur begrenzte Projektdaten angezeigt werden.|Nincs betöltött elemzés. Csak korlátozott projektadatok böngészhetők.|Nessuna analisi caricata. È possibile consultare solo dati di progetto limitati.|Nie wczytano analizy. Można przeglądać tylko ograniczone dane projektu.|Nu este încărcată nicio analiză. Pot fi consultate numai date limitate ale proiectului.|Анализ не загружен. Доступен только ограниченный просмотр данных проекта.|Analiza nije učitana. Mogu se pregledati samo ograničeni podaci projekta.|Nie je načítaná žiadna analýza. Možno prezerať iba obmedzené údaje projektu.|Analiza ni naložena. Brskati je mogoče le po omejenih podatkih projekta.|No hay ningún análisis cargado. Solo se pueden consultar datos limitados del proyecto.|Analiz yüklenmedi. Yalnızca sınırlı proje verilerine göz atılabilir.|Chưa tải phân tích. Chỉ có thể xem dữ liệu dự án giới hạn.");
            AddCore(result, UiKeys.SkipLoading, "Пропусни зареждането|Preskoči učitavanje|Přeskočit načtení|Ignorer le chargement|Laden überspringen|Betöltés kihagyása|Salta caricamento|Pomiń wczytywanie|Omite încărcarea|Пропустить загрузку|Preskoči učitavanje|Preskočiť načítanie|Preskoči nalaganje|Omitir carga|Yüklemeyi atla|Bỏ qua tải");
            return result;
        }

        static void AddCore(Dictionary<int, Dictionary<string, string>> result, string key, string translations)
        {
            string[] values = translations.Split('|');
            if (values.Length != 16)
                throw new InvalidOperationException("Expected 16 translated values for report UI key: " + key);
            for (int i = 0; i < values.Length; i++)
                result[i + 2][key] = values[i];
        }

        internal static void Validate(int index)
        {
            if (index < 0 || index >= Codes.Length)
                throw new ArgumentOutOfRangeException("LanguageIndex", "Expected 0..18.");
        }

        internal static int FindCode(string code)
        { return Xiongxin.OWTS.Localization.UiLanguage.FindCode(code); }

        internal static string GetText(int languageIndex, string key)
        {
            Validate(languageIndex);
            if (String.IsNullOrEmpty(key)) return String.Empty;
            string english;
            if (!English.TryGetValue(key, out english)) return key;
            if (languageIndex == 0) return Chinese[key];
            if (languageIndex == 18) return TraditionalResource(Chinese[key]);
            string localized; if (Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(languageIndex, english, out localized)) return localized;
            languageIndex = Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(languageIndex);
            if (languageIndex == 1) return Xiongxin.OWTS.Localization.UiLanguage.Translate(languageIndex, english);
            Dictionary<string, string> words;
            string translated;
            return Localized.TryGetValue(languageIndex, out words) && words.TryGetValue(key, out translated) &&
                   !String.IsNullOrWhiteSpace(translated) ? translated : english;
        }

        internal static string Text(int languageIndex, string key)
        {
            return GetText(languageIndex, key);
        }

        internal static string Text(int languageIndex, string english, string chinese)
        {
            Validate(languageIndex);
            if (languageIndex == 0) return chinese ?? String.Empty;
            if (languageIndex == 18) return TraditionalResource(chinese ?? String.Empty);
            string localized; if (Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(languageIndex, english, out localized)) return localized;
            languageIndex = Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(languageIndex);
            if (languageIndex == 1) return Xiongxin.OWTS.Localization.UiLanguage.Translate(languageIndex, english) ?? String.Empty;
            foreach (KeyValuePair<string, string> pair in English)
                if (String.Equals(pair.Value, english, StringComparison.Ordinal))
                    return GetText(languageIndex, pair.Key);
            return english ?? String.Empty;
        }

        internal static string Format(int languageIndex, string key, params object[] arguments)
        {
            return String.Format(CultureInfo.CurrentCulture, GetText(languageIndex, key), arguments ?? new object[0]);
        }

        internal static string[] GetLanguageNames()
        {
            return (string[])Names.Clone();
        }

        internal static string[] GetLanguageCodes()
        {
            return (string[])Codes.Clone();
        }

        internal static string[] GetUiKeys()
        {
            var keys = new string[English.Count];
            English.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }
    }
}
