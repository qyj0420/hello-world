using System;
using System.ComponentModel;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        bool forceReloadNextInput;
        double[] checkedCalibrationSource;
        bool checkedCalibrationPresent, hasAlignedCalibrationCurve, hasDampingCurve;

        [Browsable(false)]
        public bool HasCalibrationWaveform
        {
            get
            {
                if(!ReferenceEquals(checkedCalibrationSource,calibration))
                {
                    checkedCalibrationSource=calibration;
                    checkedCalibrationPresent=HasNonzeroFiniteCurve(calibration);
                }
                return checkedCalibrationPresent;
            }
        }

        [Browsable(false)]
        public bool HasDampingCurve { get { return hasDampingCurve; } }

        [Browsable(false)]
        public string CalibrationStatus
        {
            get
            {
                if(ProjectCalibrationEnabled)return ProjectCalibrationStatus+"; "+(L("Display gates: CurveVisibility[1,2,4,5] and SetCalibrationDisplay.","显示由 CurveVisibility[1,2,4,5] 与 SetCalibrationDisplay 控制。"));
                if(english)return "Calibration waveform: "+(HasCalibrationWaveform?"available":"missing or all zero")+
                    "; aligned calibration: "+(hasAlignedCalibrationCurve?"available":"unavailable")+
                    "; fitted damping: "+(HasDampingCurve?"available (supported decreasing fit)":hasAlignedCalibrationCurve?
                        "unavailable: calibration is present, but calibration/decay samples do not support a finite decreasing fit (at least two valid samples and nondegenerate sample positions are required)":
                        "unavailable (requires valid aligned calibration and nonzero incident amplitude)")+
                    ". Supply matching matrix blocks [pulse N rows, calibration N rows, decay N rows], or SetCalibration(waveform, decay, peakIndex). "+
                    "Display gates: visibility[1,2,4,5] and SetCalibrationDisplay; no calibration is synthesized from the pulse. Raw reference: "+rawDampingReferenceReason;
                return "校准波形："+(HasCalibrationWaveform?"已提供":"未提供有效波形或全零")+
                    "；对齐校准："+(hasAlignedCalibrationCurve?"可用":"不可用")+
                    "；拟合阻尼："+(HasDampingCurve?"可用（数据支持下降拟合）":hasAlignedCalibrationCurve?
                        "不可用：校准存在，但校准/阻尼样本不足以支持有限下降拟合（至少需要两个有效样本且采样位置不退化）":
                        "不可用（需要有效对齐校准和非零入射幅值）")+
                    "。输入匹配矩阵应分为 [原始 N 行、校准 N 行、阻尼 N 行] 三块，或调用 SetCalibration(waveform, decay, peakIndex)。"+
                    "显示受 visibility[1,2,4,5] 与 SetCalibrationDisplay 控制；不会根据原脉冲生成校准波形。原始参考校验："+rawDampingReferenceReason;
            }
        }

        static bool HasNonzeroFiniteCurve(double[] values)
        {
            bool any=false;
            foreach(double value in values){if(!IsFinite(value))return false;if(value!=0)any=true;}
            return any;
        }

        [Category("Diagnostics"), Description("Actual control build / 当前控件构建版本")]
        public string ControlBuildVersion { get { return "1.13.9"; } }

        [Browsable(false)] public int FilterRefreshSequence { get; private set; }

        [Category("Diagnostics"), Description("Actual assembly location; may be a host shadow-copy path / 实际程序集位置，可能为宿主缓存路径")]
        public string LoadedAssemblyPath { get { return typeof(PdMapControl).Assembly.Location; } }

        [Category("Diagnostics")]
        public bool CursorDragActive { get { return draggingCursor != 0; } }

        [Category("Diagnostics")]
        public bool BoxZoomActive { get { return boxZoomActive; } }

        [Category("Diagnostics")]
        public string LastInteractionStopReason { get; private set; } = "";

        /// <summary>Reload the next valid waveform or MAP input even if its values are unchanged.</summary>
        public void ForceReloadPdMapOnNextInput() { forceReloadNextInput = true; }

        static bool SameValues(double[] a, double[] b)
        {
            if(ReferenceEquals(a,b))return true;
            if(a==null||b==null||a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(!a[i].Equals(b[i]))return false;
            return true;
        }
    }
}
