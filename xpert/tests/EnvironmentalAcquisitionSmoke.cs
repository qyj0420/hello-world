using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

// Offline only: ART is injected and UDP binds ephemeral 127.0.0.1 ports, never 8088.
internal static class EnvironmentalAcquisitionSmoke
{
    private static int checks;
    private static void Check(bool condition, string message) { ++checks; if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double? value, double expected, string message) { Check(value.HasValue && Math.Abs(value.Value - expected) < 0.000001, message); }
    private static string Sentence(string body) { byte xor = 0; foreach (char c in body) xor ^= (byte)c; return "$" + body + "*" + xor.ToString("X2", CultureInfo.InvariantCulture); }
    private const string GoodRmc = "GNRMC,203000.250,A,3954.0000,N,11623.2000,E,2.5,123.4,280926,,,A";
    private const string GoodGga = "GNGGA,203000.250,3954.0000,N,11623.2000,E,1,09,0.9,52.6,M,0.0,M,,";
    static int Main(string[] args)
    {
        try { Art(); Nmea(); Udp().GetAwaiter().GetResult(); Console.WriteLine("ENVIRONMENTAL_ACQUISITION_PASS=" + checks); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static ArtEnvironmentReader Reader(FakeArt api) { return new ArtEnvironmentReader(() => api, TimeSpan.FromSeconds(2)); }
    private static void Art()
    {
        var api = new FakeArt();
        var reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(reading.Success && reading.HumiditySuccess && reading.TemperatureSuccess, "two independent channels succeed");
        Near(reading.HumidityPercent, 50, "humidity conversion"); Near(reading.TemperatureC, 20.625, "temperature conversion");
        Check(reading.SamplesPerChannel == 1000 && reading.CapturedUtc.HasValue, "complete 1000-point frame");
        Check(api.Channels == "Dev1/ai6:7" && api.ConfigurationCorrect, "AI6/7 RSE 0..5V 1000Hz finite1000");
        Check(api.ReadCalls == 10 && api.StopCalls == 1 && api.ClearCalls == 1, "start/read/stop/clear owned task");
        Check(api.AllScan && api.MaximumReadTimeout <= 0.25, "scan layout and bounded cancellation latency");

        api = new FakeArt { Partial = true };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(reading.Success && reading.SamplesPerChannel == 1000 && api.ReadCalls > 10, "partial reads accumulated");
        Near(reading.HumidityPercent, 50, "unfilled zeros excluded from mean");
        Near(reading.TemperatureC, 20.625, "partial scan channel indexing");

        api = new FakeArt { HumidityValue = Double.NaN };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(!reading.Success && !reading.HumiditySuccess && reading.TemperatureSuccess && reading.HumidityPercent == null, "NaN humidity cannot inherit valid temperature");
        Check(reading.ErrorCode == "invalid_data", "invalid sample status");
        foreach (double voltage in new[] { -0.01, 5.01, Double.PositiveInfinity, 0.0, 5.0 })
        {
            api = new FakeArt { HumidityValue = voltage };
            reading = Reader(api).Read("Dev1", CancellationToken.None);
            Check(!reading.HumiditySuccess && reading.HumidityPercent == null && !reading.Success, "humidity out of range never clamped: " + voltage);
        }
        api = new FakeArt { TemperatureValue = 5.0 };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(reading.HumiditySuccess && !reading.TemperatureSuccess && reading.TemperatureC == null, "temperature range independent");

        api = new FakeArt { NoData = true };
        reading = new ArtEnvironmentReader(() => api, TimeSpan.FromMilliseconds(35)).Read("Dev1", CancellationToken.None);
        Check(reading.ErrorCode == "timeout" && !reading.Success && reading.CapturedUtc == null, "timeout has no fabricated data");
        Check(api.StopCalls == 1 && api.ClearCalls == 1, "timeout releases only own AI");
        api = new FakeArt { SetupStatus = -50103 };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(reading.ErrorCode == "busy" && api.ClearCalls == 1 && api.StopCalls == 0, "busy device clears own unstarted task, no force reset");
        api = new FakeArt { ReadStatus = -12345 };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(reading.ErrorCode == "device_unavailable" && api.ClearCalls == 1, "driver read error cleaned up");
        api = new FakeArt { StopStatus = -1 };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(!reading.Success && api.ClearCalls == 1, "failed stop still attempts clear and does not report overall success");
        api = new FakeArt { ClearStatus = -1 };
        reading = Reader(api).Read("Dev1", CancellationToken.None);
        Check(!reading.Success, "failed clear does not claim successful acquisition");
        var next = new FakeArt();
        reading = Reader(next).Read("Dev1", CancellationToken.None);
        Check(next.CreateCalls == 0 && api.ClearCalls == 2 && reading.ErrorCode == "cleanup_pending",
            "failed clear retains owned task and blocks creation until cleanup succeeds");
        api.ClearStatus = 0;
        reading = Reader(next).Read("Dev1", CancellationToken.None);
        Check(reading.Success && api.ClearCalls == 3 && next.CreateCalls == 1,
            "successful owned-handle cleanup permits exactly one new acquisition");
        int factoryCalls = 0;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            reading = new ArtEnvironmentReader(() => { ++factoryCalls; return new FakeArt(); }, TimeSpan.FromSeconds(1)).Read("Dev1", cancelled.Token);
            Check(reading.ErrorCode == "cancelled" && factoryCalls == 0, "pre-cancel does not load native driver");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            api = new FakeArt { BeforeRead = () => cancelled.Cancel() };
            reading = Reader(api).Read("Dev1", cancelled.Token);
            Check(reading.ErrorCode == "cancelled" && api.StopCalls == 1 && api.ClearCalls == 1 && !reading.Success, "mid-read cancel cleans own task");
        }
        reading = new ArtEnvironmentReader(() => { throw new DllNotFoundException("private path"); }, TimeSpan.FromSeconds(1)).Read("Dev1", CancellationToken.None);
        Check(reading.ErrorCode == "driver_missing" && !reading.Diagnostic.Contains("private path"), "missing driver safe result, no actual native load");
        api = new FakeArt();
        reading = Reader(api).Read("Dev1/ai0", CancellationToken.None);
        Check(reading.ErrorCode == "device_unavailable" && api.CreateCalls == 0, "caller cannot inject other channels");
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            api = new FakeArt { BeforeRead = () => { entered.Set(); if (!release.Wait(2000)) throw new TimeoutException(); } };
            Task<TemperatureHumidityReading> first = Reader(api).ReadAsync("Dev1", CancellationToken.None);
            Check(entered.Wait(2000), "first read entered fake device");
            var second = Reader(new FakeArt()).Read("Dev1", CancellationToken.None);
            release.Set(); Check(first.GetAwaiter().GetResult().Success && second.ErrorCode == "busy", "duplicate environment requests do not create competing tasks");
        }
    }
    private static void Nmea()
    {
        GnssReading fix;
        Check(EnvironmentalNmeaParser.TryParseRmc(Sentence(GoodRmc), out fix) && fix.Success, "GNRMC parse");
        Near(fix.Latitude, 39.9, "latitude ddmm conversion"); Near(fix.Longitude, 116.3866666666667, "longitude dddmm conversion");
        Near(fix.SpeedKnots, 2.5, "speed remains knots"); Near(fix.CourseDegrees, 123.4, "course degrees");
        Check(fix.AltitudeMeters == null, "RMC does not invent zero altitude");
        Check(fix.UtcTime == new DateTimeOffset(2026, 9, 28, 20, 30, 0, TimeSpan.Zero).AddMilliseconds(250), "RMC UTC date and fractional time");
        Check(fix.ChinaTime.Value.Day == 29 && fix.ChinaTime.Value.Hour == 4 && fix.ChinaTime.Value.Offset == TimeSpan.FromHours(8), "UTC+8 crosses midnight correctly");
        Check(fix.CapturedUtc.HasValue, "GNSS capture time independent");
        Check(EnvironmentalNmeaParser.TryParseRmc(Sentence(GoodRmc.Replace(",N,", ",S,").Replace(",E,", ",W,")), out fix) && fix.Success, "southern western fix");
        Near(fix.Latitude, -39.9, "south negative"); Near(fix.Longitude, -116.3866666666667, "west negative");
        Check(EnvironmentalNmeaParser.TryParseRmc(Sentence(GoodRmc.Replace("203000.250", "235959").Replace("280926", "311226")), out fix) && fix.Success && fix.ChinaTime.Value.Year == 2027 && fix.ChinaTime.Value.Month == 1, "year rollover");
        string corrupted = Sentence(GoodRmc); corrupted = corrupted.Substring(0, corrupted.Length - 2) + "00";
        Check(!EnvironmentalNmeaParser.TryParseRmc(corrupted, out fix) && !fix.Success, "checksum mismatch rejected");
        Check(!EnvironmentalNmeaParser.TryParseRmc("$" + GoodRmc, out fix), "missing checksum rejected");
        foreach (string invalid in new[] { GoodRmc.Replace(",A,", ",V,"), GoodRmc.Replace("3954.0000", "3961.0000"), GoodRmc.Replace("11623.2000", "18123.2000"), GoodRmc.Replace("280926", "310226"), GoodRmc.Replace("203000.250", "246000"), GoodRmc.Replace("2.5,123.4", "NaN,123.4"), GoodRmc.Replace("2.5,123.4", "2.5,360"), GoodRmc.Substring(0, GoodRmc.Length - 1) + "N" })
            Check(EnvironmentalNmeaParser.TryParseRmc(Sentence(invalid), out fix) && !fix.Success && fix.Latitude == null, "invalid RMC field/status not accepted");
        var assembler = new NmeaFixAssembler();
        assembler.Add(Sentence(GoodRmc)); assembler.Add(Sentence(GoodGga));
        Near(assembler.Latest.AltitudeMeters, 52.6, "same epoch GGA altitude");
        assembler = new NmeaFixAssembler(); assembler.Add(Sentence(GoodGga)); assembler.Add(Sentence(GoodRmc));
        Near(assembler.Latest.AltitudeMeters, 52.6, "GGA may precede RMC");
        assembler = new NmeaFixAssembler(); assembler.Add(Sentence(GoodGga.Replace("203000.250", "203001.250"))); assembler.Add(Sentence(GoodRmc));
        Check(assembler.Latest.AltitudeMeters == null, "different epoch height not mixed");
        assembler = new NmeaFixAssembler(); assembler.Add(Sentence(GoodGga.Replace(",1,09,", ",0,09,"))); assembler.Add(Sentence(GoodRmc));
        Check(assembler.Latest.AltitudeMeters == null, "invalid GGA fix not used");
        assembler = new NmeaFixAssembler(); assembler.Add(Sentence(GoodGga));
        assembler.Add(Sentence(GoodGga.Replace(",1,09,", ",0,09,"))); assembler.Add(Sentence(GoodRmc));
        Check(assembler.Latest.Success && assembler.Latest.AltitudeMeters == null, "later invalid GGA clears previous same-epoch altitude");
        assembler.Add(Sentence(GoodGga));
        Near(assembler.Latest.AltitudeMeters, 52.6, "later valid GGA restores altitude");
        assembler.Add(Sentence(GoodGga.Replace(",1,09,", ",0,09,")));
        Check(assembler.Latest.Success && assembler.Latest.AltitudeMeters == null, "invalid GGA after valid RMC removes stale altitude without inventing position");
        var buffer = new NmeaSentenceAccumulator();
        byte[] sentenceBytes = Encoding.ASCII.GetBytes(Sentence(GoodRmc));
        var output = new List<string>();
        foreach (byte value in sentenceBytes) output.AddRange(buffer.Feed(new[] { value }, 1));
        Check(output.Count == 1 && output[0] == Sentence(GoodRmc), "single-byte fragments reassemble");
        byte[] two = Encoding.ASCII.GetBytes("garbage\r\n" + Sentence(GoodRmc) + "\r\n" + Sentence(GoodGga) + "\r\n");
        Check(buffer.Feed(two, two.Length).Count == 2, "multiple sentences in one datagram");
        byte[] oversized = Encoding.ASCII.GetBytes("$" + new string('A', 3000));
        Check(buffer.Feed(oversized, oversized.Length).Count == 0 && buffer.Feed(sentenceBytes, sentenceBytes.Length).Count == 1, "oversize garbage bounded and parser recovers");
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try { Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check(EnvironmentalNmeaParser.TryParseRmc(Sentence(GoodRmc), out fix) && fix.Success, "locale independent decimal parsing"); }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }
    private static async Task Udp()
    {
        var port = new TaskCompletionSource<int>();
        var reader = new GnssUdpReader(new IPEndPoint(IPAddress.Loopback, 0), TimeSpan.FromMilliseconds(1800), p => port.TrySetResult(p));
        Task<GnssReading> pending = reader.ReadAsync(CancellationToken.None);
        int assigned = await port.Task.ConfigureAwait(false);
        Check(assigned != 8088, "test uses ephemeral loopback port");
        using (var sender = new UdpClient())
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, assigned);
            string text = Sentence(GoodRmc); int middle = text.Length / 2;
            byte[] first = Encoding.ASCII.GetBytes(text.Substring(0, middle)); byte[] second = Encoding.ASCII.GetBytes(text.Substring(middle) + "\r\n" + Sentence(GoodGga));
            sender.Send(first, first.Length, endpoint); await Task.Delay(20).ConfigureAwait(false); sender.Send(second, second.Length, endpoint);
        }
        GnssReading result = await pending.ConfigureAwait(false);
        Check(result.Success, "fragmented UDP fix read"); Near(result.AltitudeMeters, 52.6, "UDP matching GGA height");
        // Port is released immediately at the end of a single acquisition.
        using (var released = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { released.ExclusiveAddressUse = true; released.Bind(new IPEndPoint(IPAddress.Loopback, assigned)); Check(released.IsBound, "single acquisition releases port"); }
        using (var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            occupied.ExclusiveAddressUse = true; occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            result = await new GnssUdpReader((IPEndPoint)occupied.LocalEndPoint, TimeSpan.FromMilliseconds(100), null).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Check(result.ErrorCode == "busy" && !result.Success, "port conflict explicit, no reuse hijack");
        }
        result = await new GnssUdpReader(new IPEndPoint(IPAddress.Loopback, 0), TimeSpan.FromMilliseconds(50), null).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Check(result.ErrorCode == "timeout" && !result.Success && result.UtcTime == null, "silent UDP times out without fabricated fix");
        using (var cancel = new CancellationTokenSource())
        {
            port = new TaskCompletionSource<int>();
            pending = new GnssUdpReader(new IPEndPoint(IPAddress.Loopback, 0), TimeSpan.FromSeconds(2), p => port.TrySetResult(p)).ReadAsync(cancel.Token);
            await port.Task.ConfigureAwait(false); cancel.Cancel(); result = await pending.ConfigureAwait(false);
            Check(result.ErrorCode == "cancelled" && !result.Success, "UDP listening cancellation");
        }
        // Fragments from different senders must never be concatenated into a valid fix.
        port = new TaskCompletionSource<int>();
        pending = new GnssUdpReader(new IPEndPoint(IPAddress.Loopback, 0), TimeSpan.FromMilliseconds(180), p => port.TrySetResult(p)).ReadAsync(CancellationToken.None);
        assigned = await port.Task.ConfigureAwait(false);
        using (var one = new UdpClient()) using (var two = new UdpClient())
        {
            var destination = new IPEndPoint(IPAddress.Loopback, assigned); string text = Sentence(GoodRmc); int middle = text.Length / 2;
            byte[] left = Encoding.ASCII.GetBytes(text.Substring(0, middle)), right = Encoding.ASCII.GetBytes(text.Substring(middle));
            one.Send(left, left.Length, destination); two.Send(right, right.Length, destination);
        }
        result = await pending.ConfigureAwait(false); Check(!result.Success, "different senders cannot splice a location");
        foreach (bool badChecksum in new[] { true, false })
        {
            port = new TaskCompletionSource<int>();
            pending = new GnssUdpReader(new IPEndPoint(IPAddress.Loopback, 0), TimeSpan.FromMilliseconds(130), p => port.TrySetResult(p)).ReadAsync(CancellationToken.None);
            assigned = await port.Task.ConfigureAwait(false);
            using (var sender = new UdpClient())
            {
                string message = Sentence(GoodRmc.Replace(",A,", ",V,"));
                if (badChecksum) message = message.Substring(0, message.Length - 2) + "XX";
                byte[] bytes = Encoding.ASCII.GetBytes(message); sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, assigned));
            }
            result = await pending.ConfigureAwait(false);
            Check(!result.Success && result.Latitude == null && result.AltitudeMeters == null, badChecksum ? "UDP checksum corruption rejected" : "UDP void fix rejected");
        }
    }
    private sealed class FakeArt : IArtEnvironmentApi
    {
        internal int CreateCalls, StopCalls, ClearCalls, ReadCalls, SetupStatus, ReadStatus, StopStatus, ClearStatus;
        internal bool Partial, NoData, ConfigurationCorrect = true, AllScan = true;
        internal double HumidityValue = 2.5, TemperatureValue = 2, MaximumReadTimeout;
        internal string Channels;
        internal Action BeforeRead;
        public int CreateTask(string name, out IntPtr task) { ++CreateCalls; task = new IntPtr(42); return 0; }
        public int CreateAI(IntPtr task, string channels, int terminal, double minimum, double maximum, int units)
        { Channels = channels; ConfigurationCorrect &= terminal == 10083 && minimum == 0 && maximum == 5 && units == 10348; return SetupStatus; }
        public int ConfigureClock(IntPtr task, double rate, int edge, int mode, int samples)
        { ConfigurationCorrect &= rate == 1000 && edge == 10280 && mode == 10178 && samples == 1000; return 0; }
        public int Start(IntPtr task) { return 0; }
        public int Read(IntPtr task, int samples, double timeout, int layout, double[] values, out int read)
        {
            ++ReadCalls; if (BeforeRead != null) BeforeRead(); AllScan &= layout == 1; MaximumReadTimeout = Math.Max(MaximumReadTimeout, timeout);
            read = NoData ? 0 : Math.Min(samples, Partial ? 37 : samples);
            for (int i = 0; i < read; ++i) { values[i * 2] = HumidityValue; values[i * 2 + 1] = TemperatureValue; }
            return ReadStatus != 0 ? ReadStatus : (NoData || Partial ? -200284 : 0);
        }
        public int Stop(IntPtr task) { ++StopCalls; return StopStatus; }
        public int Clear(IntPtr task) { ++ClearCalls; return ClearStatus; }
    }
}
