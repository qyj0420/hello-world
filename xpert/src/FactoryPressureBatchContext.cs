using System;

namespace Xiongxin.OWTS.Desktop
{
    // In-memory host intent, not an electrical permit. The resident device
    // server independently owns and consumes its one-use continuation permit.
    internal sealed class FactoryPressureBatchContext
    {
        readonly object gate = new object();
        FactoryPressureBatchRequest active;
        int completed;
        bool authorized, revoked;

        internal FactoryPressureBatchRequest Prepare(bool automatic, bool continuation,
            long request, int step, int repetition, int total, double target, string fingerprint)
        {
            if (request <= 0 || Double.IsNaN(target) || Double.IsInfinity(target) || target < 0)
                throw new InvalidOperationException("本轮请求或目标无效。");
            lock (gate)
            {
                if (!automatic || target == 0)
                {
                    active = null; completed = 0; authorized = false;
                    if (!continuation) revoked = false;
                    return new FactoryPressureBatchRequest("", false, false, 1, 1, request, step, repetition, target, fingerprint);
                }
                if (step < 0 || repetition < 1 || total < repetition || total - repetition + 1 > 100 || String.IsNullOrEmpty(fingerprint))
                    throw new InvalidOperationException("自动批次的计划或次数无效。");
                if (!continuation) { active = null; completed = 0; authorized = false; revoked = false; }
                if (revoked) throw new InvalidOperationException("原批次已停止，请重新点击开始；不能自动恢复。");
                if (active != null && continuation && active.Step == step && active.TargetKv == target)
                {
                    if (!authorized || completed != active.Round || active.Round >= active.Count ||
                        request != active.RequestSequence + 1 || repetition != active.Repetition + 1 ||
                        fingerprint != active.Fingerprint)
                        throw new InvalidOperationException("自动批次上下文已改变或上轮尚未完成，未请求下一轮。");
                    active = new FactoryPressureBatchRequest(active.Id, true, true, active.Round + 1,
                        active.Count, request, step, repetition, target, fingerprint);
                    return active;
                }
                if (active != null && continuation && (!authorized || completed != active.Count))
                    throw new InvalidOperationException("上一电压批次尚未完成，不能转入下一批次。");
                // A new voltage/plan row starts a new explicitly authorized group.
                completed = 0; authorized = false;
                active = new FactoryPressureBatchRequest(Guid.NewGuid().ToString("N"), true, false, 1,
                    total - repetition + 1, request, step, repetition, target, fingerprint);
                return active;
            }
        }

        internal void Authorize(FactoryPressureBatchRequest request)
        {
            lock (gate)
            {
                RequireCurrent(request);
                if (request.Continuation || request.Round != 1) throw new InvalidOperationException("只允许明确授权批次首轮。");
                authorized = true;
            }
        }
        internal void RequireAuthorized(FactoryPressureBatchRequest request)
        { lock (gate) { RequireCurrent(request); if (!authorized) throw new InvalidOperationException("本批次尚未确认。"); } }
        internal void Complete(FactoryPressureBatchRequest request)
        {
            lock (gate)
            {
                RequireCurrent(request);
                if (!authorized || completed != request.Round - 1) throw new InvalidOperationException("批次完成顺序不一致。");
                completed = request.Round;
            }
        }
        internal void Revoke() { lock (gate) { active = null; completed = 0; authorized = false; revoked = true; } }
        void RequireCurrent(FactoryPressureBatchRequest request)
        {
            if (revoked || request == null || !request.Automatic || !Object.ReferenceEquals(active, request))
                throw new InvalidOperationException("批次已经失效或被替换。");
        }
    }

    internal sealed class FactoryPressureBatchRequest
    {
        internal readonly string Id, Fingerprint;
        internal readonly bool Automatic, Continuation;
        internal readonly int Round, Count, Step, Repetition;
        internal readonly long RequestSequence;
        internal readonly double TargetKv;
        internal FactoryPressureBatchRequest(string id, bool automatic, bool continuation, int round, int count,
            long request, int step, int repetition, double target, string fingerprint)
        { Id=id; Automatic=automatic; Continuation=continuation; Round=round; Count=count; RequestSequence=request;
            Step=step; Repetition=repetition; TargetKv=target; Fingerprint=fingerprint; }
    }
}
