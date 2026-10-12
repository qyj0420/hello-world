using System;
using System.IO;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal static class ReviewStatusJson
    {
        // Same budgets as the existing named-analysis archive. A status is
        // metadata, not a channel for unbounded waveform or calibration data.
        internal const int MaximumCharacters = 16 * 1024 * 1024;
        internal const int MaximumDepth = 24;
        internal static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength=MaximumCharacters, RecursionLimit=MaximumDepth }; }
        internal static void ValidateText(string text)
        {
            if(text==null)throw new InvalidDataException("Analysis status is missing.");
            if(text.Length>MaximumCharacters)throw new InvalidDataException("Analysis status exceeds its size limit.");
            // Reject excessive nesting before any recursive deserialization.
            // Braces/brackets inside JSON strings do not count as structure.
            int depth=0;bool quoted=false,escaped=false;
            foreach(char c in text) {
                if(quoted) {
                    if(escaped)escaped=false;
                    else if(c=='\\')escaped=true;
                    else if(c=='\"')quoted=false;
                } else if(c=='\"')quoted=true;
                else if(c=='{'||c=='[') {
                    if(++depth>MaximumDepth)throw new InvalidDataException("Analysis status exceeds its nesting limit.");
                } else if(c=='}'||c==']') {
                    if(--depth<0)throw new InvalidDataException("Analysis status has unbalanced structure.");
                }
            }
            if(depth!=0||quoted)throw new InvalidDataException("Analysis status has incomplete structure.");
            // Full syntax/type validation remains the serializer's job. This
            // preflight is a resource boundary, not a replacement JSON parser.
        }
        internal static object DeserializeObject(string text)
        {
            ValidateText(text);
            try { return Serializer().DeserializeObject(text); }
            // Invalid JSON is a failed record, not a missing optional threshold.
            // Keep payload text out of the error and preserve the parser cause.
            catch(ArgumentException error) { throw new InvalidDataException("Analysis status has invalid JSON syntax.",error); }
            catch(InvalidOperationException error) { throw new InvalidDataException("Analysis status could not be parsed within its limits.",error); }
        }
        internal static string Serialize(object value)
        { string text=Serializer().Serialize(value);ValidateText(text);return text; }
    }
}
