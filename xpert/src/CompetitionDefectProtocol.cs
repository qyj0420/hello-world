using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Desktop
{
    // This DTO intentionally contains no hardware ports, credentials or acquisition commands.
    internal sealed class CompetitionDefectContext
    {
        public int Protocol { get; set; }
        public long Revision { get; set; }
        public long Activation { get; set; }
        public string ProjectPath { get; set; }

        internal static CompetitionDefectContext Create(AppSettings settings, ApplicationState state)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            return new CompetitionDefectContext
            {
                Protocol = 2, ProjectPath = state == null ? String.Empty : state.ProjectPath
            };
        }
    }

    internal static class CompetitionDefectProtocol
    {
        internal const int MaximumFrameBytes = 65536;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);

        internal static int ValidateArguments(string pipeName, string token)
        {
            Match match = Regex.Match(pipeName ?? String.Empty, @"\AHVX\.Defect\.(\d{1,10})\.[a-f0-9]{32}\z", RegexOptions.CultureInvariant);
            int parent;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out parent) || parent < 1 ||
                !Regex.IsMatch(token ?? String.Empty, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid standalone module launch context.");
            return parent;
        }

        internal static NamedPipeServerStream CreateServer(string name)
        {
            SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
            if (sid == null) throw new UnauthorizedAccessException("The current Windows identity is unavailable.");
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(sid);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        internal static void VerifyPeer(PipeStream pipe, int expectedId, bool serverSide)
        {
            uint actual;
            bool ok = serverSide ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out actual) :
                GetNamedPipeServerProcessId(pipe.SafePipeHandle, out actual);
            if (!ok || actual != (uint)expectedId) throw new UnauthorizedAccessException("Unexpected standalone module pipe peer.");
        }

        internal static void WriteText(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? String.Empty);
            if (bytes.Length == 0 || bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Invalid IPC frame length.");
            byte[] size = BitConverter.GetBytes(bytes.Length);
            stream.Write(size, 0, size.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }

        internal static string ReadText(Stream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            int count = BitConverter.ToInt32(header, 0);
            if (count < 1 || count > MaximumFrameBytes) throw new InvalidDataException("Invalid IPC frame length.");
            return new UTF8Encoding(false, true).GetString(ReadExactly(stream, count));
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] result = new byte[count]; int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(result, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("The main application closed the module channel.");
                offset += read;
            }
            return result;
        }

        internal static string Serialize(CompetitionDefectContext value)
        { return new JavaScriptSerializer { MaxJsonLength = MaximumFrameBytes }.Serialize(value); }

        internal static CompetitionDefectContext Deserialize(string value)
        {
            CompetitionDefectContext context = new JavaScriptSerializer { MaxJsonLength = MaximumFrameBytes, RecursionLimit = 8 }
                .Deserialize<CompetitionDefectContext>(value);
            if (context == null || context.Protocol != 2 || context.Revision < 1)
                throw new InvalidDataException("Unsupported standalone module context.");
            return context;
        }
    }
}
