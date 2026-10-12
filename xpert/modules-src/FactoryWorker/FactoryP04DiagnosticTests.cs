using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryP04DiagnosticTests
    {
        internal static string Run()
        {
            int count = 0;
            var input = new BenchSettings { RealHardware = true, ArtDevice = "Dev1", ArtSerial = "123", ArtLibraryDirectory = "fake-only" };
            var fake = new Fake(); string result = FactoryP04Diagnostic.Run(input, fake, null, CancellationToken.None);
            Check(fake.Opens == 2 && fake.Closes == 2 && fake.Reads == 20 && fake.Ports == 20 && fake.Identities == 2, "two DI-only configurations / exact counts / cleanup"); count++;
            Check(result.Contains("不一致 0 次") && result.Contains("没有 AO/DO") && result.Contains("raw=1"), "raw evidence not pressure permission"); count++;
            var mismatch = new Fake { PortLow = true }; result = FactoryP04Diagnostic.Run(input, mismatch, null, CancellationToken.None);
            Check(result.Contains("不一致 20 次"), "independent read discrepancy retained"); count++;
            Check(FactoryP04Diagnostic.LineState(new P04RawRead { Samples = 1, Bytes = 1, Raw = 0xa5 }) == "无效/未知", "unwritten sentinel not high"); count++;
            Check(FactoryP04Diagnostic.LineState(new P04RawRead { Samples = 0, Bytes = 1, Raw = 1 }) == "无效/未知", "incomplete count not high"); count++;
            Check(FactoryP04Diagnostic.LineState(new P04RawRead { Status = -1, Samples = 1, Bytes = 1, Raw = 1 }) == "无效/未知", "negative status not high"); count++;
            Check(FactoryP04Diagnostic.PortState(new P04RawRead { Raw = 0x10 }) == "高" && FactoryP04Diagnostic.PortState(new P04RawRead { Raw = 0x80 }) == "低", "Port0 bit4 only"); count++;
            var wrong = new Fake { WrongSerial = true }; Expect(() => FactoryP04Diagnostic.Run(input, wrong, null, CancellationToken.None));
            Check(wrong.Opens == 0, "identity mismatch before DI configuration"); count++;
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelFake = new Fake();
                Expect(() => FactoryP04Diagnostic.Run(input, cancelFake, (stage, text) => { if (text.Contains("ReadDigitalLines:")) cancellation.Cancel(); }, cancellation.Token));
                Check(cancelFake.Closes == 1 && cancelFake.Opens == 1, "cancel releases exact DI task and no next phase"); count++;
            }
            input.ArtDevice = "Dev1/port0"; Expect(() => FactoryP04Diagnostic.Validate(input)); count++;
            input.ArtDevice = "Dev1"; input.RealHardware = false; Expect(() => FactoryP04Diagnostic.Validate(input)); count++;
            foreach (MethodInfo method in typeof(P04ReadOnlyNative).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                var imported = (DllImportAttribute)Attribute.GetCustomAttribute(method, typeof(DllImportAttribute));
                if (imported != null) Check(imported.EntryPoint != null && (imported.Value == "kernel32.dll" || imported.EntryPoint == "ArtDAQ_ReadDigitalPort"), "native diagnostic imports only allowed read/module calls");
            }
            count++;
            return "P04 read-only diagnostic: " + count + " checks PASS; fake SDK only; no hardware or output task.";
        }
        private sealed class Fake : IP04ReadOnlyNative
        {
            internal int Opens, Closes, Reads, Ports, Identities; internal bool PortLow, WrongSerial, opened;
            public string Load(string directory) { return "fake runtime only"; }
            public string Identity(string device) { Identities++; return WrongSerial ? "USB3135 / 999" : "USB3135 / 123"; }
            public void OpenLine(string device, int grouping) { Check(device == "Dev1" && (grouping == 0 || grouping == 1) && !opened, "fixed selected DI line"); Opens++; opened = true; }
            public P04RawRead ReadLine() { Reads++; return new P04RawRead { Samples = 1, Bytes = 1, Raw = 1 }; }
            public P04RawRead ReadPort(string device) { Ports++; return new P04RawRead { Raw = PortLow ? 0u : 0x10u }; }
            public void CloseLine() { if (opened) Closes++; opened = false; }
        }
        private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException("P04 diagnostic check: " + detail); }
        private static void Expect(Action action) { try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected P04 failure."); }
    }
}
