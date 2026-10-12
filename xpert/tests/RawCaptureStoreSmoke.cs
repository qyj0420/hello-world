using System;
using System.IO;
using System.Reflection;
using HVX.OWTS.Task38;
class RawCaptureStoreSmoke
{
    static int count;
    static void Check(bool value,string text){if(!value)throw new Exception(text);count++;}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n).SetValue(o,v,null);}
    static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}catch(ArgumentException){return true;}catch(IOException){return true;}}
    static int Main(string[] args)
    {
        try{
            string dir=Path.Combine(args[0],Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            var record=new Task38RunRecord{RecordId="SIM-test-001",Simulation=true,FingerprintSha256=new string('a',64)};
            var result=new Task38CycleResult();
            Set(result,"RecordId",record.RecordId);Set(result,"Success",true);
            Set(result,"UsbDividerRawVolts",new[]{-1.0,0.0,2.5});Set(result,"PicoRawAdc",new short[]{-100,0,100});
            Set(result,"UsbSampleRateHz",10000d);Set(result,"PicoSampleRateHz",125000000d);
            Set(result,"PicoFullScaleVolts",1d);Set(result,"PicoMaximumAdc",(short)32767);
            string path=Task38RawCaptureStore.SaveNew(dir,record,result);
            Task38RawCaptureStore.Verify(path);Check(File.Exists(path),"committed capture");
            Check(Reject(()=>Task38RawCaptureStore.SaveNew(dir,record,result)),"no overwrite");
            using(var reader=new BinaryReader(File.OpenRead(path))){
                reader.ReadBytes(8);Check(reader.ReadString()==record.RecordId,"identity retained");Check(reader.ReadBoolean(),"simulation retained");
                reader.ReadString();reader.ReadInt64();reader.ReadInt64();reader.ReadBoolean();
                reader.ReadDouble();reader.ReadDouble();reader.ReadDouble();reader.ReadDouble();reader.ReadInt16();
                reader.ReadInt32();reader.ReadInt32();reader.ReadInt32();
                Check(reader.ReadInt32()==3&&reader.ReadInt32()==3,"sample counts");
                Check(reader.ReadDouble()==-1&&reader.ReadDouble()==0&&reader.ReadDouble()==2.5,"raw voltage unchanged");
                Check(reader.ReadInt16()==-100&&reader.ReadInt16()==0&&reader.ReadInt16()==100,"signed ADC unchanged");
            }
            byte[] data=File.ReadAllBytes(path);data[data.Length-33]^=1;
            string corrupt=Path.Combine(dir,"corrupt.hxraw");File.WriteAllBytes(corrupt,data);
            Check(Reject(()=>Task38RawCaptureStore.Verify(corrupt)),"corruption rejected");
            string recovery=Task38RawCaptureStore.MoveToRecovery(dir,record.RecordId);
            Check(!File.Exists(path)&&File.Exists(recovery),"recoverable removal");
            Task38RawCaptureStore.Verify(recovery);
            Set(result,"Success",false);Check(Reject(()=>Task38RawCaptureStore.SaveNew(dir,record,result)),"failed acquisition rejected");
            Set(result,"Success",true);Set(result,"PicoHardwareOverflowDiagnosticOnly",true);
            Check(Reject(()=>Task38RawCaptureStore.SaveNew(dir,record,result)),"overflow not automatically committed");
            Set(result,"PicoHardwareOverflowDiagnosticOnly",false);Set(result,"PicoSampleRateHz",Double.NaN);
            Check(Reject(()=>Task38RawCaptureStore.SaveNew(dir,record,result)),"invalid sample rate rejected");
            Set(result,"PicoSampleRateHz",125000000d);record.RecordId="../escape";Set(result,"RecordId",record.RecordId);
            Check(Reject(()=>Task38RawCaptureStore.SaveNew(dir,record,result)),"path escape rejected");
            Check(Directory.GetFiles(dir,"*.pending").Length==0,"no unfinished commits");
            Console.WriteLine("RAW_CAPTURE_PASS="+count+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
