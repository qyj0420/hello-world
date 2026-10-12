using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Verifies, without loading WinForms, that the net48 bridge exposes the exact
// ShowForXpert signature the Xpert host invokes by reflection (src/FactoryPressOwtsHost.cs).
static class ContractCheck
{
    static readonly string[] Expected =
    {
        "System.Windows.Forms.IWin32Window", "string", "string", "double", "double", "double", "int", "string", "bool",
        "System.Action`2<string,string>"
    };

    public static bool Run(string bridgeDll)
    {
        using (var pe = new PEReader(File.OpenRead(bridgeDll)))
        {
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Namespace) != "Xiongxin.OWTS.FactoryPressBridge" || md.GetString(t.Name) != "XpertFactoryPressDialog") continue;
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.GetString(m.Name) != "ShowForXpert") continue;
                    var sig = m.DecodeSignature(new Names(md), null);
                    bool ok = (m.Attributes & System.Reflection.MethodAttributes.Static) != 0
                        && (m.Attributes & System.Reflection.MethodAttributes.Public) != 0
                        && sig.ReturnType == "string"
                        && sig.ParameterTypes.SequenceEqual(Expected);
                    Console.WriteLine((ok ? "PASS " : "FAIL ") + "host contract ShowForXpert(" + string.Join(", ", sig.ParameterTypes) + ") -> " + sig.ReturnType);
                    return ok;
                }
            }
        }
        Console.WriteLine("FAIL host contract: XpertFactoryPressDialog.ShowForXpert not found in " + bridgeDll);
        return false;
    }

    sealed class Names : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader _md;
        public Names(MetadataReader md) { _md = md; }
        public string GetPrimitiveType(PrimitiveTypeCode c)
        {
            switch (c)
            {
                case PrimitiveTypeCode.String: return "string";
                case PrimitiveTypeCode.Double: return "double";
                case PrimitiveTypeCode.Int32: return "int";
                case PrimitiveTypeCode.Boolean: return "bool";
                case PrimitiveTypeCode.Void: return "void";
                default: return c.ToString();
            }
        }
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var t = r.GetTypeDefinition(h); return r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var t = r.GetTypeReference(h); return r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) { return g + "<" + string.Join(",", a) + ">"; }
        public string GetSZArrayType(string e) { return e + "[]"; }
        public string GetArrayType(string e, ArrayShape s) { return e + "[*]"; }
        public string GetByReferenceType(string e) { return e + "&"; }
        public string GetPointerType(string e) { return e + "*"; }
        public string GetPinnedType(string e) { return e; }
        public string GetGenericTypeParameter(object c, int i) { return "!" + i; }
        public string GetGenericMethodParameter(object c, int i) { return "!!" + i; }
        public string GetFunctionPointerType(MethodSignature<string> s) { return "fnptr"; }
        public string GetModifiedType(string m, string u, bool r) { return u; }
        public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) { return "spec"; }
    }
}
