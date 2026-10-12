using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Current-call identities only. The public facade creates a fresh engine;
    // caller history never enters this map and cannot acquire invented peaks.
    private sealed class A4CanonicalOutputIdentity
    {
        internal double Phase,Record;
        internal int Incident,Reflection;
        internal A4Pair Pair;
        internal string PairKey {get{return A4ReviewEndpointKey(Phase,Record,Incident)+":"+Reflection.ToString(Inv);}}
        internal string IncidentKey {get{return A4ReviewEndpointKey(Phase,Record,Incident);}}
        internal string ReflectionKey {get{return A4ReviewEndpointKey(Phase,Record,Reflection);}}
    }
    // Arrays use reference equality. Neither displayed timing nor signal values
    // are an identity: two different peaks may share an estimated leading edge.
    private readonly Dictionary<double[],A4CanonicalOutputIdentity> canonicalOutputIdentities=
        new Dictionary<double[],A4CanonicalOutputIdentity>();
    private A4CanonicalOutputIdentity[] canonicalPhysicalRows=new A4CanonicalOutputIdentity[0];
    private A4CanonicalOutputIdentity[] canonicalReviewRows=new A4CanonicalOutputIdentity[0];
    private A4CanonicalOutputIdentity[] canonicalMixedRows=new A4CanonicalOutputIdentity[0];

    private void A4RegisterCanonicalOutputIdentity(double[] row,A4Context context,A4Pair pair)
    {
        int incident,reflection;
        if(pair==null||!A4CanonicalPeaks(context,pair.Incident,pair.Reflection,out incident,out reflection))
            throw new InvalidOperationException("Current output has no validated canonical peak pair.");
        A4RegisterCanonicalOutputIdentity(row,incident,reflection,pair);
    }
    private void A4RegisterCanonicalOutputIdentity(double[] row,int incident,int reflection,A4Pair pair=null)
    {
        if(row==null||row.Length!=36||!Finite(row[0])||!Finite(row[1])||incident<0||reflection<=incident)
            throw new InvalidOperationException("Invalid current canonical output identity.");
        canonicalOutputIdentities.Add(row,new A4CanonicalOutputIdentity {
            Phase=row[0],Record=row[1],Incident=incident,Reflection=reflection,Pair=pair});
    }
    private A4CanonicalOutputIdentity A4RegisteredCanonicalIdentity(double[] row,bool allowHistorical=false)
    {
        A4CanonicalOutputIdentity identity;
        if(!canonicalOutputIdentities.TryGetValue(row,out identity)||identity==null&&!allowHistorical)
            throw new InvalidOperationException("Current output lost its canonical identity; displayed indices are not a fallback.");
        if(identity!=null&&(identity.Phase!=row[0]||identity.Record!=row[1]))
            throw new InvalidOperationException("Canonical output phase or record no longer matches its row.");
        return identity;
    }
    private void A4CaptureCanonicalPhysicalRows(List<double[]> rows)
    {canonicalPhysicalRows=rows.Select(row=>A4RegisteredCanonicalIdentity(row)).ToArray();}
    private void A4CaptureCanonicalReviewRows(List<double[]> rows)
    {canonicalReviewRows=rows.Select(row=>A4RegisteredCanonicalIdentity(row,true)).ToArray();}
    private A4Pair A4CurrentPhysicalPair(double[,] parameters,int row)
    {
        var identity=A4BoundCanonicalIdentity(canonicalPhysicalRows,row,parameters.GetLength(0),A4Row(parameters,row),false);
        if(identity.Pair==null)throw new InvalidOperationException("Current physical output lost its canonical pulse pair.");
        return identity.Pair;
    }
    private A4Pair A4CurrentMixedPair(double[,] parameters,int row)
    {
        var identity=A4BoundCanonicalIdentity(canonicalMixedRows,row,parameters.GetLength(0),A4Row(parameters,row),true);
        return identity==null?null:identity.Pair;
    }
    private static A4CanonicalOutputIdentity A4BoundCanonicalIdentity(
        A4CanonicalOutputIdentity[] identities,int row,int expectedRows,double[] parameters,bool allowHistorical)
    {
        if(identities.Length!=expectedRows||row<0||row>=identities.Length)
            throw new InvalidOperationException("Canonical output row binding does not match its matrix.");
        var identity=identities[row];
        if(identity==null&&!allowHistorical)
            throw new InvalidOperationException("Current physical output is missing a canonical peak identity.");
        if(identity!=null&&(identity.Phase!=parameters[0]||identity.Record!=parameters[1]))
            throw new InvalidOperationException("Bound canonical phase or record differs from its matrix.");
        return identity;
    }
}
