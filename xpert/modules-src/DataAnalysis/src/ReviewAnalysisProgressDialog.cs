using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HVX.PdReview
{
    // UI-thread only. Cancellation is a request to the owner, never an abort of
    // the current LOC. Only FinishAndClose (or owner disposal) ends this window.
    internal sealed class ReviewAnalysisProgressDialog:Form
    {
        readonly int languageIndex;
        readonly Panel viewport,content;
        readonly Label overallLabel,fileLabel,currentLabel,stateLabel;
        readonly ProgressBar overallProgress,currentProgress;
        readonly RoundedReviewButton cancel;
        readonly ToolTip fileTip;
        readonly ReviewTaskbarProgress taskbar=new ReviewTaskbarProgress();
        Form hostOwner;
        bool minimizedWithOwner;
        int completedFiles,totalFiles;
        bool stopping,allowClose,arranging,readingSavedAnalysis;
        internal event EventHandler CancelRequested;

        internal ReviewAnalysisProgressDialog(int languageIndex,Font font)
        {
            ReviewLanguages.Validate(languageIndex);this.languageIndex=languageIndex;
            Name="ReviewAnalysisProgressDialog";Text=L("Analysis progress");Font=font??SystemFonts.MessageBoxFont;
            AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.Manual;
            ShowInTaskbar=false;TopMost=false;MinimizeBox=MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedDialog;SizeGripStyle=SizeGripStyle.Hide;
            BackColor=ReviewTheme.SurfaceAlt;ForeColor=ReviewTheme.Ink;
            viewport=new Panel{Name="AnalysisProgressViewport",Dock=DockStyle.Fill,AutoScroll=true,BackColor=BackColor};
            content=new Panel{Name="AnalysisProgressContent",BackColor=BackColor};
            overallLabel=Label("OverallAnalysisLabel",L("Overall files")+" 0 / 0");
            currentLabel=Label("CurrentAnalysisLabel",L("Current file"));
            fileLabel=Label("CurrentAnalysisFile",ReviewLanguages.Text(languageIndex,"Pending","等待中"));
            fileLabel.UseMnemonic=false;fileLabel.AutoEllipsis=true;
            fileTip=new ToolTip{ShowAlways=true,AutoPopDelay=20000};
            stateLabel=Label("CurrentAnalysisState",L("Analyzing current file…"));
            overallProgress=new ProgressBar{Name="OverallAnalysisProgress",Minimum=0,Maximum=1,Style=ProgressBarStyle.Continuous};
            currentProgress=new ProgressBar{Name="CurrentFileAnalysisProgress",Style=ProgressBarStyle.Marquee,MarqueeAnimationSpeed=35};
            cancel=new RoundedReviewButton{Name="CancelAnalysis",Text=ReviewLanguages.Text(languageIndex,"Cancel","取消"),
                BackColor=ReviewTheme.Accent,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false};
            cancel.FlatAppearance.BorderColor=ReviewTheme.Accent;cancel.Click+=(s,e)=>RequestStop();
            content.Controls.AddRange(new Control[]{overallLabel,overallProgress,currentLabel,fileLabel,currentProgress,stateLabel,cancel});
            viewport.Controls.Add(content);Controls.Add(viewport);
            ClientSize=new Size(470,240);Arrange();
        }
        Label Label(string name,string text)
        {return new Label{Name=name,Text=text,AutoSize=false,UseMnemonic=false,ForeColor=ReviewTheme.Ink};}
        string L(string key){return ReviewAnalysisProgressText.Text(languageIndex,key);}

        internal void BeginReadingSavedAnalysis(int completed,int total)
        {
            if(IsDisposed||Disposing)return;
            readingSavedAnalysis=true;stopping=true;cancel.Enabled=false;
            cancel.Text=L("Reading saved analysis");
            Text=L("Reading saved analysis");
            UpdateReadingSavedAnalysis("",completed,total);
        }
        internal void UpdateReadingSavedAnalysis(string partPath,int completed,int total)
        {
            if(IsDisposed||Disposing)return;
            readingSavedAnalysis=true;
            total=Math.Max(0,total);completed=Math.Max(0,Math.Min(total,completed));
            completedFiles=completed;totalFiles=total;
            int maximum=Math.Max(1,total);
            if(overallProgress.Value>maximum)overallProgress.Value=0;
            overallProgress.Maximum=maximum;overallProgress.Value=completed;
            overallLabel.Text=L("Reading saved analysis")+" "+completed+" / "+total;
            string name=partPath??string.Empty;
            try{if(!string.IsNullOrEmpty(name))name=Path.GetFileName(name);}catch(ArgumentException){}
            fileLabel.Text=string.IsNullOrEmpty(name)?L("Reading diagnostics segments"):name;
            fileTip.SetToolTip(fileLabel,string.IsNullOrEmpty(partPath)?fileLabel.Text:partPath);
            fileLabel.AccessibleDescription=fileTip.GetToolTip(fileLabel);
            currentLabel.Text=L("Diagnostics segment");
            currentProgress.Style=ProgressBarStyle.Continuous;
            currentProgress.Maximum=maximum;
            if(currentProgress.Value>maximum)currentProgress.Value=0;
            currentProgress.Value=completed;
            stateLabel.Text=completed>=total&&total>0?L("Saved analysis segments loaded"):L("Reading saved analysis");
            cancel.Enabled=false;PublishTaskbarProgress();Arrange();
        }
        internal void UpdateProgress(string path,int completed,int total)
        {
            if(IsDisposed||Disposing)return;
            readingSavedAnalysis=false;
            total=Math.Max(0,total);completed=Math.Max(0,Math.Min(total,completed));
            completedFiles=completed;totalFiles=total;
            int maximum=Math.Max(1,total);
            if(overallProgress.Value>maximum)overallProgress.Value=completed;
            overallProgress.Maximum=maximum;overallProgress.Value=completed;
            overallLabel.Text=ReviewLanguages.Text(languageIndex,"Processed files","已处理文件")+" "+completed+" / "+total;
            string name=path??string.Empty;
            try{name=Path.GetFileName(name);}catch(ArgumentException){/* A display name need not be a valid path. */}
            fileLabel.Text=string.IsNullOrEmpty(name)?ReviewLanguages.Text(languageIndex,"Pending","等待中"):name;
            fileTip.SetToolTip(fileLabel,string.IsNullOrEmpty(path)?fileLabel.Text:path);
            fileLabel.AccessibleDescription=string.IsNullOrEmpty(path)?fileLabel.Text:path;
            currentLabel.Text=L("Current file");
            // Task17 supplies no pulse-level progress. The current-file bar is
            // intentionally indeterminate; no invented percentage is shown.
            currentProgress.Style=ProgressBarStyle.Marquee;
            stateLabel.Text=completed==total&&total>0?
                ReviewLanguages.Text(languageIndex,"Files processed; organizing results…","文件已处理完毕，正在整理结果…"):
                L(stopping?"Waiting for the current file to finish":"Analyzing current file…");
            PublishTaskbarProgress();
            Arrange();
        }
        internal void SetStopping()
        {
            if(IsDisposed||Disposing)return;
            stopping=true;cancel.Enabled=false;cancel.Text=L("Waiting for the current file to finish");
            stateLabel.Text=cancel.Text;PublishTaskbarProgress();Arrange();
        }
        void RequestStop()
        {
            if(stopping||allowClose||IsDisposed||Disposing)return;
            SetStopping();var handler=CancelRequested;if(handler!=null)handler(this,EventArgs.Empty);
        }
        internal void FinishAndClose()
        {
            if(IsDisposed||Disposing)return;
            allowClose=true;currentProgress.MarqueeAnimationSpeed=0;taskbar.Clear();Close();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // X is a soft stop; host teardown / application or OS exit must
            // remain free to close and dispose this owned window normally.
            if(!allowClose&&!Disposing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;RequestStop();}
            base.OnFormClosing(e);
        }
        protected override bool ProcessDialogKey(Keys keyData)
        {if(keyData==Keys.Escape){RequestStop();return true;}return base.ProcessDialogKey(keyData);}
        internal void PrepareForDisplay(Form owner)
        {
            // Set native window styles, ownership and final bounds before Show
            // creates the first visible window. Owned windows stay above their
            // owner without global TopMost or a separate taskbar window.
            if(owner!=null&&!owner.IsDisposed) { Owner=owner;Icon=owner.Icon; }
            FitToWorkingArea();Arrange();
        }
        protected override void OnLoad(EventArgs e)
        {
            // Finalize owner, taskbar style and bounds before the first visible
            // frame; changing these in Shown briefly exposed the default window.
            hostOwner=Owner;
            if(hostOwner!=null){ShowInTaskbar=false;hostOwner.Resize+=OnOwnerResize;OnOwnerResize(hostOwner,EventArgs.Empty);}
            FitToWorkingArea();Arrange();base.OnLoad(e);
        }
        protected override void OnShown(EventArgs e){base.OnShown(e);PublishTaskbarProgress();}
        void OnOwnerResize(object sender,EventArgs e)
        {
            if(IsDisposed||Disposing||hostOwner==null||hostOwner.IsDisposed)return;
            if(hostOwner.WindowState==FormWindowState.Minimized)
            {
                if(WindowState!=FormWindowState.Minimized){minimizedWithOwner=true;WindowState=FormWindowState.Minimized;}
            }
            else if(minimizedWithOwner)
            {
                minimizedWithOwner=false;WindowState=FormWindowState.Normal;
            }
        }
        void PublishTaskbarProgress()
        {
            if(!IsHandleCreated||IsDisposed||Disposing)return;
            IntPtr hwnd=Owner!=null&&!Owner.IsDisposed?Owner.Handle:Handle;
            taskbar.Update(hwnd,completedFiles,totalFiles,stopping);
        }
        protected override void OnSizeChanged(EventArgs e)
        {base.OnSizeChanged(e);Arrange();}
        protected override void OnFontChanged(EventArgs e)
        {base.OnFontChanged(e);Arrange();}
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified)
        {base.ScaleControl(factor,specified);Arrange();if(IsHandleCreated)FitToWorkingArea();}
        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                if(hostOwner!=null&&!hostOwner.IsDisposed)hostOwner.Resize-=OnOwnerResize;
                taskbar.Dispose();
                if(fileTip!=null)fileTip.Dispose();
            }
            base.Dispose(disposing);
        }
        internal static Rectangle CenteredBounds(Rectangle area,Size requested)
        {
            int width=Math.Max(1,Math.Min(requested.Width,area.Width));
            int height=Math.Max(1,Math.Min(requested.Height,area.Height));
            return new Rectangle(area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height);
        }
        void FitToWorkingArea()
        {
            // Center on the owner's monitor, not the owner rectangle: a small
            // or off-center LabVIEW window must not pull this dialog aside.
            Rectangle area=Owner!=null&&!Owner.IsDisposed?Screen.FromControl(Owner).WorkingArea:Screen.FromControl(this).WorkingArea;
            int availableWidth=Math.Max(1,area.Width-(Width-ClientSize.Width));
            int availableHeight=Math.Max(1,area.Height-(Height-ClientSize.Height));
            double scale=Math.Max(1,Font.SizeInPoints/9.0);
            int margin=(int)Math.Ceiling(16*scale);
            int waitingWidth=TextRenderer.MeasureText(L("Waiting for the current file to finish"),Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+24;
            int desiredWidth=Math.Max((int)Math.Ceiling(470*scale),waitingWidth+margin*2);
            // Measure at the compact preferred width first; do not inherit the
            // old large client area or repeatedly grow it during scaling.
            ClientSize=new Size(Math.Min(availableWidth,desiredWidth),Math.Min(availableHeight,(int)Math.Ceiling(240*scale)));
            Arrange();
            int desiredHeight=content.Height+(content.Width>viewport.ClientSize.Width?SystemInformation.HorizontalScrollBarHeight:0);
            ClientSize=new Size(ClientSize.Width,Math.Min(availableHeight,desiredHeight));
            Arrange();
            Bounds=CenteredBounds(area,Size);
        }
        void Arrange()
        {
            if(arranging||viewport==null||cancel==null)return;arranging=true;
            try{
                double scale=Math.Max(1,Font.SizeInPoints/9.0);int gap=(int)Math.Ceiling(6*scale),margin=(int)Math.Ceiling(16*scale);
                int waitingWidth=TextRenderer.MeasureText(L("Waiting for the current file to finish"),Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+24;
                int width=Math.Max(waitingWidth,Math.Max((int)(280*scale),viewport.ClientSize.Width-margin*2-(viewport.VerticalScroll.Visible?SystemInformation.VerticalScrollBarWidth:0)));
                int y=margin;PlaceLabel(overallLabel,margin,ref y,width,gap);
                overallProgress.SetBounds(margin,y,width,Math.Max(14,(int)Math.Ceiling(14*scale)));y=overallProgress.Bottom+gap*2;
                PlaceLabel(currentLabel,margin,ref y,width,gap/2);
                // Reserve two lines even before the first file arrives, so long
                // names do not resize the window. The tooltip exposes the full
                // path without forcing a monitor-wide analysis dialog.
                fileLabel.SetBounds(margin,y,width,Font.Height*2+4);y=fileLabel.Bottom+gap;
                currentProgress.SetBounds(margin,y,width,Math.Max(14,(int)Math.Ceiling(14*scale)));y=currentProgress.Bottom+gap;
                PlaceLabel(stateLabel,margin,ref y,width,gap);
                cancel.SetBounds(margin,y,width,Math.Max((int)Math.Ceiling(32*scale),Font.Height+gap*2));
                content.Size=new Size(width+margin*2,cancel.Bottom+margin);viewport.AutoScrollMinSize=content.Size;
            }finally{arranging=false;}
        }
        void PlaceLabel(Label label,int x,ref int y,int width,int after)
        {
            int height=TextRenderer.MeasureText(label.Text,Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;
            label.SetBounds(x,y,width,Math.Max(Font.Height+2,height+2));y=label.Bottom+after;
        }
    }

    internal static class ReviewAnalysisProgressText
    {
        static readonly string[] Keys={
            "Analysis progress","Overall files","Current file","Analyzing current file…","Waiting for the current file to finish",
            "Reading saved analysis","Diagnostics segment","Reading diagnostics segments","Saved analysis segments loaded"
        };
        static readonly string[][] Packs={
            "分析进度|总文件进度|当前 LOC 文件|正在分析当前文件…|正在等待当前文件完成|读取旧有分析|诊断分段|正在读取诊断分段|已加载分析分段".Split('|'),
            Keys,
            "Напредък на анализа|Общо файлове|Текущ файл|Анализ на текущия файл…|Изчакване на завършването на текущия файл|Четене на запазен анализ|Диагностичен сегмент|Четене на диагностични сегменти|Сегментите на анализа са заредени".Split('|'),
            "Napredak analize|Ukupno datoteka|Trenutna datoteka|Analiza trenutne datoteke…|Čekanje završetka trenutne datoteke|Čitanje sačuvane analize|Dijagnostički segment|Čitanje dijagnostičkih segmenata|Segmenti analize su učitani".Split('|'),
            "Průběh analýzy|Celkem souborů|Aktuální soubor|Analyzuje se aktuální soubor…|Čekání na dokončení aktuálního souboru|Načítání uložené analýzy|Diagnostický segment|Načítání diagnostických segmentů|Segmenty analýzy načteny".Split('|'),
            "Progression de l’analyse|Total des fichiers|Fichier actuel|Analyse du fichier actuel…|En attente de la fin du fichier actuel|Lecture de l’analyse enregistrée|Segment de diagnostics|Lecture des segments de diagnostics|Segments d’analyse chargés".Split('|'),
            "Analysefortschritt|Dateien insgesamt|Aktuelle Datei|Aktuelle Datei wird analysiert…|Warten auf Abschluss der aktuellen Datei|Gespeicherte Analyse lesen|Diagnosesegment|Diagnosesegmente werden gelesen|Analysesegmente geladen".Split('|'),
            "Elemzés folyamata|Összes fájl|Aktuális fájl|Az aktuális fájl elemzése…|Várakozás az aktuális fájl befejezésére|Mentett elemzés olvasása|Diagnosztikai szegmens|Diagnosztikai szegmensek olvasása|Elemzési szegmensek betöltve".Split('|'),
            "Avanzamento analisi|File totali|File corrente|Analisi del file corrente…|In attesa del completamento del file corrente|Lettura analisi salvata|Segmento diagnostica|Lettura segmenti diagnostica|Segmenti analisi caricati".Split('|'),
            "Postęp analizy|Łącznie plików|Bieżący plik|Analiza bieżącego pliku…|Oczekiwanie na zakończenie bieżącego pliku|Odczyt zapisanej analizy|Segment diagnostyczny|Odczyt segmentów diagnostycznych|Segmenty analizy wczytane".Split('|'),
            "Progresul analizei|Total fișiere|Fișier curent|Se analizează fișierul curent…|Se așteaptă finalizarea fișierului curent|Citire analiză salvată|Segment de diagnostic|Citire segmente de diagnostic|Segmentele analizei au fost încărcate".Split('|'),
            "Ход анализа|Всего файлов|Текущий файл|Анализ текущего файла…|Ожидание завершения текущего файла|Чтение сохранённого анализа|Сегмент диагностики|Чтение сегментов диагностики|Сегменты анализа загружены".Split('|'),
            "Napredak analize|Ukupno datoteka|Trenutna datoteka|Analiza trenutne datoteke…|Čekanje završetka trenutne datoteke|Čitanje sačuvane analize|Dijagnostički segment|Čitanje dijagnostičkih segmenata|Segmenti analize su učitani".Split('|'),
            "Priebeh analýzy|Celkom súborov|Aktuálny súbor|Analyzuje sa aktuálny súbor…|Čakanie na dokončenie aktuálneho súboru|Načítanie uloženej analýzy|Diagnostický segment|Načítanie diagnostických segmentov|Segmenty analýzy načítané".Split('|'),
            "Napredek analize|Skupaj datotek|Trenutna datoteka|Analiza trenutne datoteke…|Čakanje na dokončanje trenutne datoteke|Branje shranjene analize|Diagnostični segment|Branje diagnostičnih segmentov|Segmenti analize naloženi".Split('|'),
            "Progreso del análisis|Total de archivos|Archivo actual|Analizando el archivo actual…|Esperando a que finalice el archivo actual|Leyendo análisis guardado|Segmento de diagnóstico|Leyendo segmentos de diagnóstico|Segmentos de análisis cargados".Split('|'),
            "Analiz ilerlemesi|Toplam dosya|Geçerli dosya|Geçerli dosya analiz ediliyor…|Geçerli dosyanın tamamlanması bekleniyor|Kayıtlı analiz okunuyor|Tanılama segmenti|Tanılama segmentleri okunuyor|Analiz segmentleri yüklendi".Split('|'),
            "Tiến độ phân tích|Tổng số tệp|Tệp hiện tại|Đang phân tích tệp hiện tại…|Đang chờ tệp hiện tại hoàn tất|Đang đọc phân tích đã lưu|Phân đoạn chẩn đoán|Đang đọc các phân đoạn chẩn đoán|Đã tải phân đoạn phân tích".Split('|')
        };
        internal static string Text(int language,string key)
        {
            int index=Array.IndexOf(Keys,key);
            if(index<0)return ReviewLanguages.Text(language,key,key);
            if(language==18)return ReviewTraditionalChinese.Text(Packs[0][index]);
            string translated;if(language!=0&&Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(language,Packs[1][index],out translated))return translated;
            return Packs[Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(language)][index];
        }
    }
}
