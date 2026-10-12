using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HVX.ProjectModel
{
    // Presentation compatibility for the application's date-only automatic
    // project/analysis naming convention. Never rename files or stored IDs.
    internal static class AnalysisDateText
    {
        static readonly Regex LegacyDate=new Regex(@"\A([0-9]{4})年([0-9]{1,2})月([0-9]{1,2})日([0-9]{1,2})时([0-9]{1,2})分(?:([0-9]{1,2})秒)?\z",RegexOptions.CultureInvariant);
        static readonly Regex AutoName=new Regex(@"\A(.+) - ([0-9]{3,})\z",RegexOptions.CultureInvariant);
        internal static string FormatLocalTime(DateTime value)
        {return value.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);}
        static bool TryProjectDate(string value,out DateTime result)
        {
            result=default(DateTime);var match=LegacyDate.Match(value??"");
            if(match.Success)
            {
                try
                {
                    int[] parts=new int[6];for(int i=0;i<parts.Length;i++)parts[i]=match.Groups[i+1].Success?Int32.Parse(match.Groups[i+1].Value,CultureInfo.InvariantCulture):0;
                    result=new DateTime(parts[0],parts[1],parts[2],parts[3],parts[4],parts[5],DateTimeKind.Unspecified);return true;
                }
                catch(ArgumentOutOfRangeException){return false;}
            }
            return DateTime.TryParseExact(value,new[]{"yyyy-MM-dd HH-mm-ss","yyyy-MM-dd_HH-mm-ss"},CultureInfo.InvariantCulture,DateTimeStyles.None,out result);
        }
        internal static string DisplayProjectName(string value)
        {DateTime date;return TryProjectDate(value,out date)?FormatLocalTime(date):value??"";}
        internal static string FileSafeProjectPrefix(string value)
        {DateTime date;return TryProjectDate(value,out date)?date.ToString("yyyy-MM-dd HH-mm-ss",CultureInfo.InvariantCulture):value??"";}
        internal static string DisplayAutomaticName(string value)
        {
            var match=AutoName.Match(value??"");DateTime date;
            return match.Success&&TryProjectDate(match.Groups[1].Value,out date)?FormatLocalTime(date)+" - "+match.Groups[2].Value:value??"";
        }
    }
}
