using System;

namespace Xiongxin.ZeroBench
{
    internal static class StageDiagnosticsTests
    {
        internal static string Run()
        {
            int count = 0;
            var model = new StageDiagnostics();
            model.TryApply("STEP_INIT_RUNNING", "opening"); model.TryApply("STEP_ARM_RUNNING", "arming");
            Require(model["INIT"].State == StageState.Running, "entering another phase cannot pass INIT"); count++;
            model.Reset(); model.Observe("INIT", "legacy begin"); model.Observe("ARM", "legacy next");
            Require(model["INIT"].State == StageState.Running, "legacy messages cannot pass previous phase"); count++;
            model.Reset(); model.TryApply("STEP_COMPLETE_PASSED", "complete without step evidence");
            Require(model["COMPLETE"].State == StageState.Unverified, "missing steps cannot show green complete"); count++;
            model.Reset(); model.TryApply("STEP_WAIT_TRIGGER_FAILED", "PICO_STATUS=0x00000003\r\nNative stack preserved");
            model.TryApply("STEP_RELEASE_PASSED", "closed"); model.TryApply("STEP_WAIT_TRIGGER_PASSED", "late success");
            Require(model["WAIT_TRIGGER"].State == StageState.Failed && model["RELEASE"].State == StageState.Passed, "failure sticky across cleanup and late pass");
            Require(model.Details("WAIT_TRIGGER").Contains("PICO_STATUS=0x00000003\r\nNative stack preserved"), "full error retained"); count++;
            model.TryApply("STEP_COMPLETE_PASSED", "late complete");
            Require(model["COMPLETE"].State == StageState.Failed, "prior failure blocks complete pass"); count++;
            model.Reset(); model.TryApply("STEP_WAIT_TRIGGER_RUNNING", "wait"); model.TryApply("STEP_TRG_OUTPUT_RUNNING", "pulse");
            model.Abort("user canceled", true); model.TryApply("STEP_RELEASE_PASSED", "closed"); model.TryApply("STEP_WAIT_TRIGGER_PASSED", "late event");
            Require(model["WAIT_TRIGGER"].State == StageState.Canceled && model["TRG_OUTPUT"].State == StageState.Canceled && model["COMPLETE"].State == StageState.Canceled, "cancel both concurrently active phases and retain cancellation"); count++;
            model.Reset(); model.TryApply("STEP_AO_ZERO_SKIPPED", "read only");
            Require(model["AO_ZERO"].State == StageState.Skipped, "unexecuted AO not green"); count++;
            model.TryApply("STEP_TRG_OUTPUT_UNVERIFIED", "idle low write accepted"); model.TryApply("STEP_TRG_OUTPUT_RUNNING", "pulse"); model.TryApply("STEP_TRG_OUTPUT_UNVERIFIED", "write accepted, no readback");
            Require(model["TRG_OUTPUT"].State == StageState.Unverified && model["TRG_OUTPUT"].History.Count == 3, "driver acceptance distinct from physical confirmation"); count++;
            model.Reset(); foreach (string key in StageDiagnostics.Keys) if (key != "COMPLETE") model.TryApply("STEP_" + key + "_PASSED", "explicit result");
            model.TryApply("STEP_COMPLETE_PASSED", "complete"); Require(model["COMPLETE"].State == StageState.Passed, "explicit complete chain accepted"); count++;
            model.Reset(); Require(model["INIT"].State == StageState.NotStarted && model["INIT"].History.Count == 0, "new round resets only on explicit reset"); count++;
            Require(!model.TryApply("STEP_UNKNOWN_FAILED", "bad") && !model.TryApply("STEP_INIT_SUCCESS", "bad"), "unknown events rejected without implicit pass"); count++;
            model.TryApply("STEP_READ_PASSED", "data returned"); model.TryApply("STEP_RELEASE_PASSED", "closed"); model.Abort("result file verification failed", false);
            Require(model["READ"].State == StageState.Passed && model["RELEASE"].State == StageState.Passed && model["COMPLETE"].State == StageState.Failed, "post-run failure does not falsely accuse successful cleanup"); count++;
            return "Stage diagnostics: " + count + " checks passed.";
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Stage diagnostics test failed: " + message); }
    }
}
