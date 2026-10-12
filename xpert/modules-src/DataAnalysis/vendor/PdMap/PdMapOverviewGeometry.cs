using System;
using System.Linq;
using System.Text.RegularExpressions;
using HVX.Geometry;
using LabVIEWPdMap.ProjectCalibrationImplementation;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Whole-LOC measurements must not inherit the last selected MAP's phase,
        // default 415 m / 170 m/us, selection, or compensated input-row state.
        // Resolve only cached project data; this method performs no file reads and
        // does not select/load a synthetic MAP row into any control.
        internal bool TryResolveOverviewGeometry(int phase,double locVelocityMPerUs,
            out ConnectingCableGeometryResult model,out string status)
        {
            model=null;status="OverviewGeometryUnavailable";
            try
            {
                if(phase<0||phase>3)throw new ArgumentException("Invalid LOC phase.");
                if(!ProjectCalibrationEnabled||projectMetadataLoadCode!="MetadataLoaded")
                    throw new ArgumentException("Project cable metadata is unavailable.");
                string phaseName=phase==0?"ALL":phase==1?"A":phase==2?"B":"C";
                string cablePhase=phaseName;
                var blocks=projectCableBlocks.Where(b=>b.Phase==phaseName&&b.Records.Count>0).ToArray();
                if(blocks.Length==0&&phase!=0)
                {
                    cablePhase="ALL";
                    blocks=projectCableBlocks.Where(b=>b.Phase=="ALL"&&b.Records.Count>0).ToArray();
                }
                var block=blocks.FirstOrDefault();
                double total;string lengthSource;
                if(block!=null)
                {
                    if(!blocks.All(b=>b.Valid&&b.Positions.Count>0&&b.Positions[b.Positions.Count-1]>0&&
                        b.Records.SequenceEqual(block.Records)&&SameProjectMaterialSegments(block,b)))
                        throw new ArgumentException("Invalid or conflicting same-phase CABLE geometry.");
                    total=block.Positions[block.Positions.Count-1];lengthSource="CABLE.in:"+cablePhase;
                }
                else if(IsFinite(projectItemLengthM)&&projectItemLengthM>0)
                { total=projectItemLengthM;lengthSource="ITEM.in:explicit cable length"; }
                else throw new ArgumentException("No explicit project cable length for the selected LOC phase.");
                if(!IsFinite(total)||total<=connectingCableLengthM)
                    throw new ArgumentException("Connecting lead must be shorter than the total cable.");

                if(MixedCableCalculation)
                {
                    // The existing resolver validates CAL phase, marker timing,
                    // material continuity, and lead compensation. Its synthetic
                    // evidence row is private and never changes matrix/selection.
                    var evidence=new double[36];evidence[0]=phase;evidence[5]=total;
                    model=ResolveConnectingGeometry(evidence);
                    status="ReadyOverviewMixed; length="+lengthSource+"; phase="+phaseName+
                        (connectingCableLengthM>0?"; connecting lead compensated":"; CAL/material timing");
                    return true;
                }

                double velocity=double.NaN;string velocitySource="";
                if(block!=null)
                {
                    // Only a standalone phase-level declaration is a uniform
                    // speed. A material record's V applies to its own interval.
                    foreach(string record in block.Records)
                    {
                        if(Regex.IsMatch(record,@"^[012]\s")||!HasMaterialVelocityDeclaration(record))continue;
                        double declared;string unusedMaterial;
                        if(!TryMaterialVelocity(record,out declared,out unusedMaterial)||
                            declared>ConnectingCableGeometry.MaximumPhysicalVelocityMPerUs)
                            throw new ArgumentException("Invalid same-phase CABLE uniform velocity.");
                        if(IsFinite(velocity)&&!SameMixedVelocity(velocity,declared))
                            throw new ArgumentException("Conflicting same-phase CABLE uniform velocities.");
                        velocity=declared;velocitySource="CABLE.in:"+cablePhase+" uniform velocity";
                    }
                }
                if(!IsFinite(velocity)&&IsFinite(locVelocityMPerUs)&&locVelocityMPerUs>0)
                { velocity=locVelocityMPerUs;velocitySource="current LOC header"; }
                if(!IsFinite(velocity))
                {
                    // Do not invoke the selector's nearest-other-phase fallback.
                    var allowed=projectCatalog.Where(c=>c.Phase==phaseName||c.Phase=="ALL").ToList();
                    string selection;
                    Candidate cal=Selector.SelectForMap(allowed,phase,total,out selection);
                    velocity=cal.Velocity;velocitySource="CAL:"+cal.Phase+" header";
                }
                if(!IsFinite(velocity)||velocity<=0||velocity>ConnectingCableGeometry.MaximumPhysicalVelocityMPerUs)
                    throw new ArgumentException("No valid physical velocity for the selected LOC phase.");
                model=ConnectingCableGeometry.Build(total,0,true,double.NaN,new[]{0.0,total},
                    new[]{velocity},new[]{true},0);
                status="ReadyOverviewUniform; length="+lengthSource+"; velocity="+velocitySource+"; phase="+phaseName;
                return true;
            }
            catch(SelectionFailure ex)
            {status="OverviewGeometryUnavailable: "+ex.Code+": "+ex.Message;return false;}
            catch(ArgumentException ex)
            {status="OverviewGeometryUnavailable: "+ex.Message;return false;}
        }
    }
}
