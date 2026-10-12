using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class WeatherFeedbackSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type clientType, responseType, controlType;
    static DateTime clock;
    static int checks, calls;
    static readonly Queue<Func<Uri, string, CancellationToken, object>> responses = new Queue<Func<Uri, string, CancellationToken, object>>();
    static Uri lastUri;
    static string lastModified;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass release root and QA directory.");
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly module = Assembly.LoadFrom(Path.Combine(args[0], "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            clientType = module.GetType("HVX.Controls.MetWeatherClient", true);
            responseType = module.GetType("HVX.Controls.MetWeatherHttpResponse", true);
            controlType = module.GetType("HVX.Controls.ProjectCreationControl", true);
            clock = DateTime.UtcNow;
            Uri uri = (Uri)clientType.GetMethod("RequestUri", All).Invoke(null, new object[] { -16.516667, -68.166667 });
            Check(uri.AbsoluteUri == "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=-16.517&lon=-68.167", "Global coordinate request fixed HTTPS host and three decimals");
            Check(!uri.Query.Contains("project") && !uri.Query.Contains("serial") && !uri.Query.Contains("altitude"), "Request contains only rounded latitude and longitude");
            Reject(() => clientType.GetMethod("RequestUri", All).Invoke(null, new object[] { double.NaN, 0d }), "Invalid coordinate rejected");
            Reject(() => clientType.GetMethod("RequestUri", All).Invoke(null, new object[] { 90.1, 0d }), "Latitude bounds");
            Reject(() => clientType.GetMethod("RequestUri", All).Invoke(null, new object[] { 0d, 180.1 }), "Longitude bounds");
            string good = Fixture(clock, "partlycloudy_day");
            object parsed = Parse(good);
            Check((int)Field(parsed, "Code") == 2, "Weather symbol mapped without daylight alteration");
            Check((double)Field(parsed, "Temperature") == 18.5 && (double)Field(parsed, "Humidity") == 64, "Forecast values parsed independently");
            foreach (var pair in new Dictionary<string, int> { { "clearsky_night", 0 }, { "fair_day", 1 }, { "cloudy", 3 }, { "fog", 45 }, { "heavyrain", 65 }, { "lightsnow", 71 }, { "rainandthunder", 95 }, { "sleet", 999 }, { "unrecognized", 999 } })
                Check((int)Field(Parse(Fixture(clock, pair.Key)), "Code") == pair.Value, "Symbol " + pair.Key);
            foreach (string bad in new[] { "{", "{}", good.Replace("celsius", "fahrenheit"), good.Replace("18.5", "\"NaN\""), good.Replace("64.0", "110.0"), Fixture(clock.AddDays(-1), "cloudy"), new string('x', 2097153) })
                Reject(() => Parse(bad), "Malformed/invalid/stale forecast rejected");

            object client = NewClient();
            string modified = clock.AddMinutes(-10).ToString("R", CultureInfo.InvariantCulture);
            Enqueue(200, good, clock.AddMinutes(30).ToString("R"), modified);
            object result = Fetch(client, 60.1, 9.58);
            Check((string)Field(result, "State") == "updated" && calls == 1, "Initial forecast request");
            result = Fetch(client, 60.10001, 9.58001);
            Check((bool)Field(result, "Cached") && calls == 1, "Rounded-location cache avoids duplicate requests before Expires");
            clock = clock.AddMinutes(31);
            Enqueue(304, null, clock.AddMinutes(30).ToString("R"), modified);
            result = Fetch(client, 60.1, 9.58);
            Check(lastModified == modified, "Exact Last-Modified reused for conditional request");
            Check((bool)Field(result, "Cached") && !(bool)Field(result, "Stale"), "304 retains forecast and updates freshness");
            clock = clock.AddMinutes(31);
            Enqueue(429, null, null, null, "120");
            result = Fetch(client, 60.1, 9.58);
            Check((string)Field(result, "State") == "throttled" && (bool)Field(result, "Stale"), "429 retains expired cache with clear throttled state");
            int after429 = calls;
            result = Fetch(client, 10, 20);
            Check(calls == after429 && (string)Field(result, "State") == "throttled", "429 backoff applies across locations");
            clock = clock.AddMinutes(3);
            Enqueue(0, null, null, null, null, true);
            result = Fetch(client, 60.1, 9.58);
            Check((string)Field(result, "State") == "offline" && Field(result, "Forecast") != null && (bool)Field(result, "Stale"), "Offline keeps older forecast explicitly stale");
            int offlineCalls = calls;
            Fetch(client, 60.1, 9.58);
            Check(calls == offlineCalls, "Failure retry backoff avoids repeated traffic");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Reject(() => Fetch(client, 1, 2, canceled.Token), "Canceled request never transports");
                Check(calls == offlineCalls, "Canceled request sends no data");
            }
            object malformedClient = NewClient(); Enqueue(200, "{}", null, null);
            result = Fetch(malformedClient, 1, 2);
            Check((string)Field(result, "State") == "service" && Field(result, "Forecast") == null, "Invalid response not shown as valid weather");
            foreach (int language in new[] { 0, 1 })
            using (Form dialog = (Form)Activator.CreateInstance(module.GetType("HVX.Controls.WeatherLocationDialog", true), All, null,
                new object[] { language, false, double.NaN, double.NaN }, CultureInfo.InvariantCulture))
            {
                dialog.Show(); Application.DoEvents();
                Check(dialog.Icon != null, "Own weather dialog has brand icon");
                Check(!((Control)Field(dialog, "accept")).Enabled, "First use cannot continue without consent");
                ((TextBox)Field(dialog, "latitude")).Text = "60.1001";
                ((TextBox)Field(dialog, "longitude")).Text = "9.5801";
                ((CheckBox)Field(dialog, "agreement")).Checked = true;
                Check(((Control)Field(dialog, "accept")).Enabled, "Valid consent and coordinates enable forecast");
                ((TextBox)Field(dialog, "latitude")).Text = "NaN";
                Check(!((Control)Field(dialog, "accept")).Enabled, "Nonfinite input disables forecast");
                ((TextBox)Field(dialog, "latitude")).Text = "60.1001";
                Label notice = dialog.Controls[0].Controls.OfType<Label>().First(l => l.Text.Contains("api.met.no"));
                Size measured = TextRenderer.MeasureText(notice.Text, notice.Font, new Size(notice.Width, int.MaxValue), TextFormatFlags.WordBreak);
                Check(notice.Height >= measured.Height, "Full privacy notice fits at actual DPI");
                Control accept = (Control)Field(dialog, "accept");
                Check(accept.Width >= TextRenderer.MeasureText(accept.Text, accept.Font).Width + 12, "Forecast action text not clipped");
                Check(accept.Bounds.Bottom <= accept.Parent.ClientSize.Height, "Forecast action vertically contained");
                using (var screenshot = new Bitmap(dialog.Width, dialog.Height))
                { dialog.DrawToBitmap(screenshot, new Rectangle(Point.Empty, dialog.Size)); screenshot.Save(Path.Combine(output, "weather-location-" + language + ".png")); }
                dialog.Close();
            }

            clock = DateTime.UtcNow; calls = 0; responses.Clear();
            using (Form host = new Form { ClientSize = new Size(1100, 720), StartPosition = FormStartPosition.Manual, Location = new Point(20, 20) })
            using (Control control = (Control)Activator.CreateInstance(controlType))
            {
                control.Dock = DockStyle.Fill; host.Controls.Add(control);
                controlType.GetProperty("ShowDialogMessages").SetValue(control, false, null);
                controlType.GetProperty("OnlineWeatherEnabled").SetValue(control, true, null);
                controlType.GetProperty("SettingsPath").SetValue(control, Path.Combine(output, "settings.ini"), null);
                SetField(control, "weatherClient", NewClient());
                SetField(control, "weatherNetworkAvailable", new Func<bool>(() => true));
                host.Show(); Application.DoEvents();
                Check(!(bool)controlType.GetMethod("RequestWeatherRefresh").Invoke(control, null) && calls == 0, "No weather requests without confirmed location");
                Check((int)controlType.GetProperty("WeatherStateCode").GetValue(control, null) == 5, "Missing/unconfirmed location is explicit");
                controlType.GetProperty("TemperatureCelsius").SetValue(control, 27d, null);
                controlType.GetProperty("RelativeHumidity").SetValue(control, 55d, null);
                SetField(control, "manualWeatherLatitude", 60.1);
                SetField(control, "manualWeatherLongitude", 9.58);
                SetField(control, "weatherDisclosureAccepted", true);
                Enqueue(200, Fixture(clock, "cloudy"), clock.AddMinutes(20).ToString("R"), clock.AddMinutes(-5).ToString("R"));
                Check((bool)controlType.GetMethod("RequestWeatherRefresh").Invoke(control, null), "Confirmed location starts native client");
                Pump(() => (int)controlType.GetProperty("WeatherStateCode").GetValue(control, null) != 1);
                Check((double)controlType.GetProperty("TemperatureCelsius").GetValue(control, null) == 27 && (double)controlType.GetProperty("RelativeHumidity").GetValue(control, null) == 55, "Forecast never overwrites hardware temperature/humidity fields");
                Check((double)controlType.GetProperty("WeatherReferenceTemperatureCelsius").GetValue(control, null) == 18.5, "Forecast separately exposed as reference");
                string text = (string)controlType.GetProperty("Weather").GetValue(control, null);
                Check(text.Contains("MET Norway"), "Source attribution visible");
                var tooltip = (ToolTip)Field(control, "weatherToolTip");
                string attribution = tooltip.GetToolTip((Control)Field(control, "weatherText"));
                Check(attribution.Contains("creativecommons.org/licenses/by/4.0/") && attribution.Contains("api.met.no") && attribution.Contains("18.5"), "License/source and distinct forecast metrics in reference tooltip");
                SetField(control, "weatherNetworkAvailable", new Func<bool>(() => false));
                Check(!(bool)controlType.GetMethod("RequestWeatherRefresh").Invoke(control, null), "Offline request does not start transport");
                Check((int)controlType.GetProperty("WeatherStateCode").GetValue(control, null) == 6, "Offline distinguished from service error");
                SetField(control, "manualWeatherLatitude", 10d);
                controlType.GetMethod("RequestWeatherRefresh").Invoke(control, null);
                Check(string.IsNullOrEmpty((string)controlType.GetProperty("Weather").GetValue(control, null)), "Changed location never retains other-location forecast as current");
                SetField(control, "manualWeatherLatitude", 60.1);

                SetField(control, "weatherNetworkAvailable", new Func<bool>(() => true));
                SetField(control, "weatherClient", NewClient());
                var release = new ManualResetEventSlim(false);
                var started = new ManualResetEventSlim(false);
                responses.Enqueue((u, m, t) => { started.Set(); release.Wait(3000); return Response(200, Fixture(clock, "clearsky_day"), clock.AddMinutes(30).ToString("R"), null, null, false); });
                controlType.GetMethod("RequestWeatherRefresh").Invoke(control, null);
                Pump(() => started.IsSet);
                controlType.GetProperty("Weather").SetValue(control, "New project manual weather", null);
                release.Set();
                Pump(() => (int)controlType.GetProperty("WeatherStateCode").GetValue(control, null) != 1);
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Check((string)controlType.GetProperty("Weather").GetValue(control, null) == "New project manual weather", "Old asynchronous result cannot overwrite changed project/weather context");
                host.Close();
            }
            Console.WriteLine("WEATHER_FEEDBACK_PASS=" + checks + "; NETWORK_REQUESTS=0 (fixture transport only)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static object NewClient()
    {
        var constructor = clientType.GetConstructors(All).Single(c => c.GetParameters().Length == 2);
        Type delegateType = constructor.GetParameters()[0].ParameterType;
        var uri = Expression.Parameter(typeof(Uri), "uri"); var modified = Expression.Parameter(typeof(string), "modified"); var token = Expression.Parameter(typeof(CancellationToken), "token");
        var call = Expression.Call(typeof(WeatherFeedbackSmoke).GetMethod("Send", All), uri, modified, token);
        Delegate transport = Expression.Lambda(delegateType, Expression.Convert(call, responseType), uri, modified, token).Compile();
        return constructor.Invoke(new object[] { transport, new Func<DateTime>(() => clock) });
    }
    static object Send(Uri uri, string modified, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); calls++; lastUri = uri; lastModified = modified;
        if (responses.Count == 0) throw new InvalidOperationException("Unexpected transport request (network is never used by this test).");
        return responses.Dequeue()(uri, modified, token);
    }
    static object Fetch(object client, double latitude, double longitude, CancellationToken token = default(CancellationToken))
    {
        Task task = (Task)clientType.GetMethod("GetAsync", All).Invoke(client, new object[] { latitude, longitude, token });
        task.GetAwaiter().GetResult(); return task.GetType().GetProperty("Result").GetValue(task, null);
    }
    static object Parse(string json) { return clientType.GetMethod("Parse", All).Invoke(null, new object[] { json, clock }); }
    static void Enqueue(int status, string body, string expires, string modified, string retry = null, bool offline = false)
    { responses.Enqueue((u, m, t) => Response(status, body, expires, modified, retry, offline)); }
    static object Response(int status, string body, string expires, string modified, string retry, bool offline)
    {
        object response = Activator.CreateInstance(responseType, true);
        foreach (var pair in new Dictionary<string, object> { { "Status", status }, { "Body", body }, { "Expires", expires }, { "LastModified", modified }, { "RetryAfter", retry }, { "Offline", offline } }) SetField(response, pair.Key, pair.Value);
        return response;
    }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static void SetField(object owner, string name, object value) { owner.GetType().GetField(name, All).SetValue(owner, value); }
    static void Pump(Func<bool> done)
    { DateTime end = DateTime.UtcNow.AddSeconds(6); while (!done() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); } Check(done(), "Async operation completes within bounded fixture time"); }
    static void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException(label); }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (Exception error) { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Check(error is ArgumentException || error is InvalidDataException || error is InvalidOperationException || error is OperationCanceledException, label); return; }
        throw new InvalidOperationException(label + " unexpectedly accepted");
    }
    static string Fixture(DateTime time, string symbol)
    {
        return "{\"type\":\"Feature\",\"properties\":{\"meta\":{\"updated_at\":\"" + time.AddMinutes(-10).ToString("o") + "\",\"units\":{\"air_temperature\":\"celsius\",\"relative_humidity\":\"%\"}},\"timeseries\":[{\"time\":\"" + time.ToString("o") + "\",\"data\":{\"instant\":{\"details\":{\"air_temperature\":18.5,\"relative_humidity\":64.0}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"" + symbol + "\"}}}}]}}";
    }
}
