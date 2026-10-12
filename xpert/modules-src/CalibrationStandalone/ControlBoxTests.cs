using System;
using System.Collections.Generic;
using System.IO;

namespace Xiongxin.Calibration
{
    internal static class ControlBoxTests
    {
        sealed class Fake : IControlBoxIo
        {
            internal readonly List<string> Calls = new List<string>();
            internal bool High, ReadFailure, OpenFailure, CloseFailure, WriteFailure;
            public void Open() { Calls.Add("open"); if (OpenFailure) throw new IOException("open failed"); }
            public bool ReadEmergencySignalHigh() { Calls.Add("read"); if (ReadFailure) throw new IOException("read failed"); return High; }
            public void WriteShortCable(bool value) { Calls.Add("impedance:" + value); if (WriteFailure) throw new IOException("write failed"); }
            public void Close() { Calls.Add("close"); if (CloseFailure) throw new IOException("close failed"); }
        }
        static int count;
        static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        static Exception Reject(Action action, string name) { try { action(); } catch (Exception error) { count++; return error; } throw new Exception("Expected rejection: " + name); }
        internal static int Run()
        {
            var fake = new Fake(); var box = new ControlBox(fake);
            Check(typeof(IControlBoxIo).GetMethod("WriteConnectionLamp") == null, "no lamp interface");
            Check(typeof(IControlBoxIo).GetMethods().Length == 4, "scope is open, emergency input, impedance, close");
            Reject(box.BeginCapture, "disconnected blocks");
            box.Connect();
            Check(String.Join(",", fake.Calls) == "open,read", "connect only opens and reads emergency");
            Check(!fake.Calls.Exists(x => x.StartsWith("impedance")), "no automatic impedance write at connection");
            Check(box.Connected && box.EmergencySignalHigh == false, "valid low signal");
            box.SetShortCable(true); Check(fake.Calls.Contains("impedance:True"), "short cable high");
            box.BeginCapture(); Check(box.Capturing, "low permits acquisition without any HV status");
            Reject(() => box.SetShortCable(false), "cannot change impedance in acquisition");
            box.EndCapture(); fake.High = true;
            box.BeginCapture();Check(box.Capturing,"high permits external-input acquisition without HV permission");box.EndCapture();
            Check(box.EmergencySignalHigh == true, "actual high state remains observable");
            fake.High = false; box.BeginCapture(); Check(box.Capturing, "restored low permits retry"); box.EndCapture();
            fake.ReadFailure = true; Reject(box.BeginCapture, "read failure blocks");
            Check(box.EmergencySignalHigh == null, "read failure never uses stale low");
            fake.ReadFailure = false; box.Disconnect();
            Check(fake.Calls[fake.Calls.Count - 1] == "close", "disconnect has no output writes");
            Check(!box.Connected && box.EmergencySignalHigh == null, "disconnect clears status");
            fake = new Fake {High = true}; box = new ControlBox(fake); box.Connect();
            Check(box.Connected && box.EmergencySignalHigh == true, "high permits independent input connection"); box.Disconnect();
            fake = new Fake(); box = new ControlBox(fake); box.Connect(); box.BeginCapture(); fake.CloseFailure = true;
            var error = Reject(box.Disconnect, "failed cleanup propagates");
            Check(box.Connected && box.CleanupPending && box.Capturing, "failure retains ownership and acquisition state");
            Check(box.EmergencySignalHigh == null && !error.Message.Contains("已释放"), "release failure is not falsely called released");
            int calls = fake.Calls.Count;
            Reject(box.Connect, "cannot replace occupied connection"); Reject(() => box.Poll(), "cleanup pending cannot read permission");
            Reject(box.BeginCapture, "cleanup pending cannot acquire"); Reject(() => box.SetShortCable(false), "cleanup pending cannot output");
            Check(fake.Calls.Count == calls, "blocked operations issue no I/O");
            fake.CloseFailure = false; box.Disconnect();
            Check(!box.Connected && !box.CleanupPending && !box.Capturing, "explicit successful cleanup retry releases");
            box.Connect(); box.Disconnect(); Check(!box.Connected, "reconnect only after successful cleanup");
            fake = new Fake {OpenFailure = true, CloseFailure = true}; box = new ControlBox(fake);
            error = Reject(box.Connect, "partial open and cleanup failure");
            Check(error is AggregateException && ((AggregateException)error).InnerExceptions.Count == 2, "preserve both failures");
            Check(box.Connected && box.CleanupPending, "partial open retains ownership");
            fake.CloseFailure = false; box.Disconnect(); Check(!box.Connected, "partial open explicit cleanup");
            fake = new Fake {OpenFailure = true}; box = new ControlBox(fake);
            Reject(box.Connect, "open failure cleanup"); Check(!box.Connected && !box.CleanupPending, "successful cleanup clears ownership");
            fake = new Fake {ReadFailure = true}; box = new ControlBox(fake);
            Reject(box.Connect, "initial safety read fails"); Check(!box.Connected && fake.Calls.Contains("close"), "initial read failure cleans up");
            fake = new Fake(); box = new ControlBox(fake); box.Connect(); fake.WriteFailure = true;
            Reject(() => box.SetShortCable(true), "impedance write fails");
            Check(box.CleanupPending && box.Connected && !box.ShortCable, "failed impedance change retains ownership, not successful setting");
            Reject(box.BeginCapture, "failed impedance change blocks acquisition"); box.Disconnect();
            Console.WriteLine("PASS: " + count + " offline control-box checks; no native driver loaded, no hardware commands.");
            return 0;
        }
        static int Main() { return Run(); }
    }
}
