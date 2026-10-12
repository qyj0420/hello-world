using System;
using System.IO;
using System.Security.Cryptography;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // A separate row selection binds report statistics to this saved review.
        // The full native/review matrix, acceptance values and waveforms remain intact.
        string CreateEffectiveSelectionMask()
        {
            if(reviewMask==null||reviewMask.Length!=SourceRowCount||removed.Length!=SourceRowCount)
                throw new InvalidOperationException("Current effective selection is not ready.");
            if(!String.IsNullOrEmpty(locationFilterError))throw new InvalidOperationException("Cannot save failed location filtering: "+locationFilterError);
            var mask=new char[SourceRowCount];
            for(int i=0;i<mask.Length;i++)mask[i]=reviewMask[i]&&!removed[i]&&!IsAcquisitionReferenceOnly(i)?'1':'0';
            return new String(mask);
        }
        static string EffectiveSelectionHash(double[,] matrix)
        {
            using(var sha=SHA256.Create()){
                var prefix=new byte[8];Buffer.BlockCopy(new[]{matrix.GetLength(0),matrix.GetLength(1)},0,prefix,0,8);
                sha.TransformBlock(prefix,0,prefix.Length,null,0);
                var bytes=new byte[65536];long total=matrix.LongLength*8;
                for(long offset=0;offset<total;){int count=(int)Math.Min(bytes.Length,total-offset);
                    Buffer.BlockCopy(matrix,checked((int)offset),bytes,0,count);sha.TransformBlock(bytes,0,count,null,0);offset+=count;}
                sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");
            }
        }
    }
}
