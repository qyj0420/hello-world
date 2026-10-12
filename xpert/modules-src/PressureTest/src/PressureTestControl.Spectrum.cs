using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // Display-only changes have no operation mailbox or hardware ACK.
        // The completed gesture leaves the current camera in the review control
        // and publishes a coalesced audit notification, even during acquisition.
        public event EventHandler SpectrumViewChanged;
        private long spectrumInteractionSequence;
        private string spectrumInteractionCode = "";
        private int spectrumSelectionRegionCount;
        private long spectrumPointSelectionSequence;
        private int selectedSpectrumSourceRowIndex = -1;
        [Browsable(false)] public long SpectrumPointSelectionSequence { get { lock (stateLock) return spectrumPointSelectionSequence; } }
        [Browsable(false)] public int SelectedSpectrumSourceRowIndex { get { lock (stateLock) return selectedSpectrumSourceRowIndex; } }
        [Browsable(false)] public long SpectrumInteractionSequence { get { lock (stateLock) return spectrumInteractionSequence; } }
        [Browsable(false)] public string SpectrumInteractionCode { get { lock (stateLock) return spectrumInteractionCode; } }
        [Browsable(false)] public int SpectrumSelectionRegionCount { get { lock (stateLock) return spectrumSelectionRegionCount; } }
        public void SetSpectrumDataMatrix(double[,] parameterMatrix)
        {
            if (parameterMatrix == null || parameterMatrix.GetLength(1) < 36)
                throw new ArgumentException("Use the analysis-review parameter matrix with at least 36 columns.");
            RunOnUiThread(delegate {
                if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview){spectrumDashboardView.ClearData();return;}
                spectrumDashboardView.LoadMatrix(parameterMatrix);
                lock (stateLock) locationDataSequence++;
                Raise(LocationDataUpdated);
            });
        }
        public int[] ReadSpectrumSelectedSourceRows()
        {
            int[] result = new int[0];
            RunOnUiThread(delegate { result = spectrumDashboardView.Review.LastSelectedSourceRowIndices; });
            return result;
        }
        public void SaveSpectrumPng(string path, int width, int height)
        {
            RunOnUiThread(delegate {
                if (!spectrumDashboardView.Review.TrySavePng(path, width, height))
                    throw new InvalidOperationException(spectrumDashboardView.Review.LastError);
            });
        }
        private void HandleSpectrumUserAction(string code)
        {
            bool cameraOnly = code == "SPECTRUM_CAMERA_VIEW";
            if (cameraOnly) code = "SPECTRUM_VIEW"; // Keep the public audit vocabulary stable.
            string selected = spectrumDashboardView.SelectedSpectrumCode;
            lock (stateLock) {
                if (!String.Equals(selectedChannelCode, selected, StringComparison.OrdinalIgnoreCase)) {
                    selectedChannelCode = selected;
                    channelChangedSequence++;
                    code = "SPECTRUM";
                }
                spectrumInteractionSequence++;
                spectrumInteractionCode = code;
                spectrumSelectionRegionCount = spectrumDashboardView.Review.SelectionRegionCount;
            }
            if (cameraOnly && code == "SPECTRUM_VIEW") { Raise(SpectrumViewChanged); return; }
            if (!PressureParametersLocked && !IsBusy && BeginAction()) PublishAction(code);
            if (code == "SPECTRUM") { Raise(ChannelChanged); Raise(SpectrumChanged); }
        }
    }
}
