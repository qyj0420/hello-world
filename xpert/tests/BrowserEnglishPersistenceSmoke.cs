using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.Controls;

// Owned synthetic metadata and text waveforms only. No UI, SDK or capture.
internal static class BrowserEnglishPersistenceSmoke
{
    static int checks;
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static bool Rejects(Action action){try{action();return false;}catch(ArgumentException){return true;}catch(InvalidDataException){return true;}}
    static string Cable(string custom)
    {
        return "0\tA\t\t\r\n1\t"+custom+"\tOriginal terminal\t0m\r\n2\tBody\tOriginal first material\t\r\n"+
            "1\tJoint_1\tOriginal joint\t80.00m\r\n2\tBody\tOriginal second material\t\r\n1\tEnd 用户终端\tOriginal end\t170m\r\n";
    }
    static string Project(string qa,string name,Encoding encoding,bool english)
    {
        string p=Path.Combine(qa,name);Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\n"+(english?"CableLength":"电缆长度")+": = \"170\"\r\nRemarks: = \"原始备注\"\r\n",encoding);
        File.WriteAllText(Path.Combine(p,"CABLE.in"),Cable(name.Contains("gbk")?"用户始端":"用户始端 Łódź"),encoding);
        var cal=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");
        for(int i=0;i<8;i++)cal.Append("Field").Append(i).Append("\t0\r\n");
        for(int i=0;i<601;i++)cal.Append((i%7).ToString()).Append("\r\n");
        File.WriteAllText(Path.Combine(p,"A_5nC.cal"),cal.ToString(),encoding);return p;
    }
    static byte[] WaveTail(string file,int lines,Encoding encoding)
    {
        byte[] bytes=File.ReadAllBytes(file);int unit=encoding is UnicodeEncoding?2:1;
        int seen=0;
        for(int i=0;i<bytes.Length;i+=unit)if(bytes[i]==10&&++seen==lines)return bytes.Skip(i+unit).ToArray();
        throw new Exception("fixture header missing");
    }
    static void Case(string qa,string name,Encoding encoding,bool english)
    {
        string root=Project(qa,name,encoding,english),item=Path.Combine(root,"ITEM.in"),cable=Path.Combine(root,"CABLE.in"),cal=Path.Combine(root,"A_5nC.cal");
        byte[] oldItem=File.ReadAllBytes(item),oldCable=File.ReadAllBytes(cable),oldCal=File.ReadAllBytes(cal),tail=WaveTail(cal,13,encoding);
        var joints=CalibrationPersistence.ReadJoints(root);
        Check(joints.Length==3&&joints[0].IsEndpoint&&!joints[1].IsEndpoint&&joints[2].IsEndpoint,"ordered roles are independent of translated/custom labels "+name);
        Check(joints[1].PositionMetres==80&&joints[2].PositionMetres==170,"positions retained "+name);
        var noOp=CalibrationPersistence.SaveJoints(root,joints);
        Check(noOp.ChangedFileCount==0,"no-op joint save is truly no-op "+name);
        Check(oldItem.SequenceEqual(File.ReadAllBytes(item))&&oldCable.SequenceEqual(File.ReadAllBytes(cable))&&oldCal.SequenceEqual(File.ReadAllBytes(cal)),"no-op retains every original byte "+name);
        var expanded=new[]{joints[0],new CableJoint{PositionMetres=40,AttachmentType="New attachment",BodyType="New material"},joints[1],joints[2]};
        CalibrationPersistence.SaveJoints(root,expanded);string updated=File.ReadAllText(cable,encoding);
        Check(updated.Contains("1\tJoint_2\tNew attachment\t40.0m")&&updated.Contains("2\tBody\tNew material\t"),"new rows use English protocol and collision-free names "+name);
        Check(updated.Contains("1\tJoint_1\tOriginal joint\t80.00m"),"old custom name and position spelling are not rewritten "+name);
        Check(updated.Contains("End 用户终端\tOriginal end\t170m")&&updated.Contains("Original first material"),"protected endpoints and first body remain intact "+name);
        Check(oldItem.SequenceEqual(File.ReadAllBytes(item))&&oldCal.SequenceEqual(File.ReadAllBytes(cal)),"joint edits never touch ITEM or original waveform "+name);
        byte[] before=File.ReadAllBytes(cable);var bad=CalibrationPersistence.ReadJoints(root);bad[bad.Length-1].PositionMetres=171;
        Check(Rejects(()=>CalibrationPersistence.SaveJoints(root,bad))&&before.SequenceEqual(File.ReadAllBytes(cable)),"endpoint moves rejected without writes "+name);
        Check(Rejects(()=>CalibrationPersistence.CorrectCableLength(root,80))&&before.SequenceEqual(File.ReadAllBytes(cable)),"shortening through internal joint rejected "+name);
        CalibrationPersistence.CorrectCableLength(root,180);
        Check(File.ReadAllText(item,encoding).Contains((english?"CableLength":"电缆长度")+": = \"180\""),"length editor supports stored alias "+name);
        Check(CalibrationPersistence.ReadJoints(root).Last().PositionMetres==180,"endpoint length actually updated "+name);
        Check(tail.SequenceEqual(WaveTail(cal,13,encoding)),"calibration correction preserves original waveform suffix byte-for-byte "+name);
        Check(File.ReadAllText(cal,encoding).Contains("V (m/usec)\t180"),"new propagation speed follows original measured delta "+name);
        Check(File.ReadAllText(cable,encoding).Contains("Joint_1\tOriginal joint\t80.00m"),"length correction does not scale internal joint distance "+name);
    }
    static void Invalid(string qa,string name,string cable)
    {
        string root=Path.Combine(qa,name);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"CABLE.in"),cable);
        Check(Rejects(()=>CalibrationPersistence.ReadJoints(root)),"malformed structure rejected: "+name);
    }
    static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[0]);
            Case(qa,"english-utf8",new UTF8Encoding(true,true),true);
            Case(qa,"legacy-gbk",Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),false);
            Case(qa,"english-utf16",new UnicodeEncoding(false,true,true),true);
            Invalid(qa,"nonzero-start",Cable("Start").Replace("0m","1m"));
            Invalid(qa,"duplicate-position",Cable("Start").Replace("80.00m","0m"));
            Invalid(qa,"reversed-order",Cable("Start").Replace("80.00m","180m"));
            Invalid(qa,"unknown-role",Cable("Start").Replace("2\tBody","9\tBody"));
            Invalid(qa,"orphan-body","0\tA\t\t\r\n2\tBody\tXLPE\t\r\n"+Cable("Start"));
            Invalid(qa,"empty-name",Cable("Start").Replace("1\tStart\t","1\t\t"));
            Console.WriteLine("BROWSER_ENGLISH_PERSISTENCE_PASS="+checks+";ProductEntry=false;Profiles=false;Hardware=false;UserProjectWritten=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
