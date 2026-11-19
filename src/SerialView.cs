using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed class PortOption
{
    private static readonly Regex PortName = new Regex(@"^COM[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PortCaption = new Regex(@"\s*\((COM[0-9]+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Ch340Id = new Regex(@"^USB\\VID_1A86&PID_7523(?:&|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Name, Description;
    public bool IsCh340;

    public override string ToString()
    {
        string caption = String.IsNullOrWhiteSpace(Description) ? Name : Name + "  " + Description;
        return IsCh340 ? caption + "  [CH340]" : caption;
    }

    public static PortOption[] Discover()
    {
        var ports = new Dictionary<string, PortOption>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in SerialPort.GetPortNames())
            if (PortName.IsMatch(name) && !ports.ContainsKey(name))
                ports.Add(name, new PortOption { Name = name.ToUpperInvariant() });

        if (ports.Count != 0 && !DescribeFromSetupApi(ports))
        {
            // Port names from Windows remain usable even if WMI is disabled.
            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2");
                var query = new ObjectQuery("SELECT Name, PNPDeviceID FROM Win32_PnPEntity " +
                    "WHERE ClassGuid = '{4d36e978-e325-11ce-bfc1-08002be10318}' AND Present = TRUE");
                var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) };
                using (var searcher = new ManagementObjectSearcher(scope, query, options))
                using (var devices = searcher.Get())
                {
                    foreach (ManagementBaseObject device in devices)
                    {
                        using (device)
                        {
                            string caption = Convert.ToString(device["Name"]);
                            Match match = PortCaption.Match(caption);
                            PortOption port;
                            if (!match.Success || !ports.TryGetValue(match.Groups[1].Value, out port))
                                continue;
                            port.Description = caption.Substring(0, match.Index).TrimEnd();
                            port.IsCh340 = Ch340Id.IsMatch(Convert.ToString(device["PNPDeviceID"]));
                        }
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            catch (SecurityException) { }
        }

        return ports.Values.OrderByDescending(p => p.IsCh340).ThenBy(p => p.Name.Length)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DescribeFromSetupApi(Dictionary<string, PortOption> ports)
    {
        var portClass = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiGetClassDevs(ref portClass, null, IntPtr.Zero, 2); // DIGCF_PRESENT
        IntPtr invalid = new IntPtr(-1);
        if (set == invalid) return false;
        bool described = false;
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;

                IntPtr key = SetupDiOpenDevRegKey(set, ref info, 1, 0, 1, 1);
                if (key == invalid) continue;
                PortOption port;
                try
                {
                    using (var handle = new SafeRegistryHandle(key, true))
                    using (RegistryKey registry = RegistryKey.FromHandle(handle))
                    {
                        string name = Convert.ToString(registry.GetValue("PortName"));
                        if (!ports.TryGetValue(name, out port)) continue;
                    }
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (SecurityException) { continue; }

                var id = new StringBuilder(512);
                if (SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, IntPtr.Zero))
                    port.IsCh340 = Ch340Id.IsMatch(id.ToString());

                string caption = DeviceProperty(set, ref info, 12); // SPDRP_FRIENDLYNAME
                if (String.IsNullOrWhiteSpace(caption)) caption = DeviceProperty(set, ref info, 0); // SPDRP_DEVICEDESC
                Match match = PortCaption.Match(caption);
                port.Description = (match.Success ? caption.Substring(0, match.Index) : caption).Trim();
                described = true;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return described;
    }

    private static string DeviceProperty(IntPtr set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[1024];
        uint type, size;
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, buffer, (uint)buffer.Length, out size)
            || type != 1 || size > buffer.Length)
            return "";
        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        internal int Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo info, uint scope, uint profile, uint keyType, uint access);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int capacity, IntPtr required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type,
        [Out] byte[] buffer, uint capacity, out uint size);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}

internal sealed class SerialText
{
    private enum Escape { None, Start, Intermediate, Csi, ControlString, StringEscape }
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private readonly Decoder decoder = Utf8.GetDecoder();
    private Escape escape;
    private int escapeLength;
    private bool pendingCr, breakHasLf;
    private bool firstCharacter = true;

    public string Feed(byte[] bytes, int count)
    {
        return Decode(bytes, count, false);
    }

    public string Finish()
    {
        return Decode(new byte[0], 0, true);
    }

    private string Decode(byte[] bytes, int count, bool finish)
    {
        char[] chars = new char[Utf8.GetMaxCharCount(count)];
        int length = decoder.GetChars(bytes, 0, count, chars, 0, finish);
        var output = new StringBuilder(length + 1);
        for (int i = 0; i < length; i++)
        {
            char c = chars[i];
            if (firstCharacter)
            {
                firstCharacter = false;
                if (c == '\uFEFF') continue; // UTF-8 BOM in a replayed text file.
            }
            if (SkipEscape(c)) continue;
            if (c == '\r')
            {
                pendingCr = true;
                continue;
            }
            if (c == '\n')
            {
                output.Append('\n');
                pendingCr = false;
                breakHasLf = true;
                continue;
            }
            if (c != '\t' && Char.IsControl(c)) continue;
            // CRLF, LFCR and CRCRLF each contain one line advance; a CR-only break also counts once.
            if (pendingCr && !breakHasLf) output.Append('\n');
            pendingCr = breakHasLf = false;
            output.Append(c);
        }
        if (finish)
        {
            if (pendingCr && !breakHasLf) output.Append('\n');
            pendingCr = breakHasLf = false;
        }
        return output.ToString();
    }

    private bool SkipEscape(char c)
    {
        if (escape == Escape.None)
        {
            if (c != '\u001b') return false;
            escape = Escape.Start;
            escapeLength = 1;
            return true;
        }
        // A malformed control sequence must not hide the rest of the capture.
        if (++escapeLength > 4096)
        {
            escape = Escape.None;
            return SkipEscape(c);
        }

        switch (escape)
        {
            case Escape.Start:
                if (c == '[') escape = Escape.Csi;
                else if (c == ']' || c == 'P' || c == 'X' || c == '^' || c == '_') escape = Escape.ControlString;
                else if (c == '\u001b') escapeLength = 1;
                else if (c >= ' ' && c <= '/') escape = Escape.Intermediate;
                else if (c >= '0' && c <= '~') escape = Escape.None;
                else { escape = Escape.None; return false; }
                return true;
            case Escape.Intermediate:
            case Escape.Csi:
                if (c == '\u001b')
                {
                    escape = Escape.Start;
                    escapeLength = 1;
                }
                else if (c >= (escape == Escape.Csi ? '@' : '0') && c <= '~') escape = Escape.None;
                else if (c < ' ' || c > '?') { escape = Escape.None; return false; }
                return true;
            case Escape.ControlString:
                if (c == '\a') escape = Escape.None;
                else if (c == '\u001b') escape = Escape.StringEscape;
                return true;
            case Escape.StringEscape:
                if (c == '\\' || c == '\a') escape = Escape.None;
                else if (c != '\u001b') escape = Escape.ControlString;
                return true;
            default:
                return false;
        }
    }
}
