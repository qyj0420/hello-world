using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class ProtectedApiDump
{
    static string Name(Type type){
        if(type.IsByRef)return Name(type.GetElementType())+"&";
        if(type.IsArray)return Name(type.GetElementType())+"["+new string(',',type.GetArrayRank()-1)+"]";
        // Strong-name signing intentionally changes the candidate's key token;
        // compare recursive type names, not assembly-qualified generic strings.
        if(type.IsGenericType)return type.GetGenericTypeDefinition().FullName+"<"+String.Join(",",type.GetGenericArguments().Select(Name))+">";
        return type.FullName??type.ToString();
    }
    static int Main(string[] args)
    {
        try {
            var assembly=Assembly.LoadFrom(args[0]);var rows=new List<string>();
            foreach(var type in assembly.GetExportedTypes()){
                rows.Add("TYPE "+Name(type));
                foreach(var member in type.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)){
                    string line=member.MemberType+" "+Name(type)+" "+member.Name;
                    var method=member as MethodBase;
                    if(method!=null){line+="("+String.Join(",",method.GetParameters().Select(p=>Name(p.ParameterType)+" "+p.Name+" out="+p.IsOut+" optional="+p.IsOptional))+ ")";var info=method as MethodInfo;if(info!=null)line+=" -> "+Name(info.ReturnType);}
                    var property=member as PropertyInfo;if(property!=null)line+=" : "+Name(property.PropertyType)+" r="+property.CanRead+" w="+property.CanWrite;
                    var field=member as FieldInfo;if(field!=null)line+=" : "+Name(field.FieldType);
                    var evt=member as EventInfo;if(evt!=null)line+=" : "+Name(evt.EventHandlerType);
                    rows.Add(line);
                }
            }
            foreach(var row in rows.OrderBy(x=>x,StringComparer.Ordinal))Console.WriteLine(row);
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
