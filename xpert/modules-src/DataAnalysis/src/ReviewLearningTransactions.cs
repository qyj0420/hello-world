using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // This folder is an append-only pending dataset, never an inference model.
    // Only GUID transactions bearing our exact owner marker can be archived.
    internal static class ReviewLearningTransactions
    {
        const string Owner="HVX.PdReview.Task12Learning.v1";
        internal const int ManualDischargeTypeMaxLength=160;
        static readonly object gate=new object();
        internal static int Count(string root){lock(gate)return Active(root).Length;}
        static string[] Active(string root)
        {
            string path=Path.Combine(root,"active");
            if(!Directory.Exists(path))return new string[0];
            ValidateRoot(root,false);
            return Directory.GetDirectories(path).Where(IsOwnedTransaction).OrderBy(CreatedUtc).ThenBy(p=>Path.GetFileName(p),StringComparer.Ordinal).ToArray();
        }
        static DateTime CreatedUtc(string path)
        {
            try
            {
                var file=new FileInfo(Path.Combine(path,"manifest.json"));if(file.Length>4*1024*1024)throw new InvalidDataException("Owned sample manifest exceeds the supported budget.");
                var manifest=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Deserialize<Dictionary<string,object>>(File.ReadAllText(file.FullName,Encoding.UTF8));
                object value;DateTime created;
                if(manifest!=null&&manifest.TryGetValue("created_utc",out value)&&DateTime.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out created)&&created.Kind==DateTimeKind.Utc)return created;
            }
            catch(Exception ex){throw new InvalidDataException("Cannot determine the creation time of owned sample "+Path.GetFileName(path)+"; no samples were removed.",ex);}
            throw new InvalidDataException("Owned sample has no valid UTC creation time; no samples were removed: "+Path.GetFileName(path));
        }
        static bool IsOwnedTransaction(string path)
        {
            string name=Path.GetFileName(path);Guid id;
            return (name.Length==32||name.Length>33)&&Guid.TryParseExact(name.Substring(name.Length-32),"N",out id)&&
                File.Exists(Path.Combine(path,"owner.txt"))&&File.ReadAllText(Path.Combine(path,"owner.txt"),Encoding.UTF8)==Owner&&
                File.Exists(Path.Combine(path,"manifest.json"));
        }
        static void ValidateRoot(string root,bool create)
        {
            root=Path.GetFullPath(root);
            RejectReparse(root);
            if(String.Equals(root.TrimEnd('\\','/'),Path.GetPathRoot(root).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("A drive root cannot be a learning store.");
            string marker=Path.Combine(root,"owner.txt");
            if(File.Exists(marker))
            {if(File.ReadAllText(marker,Encoding.UTF8)!=Owner)throw new InvalidOperationException("The learning directory belongs to another application.");}
            else if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Refusing to adopt an existing unowned learning directory.");
            else if(create){Directory.CreateDirectory(root);File.WriteAllText(marker,Owner,new UTF8Encoding(false));}
            RejectReparse(root);
        }
        static void RejectReparse(string path)
        {
            for(var current=new DirectoryInfo(Path.GetFullPath(path));current!=null;current=current.Parent)
                if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Learning transactions cannot traverse a redirected directory.");
        }
        internal static void Add(string root,RecognitionSnapshot input,string model,string sha,string labels,string operatorName,ReviewTask12Runtime runtime)
        {
            Add(root,input,model,sha,labels,operatorName,"",true,"canonical_confirmed",runtime);
        }
        internal static void Add(string root,RecognitionSnapshot input,string model,string sha,string labels,string operatorName,
            string manualDischargeType,bool task12Compatible,string taxonomyStatus,ReviewTask12Runtime runtime)
        { Add(root,input,model,sha,labels,operatorName,manualDischargeType,task12Compatible,taxonomyStatus,runtime,null); }
        internal static void Add(string root,RecognitionSnapshot input,string model,string sha,string labels,string operatorName,
            string manualDischargeType,bool task12Compatible,string taxonomyStatus,ReviewTask12Runtime runtime,Func<bool> current,object commitGate=null)
        {
            string labelText=labels??"";
            if(labelText.Length>256||labelText.Any(Char.IsControl))throw new ArgumentException("Canonical label keys are invalid.");
            labels=labelText.Trim();manualDischargeType=NormalizeManualDischargeType(manualDischargeType);
            taxonomyStatus=(taxonomyStatus??"").Trim();
            if(task12Compatible&&labels.Length==0)throw new ArgumentException("A Task12-compatible sample requires at least one canonical label key.");
            if(task12Compatible&&runtime==null)throw new InvalidOperationException("Task12 runtime is required for a compatible learning sample.");
            if(!task12Compatible&&manualDischargeType.Length==0)throw new ArgumentException("A taxonomy-review sample requires an operator-entered discharge type.");
            if(task12Compatible&&taxonomyStatus!="canonical_confirmed"&&taxonomyStatus!="canonical_mapped"||
                !task12Compatible&&taxonomyStatus!="pending_taxonomy_review")
                throw new ArgumentException("Taxonomy status does not match Task12 compatibility.");
            lock(gate)
            {
                if(current!=null&&!current())throw new OperationCanceledException();
                ValidateRoot(root,true);
                // The full timestamp is in the manifest. Repeating it in every
                // directory made canonical Task12 image paths exceed legacy limits.
                string id=Guid.NewGuid().ToString("N");
                string staging=Path.Combine(root,"staging",id),active=Path.Combine(root,"active",id);
                ValidateTransactionBudget(root,id,task12Compatible);
                RejectReparse(staging);Directory.CreateDirectory(staging);File.WriteAllText(Path.Combine(staging,"owner.txt"),Owner,new UTF8Encoding(false));
                try
                {
                    string image=Path.Combine(staging,"prpd.png"),csv=Path.Combine(staging,"phase-charge.csv");
                    RenderPrpd(input,image);
                    using(var writer=new StreamWriter(csv,false,new UTF8Encoding(false)))
                    {
                        writer.WriteLine("SourceRow,PhysicalPhase,PhaseDegrees,ChargePc");
                        for(int i=0;i<input.Rows.Length;i++)writer.WriteLine(input.Rows[i].ToString(CultureInfo.InvariantCulture)+","+input.Phases[i]+","+
                            input.Angles[i].ToString("R",CultureInfo.InvariantCulture)+","+input.Charges[i].ToString("R",CultureInfo.InvariantCulture));
                    }
                    // Only canonical labels may enter Task12's dataset. Unknown operator text
                    // remains an auditable taxonomy-review transaction and is never submitted.
                    if(task12Compatible)runtime.AddSample(model,image,labels,Path.Combine(staging,"task12-dataset"),operatorName);
                    if(ReviewTask12Runtime.HashFile(model)!=sha)throw new IOException("The model changed while preparing the learning sample; transaction not committed.");
                    string[] canonicalKeys=labels.Split(new[]{'+'},StringSplitOptions.RemoveEmptyEntries);
                    var manifest=new Dictionary<string,object>{{"owner",Owner},{"schema",2},{"transaction_id",id},{"created_utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},
                        {"labels",labels},{"label_source","manual_confirmed"},{"operator",operatorName??""},{"model_path",model},{"model_sha256",sha},
                        {"manual_discharge_type",manualDischargeType},{"canonical_label_keys",canonicalKeys},{"task12_compatible",task12Compatible},{"taxonomy_status",taxonomyStatus},
                        {"source_project",input.Project??""},{"source_scope",input.Scope},{"source_rows",input.Rows},{"physical_phases",input.Phases.Distinct().ToArray()},
                        {"point_count",input.Rows.Length},{"context",input.Context},{"csv_sha256",ReviewTask12Runtime.HashFile(csv)},{"png_sha256",ReviewTask12Runtime.HashFile(image)},
                        {"training_status",task12Compatible?"pending_not_trained":"pending_taxonomy_review"},{"production_model_modified",false}};
                    File.WriteAllText(Path.Combine(staging,"manifest.json"),new JavaScriptSerializer().Serialize(manifest),new UTF8Encoding(false));
                    ValidateTree(staging,active);
                    lock(commitGate??new object())
                    {
                        if(current!=null&&!current())throw new OperationCanceledException();
                        RejectReparse(Path.GetDirectoryName(active));Directory.CreateDirectory(Path.GetDirectoryName(active));Directory.Move(staging,active);
                    }
                }
                catch(Exception original)
                {
                    if(Directory.Exists(staging))try{string history=Path.Combine(root,"history","f_"+id);ValidateTree(staging,history);RejectReparse(Path.GetDirectoryName(history));Directory.CreateDirectory(Path.GetDirectoryName(history));Directory.Move(staging,history);}
                    catch(Exception recovery){throw new IOException("The sample was not committed; recovery failed and staging is retained at "+staging,new AggregateException(original,recovery));}
                    throw;
                }
            }
        }
        static string NormalizeManualDischargeType(string value)
        {
            string original=value??"";
            if(original.Length>ManualDischargeTypeMaxLength)throw new ArgumentException("The manually entered discharge type is too long.");
            if(original.Any(Char.IsControl))throw new ArgumentException("The manually entered discharge type cannot contain control characters.");
            return original.Trim();
        }
        internal static bool Undo(string root)
        {
            lock(gate)
            {
                var records=Active(root);if(records.Length==0)return false;
                var plan=PrepareArchive(root,records[records.Length-1],"undo");CommitArchive(root,plan,"undo");return true;
            }
        }
        internal static int Reset(string root)
        {
            lock(gate)
            {
                // Preflight every destination before moving the first record.
                var plans=Active(root).Select(record=>PrepareArchive(root,record,"reset")).ToArray();int completed=0;
                try{foreach(var plan in plans){CommitArchive(root,plan,"reset");completed++;}return completed;}
                catch(Exception ex){throw new IOException("Archived "+completed+" of "+plans.Length+" samples; remaining active samples were not reported as cleared. / 已归档 "+completed+"/"+plans.Length+" 条，其余有效样本未标为已清空。",ex);}
            }
        }
        sealed class ArchivePlan {internal string Source,Directory,Sample,Receipt;}
        static ArchivePlan PrepareArchive(string root,string record,string operation)
        {
            ValidateRoot(root,false);RejectReparse(record);
            string active=Path.GetFullPath(Path.Combine(root,"active"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(record).StartsWith(active,StringComparison.OrdinalIgnoreCase)||!IsOwnedTransaction(record))throw new IOException("The transaction is not owned by this control.");
            string history=Path.Combine(root,"history",(operation=="undo"?"u_":"r_")+Guid.NewGuid().ToString("N")),receipt=history+".receipt.json";
            RequirePath(history,true);RequirePath(receipt,false);ValidateTree(record,history);RejectReparse(history);
            return new ArchivePlan{Source=record,Directory=history,Sample=history,Receipt=receipt};
        }
        static void CommitArchive(string root,ArchivePlan plan,string operation)
        {
            ValidateRoot(root,false);ValidateTree(plan.Source,plan.Sample);RejectReparse(plan.Directory);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Directory));
            var receipt=new Dictionary<string,object>{{"owner",Owner},{"schema",1},{"operation",operation},{"transaction_id",Path.GetFileName(plan.Source)},
                {"requested_utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},{"operator",Environment.UserName},
                {"archived_directory",Path.GetFileName(plan.Directory)},{"completion_rule","Committed only when archived_directory exists beside this receipt."},{"production_model_modified",false}};
            File.WriteAllText(plan.Receipt,new JavaScriptSerializer().Serialize(receipt),new UTF8Encoding(false));
            // Same-volume rename commits one record. A failed move leaves it active.
            Directory.Move(plan.Source,plan.Sample);
        }
        static void RequirePath(string path,bool directory)
        {
            if(path.Length>=(directory?248:260)||Path.GetFullPath(path).Length>=(directory?248:260))throw new IOException("Learning path exceeds the legacy compatibility budget. Use a shorter learning directory; no new sample is committed. / 学习路径过长，请使用较短的学习库；未提交新样本。");
        }
        static void ValidateTree(string source,string destination)
        {
            RejectReparse(source);RejectReparse(destination);RequirePath(destination,true);
            foreach(string entry in Directory.GetFileSystemEntries(source))
            {
                var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Redirected learning files cannot be archived.");
                string target=Path.Combine(destination,Path.GetFileName(entry));
                if((attributes&FileAttributes.Directory)!=0)ValidateTree(entry,target);else RequirePath(target,false);
            }
        }
        static void ValidateTransactionBudget(string root,string id,bool canonical)
        {
            foreach(string directory in new[]{Path.Combine(root,"staging",id),Path.Combine(root,"active",id),Path.Combine(root,"history","f_"+id),Path.Combine(root,"history","r_"+new string('a',32))})
            {
                RequirePath(directory,true);
                foreach(string file in new[]{"owner.txt","manifest.json","phase-charge.csv","prpd.png"})RequirePath(Path.Combine(directory,file),false);
                if(canonical)
                {
                    string dataset=Path.Combine(directory,"task12-dataset");RequirePath(dataset,true);
                    foreach(string images in new[]{"images","model_images"}){string imageDirectory=Path.Combine(dataset,images);RequirePath(imageDirectory,true);RequirePath(Path.Combine(imageDirectory,new string('a',32)),false);}
                }
            }
        }
        static void RenderPrpd(RecognitionSnapshot input,string path)
        {
            // A plot of the actual submitted phase/charge pairs, not an invented PRPD image.
            // 512x384 deliberately avoids Task12's legacy full-product-screenshot crop heuristic.
            using(var image=new Bitmap(512,384))using(var graphics=Graphics.FromImage(image))using(var ink=new SolidBrush(Color.FromArgb(0,120,156)))
            using(var border=new Pen(Color.FromArgb(90,100,110)))
            {
                graphics.Clear(Color.White);var box=new Rectangle(42,20,448,328);graphics.DrawRectangle(border,box);
                double maximum=input.Charges.Select(Math.Abs).DefaultIfEmpty(0).Max();if(maximum<=0)throw new ArgumentException("No nonzero charge samples can be learned.");
                for(int i=0;i<input.Angles.Length;i++)
                {
                    double phase=((input.Angles[i]%360)+360)%360;
                    float x=(float)(box.Left+phase/360*box.Width),y=(float)(box.Bottom-Math.Abs(input.Charges[i])/maximum*box.Height);
                    graphics.FillEllipse(ink,x-1.5f,y-1.5f,3,3);
                }
                image.Save(path,ImageFormat.Png);
            }
        }
    }
}
