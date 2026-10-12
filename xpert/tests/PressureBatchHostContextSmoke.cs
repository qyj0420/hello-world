using System;
using Xiongxin.OWTS.Desktop;

internal static class PressureBatchHostContextSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){checks++;try{action();}catch(InvalidOperationException){return;}throw new Exception(message);}
    static int Main()
    {
        var owner=new FactoryPressureBatchContext();
        var first=owner.Prepare(true,false,1,0,1,3,5,"project|phase|identity|plan");
        Check(first.Automatic&&!first.Continuation&&first.Round==1&&first.Count==3,"first");
        Reject(()=>owner.RequireAuthorized(first),"unconfirmed");
        owner.Authorize(first);owner.RequireAuthorized(first);owner.Complete(first);
        var second=owner.Prepare(true,true,2,0,2,3,5,first.Fingerprint);
        Check(second.Id==first.Id&&second.Continuation&&second.Round==2,"continuation");
        Reject(()=>owner.Authorize(second),"no synthesized first authorization");
        Reject(()=>owner.Complete(first),"old callback");
        owner.RequireAuthorized(second);owner.Complete(second);
        Reject(()=>owner.Complete(second),"duplicate completion");
        Reject(()=>owner.Prepare(true,true,2,0,3,3,5,first.Fingerprint),"duplicate request");
        Reject(()=>owner.Prepare(true,true,3,0,3,3,5,"other identity"),"context change");
        var final=owner.Prepare(true,true,3,0,3,3,5,first.Fingerprint);owner.Complete(final);
        var next=owner.Prepare(true,true,4,1,1,2,6,"second voltage");
        Check(next.Id!=first.Id&&!next.Continuation&&next.Round==1&&next.Count==2,"target requires new authorization");
        Reject(()=>owner.RequireAuthorized(next),"target not yet authorized");
        owner.Authorize(next);owner.Complete(next);owner.Revoke();
        Reject(()=>owner.Prepare(true,true,5,1,2,2,6,"second voltage"),"revoked batch cannot resume");
        var manualRestart=owner.Prepare(true,false,6,1,2,2,6,"second voltage");
        Check(!manualRestart.Continuation&&manualRestart.Count==1,"manual explicit new group");
        owner.Authorize(manualRestart);owner.Complete(manualRestart);
        var single=owner.Prepare(false,false,7,1,2,2,6,"single");
        Check(!single.Automatic&&!single.Continuation&&single.Id=="","single remains single");
        var zero=owner.Prepare(true,true,8,2,1,1,0,"zero");
        Check(!zero.Automatic,"zero not HV batch");
        var incomplete=new FactoryPressureBatchContext();var a=incomplete.Prepare(true,false,1,0,1,3,5,"A");incomplete.Authorize(a);
        Reject(()=>incomplete.Prepare(true,true,2,0,2,3,5,"A"),"not completed");
        incomplete.Complete(a);
        Reject(()=>incomplete.Prepare(true,true,2,1,1,3,6,"B"),"unfinished target cannot switch automatically");
        Reject(()=>incomplete.Prepare(true,true,3,0,2,3,5,"A"),"skipped request");
        Reject(()=>new FactoryPressureBatchContext().Prepare(true,false,1,0,1,1000,5,"A"),"count upper bound");
        Reject(()=>new FactoryPressureBatchContext().Prepare(true,false,1,0,1,3,Double.NaN,"A"),"nonfinite target");
        Console.WriteLine("PASS "+checks+" batch host intent checks; no SDK or hardware calls.");return 0;
    }
}
