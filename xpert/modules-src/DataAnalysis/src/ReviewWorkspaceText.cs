using System;
using System.Collections.Generic;
namespace HVX.PdReview
{
    internal static class ReviewWorkspaceText
    {
        static readonly Dictionary<string,string[]> Words=new Dictionary<string,string[]>(StringComparer.Ordinal){
            {"Diagnostics","Диагностика|Dijagnostika|Diagnostika|Diagnostic|Diagnose|Diagnosztika|Diagnostica|Diagnostyka|Diagnostic|Диагностика|Dijagnostika|Diagnostika|Diagnostika|Diagnóstico|Tanılama|Chẩn đoán".Split('|')},
            {"Delete all","Изтрий всички|Izbriši sve|Smazat vše|Tout supprimer|Alles löschen|Összes törlése|Elimina tutto|Usuń wszystko|Șterge tot|Удалить всё|Obriši sve|Vymazať všetko|Izbriši vse|Eliminar todo|Tümünü sil|Xóa tất cả".Split('|')},
            {"Automatic analysis","Автоматичен анализ|Automatska analiza|Automatická analýza|Analyse automatique|Automatische Analyse|Automatikus elemzés|Analisi automatica|Analiza automatyczna|Analiză automată|Автоматический анализ|Automatska analiza|Automatická analýza|Samodejna analiza|Análisis automático|Otomatik analiz|Phân tích tự động".Split('|')},
            {"Manual analysis","Ръчен анализ|Ručna analiza|Ruční analýza|Analyse manuelle|Manuelle Analyse|Kézi elemzés|Analisi manuale|Analiza ręczna|Analiză manuală|Ручной анализ|Ručna analiza|Ručná analýza|Ročna analiza|Análisis manual|Manuel analiz|Phân tích thủ công".Split('|')},
            {"AI learning mode","Режим за обучение с AI|Način AI učenja|Režim učení AI|Mode d’apprentissage IA|KI-Lernmodus|MI tanulási mód|Modalità apprendimento IA|Tryb uczenia AI|Mod de învățare AI|Режим обучения ИИ|Režim AI učenja|Režim učenia AI|Način učenja UI|Modo de aprendizaje de IA|Yapay zekâ öğrenme modu|Chế độ học AI".Split('|')},
            {"Local AI suggestion","Локално AI предложение|Lokalni AI prijedlog|Místní návrh AI|Suggestion IA locale|Lokaler KI-Vorschlag|Helyi MI-javaslat|Suggerimento IA locale|Lokalna sugestia AI|Sugestie AI locală|Локальная подсказка ИИ|Lokalni AI predlog|Lokálny návrh AI|Lokalni predlog UI|Sugerencia de IA local|Yerel yapay zekâ önerisi|Gợi ý AI cục bộ".Split('|')},
            {"Generate conclusion","Генерирай заключение|Generiraj zaključak|Vytvořit závěr|Générer la conclusion|Schlussfolgerung erzeugen|Következtetés létrehozása|Genera conclusione|Generuj wniosek|Generează concluzia|Сформировать заключение|Generiši zaključak|Vytvoriť záver|Ustvari zaključek|Generar conclusión|Sonuç oluştur|Tạo kết luận".Split('|')},
            {"Correct and rate","Коригирай и оцени|Ispravi i ocijeni|Opravit a ohodnotit|Corriger et noter|Korrigieren und bewerten|Javítás és értékelés|Correggi e valuta|Popraw i oceń|Corectează și evaluează|Исправить и оценить|Ispravi i oceni|Opraviť a ohodnotiť|Popravi in oceni|Corregir y puntuar|Düzelt ve puanla|Sửa và chấm điểm".Split('|')},
            {"Manual threshold (unchecked: automatic)","Ръчен праг (иначе автоматичен)|Ručni prag (inače automatski)|Ruční práh (jinak automatický)|Seuil manuel (sinon automatique)|Manueller Schwellwert (sonst automatisch)|Kézi küszöb (különben automatikus)|Soglia manuale (altrimenti automatica)|Próg ręczny (inaczej automatyczny)|Prag manual (altfel automat)|Ручной порог (иначе авто)|Ručni prag (inače automatski)|Ručný prah (inak automatický)|Ročni prag (sicer samodejni)|Umbral manual (si no, automático)|Manuel eşik (yoksa otomatik)|Ngưỡng thủ công (bỏ chọn: tự động)".Split('|')},
            {"Apply to all files","За всички файлове|Za sve datoteke|Pro všechny soubory|Tous les fichiers|Für alle Dateien|Minden fájlra|Tutti i file|Wszystkie pliki|Toate fișierele|Для всех файлов|Za sve datoteke|Všetky súbory|Vse datoteke|Todos los archivos|Tüm dosyalara|Áp dụng cho tất cả".Split('|')},
            {"Compensated charge","Компенсиран заряд|Kompenzirani naboj|Kompenzovaný náboj|Charge compensée|Kompensierte Ladung|Kompenzált töltés|Carica compensata|Ładunek skompensowany|Sarcină compensată|Компенсированный заряд|Kompenzovano naelektrisanje|Kompenzovaný náboj|Kompenzirani naboj|Carga compensada|Kompanse yük|Điện tích đã bù".Split('|')},
            {"Location data","Данни за локализация|Podaci lokacije|Data lokalizace|Données de localisation|Ortungsdaten|Helymeghatározási adatok|Dati di localizzazione|Dane lokalizacji|Date de localizare|Данные локализации|Podaci lokacije|Údaje lokalizácie|Podatki lokacije|Datos de localización|Konum verileri|Dữ liệu định vị".Split('|')},
            {"Show this location group","Покажи тази група|Prikaži ovu skupinu|Zobrazit tuto skupinu|Afficher ce groupe|Diese Gruppe anzeigen|Csoport megjelenítése|Mostra questo gruppo|Pokaż tę grupę|Arată acest grup|Показать эту группу|Prikaži ovu grupu|Zobraziť túto skupinu|Prikaži to skupino|Mostrar este grupo|Bu grubu göster|Hiển thị nhóm này".Split('|')},
            {"Exit group view","Изход от групата|Izađi iz skupine|Ukončit zobrazení skupiny|Quitter le groupe|Gruppenansicht beenden|Csoportnézet bezárása|Esci dal gruppo|Wyjdź z grupy|Ieși din grup|Выйти из группы|Izađi iz grupe|Ukončiť skupinu|Zapri skupino|Salir del grupo|Grup görünümünden çık|Thoát chế độ nhóm".Split('|')}
        };
        internal static string Text(int language,string en,string zh)
        {
            if(language==18)return ReviewTraditionalChinese.Text(zh);
            string translated;if(language!=0&&Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(language,en,out translated))return translated;
            language=Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(language);
            if(language<2)return language==0?zh:en;
            string[] words;return Words.TryGetValue(en,out words)?words[language-2]:ReviewLanguages.Text(language,en,zh);
        }
    }
}
