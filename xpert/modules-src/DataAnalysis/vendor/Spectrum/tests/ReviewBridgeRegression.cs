using System;
using System.Linq;
using System.Reflection;
using HVX.UnifiedSpectrum;

internal static class ReviewBridgeRegression
{
    static int passed;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; }
    static double[,] Matrix()
    {
        var m = new double[3, 37];
        for (int r = 0; r < 3; r++)
        {
            m[r,0] = r % 2 + 1; m[r,1] = r + 10; m[r,2] = 20; m[r,3] = 30+r*90;
            m[r,5] = 415; m[r,9] = 2; m[r,13] = 80+r*100; m[r,15] = 100+r*10;
            m[r,16] = 200+r*10; m[r,23] = 100+r; m[r,24] = 1000000+r*100000;
            m[r,33] = .8; m[r,35] = 1; m[r,36] = r+1;
        }
        return m;
    }
    static double[] Row(double[,] m,int row) { return Enumerable.Range(0,m.GetLength(1)).Select(c=>m[row,c]).ToArray(); }
    static T Call<T>(object c,string name,params object[] args)
    { return (T)c.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,args); }
    static void Throws(Action action,string label)
    { bool threw=false;try{action();}catch(ArgumentException){threw=true;}Check(threw,label); }

    [STAThread]
    static int Main()
    {
        try
        {
            using(var c = new UnifiedSpectrumControl())
            {
                c.AutomaticConclusions=false; c.AcceptedOnly=true;
                var m=Matrix(); c.LoadDataMatrix(m);
                int eventCount=0;c.PdMapSelected+=(s,e)=>eventCount++;
                c.AddSelectionRegion(0,415,0,1000,false);
                double[] beforeRanges=c.CurrentAxisRanges; int clones=c.MatrixCloneCount;
                c.HighlightSourceRow(1);
                Check(c.SelectedSourceRowIndex==1&&c.SelectedPdMapId==2,"highlight duplicate ID by row");
                Check(eventCount==0&&c.PdMapSelectionSequence==0&&c.DoubleClickedSourceRowIndices.Length==0,"highlight no synthetic click");
                string oldContext=Call<string>(c,"ConclusionContextKey",true);
                var row=Row(m,1);row[13]=199;row[9]=99;c.UpdateReviewRow(1,row);
                row[13]=300;
                Check(c.ResultParameterMatrix[1,13]==199&&m[1,13]==180,"row update detached");
                Check(c.SelectedSourceRowIndex==1&&c.SelectedPdMapId==99,"selection identity refreshed");
                Check(c.CurrentAxisRanges.SequenceEqual(beforeRanges)&&c.SelectionRegionCount==1,"row update preserves zoom and ROI");
                Check(c.MatrixCloneCount==clones,"row update no full clone");
                Check(oldContext!=Call<string>(c,"ConclusionContextKey",true),"row update invalidates conclusion context");
                int builds=c.DataBuildCount;c.UpdateReviewRow(1,Row(c.ResultParameterMatrix,1));
                Check(c.DataBuildCount==builds,"identical row cached");
                Throws(()=>c.UpdateReviewRow(1,new double[36]),"invalid width rejected");
                Throws(()=>c.UpdateReviewRow(4,new double[37]),"invalid index rejected");
                Check(c.ResultParameterMatrix[1,13]==199,"invalid update atomic");
                bool[] mask={true,false,true};c.SetReviewVisibility(mask);mask[0]=false;
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,2}),"mask cloned and source identity retained");
                Check(c.SelectedSourceRowIndex==-1,"hidden selection cleared");
                Check(c.SelectionRegionCount==1&&c.CurrentAxisRanges.SequenceEqual(beforeRanges),"mask retains regions/ranges");
                Check(c.ResultParameterMatrix[1,35]==1,"mask does not reject rows");
                Check(Call<double[,]>(c,"ConclusionInput",7).GetLength(0)==2,"task15 respects mask");
                c.HighlightSourceRow(1);Check(c.SelectedSourceRowIndex==-1,"hidden highlight not stale");
                var returned=c.VisibleSourceRowIndices;returned[0]=100;
                Check(c.VisibleSourceRowIndices[0]==0,"visible rows cloned");
                Throws(()=>c.SetReviewVisibility(new bool[1]),"invalid mask rejected");
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,2}),"invalid mask atomic");
                for(int mode=0;mode<=12;mode++)
                {
                    c.DisplayModeIndex=mode;
                    Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,2}),"all modes filtered "+mode);
                    Check(c.RunSelfCheck(false),"selfcheck mode "+mode);
                }
                c.DisplayModeIndex=0;c.SetReviewVisibility(null);
                Check(c.VisibleSourceRowCount==3,"null restores review mask");
                c.SetReviewVisibility(new[]{true,false,true});
                c.LoadDataMatrix(c.ResultParameterMatrix);
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,2}),"equal reload preserves mask");
                var newer=c.ResultParameterMatrix;newer[0,13]=81;c.LoadDataMatrix(newer);
                Check(c.VisibleSourceRowCount==3,"new data clears mask");
                c.SetReviewVisibility(new[]{false,false,false});
                Check(c.EmptyStateText=="当前筛选无可见脉冲","empty review status must not assert absence of physical discharge");
                Check(Call<double[,]>(c,"ConclusionInput",7).GetLength(0)==0,"no masked conclusions");
                c.SetReviewVisibility(null);c.AddSelectionRegion(0,100,0,1000,false);
                c.RegionActionCommand=1;
                c.SetReviewVisibility(new[]{true,true,false});
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{1}),"ROI deletion intersects review mask");
                c.RestoreDeletedRows();
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,1}),"restoring ROI preserves review mask");
                row=Row(c.ResultParameterMatrix,1);row[35]=0;c.UpdateReviewRow(1,row);
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0}),"accepted update linked");
                row[35]=1;c.UpdateReviewRow(1,row);
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,1}),"reaccept linked");
                c.UpdateInputs("",Matrix(),0,false,-1,new[]{true,true,true},true);
                Check(c.VisibleSourceRowCount==3,"atomic new input clears mask");
                c.HighlightSourceRow(-1);Check(c.SelectedSourceRowIndex==-1,"explicit clear highlight");
            }
            using(var c=new UnifiedSpectrumControl())
            {
                c.AutomaticConclusions=false; c.AcceptedOnly=true;
                var m=Matrix();m[0,13]=m[1,13]=m[2,13]=80;c.LoadDataMatrix(m);
                c.DisplayModeIndex=1;string context=Call<string>(c,"ConclusionContextKey",true);
                var range=new[]{2.5,1.5,999.0};c.SetReviewCountFilter(range);range[0]=100;
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{0,2}),"N-L per-phase counts not combined before filter");
                Check(c.GetDisplayedPoints().Cast<double>().Contains(2.0),"N-L kept phase contributes count 2");
                Check(c.ResultParameterMatrix.Cast<double>().SequenceEqual(m.Cast<double>()),"count filter never edits source");
                Check(Call<double[,]>(c,"ConclusionInput",7).GetLength(0)==3,"count filter not applied to conclusions");
                Check(context==Call<string>(c,"ConclusionContextKey",true),"count filter does not invalidate unchanged conclusions");
                for(int mode=0;mode<=12;mode++) if(mode!=1)
                {c.DisplayModeIndex=mode;Check(c.VisibleSourceRowCount==3,"count range N-L only "+mode);}
                c.DisplayModeIndex=1;c.SetReviewCountFilter(new[]{.5,1.0});
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{1}),"count closed upper bound");
                c.SetReviewCountFilter(new[]{1.0,2.0});
                Check(c.VisibleSourceRowCount==3,"count both inclusive boundaries");
                c.SetReviewCountFilter(new[]{3.0,4.0});
                Check(c.VisibleSourceRowCount==0&&Enumerable.Range(0,c.GetDisplayedPoints().GetLength(0)).All(i=>c.GetDisplayedPoints()[i,1]==0),"count rejected bins retained as zero");
                foreach(var disabled in new double[][]{null,new double[0],new[]{1.0},new[]{2.0,2.0},new[]{double.NaN,2},new[]{1,double.PositiveInfinity}})
                {
                    c.SetReviewCountFilter(new[]{3.0,4.0});c.SetReviewCountFilter(disabled);
                    Check(c.VisibleSourceRowCount==3,"Task7 invalid/equal count range disables");
                }
                c.SetReviewCountFilter(new[]{2.5,1.5});int builds=c.DataBuildCount;c.SetReviewCountFilter(new[]{1.5,2.5,100.0});
                Check(c.DataBuildCount==builds,"normalized repeat count range cached");
                c.SetReviewVisibility(new[]{false,true,true});
                Check(c.VisibleSourceRowCount==0,"count evaluated after independent row mask");
                c.SetReviewCountFilter(null);
                Check(c.VisibleSourceRowIndices.SequenceEqual(new[]{1,2}),"disable count retains row mask");
                Check(c.RunSelfCheck(false),"count bridge final selfcheck");
            }
            Console.WriteLine("ReviewBridgeRegression PASS "+passed);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
