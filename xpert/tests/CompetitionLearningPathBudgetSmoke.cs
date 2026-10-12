using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HVX.PdReview;

internal static class CompetitionLearningPathBudgetSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static string Root(string parent,int length)
    {
        string root=Path.Combine(parent,Guid.NewGuid().ToString("N"));
        if(root.Length>length)throw new Exception("Fixture parent is too long for the requested real-world budget.");
        root+=new string('p',length-root.Length);return root;
    }
    static string OldRecord(string root,string id,DateTime created,bool deep)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"owner.txt"),"HVX.PdReview.Task12Learning.v1");
        string path=Path.Combine(root,"active",id);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"owner.txt"),"HVX.PdReview.Task12Learning.v1");
        File.WriteAllText(Path.Combine(path,"manifest.json"),new JavaScriptSerializer().Serialize(new{created_utc=created.ToString("o"),transaction_id=id}));
        if(deep){string images=Path.Combine(path,"task12-dataset","model_images");Directory.CreateDirectory(images);File.WriteAllText(Path.Combine(images,new string('x',28)+".png"),"fixture");}
        return path;
    }
    static int Main(string[] args)
    {
        try
        {
            // Use a short, uniquely owned build fixture. Never touch LocalAppData
            // or any real pending learning store, even when simulating its length.
            string build=Path.GetFullPath(args[0]);string parent=Path.Combine(build,"pb-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(parent);
            string root=Root(parent,141),model=Path.Combine(parent,"fake.pdm");File.WriteAllText(model,"synthetic fixture, not a production model");string hash=ReviewTask12Runtime.HashFile(model);
            var input=new RecognitionSnapshot{Project="fixture",Scope="fixture",Rows=new[]{0,1,2},Phases=new[]{1,1,1},Angles=new[]{10d,90d,180d},Charges=new[]{100d,-80d,70d},Context="fixture"};
            var runtime=new ReviewTask12Runtime();ReviewLearningTransactions.Add(root,input,model,hash,"tip","qa",runtime);
            var record=Directory.GetDirectories(Path.Combine(root,"active")).Single();Check(Path.GetFileName(record).Length==32,"new transaction is GUID32");
            Check(Directory.GetFiles(record,"*.png",SearchOption.AllDirectories).Length==3,"canonical nested images were actually produced");
            Check(Directory.GetFileSystemEntries(record,"*",SearchOption.AllDirectories).All(p=>p.Length<(Directory.Exists(p)?248:260)),"default-length active tree fits compatibility limits");
            Check(ReviewLearningTransactions.Undo(root),"default-length canonical undo succeeds");Check(ReviewLearningTransactions.Count(root)==0,"undo excludes the active record");
            string archived=Directory.GetDirectories(Path.Combine(root,"history")).Single();Check(Directory.GetFiles(archived,"*.png",SearchOption.AllDirectories).Length==3,"undo preserves original image tree");
            Check(File.ReadAllText(archived+".receipt.json").Contains(Path.GetFileName(record)),"receipt preserves original transaction ID");
            Check(Directory.GetFiles(archived,"*",SearchOption.AllDirectories).All(p=>p.Length<260),"history tree fits default budget");
            string mixed=Root(parent,118);string oldId="20200101T0000000000000_"+new string('f',32),newId=new string('0',32);
            string older=OldRecord(mixed,oldId,DateTime.UtcNow.AddDays(-1),true),newer=OldRecord(mixed,newId,DateTime.UtcNow,false);
            Check(ReviewLearningTransactions.Count(mixed)==2,"legacy timestamp+GUID and new GUID both remain readable");
            Check(ReviewLearningTransactions.Undo(mixed)&&Directory.Exists(older)&&!Directory.Exists(newer),"latest undo follows UTC, not GUID lexical order");
            Check(ReviewLearningTransactions.Reset(mixed)==1&&!Directory.Exists(older),"legacy nested tree reset uses short backup names");
            string tooLong=Root(parent,166);bool rejected=false;try{ReviewLearningTransactions.Add(tooLong,input,model,hash,"tip","qa",runtime);}catch(IOException ex){rejected=ex.Message.Contains("compatibility budget");}
            Check(rejected,"long canonical path is rejected before staging");Check(!Directory.Exists(Path.Combine(tooLong,"active"))&&!Directory.Exists(Path.Combine(tooLong,"staging")),"rejected add has no staged or active sample");
            string bad=Root(parent,141),badRecord=OldRecord(bad,new string('a',32),DateTime.UtcNow,false);File.WriteAllText(Path.Combine(badRecord,"manifest.json"),"{}");
            rejected=false;try{ReviewLearningTransactions.Undo(bad);}catch(InvalidDataException){rejected=true;}Check(rejected&&Directory.Exists(badRecord),"bad UTC manifest cannot silently undo another record");
            string preflight=Root(parent,141);string safe=OldRecord(preflight,new string('d',32),DateTime.UtcNow.AddDays(-1),false),edge=OldRecord(preflight,new string('e',32),DateTime.UtcNow,false);
            string edgeFile=Path.Combine(edge,new string('x',258-edge.Length-1));File.WriteAllText(edgeFile,"must remain active");
            rejected=false;try{ReviewLearningTransactions.Reset(preflight);}catch(IOException ex){rejected=ex.Message.Contains("compatibility budget");}
            Check(rejected&&Directory.Exists(safe)&&Directory.Exists(edge)&&File.Exists(edgeFile),"batch preflight rejects an overlong second destination before moving the first");
            Check(!Directory.Exists(Path.Combine(preflight,"history")),"failed preflight writes no recovery receipt");
            string partial=Root(parent,141);string first=OldRecord(partial,new string('b',32),DateTime.UtcNow.AddDays(-1),false),second=OldRecord(partial,new string('c',32),DateTime.UtcNow,false);
            using(var blocked=new FileStream(Path.Combine(second,"owner.txt"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                rejected=false;try{ReviewLearningTransactions.Reset(partial);}catch(IOException ex){rejected=ex.Message.Contains("Archived 1 of 2");}
                Check(rejected&&!Directory.Exists(first)&&Directory.Exists(second),"mid-reset I/O failure reports exact completed count and retains remainder");
            }
            Check(ReviewTask12Runtime.HashFile(model)==hash,"model bytes remain unchanged");
            Console.WriteLine("COMPETITION_LEARNING_PATH_BUDGET_PASS="+checks+";HARDWARE=False;REAL_STORE_WRITE=False;QA="+parent);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
namespace HVX.PdReview
{
    internal sealed class RecognitionSnapshot {internal string Project,Scope,Context;internal int[] Rows,Phases;internal double[] Angles,Charges;}
    internal sealed class ReviewTask12Runtime
    {
        internal static string HashFile(string path){using(var hash=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}
        internal void AddSample(string model,string image,string labels,string directory,string who)
        {
            // Match the actual Task12 2.4 image-path shape (32-character PNG).
            foreach(string folder in new[]{"images","model_images"}){string target=Path.Combine(directory,folder);Directory.CreateDirectory(target);File.Copy(image,Path.Combine(target,"20261001_000000_123456789012.png"));}
            File.WriteAllText(Path.Combine(directory,"labels.csv"),labels);
        }
    }
}
