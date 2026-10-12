using System;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        const double ReviewBandGapHz=1000;
        bool reviewBandAvailable=true;
        double reviewBandSampleRate=double.NaN;
        bool reviewBandHasSelection;
        Timer reviewFilterDragTimer;
        int reviewFilterDragEndpoint;
        double CurrentReviewSampleRate { get { return selected>=0?working[selected,7]:double.NaN; } }

        double ReviewBandMaximumHz()
        {
            double fs=CurrentReviewSampleRate;
            if(selected<0)return 60000000;
            if(!Finite(fs)||fs<=0)return 0;
            // The numeric editor resolves 0.001 MHz. Keep its maximum strictly
            // below Nyquist, including when Nyquist lies exactly on that grid.
            return Math.Min(60000000,Math.Floor((fs*.5-Math.Max(1,fs*5e-13))/1000)*1000);
        }

        double[] NormalizeReviewFilters(double[] input,int endpoint)
        {
            double[] p={0,50000,7000000,4,5,2};
            if(input!=null)for(int i=0;i<Math.Min(input.Length,p.Length);i++)if(Finite(input[i]))p[i]=input[i];
            p[0]=p[0]==1?1:0;
            p[3]=p[3]==2?2:p[3]==8?8:4;
            p[4]=Math.Round(Math.Max(1,Math.Min(20,p[4])));
            p[5]=Math.Round(Math.Max(0,Math.Min(p[4]-1,p[5])));
            double upper=ReviewBandMaximumHz();reviewBandAvailable=upper>=1000000;
            if(!reviewBandAvailable)upper=60000000;
            p[1]=Math.Max(10000,Math.Min(Math.Min(3000000,upper-ReviewBandGapHz),p[1]));
            p[2]=Math.Max(1000000,Math.Min(upper,p[2]));
            if(p[1]>=p[2])
            {
                if(endpoint==2)p[1]=Math.Max(10000,p[2]-ReviewBandGapHz);
                else p[2]=Math.Min(upper,p[1]+ReviewBandGapHz);
            }
            return p;
        }

        double[] SetReviewFilters(double[] input,int endpoint=0)
        {
            var p=NormalizeReviewFilters(input,endpoint);var prior=pdmap.FilterParameters;
            bool changed=false;for(int i=0;i<p.Length;i++)if(!p[i].Equals(prior[i])){changed=true;break;}
            bool previousSync=uiSync;uiSync=true;
            try
            {
                if(changed)pdmap.FilterParameters=p;
                if(!reviewBandAvailable&&p[0]==0)pdmap.ShowFilteredCurve=false;
            }
            finally { uiSync=previousSync; }
            return p;
        }

        void SyncReviewBandRange()
        {
            reviewBandSampleRate=CurrentReviewSampleRate;
            reviewBandHasSelection=selected>=0;
            double upper=reviewBandAvailable?ReviewBandMaximumHz():60000000;
            highPass.Minimum=10;highPass.Maximum=(decimal)(Math.Min(3000000,upper-ReviewBandGapHz)/1000);
            lowPass.Minimum=1;lowPass.Maximum=(decimal)(upper/1000000);
            filterChoices[1].Enabled=reviewBandAvailable;
        }

        void PreviewReviewBandEndpoint(int endpoint)
        {
            if(endpoint==0)return;
            var p=pdmap.FilterParameters;p[1]=(double)highPass.Value*1000;p[2]=(double)lowPass.Value*1000000;
            p=NormalizeReviewFilters(p,endpoint);
            bool previousSync=uiSync;uiSync=true;
            try { SetNumeric(highPass,p[1]/1000);SetNumeric(lowPass,p[2]/1000000);SyncFilterSliders(); }
            finally { uiSync=previousSync; }
        }

        void ScheduleReviewFilterDrag(TrackBar slider,int endpoint)
        {
            PreviewReviewBandEndpoint(endpoint);
            if(!slider.Capture){ApplyFilterFromUi(endpoint);return;}
            reviewFilterDragEndpoint=endpoint;
            if(reviewFilterDragTimer==null)
            {
                reviewFilterDragTimer=new Timer { Interval=50 };
                reviewFilterDragTimer.Tick+=(s,e)=>FlushReviewFilterDrag();
                Disposed+=(s,e)=>reviewFilterDragTimer.Dispose();
            }
            if(!reviewFilterDragTimer.Enabled)reviewFilterDragTimer.Start();
        }

        void FlushReviewFilterDrag()
        {
            if(reviewFilterDragTimer==null||!reviewFilterDragTimer.Enabled)return;
            reviewFilterDragTimer.Stop();ApplyFilterFromUi(reviewFilterDragEndpoint);
        }
    }
}
