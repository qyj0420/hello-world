using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HVX.PdReview;

// Pure managed regression: no control instance, product entry or SDK is invoked.
internal static class R58AnalysisMatrixMergePerformance
{
    sealed class Input
    {
        internal double[,] Working,Matching,Original;
        internal ReviewAnalysisResult[] Results;
    }
    static int checks;
    static void Require(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
    static void Reference(Input input,out double[,] parameters,out double[,] waveforms)
    {
        var working=input.Working;var matching=input.Matching;var original=input.Original;var results=input.Results;
        int oldN=working.GetLength(0),total=checked(oldN+results.Sum(x=>x.Parameters.GetLength(0)));
        int columns=Math.Max(36,working.GetLength(1)),width=matching.GetLength(1);
        foreach(var result in results)width=Math.Max(width,Math.Max(result.Raw.GetLength(1),Math.Max(result.Filtered.GetLength(1),result.Equivalent.GetLength(1))));
        if((long)3*total*width>100000000)throw new InvalidOperationException("Combined waveform matrix exceeds the supported memory budget.");
        parameters=new double[total,columns];waveforms=new double[checked(3*total),width];
        for(int r=0;r<3*total;r++)for(int c=0;c<width;c++)waveforms[r,c]=double.NaN;
        for(int r=0;r<oldN;r++){
            for(int c=0;c<columns;c++)parameters[r,c]=working[r,c];
            bool blocks=matching.GetLength(0)==3L*oldN;int raw=matching.GetLength(0)==oldN||blocks?r:(int)original[r,9];
            for(int c=0;c<matching.GetLength(1);c++){
                waveforms[r,c]=matching[raw,c];waveforms[r+total,c]=matching[blocks?r+oldN:raw,c];
                if(blocks)waveforms[r+2*total,c]=matching[r+2*oldN,c];
            }
        }
        int offset=oldN;
        foreach(var result in results){
            for(int r=0;r<result.Parameters.GetLength(0);r++){
                for(int c=0;c<columns;c++)parameters[offset+r,c]=c<result.Parameters.GetLength(1)?result.Parameters[r,c]:double.NaN;
                for(int c=0;c<result.Raw.GetLength(1);c++)waveforms[offset+r,c]=result.Raw[r,c];
                for(int c=0;c<result.Filtered.GetLength(1);c++)waveforms[offset+r+total,c]=result.Filtered[r,c];
                for(int c=0;c<result.Equivalent.GetLength(1);c++)waveforms[offset+r+2*total,c]=result.Equivalent[r,c];
            }offset+=result.Parameters.GetLength(0);
        }
        for(int r=0;r<total;r++)parameters[r,9]=r;
    }
    static void Candidate(Input input,out double[,] parameters,out double[,] waveforms)
    {PdReviewControl.MergeAnalysisResultsCore(input.Working,input.Matching,input.Original,input.Results,out parameters,out waveforms);}
    static double[,] Matrix(int rows,int columns,int seed)
    {
        var values=new double[rows,columns];
        for(int r=0;r<rows;r++)for(int c=0;c<columns;c++){
            int n=(r*131+c+seed)%113;
            values[r,c]=n==0?BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8000000000035UL)):
                n==1?BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL)):
                n==2?double.PositiveInfinity:n==3?double.NegativeInfinity:(r+seed)*0.25-c*0.125;
        }return values;
    }
    static ReviewAnalysisResult Result(int rows,int parameters,int raw,int filtered,int equivalent,int seed)
    {return new ReviewAnalysisResult{Parameters=Matrix(rows,parameters,seed),Raw=Matrix(rows,raw,seed+1),Filtered=Matrix(rows,filtered,seed+2),Equivalent=Matrix(rows,equivalent,seed+3)};}
    static string Digest(double[,] values)
    {
        using(var hash=SHA256.Create()){
            int bytes=checked(values.Length*sizeof(double));var buffer=new byte[Math.Min(bytes,65536)];
            for(int offset=0;offset<bytes;){int count=Math.Min(buffer.Length,bytes-offset);Buffer.BlockCopy(values,offset,buffer,0,count);hash.TransformBlock(buffer,0,count,null,0);offset+=count;}
            hash.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(hash.Hash).Replace("-","");
        }
    }
    static string[] InputDigests(Input input)
    {
        var values=new List<double[,]>{input.Working,input.Matching,input.Original};
        foreach(var result in input.Results)values.AddRange(new[]{result.Parameters,result.Raw,result.Filtered,result.Equivalent});
        return values.Select(Digest).ToArray();
    }
    static void Equal(double[,] expected,double[,] actual,string name)
    {
        Require(expected.GetLength(0)==actual.GetLength(0)&&expected.GetLength(1)==actual.GetLength(1),name+" dimensions");
        for(int r=0;r<expected.GetLength(0);r++)for(int c=0;c<expected.GetLength(1);c++)
            if(BitConverter.DoubleToInt64Bits(expected[r,c])!=BitConverter.DoubleToInt64Bits(actual[r,c]))throw new Exception(name+" bits differ at "+r+","+c);
        checks++;
    }
    static Dictionary<string,object> Compare(Input input,string name)
    {
        var before=InputDigests(input);double[,] oldP,oldW,newP,newW;Reference(input,out oldP,out oldW);Candidate(input,out newP,out newW);
        Equal(oldP,newP,name+" parameters");Equal(oldW,newW,name+" waveforms");
        Require(before.SequenceEqual(InputDigests(input)),name+" inputs unchanged");
        for(int row=0;row<newP.GetLength(0);row++)if(newP[row,9]!=row)throw new Exception(name+" sequential ID");checks++;
        return new Dictionary<string,object>{{"Name",name},{"WaveformElements",newW.LongLength},{"Rows",newP.GetLength(0)},
            {"WaveformWidth",newW.GetLength(1)},{"ParametersSHA256",Digest(newP)},{"WaveformsSHA256",Digest(newW)},{"BitIdentical",true}};
    }
    static Input Make(int oldRows,int oldWidth,int layout,params ReviewAnalysisResult[] results)
    {
        var input=new Input{Working=Matrix(oldRows,40,10),Original=Matrix(oldRows,40,11),
            Matching=Matrix(layout==0?oldRows:layout==1?3*oldRows:oldRows+3,oldWidth,12),Results=results};
        if(layout==2)for(int r=0;r<oldRows;r++)input.Original[r,9]=oldRows-r;
        return input;
    }
    static void Rejected(Input input,string name)
    {
        Exception oldError=null,newError=null;double[,] p,w;
        try{Reference(input,out p,out w);}catch(Exception e){oldError=e;}
        try{Candidate(input,out p,out w);}catch(Exception e){newError=e;}
        Require(oldError!=null&&newError!=null&&oldError.GetType()==newError.GetType(),name+" rejection preserved");
    }
    static List<object> Contract()
    {
        var proof=new List<object>();
        proof.Add(Compare(Make(0,0,0),"empty"));
        proof.Add(Compare(Make(0,7,0,Result(0,36,3,5,9,1)),"zero rows positive width"));
        proof.Add(Compare(Make(0,0,0,Result(3,36,0,0,0,1)),"zero width with rows"));
        for(int layout=0;layout<3;layout++){
            proof.Add(Compare(Make(4,5,layout),"old only layout "+layout));
            proof.Add(Compare(Make(4,5,layout,Result(3,36,9,4,7,20),Result(2,40,2,11,0,30)),"mixed widths layout "+layout));
        }
        proof.Add(Compare(Make(0,0,0,Result(1,36,1,1,1,110)),"single element custom NaN and signed zero"));
        var random=new Random(58);
        for(int i=0;i<48;i++)proof.Add(Compare(Make(random.Next(0,8),random.Next(0,21),random.Next(0,3),
            Result(random.Next(0,8),36,random.Next(0,21),random.Next(0,21),random.Next(0,21),i)),"deterministic boundary "+i));
        var budget=Make(0,34000000,0,Result(1,36,0,0,0,1));Rejected(budget,"100m budget before allocation");
        var shortParameters=Make(1,1,0);shortParameters.Working=new double[1,10];Rejected(shortParameters,"short old parameters");
        return proof;
    }
    static double Time(Input input,bool candidate)
    {
        GC.Collect();GC.WaitForPendingFinalizers();double[,] p,w;var watch=Stopwatch.StartNew();
        if(candidate)Candidate(input,out p,out w);else Reference(input,out p,out w);
        watch.Stop();GC.KeepAlive(p);GC.KeepAlive(w);return watch.Elapsed.TotalMilliseconds;
    }
    static Dictionary<string,object> Measure(int width)
    {
        var input=Make(200,width,1,Result(800,36,width,width,width,71));
        var proof=Compare(input,"performance width "+width);Time(input,false);Time(input,true);
        var oldTimes=new List<double>();var newTimes=new List<double>();
        for(int i=0;i<7;i++){
            if((i&1)==0){oldTimes.Add(Time(input,false));newTimes.Add(Time(input,true));}
            else{newTimes.Add(Time(input,true));oldTimes.Add(Time(input,false));}
        }
        proof["ReferenceMilliseconds"]=oldTimes.ToArray();proof["CandidateMilliseconds"]=newTimes.ToArray();
        proof["ReferenceMedianMilliseconds"]=oldTimes.OrderBy(x=>x).ElementAt(3);proof["CandidateMedianMilliseconds"]=newTimes.OrderBy(x=>x).ElementAt(3);
        return proof;
    }
    static int Main(string[] args)
    {
        try{
            var contract=Contract();var measured=new[]{Measure(334),Measure(1000),Measure(3000)};
            var proof=new Dictionary<string,object>{{"Schema","HVX.R58.AnalysisMatrixMerge.Performance/1"},{"Checks",checks},{"Contract",contract.ToArray()},
                {"Measurements",measured},{"MemoryBudgetElements",100000000},{"AllOutputsBitIdentical",true},{"InputsUnchanged",true},
                {"HardwareAccessed",false},{"SDKCalled",false},{"ProductEntryExecuted",false},{"FullAnalysisSpeedVerified",false},
                {"Scope","Actual matrix merge core vs original loops only. Candidate extraction, thresholds, plotting and E+ core are not timed."}};
            File.WriteAllText(args[0],new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(proof),new UTF8Encoding(false));
            Console.WriteLine("R58_MATRIX_MERGE_PASS="+checks+"; proof="+args[0]);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
