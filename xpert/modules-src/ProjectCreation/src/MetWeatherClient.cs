using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    internal sealed class MetWeatherForecast
    {
        internal int Code;
        internal double Temperature, Humidity;
        internal DateTime ForecastUtc, UpdatedUtc, RetrievedUtc;
    }
    internal sealed class MetWeatherResult
    {
        internal string State;
        internal MetWeatherForecast Forecast;
        internal bool Cached, Stale;
        internal DateTime RetryUtc, CacheExpiresUtc;
    }
    internal sealed class MetWeatherHttpResponse
    {
        internal int Status;
        internal string Body, LastModified, Expires, RetryAfter;
        internal bool Offline;
    }

    // The UI must disclose coordinate/IP use and obtain first-use confirmation
    // before invoking this client. No project/device/customer metadata is sent.
    internal sealed class MetWeatherClient
    {
        internal const string SourceUrl = "https://api.met.no/weatherapi/locationforecast/2.0/documentation";
        internal const string LicenseUrl = "https://creativecommons.org/licenses/by/4.0/";
        internal const string UserAgent = "HV-DAC-Xpert/1.1.3 (XIONGXIN)";
        const int MaxBody = 2097152;
        sealed class CacheEntry
        {
            internal string Json, Modified, ErrorState;
            internal DateTime Expires, Retrieved, NextAttempt;
            internal MetWeatherForecast Forecast;
        }
        readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly Func<Uri, string, CancellationToken, MetWeatherHttpResponse> transport;
        readonly Func<DateTime> utcNow;
        DateTime throttledUntil;
        internal static readonly MetWeatherClient Shared = new MetWeatherClient();
        internal MetWeatherClient() : this(FetchHttp, () => DateTime.UtcNow) { }
        internal MetWeatherClient(Func<Uri, string, CancellationToken, MetWeatherHttpResponse> transport, Func<DateTime> utcNow)
        { this.transport = transport; this.utcNow = utcNow; }

        internal static Uri RequestUri(double latitude, double longitude)
        {
            if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < -90 || latitude > 90 ||
                double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < -180 || longitude > 180)
                throw new ArgumentOutOfRangeException("coordinates");
            string lat = Math.Round(latitude, 3, MidpointRounding.AwayFromZero).ToString("F3", CultureInfo.InvariantCulture);
            string lon = Math.Round(longitude, 3, MidpointRounding.AwayFromZero).ToString("F3", CultureInfo.InvariantCulture);
            return new Uri("https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + lat + "&lon=" + lon);
        }

        internal async Task<MetWeatherResult> GetAsync(double latitude, double longitude, CancellationToken token)
        {
            Uri uri = RequestUri(latitude, longitude);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                DateTime now = utcNow();
                CacheEntry entry;
                cache.TryGetValue(uri.Query, out entry);
                if (entry != null && entry.Json != null && now < entry.Expires)
                    return CachedResult(entry, now, "cached", DateTime.MinValue);
                if (now < throttledUntil) return CachedResult(entry, now, "throttled", throttledUntil);
                if (entry != null && now < entry.NextAttempt) return CachedResult(entry, now, entry.ErrorState ?? "service", entry.NextAttempt);
                MetWeatherHttpResponse response;
                try { response = await Task.Run(() => transport(uri, entry == null ? null : entry.Modified, token), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException || error is WebException)
                { response = new MetWeatherHttpResponse { Status = 0, Offline = true }; }
                catch (Exception error) when (error is InvalidDataException || error is DecoderFallbackException)
                { response = new MetWeatherHttpResponse { Status = 502 }; }
                token.ThrowIfCancellationRequested();
                now = utcNow();
                if (response.Status == 429)
                {
                    throttledUntil = RetryAt(response.RetryAfter, now);
                    return CachedResult(entry, now, "throttled", throttledUntil);
                }
                if (response.Status == 304 && entry != null && entry.Json != null)
                {
                    entry.Expires = Expiry(response.Expires, now);
                    entry.NextAttempt = DateTime.MinValue;
                    return CachedResult(entry, now, "cached", DateTime.MinValue);
                }
                if (response.Status == 200 || response.Status == 203)
                {
                    try
                    {
                        MetWeatherForecast forecast = Parse(response.Body, now);
                        entry = new CacheEntry { Json = response.Body, Modified = ValidModified(response.LastModified, now), Expires = Expiry(response.Expires, now), Retrieved = now, Forecast = forecast };
                        EvictFor(uri.Query);
                        cache[uri.Query] = entry;
                        return new MetWeatherResult { State = response.Status == 203 ? "deprecated" : "updated", Forecast = forecast, CacheExpiresUtc = entry.Expires };
                    }
                    catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is InvalidDataException) { }
                }
                if (entry == null)
                {
                    EvictFor(uri.Query);
                    entry = new CacheEntry { Retrieved = now };
                    cache[uri.Query] = entry;
                }
                entry.NextAttempt = now.AddMinutes(10);
                entry.ErrorState = response.Offline ? "offline" : "service";
                return CachedResult(entry, now, entry.ErrorState, entry.NextAttempt);
            }
            finally { gate.Release(); }
        }

        void EvictFor(string key)
        { if (cache.Count >= 32 && !cache.ContainsKey(key)) cache.Remove(cache.OrderBy(pair => pair.Value.Retrieved).First().Key); }
        static MetWeatherResult CachedResult(CacheEntry entry, DateTime now, string state, DateTime retry)
        {
            MetWeatherForecast forecast = entry == null ? null : entry.Forecast;
            if (entry != null && entry.Json != null)
            {
                try { forecast = Parse(entry.Json, now); forecast.RetrievedUtc = entry.Retrieved; }
                catch (InvalidDataException) { }
            }
            return new MetWeatherResult { State = state, Forecast = forecast, Cached = forecast != null,
                Stale = forecast != null && (now >= entry.Expires || Math.Abs((forecast.ForecastUtc - now).TotalHours) > 2), RetryUtc = retry,
                CacheExpiresUtc = entry == null ? DateTime.MinValue : entry.Expires };
        }
        static DateTime Expiry(string value, DateTime now)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) && parsed > now ? parsed : now.AddMinutes(10);
        }
        static string ValidModified(string value, DateTime now)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) && parsed <= now ? value : null;
        }
        static DateTime RetryAt(string value, DateTime now)
        {
            int seconds;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return now.AddSeconds(Math.Max(60, Math.Min(86400, seconds)));
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) && parsed > now)
                return parsed > now.AddDays(1) ? now.AddDays(1) : parsed;
            return now.AddMinutes(15);
        }

        internal static MetWeatherForecast Parse(string json, DateTime now)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxBody) throw new InvalidDataException("Weather response size invalid.");
            var root = new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 24 }.DeserializeObject(json) as Dictionary<string, object>;
            var properties = Object(root, "properties");
            var meta = Object(properties, "meta");
            var units = Object(meta, "units");
            if (StringValue(units, "air_temperature") != "celsius" || StringValue(units, "relative_humidity") != "%") throw new InvalidDataException("Weather units unsupported.");
            DateTime updated = Time(StringValue(meta, "updated_at"));
            object raw;
            if (properties == null || !properties.TryGetValue("timeseries", out raw) || !(raw is object[])) throw new InvalidDataException("Weather timeseries missing.");
            var rows = (object[])raw;
            if (rows.Length == 0 || rows.Length > 1000) throw new InvalidDataException("Weather timeseries invalid.");
            Dictionary<string, object> nearest = null;
            DateTime nearestTime = DateTime.MinValue;
            double distance = double.MaxValue;
            foreach (object row in rows)
            {
                var point = row as Dictionary<string, object>;
                DateTime time = Time(StringValue(point, "time"));
                double d = Math.Abs((time - now).TotalMinutes);
                if (d < distance) { distance = d; nearest = point; nearestTime = time; }
            }
            if (nearest == null || distance > 90 || updated > now.AddMinutes(10)) throw new InvalidDataException("No current forecast available.");
            var data = Object(nearest, "data");
            var instant = Object(Object(data, "instant"), "details");
            double temperature = Number(instant, "air_temperature"), humidity = Number(instant, "relative_humidity");
            if (temperature < -100 || temperature > 80 || humidity < 0 || humidity > 100) throw new InvalidDataException("Weather values invalid.");
            string symbol = string.Empty;
            foreach (string period in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
            { symbol = StringValue(Object(Object(data, period), "summary"), "symbol_code"); if (symbol.Length > 0) break; }
            return new MetWeatherForecast { Code = SymbolCode(symbol), Temperature = temperature, Humidity = humidity,
                ForecastUtc = nearestTime, UpdatedUtc = updated, RetrievedUtc = now };
        }
        static Dictionary<string, object> Object(Dictionary<string, object> parent, string key)
        { object value; return parent != null && parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        static string StringValue(Dictionary<string, object> parent, string key)
        { object value; return parent != null && parent.TryGetValue(key, out value) ? value as string ?? string.Empty : string.Empty; }
        static DateTime Time(string value)
        { DateTime result; if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result)) throw new InvalidDataException("Weather timestamp invalid."); return result; }
        static double Number(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent == null || !parent.TryGetValue(key, out value) || !(value is int || value is long || value is double || value is decimal)) throw new InvalidDataException("Weather measurement missing.");
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) throw new InvalidDataException("Weather measurement invalid.");
            return result;
        }
        static int SymbolCode(string symbol)
        {
            string value = (symbol ?? string.Empty).Split('_')[0];
            if (value.Contains("thunder")) return 95;
            if (value.Contains("sleet")) return 999;
            if (value.Contains("snow")) return value.Contains("heavy") ? 75 : value.Contains("light") ? 71 : 73;
            if (value.Contains("rain")) return value.Contains("heavy") ? 65 : value.Contains("light") ? 61 : 63;
            if (value == "clearsky") return 0;
            if (value == "fair") return 1;
            if (value == "partlycloudy") return 2;
            if (value == "cloudy") return 3;
            if (value == "fog") return 45;
            return 999;
        }

        static MetWeatherHttpResponse FetchHttp(Uri uri, string modified, CancellationToken token)
        {
            for (int redirect = 0; redirect <= 3; redirect++)
            {
                token.ThrowIfCancellationRequested();
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "GET"; request.UserAgent = UserAgent; request.Accept = "application/json";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.AllowAutoRedirect = false; request.Timeout = 10000; request.ReadWriteTimeout = 10000;
                if (!string.IsNullOrEmpty(modified)) request.IfModifiedSince = DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                using (token.Register(request.Abort))
                {
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException error)
                    {
                        token.ThrowIfCancellationRequested();
                        response = error.Response as HttpWebResponse;
                        if (response == null) return new MetWeatherHttpResponse { Offline = true, Status = 0 };
                    }
                    using (response)
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 307 || status == 308)
                        {
                            Uri next;
                            if (!Uri.TryCreate(uri, response.Headers[HttpResponseHeader.Location], out next) || next.Scheme != "https" ||
                                next.Host != "api.met.no" || !next.AbsolutePath.StartsWith("/weatherapi/locationforecast/2.0/", StringComparison.Ordinal))
                                return new MetWeatherHttpResponse { Status = 502 };
                            uri = next; continue;
                        }
                        var result = new MetWeatherHttpResponse { Status = status, LastModified = response.Headers[HttpResponseHeader.LastModified],
                            Expires = response.Headers[HttpResponseHeader.Expires], RetryAfter = response.Headers[HttpResponseHeader.RetryAfter] };
                        if (status == 200 || status == 203)
                        {
                            if (response.ContentLength > MaxBody || !(response.ContentType ?? string.Empty).StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Weather response rejected.");
                            using (Stream stream = response.GetResponseStream())
                            using (var buffer = new MemoryStream())
                            {
                                byte[] chunk = new byte[8192]; int count;
                                while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
                                { token.ThrowIfCancellationRequested(); if (buffer.Length + count > MaxBody) throw new InvalidDataException("Weather response too large."); buffer.Write(chunk, 0, count); }
                                result.Body = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                            }
                        }
                        return result;
                    }
                }
            }
            return new MetWeatherHttpResponse { Status = 502 };
        }
    }
}
