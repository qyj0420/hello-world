using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        [Browsable(false)] public Action<string> DiagnosticSink {get;set;}
        [Browsable(false)] public string LastCustomerDiagnostic {get;private set;}="";
        string ComparisonJointCustomerMessage(string diagnostic,bool stopped)
        {
            LastCustomerDiagnostic=diagnostic??"";
            try {if(DiagnosticSink!=null&&LastCustomerDiagnostic.Length>0)DiagnosticSink(LastCustomerDiagnostic);}
            catch(Exception error){if(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)throw;}
            return stopped?T("接头查找未完成。请检查所选校准，重新计算平均波形后再试。","Joint search did not complete. Check the selected calibrations, recalculate the mean waveform, and retry."):
                T("未检出可复核的接头候选。请检查校准波形，或人工复核。","No joint candidates were found for review. Check the calibration waveforms, or review them manually.");
        }
    }
}
