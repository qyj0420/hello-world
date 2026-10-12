using System;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // All seven methods share one geometry gate. No position is clipped and
    // no negative/zero delay is made into a separated reflection pair.
    private static bool A4BoundaryTimingValid(A4Context c,double delay)
    {
        if(c==null||!Finite(delay)||delay<=0||delay>int.MaxValue||delay!=Math.Floor(delay))return false;
        double position=A4BoundaryOutputPosition((int)delay,c.Fs,c.Joints,c.Velocities,c.Length);
        return HVX.SignalEvidence.BoundaryConfidenceMath.WithinTolerance(position,c.Length);
    }

    // Final downgrade only, after display timing and any endpoint proposal.
    // Keep raw indices/coordinate untouched. Inside tolerance neither invents
    // missing evidence nor changes an unaccepted review into acceptance.
    private static bool A4ApplyFinalBoundaryConfidence(A4Context c,double[,] rows,int row)
    {
        // Equal, explicitly stored cursor indices do not establish a second
        // echo. Keep localisation unknown rather than label this as zero-score
        // interference. No self correlation is invented without waveform data.
        if(HVX.SignalEvidence.CoincidentConfidenceMath.IsCoincidentRow(rows,row))
        {rows[row,33]=double.NaN;rows[row,35]=0;return true;}
        double delay=rows[row,12],position=rows[row,13],incident=rows[row,10],reflection=rows[row,11];
        bool indices=Finite(incident)&&Finite(reflection)&&incident>=0&&reflection>=0&&
            incident<=int.MaxValue&&reflection<=int.MaxValue&&incident==Math.Floor(incident)&&reflection==Math.Floor(reflection)&&
            reflection-incident==delay;
        if(indices&&A4BoundaryTimingValid(c,delay)&&HVX.SignalEvidence.BoundaryConfidenceMath.WithinTolerance(position,c.Length))
        {
            if(!Finite(rows[row,33])||rows[row,33]<0){rows[row,35]=0;return true;}
            return false;
        }
        bool validDelay=Finite(delay)&&delay>=int.MinValue&&delay<=int.MaxValue&&delay==Math.Floor(delay);
        double computed=c!=null&&validDelay?A4BoundaryOutputPosition((int)delay,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
        rows[row,33]=!indices||!Finite(rows[row,33])||!validDelay||!Finite(position)||!Finite(computed)?double.NaN:0;
        rows[row,35]=0;
        return true;
    }

    // Signed extension of the existing reversed piecewise travel-time map.
    // Candidate timing validity is checked separately; this map itself never
    // expands a search or converts an unaccepted pair to an accepted pair.
    // delay = 0 maps to the far end (L); exact round-trip delay maps to zero.
    // Use each endpoint's own segment velocity for extrapolation, not the cable
    // average, and never use the rounded diagnostic round-trip as the zero point.
    private static double A4BoundaryOutputPosition(int delay,double fs,double[] joints,double[] velocities,double length)
    {
        if(!Finite(fs)||fs<=0||!Finite(length)||length<=0||joints==null||velocities==null||
            velocities.Length<1||joints.Length!=velocities.Length+1||joints[0]!=0||joints[joints.Length-1]!=length)
            return double.NaN;
        double roundTripExact=0;
        for(int k=0;k<velocities.Length;k++)
        {
            if(!Finite(joints[k])||!Finite(joints[k+1])||joints[k+1]<=joints[k]||
                !Finite(velocities[k])||velocities[k]<=0)return double.NaN;
            roundTripExact+=2*(joints[k+1]-joints[k])/velocities[k]*1e-6*fs;
        }
        if(!Finite(roundTripExact)||roundTripExact<=0)return double.NaN;
        double position;
        if(delay<0)
            position=length-(double)delay/fs*1e6/2*velocities[velocities.Length-1];
        else if(delay>roundTripExact)
            position=-((double)delay-roundTripExact)/fs*1e6/2*velocities[0];
        else
            // Keep every physically in-range integer timing output on the exact
            // pre-existing integration path; this also maps delay=0 to L.
            position=A4Position(delay,fs,joints,velocities,length);
        if(!Finite(position))return double.NaN;
        // Accumulated IEEE754 rounding only: many orders below one sample's
        // spatial resolution, even for mixed velocities. Real signed extension
        // coordinates such as -0.5 m must remain signed and must not be clipped.
        double endpointEpsilon=32*2.2204460492503131e-16*Math.Max(1,length)*Math.Max(1,velocities.Length);
        if(Math.Abs(position)<=endpointEpsilon)return 0;
        if(Math.Abs(position-length)<=endpointEpsilon)return length;
        return position;
    }

    private static double A4BoundaryCompensateCharge(double q,double position,double length,double attenuationLossDb)
    {
        if(!Finite(q)||!Finite(position)||!Finite(length)||length<=0)return double.NaN;
        // Preserve the existing unavailable/disabled attenuation behavior while
        // preventing a legitimate signed display coordinate from propagating NaN.
        // Compensation remains anchored to canonical peaks, not first arrival.
        return CompensateCharge(q,Clamp(position,0,length),length,attenuationLossDb);
    }

    private static double A4ContextCompensateCharge(A4Context c,double q,double position)
    {
        // CAL loss still describes the full measured path. Geometry compensation
        // never invents a lead attenuation coefficient or de-embeds amplitude.
        if(c.ConnectingCableLengthM>0)
            return A4BoundaryCompensateCharge(q,position+c.ConnectingCableLengthM,
                c.Length+c.ConnectingCableLengthM,c.Calibration.AttenuationLossDb);
        return A4BoundaryCompensateCharge(q,position,c.Length,c.Calibration.AttenuationLossDb);
    }
}
