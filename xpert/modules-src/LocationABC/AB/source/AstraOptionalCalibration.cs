using System;
using System.IO;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // CAL supplies optional reference evidence. Geometry, LOC identity and project
    // errors remain errors; absence of CAL must not manufacture zero-loss data or
    // prevent an otherwise self-contained engineering-value LOC from being read.
    private static ParsedCalibration A4ResolveOptionalCalibration(string explicitPath,string locPath,
        string projectPath,string requestedPhase,out string resolvedPath,out string source,
        out string priority,out int candidateCount)
    {
        if(!String.IsNullOrWhiteSpace(projectPath))
        {
            string project=Path.GetFullPath(projectPath.Trim());
            if(!Directory.Exists(project)&&!File.Exists(project))
                throw new FileNotFoundException("The project path is unavailable; this is not an optional CAL error.",project);
        }
        if(!String.IsNullOrWhiteSpace(explicitPath))
        {
            string path=Path.GetFullPath(explicitPath.Trim());
            if(!String.Equals(Path.GetExtension(path),".cal",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("calibrationFilePath must be a .cal file.");
            if(!File.Exists(path))return A4UnavailableCalibration(requestedPhase,"explicit_calibration_missing",
                out resolvedPath,out source,out priority,out candidateCount);
            try
            {
                return ResolveCalibration(path,locPath,projectPath,requestedPhase,
                    out resolvedPath,out source,out priority,out candidateCount);
            }
            catch(IOException)
            {
                return A4UnavailableCalibration(requestedPhase,"explicit_calibration_unreadable",
                    out resolvedPath,out source,out priority,out candidateCount);
            }
            catch(InvalidDataException)
            {
                return A4UnavailableCalibration(requestedPhase,"explicit_calibration_unreadable",
                    out resolvedPath,out source,out priority,out candidateCount);
            }
            catch(UnauthorizedAccessException)
            {
                return A4UnavailableCalibration(requestedPhase,"explicit_calibration_unreadable",
                    out resolvedPath,out source,out priority,out candidateCount);
            }
        }
        // Delegate every successful selection to the frozen original resolver:
        // same phase, ALL, then other phase; calibration value/path tie ordering
        // and successful parsed values are unchanged.
        try
        {
            return ResolveCalibration(explicitPath,locPath,projectPath,requestedPhase,
                out resolvedPath,out source,out priority,out candidateCount);
        }
        catch(FileNotFoundException ex)
        {
            // The resolver catches individual CAL read failures. This exact
            // condition means no readable CAL survived, not a missing project,
            // missing LOC, directory access failure or unrelated I/O error.
            if(ex.Message!="calibrationFilePath is blank and no usable CAL file was found in the LOC/project directory.")throw;
            string[] roots={Path.GetDirectoryName(locPath),String.IsNullOrWhiteSpace(projectPath)?null:
                Directory.Exists(projectPath)?Path.GetFullPath(projectPath):Path.GetDirectoryName(Path.GetFullPath(projectPath))};
            bool found=false;
            foreach(string root in roots.Where(p=>!String.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(!Directory.Exists(root))throw new DirectoryNotFoundException("Calibration search project directory is unavailable: "+root);
                // Enumeration errors deliberately propagate: they are not proof
                // of an absent CAL and must not hide an inaccessible project.
                if(Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly)
                    .Any(p=>String.Equals(Path.GetExtension(p),".cal",StringComparison.OrdinalIgnoreCase)))found=true;
            }
            return A4UnavailableCalibration(requestedPhase,found?"automatic_calibration_files_unreadable":"automatic_calibration_not_found",
                out resolvedPath,out source,out priority,out candidateCount);
        }
    }
    private static ParsedCalibration A4UnavailableCalibration(string phase,string reason,
        out string resolvedPath,out string source,out string priority,out int candidateCount)
    {
        resolvedPath="";source="unavailable_optional_calibration";priority=reason;candidateCount=0;
        return new ParsedCalibration {Path="",Phase=NormalizePhase(phase),Samples=new double[0],
            CalibrationValuePc=double.NaN,CableLength=double.NaN,VelocityMPerUs=double.NaN,
            SampleRate=double.NaN,CalibrationCoefficient=double.NaN,StartTime=double.NaN,
            FinalTime=double.NaN,AttenuationLossDb=double.NaN};
    }
}
