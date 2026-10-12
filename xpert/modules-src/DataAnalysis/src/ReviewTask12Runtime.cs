using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Desktop;

namespace HVX.PdReview
{
    // No private Task12 entry points and no training API. One session is reused per verified model.
    internal sealed class ReviewTask12Runtime : IDisposable
    {
        internal const string RuntimeFile="ec59cbc4e4ac0bf1804fc88b.V1.0.12.dll";
        readonly object gate=new object();
        object engine;
        string identity;
        Type engineType;
        static readonly object nativeGate=new object();
        static readonly Dictionary<string,IntPtr> nativeHandles=new Dictionary<string,IntPtr>(StringComparer.OrdinalIgnoreCase);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);

        internal static string ResolveModelPath(string input)
        {
            string path=(input??"").Trim();
            if(path.Length==0)
            {
                path=DefectModelCatalog.Resolve(DefectModelCatalog.Standard,null,null);
            }
            path=Path.GetFullPath(path);
            if(Directory.Exists(path))
            {
                var candidates=Directory.GetFiles(path,"*",SearchOption.TopDirectoryOnly).Where(DefectModelCatalog.ModelExtension).ToArray();
                if(candidates.Length==0&&Directory.Exists(Path.Combine(path,"Model")))candidates=Directory.GetFiles(Path.Combine(path,"Model"),"*",SearchOption.TopDirectoryOnly).Where(DefectModelCatalog.ModelExtension).ToArray();
                if(candidates.Length!=1)throw new ArgumentException("Select one PDM or HXM model file; the directory contains none or multiple models.");
                path=candidates[0];
            }
            if(!File.Exists(path))throw new FileNotFoundException("Task12 model was not found.",path);
            if(!DefectModelCatalog.ModelExtension(path))throw new ArgumentException("Select an encrypted PDM or HXM model, not an ONNX file or learning dataset.");
            return path;
        }
        internal static string HashFile(string path)
        { using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",""); }
        internal static string ResolveRuntimePath(string model)
        {
            // Selecting model data never authorizes native or managed code from
            // the model's directory. Use only the control's deployed runtime.
            string control=Path.GetDirectoryName(ReviewAnalysisEngine.ControlSourcePath);
            var locations=new[]{Path.Combine(control,"Task12"),control};
            foreach(string root in locations.Where(p=>!String.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {string path=Path.Combine(root,RuntimeFile);if(File.Exists(path))return path;}
            throw new FileNotFoundException("Task12 runtime was not found in the control deployment. Repair the application installation.",RuntimeFile);
        }
        internal T Invoke<T>(string model,Func<object,Type,T> action)
        {
            lock(gate)
            {
                if(IntPtr.Size!=8)throw new PlatformNotSupportedException("The supplied Task12 ONNX runtime supports x64 hosts only. PRPD display and analysis remain available; use x64 for defect recognition.");
                string path=ResolveModelPath(model),runtime=ResolveRuntimePath(path),root=Path.GetDirectoryName(runtime);
                using(var verifiedModel=DefectModelCatalog.OpenVerified(path))
                {
                string modelHash=verifiedModel.Hash;
                string key=path+"|"+modelHash+"|"+runtime+"|"+HashFile(runtime);
                ResolveEventHandler resolver=(sender,args)=>
                {
                    var name=new AssemblyName(args.Name).Name;
                    if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
                    string candidate=Path.Combine(root,name+".dll");
                    return File.Exists(candidate)?Assembly.LoadFrom(candidate):null;
                };
                AppDomain.CurrentDomain.AssemblyResolve+=resolver;
                try
                {
                    if(engine==null||identity!=key)
                    {
                        DisposeEngine();
                        string native=Path.Combine(root,"onnxruntime.dll");
                        if(!File.Exists(native))throw new FileNotFoundException("Task12 ONNX native runtime is missing.",native);
                        lock(nativeGate)
                        {
                            if(!nativeHandles.ContainsKey(native))
                            {
                                IntPtr handle=LoadLibraryEx(native,IntPtr.Zero,8);
                                if(handle==IntPtr.Zero)throw new InvalidOperationException("Task12 native runtime could not load; check host architecture and dependent libraries. Windows error "+Marshal.GetLastWin32Error()+".");
                                nativeHandles.Add(native,handle);
                            }
                        }
                        var assembly=Assembly.LoadFrom(runtime);
                        string loaded=ReviewAnalysisEngine.OriginalAssemblyPath(assembly);
                        if(!File.Exists(loaded)||HashFile(loaded)!=HashFile(runtime))throw new InvalidOperationException("A different Task12 assembly is already loaded. Restart the host with one verified deployment.");
                        engineType=assembly.GetType("PDDefect.Product.PDDefectEngine",true);
                        engine=Activator.CreateInstance(engineType,new object[]{path});identity=key;
                    }
                    return action(engine,engineType);
                }
                catch(TargetInvocationException ex){throw ex.InnerException??ex;}
                finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
                }
            }
        }
        internal Dictionary<string,object> Classify(string model,double[] phase,double[] charge)
        {
            return Invoke(model,(instance,type)=>{
                object result=type.GetMethod("ClassifyPDData",new[]{typeof(double[]),typeof(double[])}).Invoke(instance,new object[]{phase,charge});
                string json=Convert.ToString(result.GetType().GetProperty("RawJson").GetValue(result,null));
                return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            });
        }
        internal void AddSample(string model,string image,string labels,string dataset,string operatorName)
        {
            Invoke(model,(instance,type)=>{
                string json=(string)type.GetMethod("AddConfirmedSampleJson").Invoke(instance,new object[]{image,labels,dataset,operatorName??""});
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
                if(data==null||!data.ContainsKey("ok")||!Convert.ToBoolean(data["ok"]))throw new InvalidOperationException(data!=null&&data.ContainsKey("error")?Convert.ToString(data["error"]):"Task12 did not commit the sample.");
                return true;
            });
        }
        void DisposeEngine(){var disposable=engine as IDisposable;if(disposable!=null)disposable.Dispose();engine=null;identity=null;engineType=null;}
        public void Dispose(){lock(gate)DisposeEngine();}
    }
}
