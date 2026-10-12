using System;
using System.Linq;
using HVX.Geometry;
using LabVIEWPdMap.ProjectCalibrationImplementation;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        ConnectingCableGeometryResult[] reviewConnectingModels=Array.Empty<ConnectingCableGeometryResult>();
        internal void SetReviewConnectingGeometry(ConnectingCableGeometryResult[] models)
        { reviewConnectingModels=models==null?Array.Empty<ConnectingCableGeometryResult>():(ConnectingCableGeometryResult[])models.Clone(); }
        internal ConnectingCableGeometryResult[] SnapshotReviewConnectingGeometry()
        {return (ConnectingCableGeometryResult[])reviewConnectingModels.Clone();}
        bool ApplyReviewConnectingMixedModel()
        {
            if(currentParameterRowIndex<0||currentParameterRowIndex>=reviewConnectingModels.Length)return false;
            var model=reviewConnectingModels[currentParameterRowIndex];if(model==null)return false;
            mixedBoundaries=model.DutJointsM;mixedVelocities=model.DutSegmentVelocitiesMPerUs;
            mixedVelocitySources=new string[mixedVelocities.Length];
            mixedForwardTimesUs=new double[mixedBoundaries.Length];mixedReverseTimesUs=new double[mixedBoundaries.Length];
            for(int i=0;i<mixedVelocities.Length;i++)
            { mixedVelocitySources[i]="ConnectingCableResolved";mixedForwardTimesUs[i+1]=mixedForwardTimesUs[i]+(mixedBoundaries[i+1]-mixedBoundaries[i])/mixedVelocities[i]; }
            for(int i=mixedVelocities.Length-1;i>=0;i--)mixedReverseTimesUs[i]=mixedReverseTimesUs[i+1]+(mixedBoundaries[i+1]-mixedBoundaries[i])/mixedVelocities[i];
            mixedOneWayUs=mixedForwardTimesUs[mixedVelocities.Length];mixedCableModelValid=true;
            mixedModelCode="ReadyConnectingCableCompensated";mixedModelDetails="DUT-only geometry; connecting lead=150 m/us; PD incident/reflection delay unchanged.";
            return true;
        }
        internal ConnectingCableGeometryResult ResolveConnectingGeometry(double[] sourceRow)
        { return ResolveConnectingGeometry(sourceRow,false); }
        ConnectingCableGeometryResult ResolveConnectingGeometry(double[] sourceRow,bool requireCompleteProject)
        {
            if(sourceRow==null||sourceRow.Length<36)throw new ArgumentException("连接线补偿需要完整定位结果。 / Complete positioning row required.");
            double total=sourceRow[5],phaseValue=sourceRow[0];
            if(!IsFinite(total)||total<=connectingCableLengthM||!IsFinite(phaseValue)||phaseValue<0||phaseValue>3||phaseValue!=Math.Round(phaseValue))
                throw new ArgumentException("连接线长度必须小于含连接线的总长度，且相别须有效。 / Lead must be shorter than total cable length; valid phase required.");
            if(!ProjectCalibrationEnabled||projectCatalogStatus!="CatalogLoaded")
                throw new ArgumentException("连接线补偿需要项目校准数据，请先设置项目路径。 / Project CAL data is required for connecting-cable compensation.");
            string selection;
            Candidate cal;
            try { cal=Selector.SelectForMap(projectCatalog,(int)phaseValue,total,out selection); }
            catch(SelectionFailure ex) {throw new ArgumentException("连接线补偿校准不可用。 / Connecting-cable CAL unavailable: "+ex.Code,ex);}
            string requiredPhase=phaseValue==0?"ALL":phaseValue==1?"A":phaseValue==2?"B":"C";
            if(cal.Phase!=requiredPhase&&cal.Phase!="ALL")
                throw new ArgumentException("连接线補偿仅接受同相或ALL校准。 / Lead compensation requires same-phase or ALL CAL.");
            double calUs=(cal.Reflection-(double)cal.Incident)*1e6/cal.Rate;
            return ResolveConnectingGeometryFromReference(sourceRow,calUs,2e6/cal.Rate,requireCompleteProject);
        }
        internal ConnectingCableGeometryResult ResolveChangedProvidedReviewGeometry(double[] currentRow,double previousLead)
        {
            if(currentRow==null||currentRow.Length<36||!IsFinite(currentRow[5])||currentRow[5]<=0||
                !IsFinite(currentRow[6])||currentRow[6]<=0||!IsFinite(previousLead)||previousLead<0)
                throw new ArgumentException("已补偿输入缺少可恢复的原几何，请重新分析。 / Re-analyze: original compensated geometry is unavailable.");
            var total=(double[])currentRow.Clone();total[5]+=previousLead;
            var model=ResolveConnectingGeometry(total,true);
            double previousTime=2*currentRow[5]/NormalizeVelocity(currentRow[6])+2*previousLead/150.0;
            double newTime=model.GeometryDutRoundTripUs+2*connectingCableLengthM/150.0;
            // Equivalent velocity is unrounded geometry evidence, unlike diagnostic column 14.
            if(!IsFinite(previousTime)||Math.Abs(previousTime-newTime)>Math.Max(1e-7,1e-7*Math.Max(previousTime,newTime)))
                throw new ArgumentException("已补偿几何与项目校准不一致，请按新连接线长度重新分析。 / Re-analyze with the new lead: compensated geometry conflicts with project CAL.");
            return model;
        }
        internal ConnectingCableGeometryResult ResolveProvidedDutGeometry(double[] dutRow)
        {
            double length=dutRow[5],speed=dutRow[6];
            if(IsFinite(speed)&&speed>0)speed=NormalizeVelocity(speed);
            if(!IsFinite(length)||length<=0||!IsFinite(speed)||speed<=0)
                throw new ArgumentException("已补偿输入需要有效的被测电缆长度和等效波速。 / Compensated input requires valid DUT length and equivalent velocity.");
            var totalRow=(double[])dutRow.Clone();totalRow[5]=length+connectingCableLengthM;
            // This is exact host geometry from the unrounded equivalent speed,
            // NOT a measured CAL marker and NOT rounded matrix column 14.
            double reference=2*length/speed+2*connectingCableLengthM/ConnectingCableGeometry.ConnectingCableVelocityMPerUs;
            return ResolveConnectingGeometryFromReference(totalRow,reference,1e-9*Math.Max(1,reference));
        }
        ConnectingCableGeometryResult ResolveConnectingGeometryFromReference(double[] sourceRow,double calUs,double timingTolerance,bool requireCompleteProject=false)
        {
            double total=sourceRow[5],phaseValue=sourceRow[0];
            if(!IsFinite(phaseValue)||phaseValue<0||phaseValue>3||phaseValue!=Math.Round(phaseValue))throw new ArgumentException("Invalid geometry phase.");
            string phase=phaseValue==0?"ALL":phaseValue==1?"A":phaseValue==2?"B":"C";
            var blocks=projectCableBlocks.Where(b=>b.Phase==phase&&b.Records.Count>0).ToArray();
            if(blocks.Length==0&&phase!="ALL")blocks=projectCableBlocks.Where(b=>b.Phase=="ALL"&&b.Records.Count>0).ToArray();
            var first=blocks.FirstOrDefault();
            if(first!=null&&(!blocks.All(b=>b.Valid&&b.Records.SequenceEqual(first.Records)&&SameProjectMaterialSegments(first,b))||
                first.Positions.Count<2||Math.Abs(first.Positions[first.Positions.Count-1]-total)>Math.Max(1e-6,total*1e-9)))
                throw new ArgumentException("项目电缆长度或重复相别结构与输入不一致。 / Project cable geometry conflicts with input total length or duplicate phase records.");
            bool complete=first!=null&&blocks.All(b=>b.Valid&&b.Records.SequenceEqual(first.Records)&&SameProjectMaterialSegments(first,b))&&
                first.Materials.Count>0&&first.Materials[0].StartM==0&&Math.Abs(first.Materials[first.Materials.Count-1].EndM-total)<=Math.Max(1e-6,total*1e-9);
            if(complete)for(int i=1;i<first.Materials.Count;i++)if(first.Materials[i].StartM!=first.Materials[i-1].EndM)complete=false;
            if(requireCompleteProject&&!complete)
                throw new ArgumentException("更改已补偿结果需要完整的同相项目电缆结构及校准，请补全后重试或重新分析。 / Re-analyze, or provide complete same-phase project cable geometry and CAL before changing compensated results.");
            if(first!=null&&(first.MaterialIssueCount>0||first.PendingMaterialCount>0||first.Materials.Count>0&&!complete))
                throw new ArgumentException("电缆材料区间缺失或不连续，请核对CABLE.in。 / Incomplete or discontinuous CABLE material intervals.");
            double[] bounds,speeds;bool[] explicitSpeeds;
            if(complete)
            {
                bounds=new double[first.Materials.Count+1];speeds=new double[first.Materials.Count];explicitSpeeds=new bool[speeds.Length];
                for(int i=0;i<speeds.Length;i++)
                {
                    bounds[i]=first.Materials[i].StartM;bounds[i+1]=i==speeds.Length-1?total:first.Materials[i].EndM;
                    string text=first.Materials[i].OriginalName,key;double v;
                    explicitSpeeds[i]=TryMaterialVelocity(text,out v,out key);speeds[i]=explicitSpeeds[i]?v:double.NaN;
                    if(!explicitSpeeds[i]&&HasMaterialVelocityDeclaration(text))throw new ArgumentException("电缆材料波速声明无效。 / Invalid material velocity declaration.");
                }
            }
            else { bounds=new[]{0.0,total};speeds=new[]{double.NaN};explicitSpeeds=new[]{false}; }
            if(connectingCableLengthM==0)
            {
                // Restoring total geometry from a DUT-only result must use the complete
                // CAL path, never extend the old DUT's equivalent speed into the lead.
                double knownTime=0,unknownLength=0;
                for(int i=0;i<speeds.Length;i++)
                    if(explicitSpeeds[i])
                    {
                        if(!IsFinite(speeds[i])||speeds[i]<=0||speeds[i]>ConnectingCableGeometry.MaximumPhysicalVelocityMPerUs)
                            throw new ArgumentException("电缆材料波速超出物理范围。 / Material velocity is outside physical limits.");
                        knownTime+=(bounds[i+1]-bounds[i])/speeds[i];
                    }
                    else unknownLength+=bounds[i+1]-bounds[i];
                if(unknownLength>0)
                {
                    double speed=unknownLength/(calUs*.5-knownTime);
                    if(!IsFinite(speed)||speed<=0||speed>ConnectingCableGeometry.MaximumPhysicalVelocityMPerUs)
                        throw new ArgumentException("CAL无法恢复有效的总电缆波速。 / CAL cannot restore a valid total cable velocity.");
                    for(int i=0;i<speeds.Length;i++)if(!explicitSpeeds[i])speeds[i]=speed;
                }
                var totalModel=ConnectingCableGeometry.Build(total,0,true,calUs,bounds,speeds,explicitSpeeds,timingTolerance);
                if(Math.Abs(totalModel.GeometryDutRoundTripUs-calUs)>timingTolerance+1e-9)
                    throw new ArgumentException("总电缆材料传播时间与校准不一致。 / Total material travel time conflicts with CAL.");
                return totalModel;
            }
            var result=ConnectingCableGeometry.Build(total,connectingCableLengthM,true,calUs,bounds,speeds,explicitSpeeds,timingTolerance);
            if(result.GeometryCalibrationConflict)throw new ArgumentException("材料传播时间与校准时间不一致，请核对电缆参数。 / Material travel time conflicts with CAL timing.");
            return result;
        }

        // Only project CABLE coordinates include the lead. Explicit structure ports
        // retain their established DUT-coordinate contract and are never shifted twice.
        double ProjectPositionToDut(double position) { return position-connectingCableLengthM; }
        double CalibrationSelectionTotalLength(double dutLength)
        { return connectingCableLengthM>0&&inputGeometryAlreadyCompensated?dutLength+connectingCableLengthM:dutLength; }
    }
}
