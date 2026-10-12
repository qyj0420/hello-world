using System;
using System.IO;
using Xiongxin.Calibration;

internal static class CalibrationArtOwnershipBaselineSmoke
{
    sealed class FailingClose : IControlBoxIo
    {
        internal int CloseCalls;
        public void Open() { }
        public bool ReadEmergencySignalHigh() { return false; }
        public void WriteConnectionLamp(bool on) { }
        public void WriteShortCable(bool on) { }
        public void Close() { CloseCalls++; throw new IOException("Injected Clear failure"); }
    }
    static int Main()
    {
        var io = new FailingClose();
        var box = new ControlBox(io);
        box.Connect();
        bool failed = false;
        try { box.Disconnect(); } catch (IOException) { failed = true; }
        if (!failed || io.CloseCalls != 1) throw new Exception("Fixture did not reach failed release.");
        if (box.Connected) throw new Exception("Historical false-release defect no longer reproduces.");
        if (typeof(IControlBoxIo).GetMethod("WriteConnectionLamp") == null)
            throw new Exception("Historical out-of-scope output no longer present.");
        Console.WriteLine("REPRODUCED: failed release reports Connected=false; calibration interface exposes connection-lamp output.");
        Console.WriteLine("Injected fake only; no native driver loaded or hardware accessed.");
        return 0;
    }
}
