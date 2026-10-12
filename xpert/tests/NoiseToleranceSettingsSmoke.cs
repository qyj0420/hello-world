using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// No credentials, hardware or live profile are used. Production authorization
// remains closed; the manufacturer panel is composed directly only in this
// isolated fixture to exercise the same load/capture methods after that gate.
internal static class NoiseToleranceSettingsSmoke
{
    static int checks;
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    static object Field(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string qa=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            Check(new AppSettings().NoiseSimilarityTolerancePercent==1,"New configuration defaults to one percent");
            string absent=Path.Combine(qa,"legacy-missing-key.xml");
            File.WriteAllText(absent,"<HVXUnifiedDesktopSettings></HVXUnifiedDesktopSettings>");
            AppSettings loaded;string message;
            Check(AppSettings.TryLoad(absent,out loaded,out message)&&loaded.NoiseSimilarityTolerancePercent==1,"Old configuration without the key preserves one-percent default");
            Check(AppSettings.Load(Path.Combine(qa,"not-created.xml")).NoiseSimilarityTolerancePercent==1,"Missing settings file preserves default");
            foreach(double configured in new[]{1.0,0.0,2.5,10.0})
            {
                var source=new AppSettings();source.NoiseSimilarityTolerancePercent=configured;
                string path=Path.Combine(qa,"value-"+configured.ToString(System.Globalization.CultureInfo.InvariantCulture)+".xml");source.Save(path);
                loaded=AppSettings.Load(path);
                Check(loaded.NoiseSimilarityTolerancePercent==configured,"Stored explicit value is preserved: "+configured);
                using(var form=new SettingsForm(loaded))
                {
                    Check(Field(form,"noiseSimilarityTolerance")==null,"Ordinary UI does not construct or expose manufacturer tolerance editor");
                    Check(typeof(SettingsForm).GetMethod("PromptForPassword",Hidden)==null,"no application password prompt");
                    Check(!(bool)Field(form,"plusDataUnlocked"),"Unvisited editor is not constructed");
                    var captured=(AppSettings)Call(form,"CaptureValues");
                    Check(captured.NoiseSimilarityTolerancePercent==configured,"Ordinary unchanged save preserves configured value");
                    captured.Save(path);
                    Check(AppSettings.Load(path).NoiseSimilarityTolerancePercent==configured,"Saved round trip retains same configuration key and value");
                    Check(source.NoiseSimilarityTolerancePercent==configured,"Source settings object remains unchanged");

                    // Build exactly the manufacturer contents without changing the
                    // running application's authorization state or storing secrets.
                    using(var parameters=(Control)Call(form,"BuildPlusDataPanel"))
                    {
                        Call(form,"LoadPlusDataValues",loaded);
                        var numeric=(NumericUpDown)Field(form,"noiseSimilarityTolerance");
                        Check(parameters.Contains(numeric),"Tolerance lives in protected Plus Data parameters");
                        Check(numeric.Name=="ManufacturerNoiseSimilarityTolerance"&&numeric.Value==(decimal)configured,"Manufacturer UI loads the persisted value, including explicit zero");
                        Check(numeric.Minimum==0&&numeric.Maximum==10,"Existing permissible range is unchanged");
                        numeric.Value=3.25M;
                        Check(((AppSettings)Call(form,"CaptureValues")).NoiseSimilarityTolerancePercent==configured,"Unapproved hidden control changes do not affect ordinary save");
                        // A synthetic test-only granted flag models the successful
                        // authorization branch without exposing a real credential.
                        typeof(SettingsForm).GetField("plusDataUnlocked",Hidden).SetValue(form,true);
                        captured=(AppSettings)Call(form,"CaptureValues");
                        Check(captured.NoiseSimilarityTolerancePercent==3.25,"Authorized manufacturer edit is captured");
                        captured.Save(path);
                        Check(AppSettings.Load(path).NoiseSimilarityTolerancePercent==3.25,"Manufacturer edit round trips using existing configuration key");
                    }
                }
            }
            Console.WriteLine("NOISE_TOLERANCE_SETTINGS_PASS="+checks);
            Console.WriteLine("MANUFACTURER_UI_RELOCATION_VERIFIED=True;HARDWARE_ACCESSED=False");
            Console.WriteLine("EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
