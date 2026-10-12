using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;

// Test-only names from the exact final build's protection mapping. It never
// patches/deprotects an assembly or executes an intermediate as a fallback.
internal static class CalibrationProtectedTestReflection
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly Dictionary<string,XElement> classes=new Dictionary<string,XElement>(StringComparer.Ordinal);
    static string finalHash;
    static string Hash(string p){using(var f=File.OpenRead(p))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","");}
    static string TypeName(string name){int close=name.IndexOf(']');if(!name.StartsWith("[",StringComparison.Ordinal)||close<1)throw new InvalidDataException("Protection mapping type identity missing.");return name.Substring(close+1).Replace('/','+');}
    internal static void InitializeFor(Assembly final)
    {
        string path=Environment.GetEnvironmentVariable("HVX_CAL_TEST_MAP");if(String.IsNullOrEmpty(path))return;
        string mapHash=Environment.GetEnvironmentVariable("HVX_CAL_TEST_MAP_SHA256");finalHash=Environment.GetEnvironmentVariable("HVX_CAL_TEST_ALGORITHM_SHA256");
        if(Hash(path)!=mapHash||Hash(final.Location)!=finalHash)throw new InvalidDataException("Final CAL test mapping/assembly hash mismatch.");
        if(classes.Count!=0)return;
        foreach(var c in XDocument.Load(path).Descendants("renamedClass"))classes.Add(TypeName((string)c.Attribute("oldName")),c);
        if(classes.Count==0)throw new InvalidDataException("Missing protection mapping classes.");
    }
    internal static Type Type(Assembly asm,string original)
    {
        Type direct=asm.GetType(original,false);if(direct!=null)return direct;
        if(finalHash==null||Hash(asm.Location)!=finalHash)throw new TypeLoadException("No exact final mapping for "+original);
        XElement c;if(!classes.TryGetValue(original,out c))throw new TypeLoadException("Missing mapped type "+original);
        return asm.GetType(TypeName((string)c.Attribute("newName")),true);
    }
    internal static object Call(Type t,string original,params object[] args)
    {
        var direct=t.GetMethods(F).Where(m=>m.Name==original&&m.GetParameters().Length==args.Length).ToArray();
        if(direct.Length==1)return direct[0].Invoke(null,args);
        if(direct.Length>1)throw new AmbiguousMatchException("Ambiguous original CAL test method.");
        if(finalHash==null||Hash(t.Assembly.Location)!=finalHash)throw new MissingMethodException(t.FullName,original);
        XElement c=classes.Values.Single(x=>TypeName((string)x.Attribute("newName"))==t.FullName);
        string originalType=(string)c.Attribute("oldName"),prefix=originalType+"::"+original+"["+args.Length+"](";
        XElement method=c.Elements("renamedMethod").Single(x=>((string)x.Attribute("oldName")).StartsWith(prefix,StringComparison.Ordinal));
        string actual=(string)method.Attribute("newName");
        string signature=(string)method.Attribute("oldName");string body=signature.Substring(prefix.Length,signature.Length-prefix.Length-1);
        string[] expected=body.Length==0?new string[0]:body.Split(',').Select(token=>{
            string name=TypeName(token);string suffix="";while(name.EndsWith("[]",StringComparison.Ordinal)){suffix+="[]";name=name.Substring(0,name.Length-2);}
            XElement item;if(classes.TryGetValue(name,out item))name=TypeName((string)item.Attribute("newName"));return name+suffix;
        }).ToArray();
        MethodInfo mapped=t.GetMethods(F).Where(m=>m.Name==actual&&m.GetParameters().Select(p=>p.ParameterType.FullName).SequenceEqual(expected)).Single();
        return mapped.Invoke(null,args);
    }
}
