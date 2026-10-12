using System;
using System.Collections.Generic;
using System.IO;

namespace HVX.PdReview
{
    public sealed class PressureCalibrationChargeReview
    {
        public string Schema { get; private set; }
        public double Phase { get; private set; }
        public double FileId { get; private set; }
        public double ThresholdPc { get; private set; }
        public int RequiredPulseCount { get; private set; }
        public int IndependentPulseCount { get; private set; }
        public bool AtMaximumRange { get; private set; }
        public bool ShouldRemind { get; private set; }

        internal PressureCalibrationChargeReview(double phase,double fileId,int count,bool maximum)
        {
            Schema="HVX.Pressure.CalibrationChargeReview/V1";
            Phase=phase;FileId=fileId;ThresholdPc=200000;RequiredPulseCount=3;
            IndependentPulseCount=count;AtMaximumRange=maximum;
            ShouldRemind=!maximum&&count>=RequiredPulseCount;
        }
    }

    // Stateless review of already extracted events, not another pulse detector.
    // Columns: phase 0, file ID 1, rate 7, original-coordinate incident 10,
    // uncorrected charge pC 15, original-coordinate window limits 31 and 32.
    // Column 9 is a cumulative MAP row number and is never an event identity.
    public static class PressureCalibrationChargeReviewPolicy
    {
        public static PressureCalibrationChargeReview Evaluate(double[,] matrix,double phase,double fileId,int actualCaptureRangeIndex)
        {
            if(matrix==null||matrix.GetLength(1)!=36)throw new ArgumentException("Invalid 36-column location matrix.","matrix");
            if(!Integer(phase)||phase<1||phase>3)throw new ArgumentOutOfRangeException("phase");
            if(!Integer(fileId)||fileId<0)throw new ArgumentOutOfRangeException("fileId");
            if(actualCaptureRangeIndex<0||actualCaptureRangeIndex>10)throw new ArgumentOutOfRangeException("actualCaptureRangeIndex");
            var incidents=new HashSet<int>();
            for(int row=0;row<matrix.GetLength(0);row++)
            {
                if(matrix[row,0]!=phase||matrix[row,1]!=fileId)continue;
                double charge=matrix[row,15],sampleRate=matrix[row,7];
                double first=matrix[row,31],last=matrix[row,32],incident=matrix[row,10];
                if(!Finite(charge)||charge<=200000||!Finite(sampleRate)||sampleRate<=0||
                    !Integer(first)||!Integer(last)||first<0||last<first||last>Int32.MaxValue||
                    !Integer(incident)||incident<first||incident>last)continue;
                // Accepted / pending classification is not measurement validity.
                // Both 0 and 1 are legitimate; a corrupt status is not evidence.
                if(matrix[row,35]!=0&&matrix[row,35]!=1)continue;
                incidents.Add((int)incident);
            }
            return new PressureCalibrationChargeReview(phase,fileId,incidents.Count,actualCaptureRangeIndex==10);
        }

        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static bool Integer(double value){return Finite(value)&&value==Math.Truncate(value);}
    }

    public sealed partial class PdReviewControl
    {
        // Host passes the fresh cumulative result and this round's actual range.
        // This API never changes the matrix, classification, files or UI. The
        // host owns its once-per-round reminder and records the review in logs.
        public PressureCalibrationChargeReview GetPressureCalibrationChargeReview(double[,] matrix,string savedRecord,int actualCaptureRangeIndex)
        {
            if(InvokeRequired)throw new InvalidOperationException("Read the record catalog on the UI thread.");
            if(String.IsNullOrWhiteSpace(savedRecord))throw new ArgumentException("A saved current-round record is required.","savedRecord");
            string current=Path.GetFullPath(savedRecord);
            var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
            if(catalog==null||catalog.GetLength(0)!=paths.Length||catalog.GetLength(1)<2)
                throw new InvalidDataException("Current record catalog is inconsistent.");
            int file=-1;
            for(int index=0;index<paths.Length;index++)
            {
                if(!String.Equals(Path.GetFullPath(paths[index]),current,StringComparison.OrdinalIgnoreCase))continue;
                if(file>=0)throw new InvalidDataException("Current record path is ambiguous.");
                file=index;
            }
            if(file<0)throw new InvalidDataException("本轮记录尚未进入项目分析目录。");
            // The catalog guarantees IDs only within a phase, not globally.
            return PressureCalibrationChargeReviewPolicy.Evaluate(matrix,catalog[file,0],catalog[file,1],actualCaptureRangeIndex);
        }
    }
}
