using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    // NMEA RMC/GGA field meanings checked against receiver-manufacturer documentation:
    // https://help.fieldsystems.trimble.com/r780/nmea0183-messages-rmc.htm
    // https://help.fieldsystems.trimble.com/r780/nmea0183-messages-gga.htm
    // NMEA checksum is XOR, not a CRC. This reader requires it for every accepted sentence.
    internal static class EnvironmentalNmeaParser
    {
        internal static bool TryParseRmc(string sentence, out GnssReading result)
        {
            result = GnssReading.Failure("invalid_data", "Invalid RMC sentence.");
            string[] fields;
            if (!TryFields(sentence, out fields) || (fields[0] != "GNRMC" && fields[0] != "GPRMC")) return false;
            if (fields.Length < 10) return true;
            if (fields[2] != "A") { result.Diagnostic = "RMC fix is void."; return true; }
            // Reject explicit invalid, estimated, simulated or manually entered fixes.
            if (fields.Length > 12 && fields[12].Length != 0 && (fields[12].Length != 1 || "ADFRP".IndexOf(fields[12], StringComparison.Ordinal) < 0)) return true;
            if (fields.Length > 13 && fields[13] == "V") return true;
            double latitude, longitude;
            TimeSpan time;
            DateTimeOffset date;
            double? speed, course;
            if (!Coordinate(fields[3], fields[4], true, out latitude) || !Coordinate(fields[5], fields[6], false, out longitude) ||
                !TryTime(fields[1], out time) || !TryDate(fields[9], out date) ||
                !OptionalNumber(fields[7], 0, Double.MaxValue, out speed) || !OptionalNumber(fields[8], 0, 360, out course) || course == 360) return true;
            result = new GnssReading
            {
                Success = true, Latitude = latitude, Longitude = longitude,
                UtcTime = date.Add(time), CapturedUtc = DateTimeOffset.UtcNow,
                SpeedKnots = speed, CourseDegrees = course
            };
            return true;
        }

        internal static bool TryParseGga(string sentence, out GgaReading reading)
        {
            reading = null;
            string[] fields;
            if (!TryFields(sentence, out fields) || (fields[0] != "GNGGA" && fields[0] != "GPGGA") || fields.Length < 11) return false;
            int quality, satellites;
            double altitude, latitude, longitude;
            TimeSpan time;
            if (!Int32.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out quality) ||
                (quality != 1 && quality != 2 && quality != 4 && quality != 5) ||
                !Int32.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out satellites) || satellites < 1 ||
                fields[10] != "M" || !Number(fields[9], out altitude) || !TryTime(fields[1], out time) ||
                !Coordinate(fields[2], fields[3], true, out latitude) || !Coordinate(fields[4], fields[5], false, out longitude)) return false;
            reading = new GgaReading { Time = time, Latitude = latitude, Longitude = longitude, Altitude = altitude };
            return true;
        }

        internal static bool TryFields(string sentence, out string[] fields)
        {
            fields = null;
            if (sentence == null) return false;
            sentence = sentence.TrimEnd('\r', '\n');
            if (sentence.Length < 10 || sentence.Length > 2048 || sentence[0] != '$') return false;
            int star = sentence.IndexOf('*');
            if (star < 6 || star != sentence.Length - 3) return false;
            byte expected;
            if (!Byte.TryParse(sentence.Substring(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out expected)) return false;
            byte actual = 0;
            for (int i = 1; i < star; ++i)
            { if (sentence[i] < 32 || sentence[i] > 126 || sentence[i] == '$') return false; actual ^= (byte)sentence[i]; }
            if (expected != actual) return false;
            fields = sentence.Substring(1, star - 1).Split(',');
            return true;
        }

        private static bool Coordinate(string value, string hemisphere, bool latitude, out double result)
        {
            result = 0;
            int wholeDigits = latitude ? 4 : 5;
            if (String.IsNullOrEmpty(value) || value.Length < wholeDigits || (value.Length > wholeDigits && value[wholeDigits] != '.')) return false;
            for (int i = 0; i < value.Length; ++i) if (i != wholeDigits && (value[i] < '0' || value[i] > '9')) return false;
            double number;
            if (!Number(value, out number)) return false;
            double degrees = Math.Floor(number / 100), minutes = number - degrees * 100;
            int maximum = latitude ? 90 : 180;
            if (minutes >= 60 || degrees > maximum || (degrees == maximum && minutes != 0)) return false;
            if (hemisphere != (latitude ? "N" : "E") && hemisphere != (latitude ? "S" : "W")) return false;
            result = (degrees + minutes / 60) * ((hemisphere == "S" || hemisphere == "W") ? -1 : 1);
            return true;
        }
        private static bool TryTime(string value, out TimeSpan result)
        {
            result = default(TimeSpan);
            if (String.IsNullOrEmpty(value) || value.Length < 6 || (value.Length > 6 && value[6] != '.')) return false;
            for (int i = 0; i < value.Length; ++i) if (i != 6 && (value[i] < '0' || value[i] > '9')) return false;
            int hour, minute;
            double second;
            if (!Int32.TryParse(value.Substring(0, 2), out hour) || !Int32.TryParse(value.Substring(2, 2), out minute) ||
                !Number(value.Substring(4), out second) || hour > 23 || minute > 59 || second < 0 || second >= 60) return false;
            result = TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(minute) + TimeSpan.FromTicks((long)Math.Round(second * TimeSpan.TicksPerSecond));
            return result.TotalDays < 1;
        }
        private static bool TryDate(string value, out DateTimeOffset result)
        {
            result = default(DateTimeOffset);
            if (value == null || value.Length != 6) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            int day = Int32.Parse(value.Substring(0, 2), CultureInfo.InvariantCulture), month = Int32.Parse(value.Substring(2, 2), CultureInfo.InvariantCulture);
            int year = Int32.Parse(value.Substring(4, 2), CultureInfo.InvariantCulture);
            // Fixed GNSS-era pivot, independent of Windows regional calendar settings.
            year += year >= 80 ? 1900 : 2000;
            try { result = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero); return true; }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        private static bool Number(string text, out double value)
        { return Double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool OptionalNumber(string text, double minimum, double maximum, out double? result)
        {
            result = null; if (String.IsNullOrEmpty(text)) return true;
            double value; if (!Number(text, out value) || value < minimum || value > maximum) return false;
            result = value; return true;
        }
        internal sealed class GgaReading { internal TimeSpan Time; internal double Latitude, Longitude, Altitude; }
    }

    internal sealed class NmeaSentenceAccumulator
    {
        private readonly StringBuilder pending = new StringBuilder();
        private int star = -1;
        internal IList<string> Feed(byte[] data, int count)
        {
            if (data == null || count < 0 || count > data.Length) throw new ArgumentOutOfRangeException("count");
            var sentences = new List<string>();
            for (int i = 0; i < count; ++i)
            {
                char c = (char)data[i];
                if (c == '$') { pending.Clear(); pending.Append(c); star = -1; continue; }
                if (pending.Length == 0) continue;
                if (c < 32 || c > 126 || pending.Length >= 2048) { pending.Clear(); star = -1; continue; }
                pending.Append(c);
                if (c == '*') star = pending.Length - 1;
                if (star >= 0 && pending.Length == star + 3)
                { sentences.Add(pending.ToString()); pending.Clear(); star = -1; }
            }
            return sentences;
        }
    }

    internal sealed class NmeaFixAssembler
    {
        private EnvironmentalNmeaParser.GgaReading gga;
        internal GnssReading Latest { get; private set; }
        internal void Add(string sentence)
        {
            GnssReading rmc;
            EnvironmentalNmeaParser.GgaReading candidate;
            if (EnvironmentalNmeaParser.TryParseRmc(sentence, out rmc)) Latest = rmc;
            else if (EnvironmentalNmeaParser.TryParseGga(sentence, out candidate)) gga = candidate;
            else
            {
                string[] fields;
                // A checksummed GGA with no usable fix supersedes the previous
                // height, even at the same epoch. Do not revive a stale height
                // when the accompanying RMC arrives after the invalidation.
                if (EnvironmentalNmeaParser.TryFields(sentence, out fields) &&
                    (fields[0] == "GNGGA" || fields[0] == "GPGGA")) gga = null;
            }
            if (Latest != null && Latest.Success)
            {
                Latest.AltitudeMeters = null;
                if (gga != null && Math.Abs((Latest.UtcTime.Value.TimeOfDay - gga.Time).TotalMilliseconds) <= 1 &&
                    Math.Abs(Latest.Latitude.Value - gga.Latitude) < 0.0001 && Math.Abs(Latest.Longitude.Value - gga.Longitude) < 0.0001)
                    Latest.AltitudeMeters = gga.Altitude;
            }
        }
    }

    internal sealed class GnssUdpReader
    {
        private readonly IPEndPoint localEndPoint;
        private readonly TimeSpan timeout;
        private readonly Action<int> onBound;
        public GnssUdpReader() : this(new IPEndPoint(IPAddress.Any, 8088), TimeSpan.FromMilliseconds(2500), null) { }
        // An ephemeral loopback endpoint is used by offline tests, never the production port.
        internal GnssUdpReader(IPEndPoint endpoint, TimeSpan timeout, Action<int> onBound)
        { localEndPoint = endpoint ?? throw new ArgumentNullException("endpoint"); this.timeout = timeout; this.onBound = onBound; }
        public Task<GnssReading> ReadAsync(CancellationToken cancellationToken)
        { return Task.Run(() => Read(cancellationToken)); }
        private GnssReading Read(CancellationToken cancellationToken)
        {
            DateTimeOffset attempted = DateTimeOffset.UtcNow;
            var peers = new Dictionary<string, PeerState>(StringComparer.Ordinal);
            GnssReading last = null, candidate = null;
            string candidatePeer = null;
            double candidateAt = 0;
            var timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ExclusiveAddressUse = true;
                    socket.Bind(localEndPoint);
                    if (onBound != null) onBound(((IPEndPoint)socket.LocalEndPoint).Port);
                    byte[] buffer = new byte[2048];
                    while (timer.Elapsed < timeout)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // A brief grace period permits a matching GGA in the next datagram.
                        if (candidate != null && candidate.Success && (candidate.AltitudeMeters.HasValue || timer.Elapsed.TotalMilliseconds - candidateAt >= 150)) break;
                        int wait = (int)Math.Min(100000, Math.Max(1, (timeout - timer.Elapsed).TotalMilliseconds * 1000));
                        if (!socket.Poll(wait, SelectMode.SelectRead)) continue;
                        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                        int count;
                        try { count = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref source); }
                        catch (SocketException error) { if (error.SocketErrorCode == SocketError.MessageSize) { last = GnssReading.Failure("invalid_data", "Oversized NMEA datagram."); continue; } throw; }
                        string key = source.ToString();
                        PeerState peer;
                        if (!peers.TryGetValue(key, out peer))
                        {
                            if (peers.Count >= 8) continue;
                            peer = new PeerState(); peers.Add(key, peer);
                        }
                        foreach (string sentence in peer.Buffer.Feed(buffer, count)) peer.Fixes.Add(sentence);
                        if (peer.Fixes.Latest != null)
                        {
                            last = peer.Fixes.Latest;
                            if (last.Success && (candidate == null || key == candidatePeer))
                            { if (candidate == null) candidateAt = timer.Elapsed.TotalMilliseconds; candidate = last; candidatePeer = key; }
                            else if (!last.Success && key == candidatePeer) { candidate = null; candidatePeer = null; }
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = candidate ?? last ?? GnssReading.Failure("timeout", "No valid GNSS fix received within the acquisition window.");
                result.AttemptedUtc = attempted;
                return result;
            }
            catch (OperationCanceledException) { return Failure("cancelled", "GNSS acquisition cancelled.", attempted); }
            catch (SocketException error)
            { return Failure(error.SocketErrorCode == SocketError.AddressAlreadyInUse || error.SocketErrorCode == SocketError.AccessDenied ? "busy" : "device_unavailable", "GNSS UDP socket error: " + error.SocketErrorCode, attempted); }
            catch (Exception error) { return Failure("device_unavailable", error.GetType().Name, attempted); }
        }
        private static GnssReading Failure(string code, string diagnostic, DateTimeOffset attempted)
        { var result = GnssReading.Failure(code, diagnostic); result.AttemptedUtc = attempted; return result; }
        private sealed class PeerState
        { internal readonly NmeaSentenceAccumulator Buffer = new NmeaSentenceAccumulator(); internal readonly NmeaFixAssembler Fixes = new NmeaFixAssembler(); }
    }
}
