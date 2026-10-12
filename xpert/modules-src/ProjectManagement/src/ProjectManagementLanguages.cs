using System;
using System.Collections.Generic;

namespace HVX.Controls
{
    internal static class ProjectManagementLanguages
    {
        internal static readonly string[] Names = Xiongxin.OWTS.Localization.UiLanguage.Names;

        internal static readonly string[] Codes = Xiongxin.OWTS.Localization.UiLanguage.Codes;

        // Culture names are intentionally separate from the public language codes. The
        // public codes must stay identical to the Task -3 language selector, while date
        // formatting needs concrete (non-neutral) cultures on .NET Framework.
        internal static readonly string[] CultureNames = Xiongxin.OWTS.Localization.UiLanguage.CultureNames;

        static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "PROJECT MANAGEMENT", Pack("УПРАВЛЕНИЕ НА ПРОЕКТИ|UPRAVLJANJE PROJEKTIMA|SPRÁVA PROJEKTŮ|GESTION DE PROJETS|PROJEKTVERWALTUNG|PROJEKTKEZELÉS|GESTIONE PROGETTI|ZARZĄDZANIE PROJEKTAMI|GESTIONAREA PROIECTELOR|УПРАВЛЕНИЕ ПРОЕКТАМИ|UPRAVLJANJE PROJEKTIMA|SPRÁVA PROJEKTOV|UPRAVLJANJE PROJEKTOV|GESTIÓN DE PROYECTOS|PROJE YÖNETİMİ|QUẢN LÝ DỰ ÁN") },
            { "SEARCH", Pack("ТЪРСЕНЕ|PRETRAŽI|HLEDAT|RECHERCHER|SUCHEN|KERESÉS|CERCA|SZUKAJ|CĂUTARE|ПОИСК|PRETRAGA|HĽADAŤ|ISKANJE|BUSCAR|ARA|TÌM KIẾM") },
            { "SORT", Pack("СОРТИРАНЕ|SORTIRAJ|ŘADIT|TRIER|SORTIEREN|RENDEZÉS|ORDINA|SORTUJ|SORTARE|СОРТИРОВКА|SORTIRAJ|ZORADIŤ|RAZVRSTI|ORDENAR|SIRALA|SẮP XẾP") },
            { "Project time", Pack("Време|Vrijeme|Čas|Date|Zeit|Időpont|Data|Czas|Dată|Время|Vreme|Čas|Čas|Fecha|Zaman|Thời gian") },
            { "Project name", Pack("Проект|Projekt|Projekt|Projet|Projekt|Projekt|Progetto|Projekt|Proiect|Проект|Projekat|Projekt|Projekt|Proyecto|Proje|Dự án") },
            { "Project (start – end)", Pack("Проект (начало – край)|Projekt (početak – kraj)|Projekt (začátek – konec)|Projet (début – fin)|Projekt (Anfang – Ende)|Projekt (kezdet – vég)|Progetto (inizio – fine)|Projekt (początek – koniec)|Proiect (început – sfârșit)|Проект (начало – конец)|Пројекат (почетак – крај)|Projekt (začiatok – koniec)|Projekt (začetek – konec)|Proyecto (inicio – fin)|Proje (başlangıç – bitiş)|Dự án (đầu – cuối)") },
            { "Cable length", Pack("Дължина на кабела|Duljina kabela|Délka kabelu|Longueur du câble|Kabellänge|Kábelhossz|Lunghezza cavo|Długość kabla|Lungime cablu|Длина кабеля|Dužina kabla|Dĺžka kábla|Dolžina kabla|Longitud del cable|Kablo uzunluğu|Chiều dài cáp") },
            { "Report status", Pack("Състояние на отчета|Status izvješća|Stav zprávy|État du rapport|Berichtsstatus|Jelentés állapota|Stato rapporto|Stan raportu|Stare raport|Статус отчёта|Status izveštaja|Stav správy|Stanje poročila|Estado del informe|Rapor durumu|Trạng thái báo cáo") },
            { "Conclusion", Pack("Заключение|Zaključak|Závěr|Conclusion|Schlussfolgerung|Következtetés|Conclusione|Wniosek|Concluzie|Заключение|Zaključak|Záver|Sklep|Conclusión|Sonuç|Kết luận") },
            { "Not analyzed", Pack("Не е анализирано|Nije analizirano|Neanalyzováno|Non analysé|Nicht analysiert|Nincs elemezve|Non analizzato|Nie analizowano|Neanalizat|Не проанализировано|Nije analizirano|Neanalyzované|Ni analizirano|Sin analizar|Analiz edilmedi|Chưa phân tích") },
            { "No conclusion", Pack("Няма заключение|Nema zaključka|Bez závěru|Aucune conclusion|Kein Ergebnis|Nincs következtetés|Nessuna conclusione|Brak wniosków|Fără concluzie|Нет заключения|Nema zaključka|Bez záveru|Brez sklepa|Sin conclusión|Sonuç yok|Chưa có kết luận") },
            { "Conclusion unavailable", Pack("Заключението е недостъпно|Zaključak nije dostupan|Závěr není dostupný|Conclusion indisponible|Ergebnis nicht verfügbar|Következtetés nem elérhető|Conclusione non disponibile|Wniosek niedostępny|Concluzie indisponibilă|Заключение недоступно|Zaključak nije dostupan|Záver nie je dostupný|Sklep ni na voljo|Conclusión no disponible|Sonuç kullanılamıyor|Kết luận không khả dụng") },
            { "Record", Pack("Запис|Zabilježiti|Zaznamenat|Consigner|Dokumentieren|Rögzítés|Registrare|Zarejestrować|Înregistrare|Записать|Zabeležiti|Zaznamenať|Zabeležiti|Registrar|Kaydet|Ghi nhận") },
            { "Observe", Pack("Наблюдение|Promatrati|Sledovat|Observer|Beobachten|Megfigyelés|Osservare|Obserwować|Observare|Наблюдать|Posmatrati|Sledovať|Opazovati|Observar|Gözle|Theo dõi") },
            { "Priority attention", Pack("Приоритетно внимание|Prioritetna pozornost|Zvýšená pozornost|Attention prioritaire|Besondere Beachtung|Kiemelt figyelem|Attenzione prioritaria|Szczególna uwaga|Atenție prioritară|Повышенное внимание|Prioritetna pažnja|Zvýšená pozornosť|Posebna pozornost|Atención prioritaria|Öncelikli dikkat|Chú ý ưu tiên") },
            { "Timely action", Pack("Навременни мерки|Pravodobno postupanje|Včasné opatření|Intervention rapide|Zeitnah handeln|Időben intézkedni|Intervento tempestivo|Terminowe działanie|Intervenție promptă|Своевременные меры|Pravovremeno postupanje|Včasné opatrenie|Pravočasno ukrepanje|Intervención oportuna|Zamanında müdahale|Xử lý kịp thời") },
            { "Source analysis", Pack("Изходен анализ|Izvorna analiza|Zdrojová analýza|Analyse source|Quellanalyse|Forráselemzés|Analisi di origine|Analiza źródłowa|Analiză sursă|Исходный анализ|Izvorna analiza|Zdrojová analýza|Izvorna analiza|Análisis de origen|Kaynak analiz|Phân tích nguồn") },
            { "Saved at", Pack("Запазено на|Spremljeno|Uloženo|Enregistré le|Gespeichert am|Mentés ideje|Salvato il|Zapisano|Salvat la|Сохранено|Sačuvano|Uložené|Shranjeno|Guardado el|Kaydedildi|Thời gian lưu") },
            { "Select all", Pack("Избери всички|Odaberi sve|Vybrat vše|Tout sélectionner|Alle auswählen|Összes kijelölése|Seleziona tutto|Zaznacz wszystko|Selectează tot|Выбрать всё|Izaberi sve|Vybrať všetko|Izberi vse|Seleccionar todo|Tümünü seç|Chọn tất cả") },
            { "Load project", Pack("Зареди|Učitaj|Načíst|Charger|Laden|Betöltés|Carica|Wczytaj|Încarcă|Загрузить|Učitaj|Načítať|Naloži|Cargar|Yükle|Tải dự án") },
            { "Refresh", Pack("Обнови|Osvježi|Obnovit|Actualiser|Aktualisieren|Frissítés|Aggiorna|Odśwież|Actualizează|Обновить|Osveži|Obnoviť|Osveži|Actualizar|Yenile|Làm mới") },
            { "Import", Pack("Импорт|Uvoz|Importovat|Importer|Importieren|Importálás|Importa|Importuj|Importă|Импорт|Uvoz|Importovať|Uvozi|Importar|İçe aktar|Nhập") },
            { "Export data", Pack("Износ данни|Izvoz podataka|Export dat|Exporter données|Datenexport|Adat export|Esporta dati|Eksport danych|Export date|Экспорт данных|Izvoz podataka|Export údajov|Izvoz podatkov|Exportar datos|Veri aktar|Xuất dữ liệu") },
            { "Export reports", Pack("Износ отчети|Izvoz izvješća|Export zpráv|Exporter rapports|Berichte exportieren|Jelentés export|Esporta rapporti|Eksport raportów|Export rapoarte|Экспорт отчётов|Izvoz izveštaja|Export správ|Izvoz poročil|Exportar informes|Rapor aktar|Xuất báo cáo") },
            { "Merge reports", Pack("Обедини отчети|Spoji izvješća|Sloučit zprávy|Fusionner rapports|Berichte bündeln|Jelentések egyesítése|Unisci rapporti|Scal raporty|Îmbină rapoarte|Объединить отчёты|Spoji izveštaje|Zlúčiť správy|Združi poročila|Combinar informes|Raporları birleştir|Gộp báo cáo") },
            { "Delete", Pack("Изтрий|Izbriši|Odstranit|Supprimer|Löschen|Törlés|Elimina|Usuń|Șterge|Удалить|Izbriši|Odstrániť|Izbriši|Eliminar|Sil|Xóa") },
            { "Click a row", Pack("Щракнете ред|Kliknite red|Klikněte na řádek|Cliquez une ligne|Zeile anklicken|Kattintson egy sorra|Fare clic su una riga|Kliknij wiersz|Faceți clic pe rând|Щёлкните строку|Kliknite red|Kliknite na riadok|Kliknite vrstico|Haga clic en una fila|Bir satıra tıklayın|Nhấp vào một hàng") },
            { "Selected", Pack("Избрани|Odabrano|Vybráno|Sélectionné|Ausgewählt|Kijelölve|Selezionati|Wybrano|Selectate|Выбрано|Izabrano|Vybrané|Izbrano|Seleccionados|Seçili|Đã chọn") },
            { "No report", Pack("Без отчет|Bez izvješća|Bez zprávy|Aucun rapport|Kein Bericht|Nincs jelentés|Nessun rapporto|Brak raportu|Fără raport|Нет отчёта|Bez izveštaja|Bez správy|Ni poročila|Sin informe|Rapor yok|Không có báo cáo") },
            { "Cancel", Pack("Отказ|Odustani|Zrušit|Annuler|Abbrechen|Mégse|Annulla|Anuluj|Anulează|Отмена|Otkaži|Zrušiť|Prekliči|Cancelar|İptal|Hủy") },
            { "Import project", Pack("Импорт на проект|Uvoz projekta|Import projektu|Importer un projet|Projekt importieren|Projekt importálása|Importa progetto|Import projektu|Import proiect|Импорт проекта|Uvoz projekta|Import projektu|Uvoz projekta|Importar proyecto|Proje içe aktar|Nhập dự án") },
            { "Consolidate reports", Pack("Обедини отчети|Objedini izvješća|Sloučit zprávy|Consolider rapports|Berichte konsolidieren|Jelentések összesítése|Consolida rapporti|Scal raporty|Consolidează rapoarte|Сводный отчёт|Objedini izveštaje|Konsolidovať správy|Združi poročila|Consolidar informes|Raporları birleştir|Tổng hợp báo cáo") },
            { "Introduction", Pack("Въведение|Uvod|Úvod|Introduction|Einleitung|Bevezetés|Introduzione|Wprowadzenie|Introducere|Введение|Uvod|Úvod|Uvod|Introducción|Giriş|Giới thiệu") },
            { "Project", Pack("Проект|Projekt|Projekt|Projet|Projekt|Projekt|Progetto|Projekt|Proiect|Проект|Projekat|Projekt|Projekt|Proyecto|Proje|Dự án") },
            { "Remark", Pack("Бележка|Napomena|Poznámka|Remarque|Bemerkung|Megjegyzés|Nota|Uwaga|Observație|Примечание|Napomena|Poznámka|Opomba|Observación|Not|Ghi chú") },
            { "Test time", Pack("Време на изпитване|Vrijeme ispitivanja|Čas zkoušky|Date d’essai|Prüfzeit|Vizsgálat ideje|Ora prova|Czas próby|Timp test|Время испытания|Vreme ispitivanja|Čas skúšky|Čas preskusa|Hora de prueba|Test zamanı|Thời gian thử") },
            { "Project folder", Pack("Папка на проекта|Mapa projekta|Složka projektu|Dossier du projet|Projektordner|Projektmappa|Cartella progetto|Folder projektu|Dosar proiect|Папка проекта|Fascikla projekta|Priečinok projektu|Mapa projekta|Carpeta del proyecto|Proje klasörü|Thư mục dự án") },
            { "Continue", Pack("Продължи|Nastavi|Pokračovat|Continuer|Weiter|Tovább|Continua|Kontynuuj|Continuă|Продолжить|Nastavi|Pokračovať|Nadaljuj|Continuar|Devam|Tiếp tục") }
            ,{ "Loaded {0} projects", Pack("Заредени са {0} проекта|Učitano {0} projekata|Načteno {0} projektů|{0} projets chargés|{0} Projekte geladen|{0} projekt betöltve|Caricati {0} progetti|Załadowano {0} projektów|S-au încărcat {0} proiecte|Загружено проектов: {0}|Učitano {0} projekata|Načítaných projektov: {0}|Naloženih projektov: {0}|Se cargaron {0} proyectos|{0} proje yüklendi|Đã tải {0} dự án") }
            ,{ "; skipped {0} unread directories", Pack("; пропуснати са {0} нечетими папки|; preskočeno {0} nečitljivih mapa|; přeskočeno {0} nečitelných složek| ; {0} dossiers illisibles ignorés|; {0} unlesbare Ordner übersprungen|; {0} olvashatatlan mappa kihagyva|; ignorate {0} cartelle illeggibili|; pominięto {0} nieczytelnych folderów|; s-au omis {0} dosare inaccesibile|; пропущено нечитаемых папок: {0}|; preskočeno {0} nečitljivih fascikli|; preskočených nečitateľných priečinkov: {0}|; preskočenih neberljivih map: {0}|; se omitieron {0} carpetas ilegibles|; okunamayan {0} klasör atlandı|; bỏ qua {0} thư mục không đọc được") }
            ,{ "Waiting for the project tree to provide a root or project path.", Pack("Очаква се коренна папка или път към проект от дървото.|Čeka se korijenska mapa ili putanja projekta iz stabla.|Čeká se na kořenovou složku nebo cestu projektu ze stromu.|En attente du dossier racine ou du chemin de projet fourni par l’arborescence.|Warte auf Stammordner oder Projektpfad aus dem Projektbaum.|Várakozás a projektfa gyökérmappájára vagy projektútvonalára.|In attesa della cartella radice o del percorso progetto dall'albero.|Oczekiwanie na folder główny lub ścieżkę projektu z drzewa.|Se așteaptă dosarul rădăcină sau calea proiectului din arbore.|Ожидание корневой папки или пути проекта из дерева.|Čeka se osnovna fascikla ili putanja projekta iz stabla.|Čaká sa na koreňový priečinok alebo cestu projektu zo stromu.|Čakanje na korensko mapo ali pot projekta iz drevesa.|Esperando la carpeta raíz o la ruta del proyecto del árbol.|Proje ağacından kök klasör veya proje yolu bekleniyor.|Đang chờ thư mục gốc hoặc đường dẫn dự án từ cây dự án.") }
            ,{ "Scanning the project data root…", Pack("Сканиране на коренната папка с проектни данни…|Skeniranje korijenske mape podataka projekta…|Prohledávání kořenové složky projektových dat…|Analyse du dossier racine des données de projet…|Projekt-Datenstammordner wird durchsucht…|Projektadatok gyökérmappájának vizsgálata…|Scansione della cartella radice dei dati di progetto…|Skanowanie folderu głównego danych projektu…|Se scanează dosarul rădăcină al datelor proiectului…|Сканирование корневой папки данных проекта…|Skeniranje osnovne fascikle podataka projekta…|Prehľadávanie koreňového priečinka projektových dát…|Pregledovanje korenske mape podatkov projekta…|Analizando la carpeta raíz de datos del proyecto…|Proje veri kök klasörü taranıyor…|Đang quét thư mục gốc dữ liệu dự án…") }
            ,{ "Loaded from the project tree and pinned first:", Pack("Зареден от дървото и поставен най-отгоре:|Učitano iz stabla i prikazano prvo:|Načteno ze stromu a připnuto nahoru:|Chargé depuis l’arborescence et épinglé en premier :|Aus Projektbaum geladen und oben angeheftet:|Projektfából betöltve, első helyre rögzítve:|Caricato dall'albero e fissato in alto:|Załadowano z drzewa i przypięto na górze:|Încărcat din arbore și fixat primul:|Загружено из дерева и закреплено первым:|Učitano iz stabla i prikazano prvo:|Načítané zo stromu a pripnuté navrchu:|Naloženo iz drevesa in pripeto na vrh:|Cargado desde el árbol y fijado primero:|Ağaçtan yüklendi ve en üste sabitlendi:|Đã tải từ cây dự án và ghim lên đầu:") }
            ,{ "Project path emitted:", Pack("Изведен път към проект:|Poslana putanja projekta:|Předána cesta projektu:|Chemin du projet transmis :|Projektpfad ausgegeben:|Projektútvonal kiadva:|Percorso progetto inviato:|Przekazano ścieżkę projektu:|Calea proiectului a fost transmisă:|Путь проекта передан:|Poslata putanja projekta:|Cesta projektu odoslaná:|Pot projekta posredovana:|Ruta del proyecto enviada:|Proje yolu gönderildi:|Đã xuất đường dẫn dự án:") }
            ,{ "Completed: {0} item(s)", Pack("Завършено: {0} елемента|Dovršeno: {0} stavki|Dokončeno: {0} položek|Terminé : {0} élément(s)|Abgeschlossen: {0} Element(e)|Kész: {0} elem|Completato: {0} elemento/i|Ukończono: {0} elementów|Finalizat: {0} elemente|Завершено элементов: {0}|Završeno: {0} stavki|Dokončených položiek: {0}|Končano: {0} elementov|Completado: {0} elementos|Tamamlandı: {0} öğe|Hoàn thành: {0} mục") }
            ,{ "Operation failed; see error details.", Pack("Операцията е неуспешна; вижте подробностите за грешката.|Operacija nije uspjela; pogledajte detalje pogreške.|Operace selhala; viz podrobnosti chyby.|Échec de l’opération ; consultez les détails de l’erreur.|Vorgang fehlgeschlagen; siehe Fehlerdetails.|A művelet sikertelen; lásd a hiba részleteit.|Operazione non riuscita; vedere i dettagli dell'errore.|Operacja nie powiodła się; sprawdź szczegóły błędu.|Operațiunea a eșuat; consultați detaliile erorii.|Операция не выполнена; см. подробности ошибки.|Operacija nije uspela; pogledajte detalje greške.|Operácia zlyhala; pozrite si podrobnosti chyby.|Postopek ni uspel; glejte podrobnosti napake.|La operación falló; consulte los detalles del error.|İşlem başarısız oldu; hata ayrıntılarına bakın.|Thao tác thất bại; xem chi tiết lỗi.") }
        };

        static string[] Pack(string value) { return value.Split('|'); }

        static ProjectManagementLanguages()
        {
            foreach (KeyValuePair<string, string[]> item in Texts)
                if (item.Value.Length != 16) throw new InvalidOperationException("Language vocabulary length: " + item.Key);
        }

        internal static void Validate(int value)
        {
            if (value < 0 || value >= Names.Length) throw new ArgumentOutOfRangeException("LanguageIndex", "Expected 0..18.");
        }

        internal static int IndexOfCode(string code)
        { return Xiongxin.OWTS.Localization.UiLanguage.FindCode(code); }

        internal static string Text(int language, string english, string chinese)
        {
            Validate(language);
            if (language == 0) return chinese;
            if (language == 18) return Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(chinese);
            string localized; if (Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(language, english, out localized)) return localized;
            language = Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(language);
            if (language == 1 || string.IsNullOrEmpty(english)) return Xiongxin.OWTS.Localization.UiLanguage.Translate(language, english);
            string trimmed = english.Trim();
            string[] values;
            if (!Texts.TryGetValue(trimmed, out values)) return english;
            string prefix = english.Substring(0, english.Length - english.TrimStart().Length);
            string suffix = english.Substring(english.TrimEnd().Length);
            return prefix + values[language - 2] + suffix;
        }
    }
}
