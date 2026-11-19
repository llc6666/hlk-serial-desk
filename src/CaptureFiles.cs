using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Owned by one serial worker. RX evidence is separate from operator actions.
internal sealed class CaptureFiles : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly Encoding ReadableUtf8 = new UTF8Encoding(true);
    private readonly SerialText readable = new SerialText();
    private FileStream raw;
    private StreamWriter transcript;
    private StreamWriter events;
    private bool closed;

    public string Folder { get; private set; }

    public CaptureFiles(string root, string port, int baud)
    {
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "-" + FileLabel(port) + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        Folder = Path.Combine(Path.GetFullPath(root), name);
        Directory.CreateDirectory(Folder);

        try
        {
            raw = NewFile("rx.raw");
            transcript = new StreamWriter(NewFile("rx.txt"), ReadableUtf8);
            events = NewTextFile("events.log");
            Event("OPEN " + port + " " + baud.ToString(CultureInfo.InvariantCulture)
                + " 8N1, flow=none, DTR=off, RTS=off");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Receive(byte[] bytes, int count)
    {
        EnsureOpen();
        ValidateCount(bytes, count);
        if (count == 0) return;

        raw.Write(bytes, 0, count);
        raw.Flush();
        WriteReadable(readable.Feed(bytes, count));
    }

    public void Sent(byte[] bytes, int count)
    {
        EnsureOpen();
        ValidateCount(bytes, count);
        if (count == 0) return;
        Event("TX " + count.ToString(CultureInfo.InvariantCulture) + " bytes | HEX "
            + BitConverter.ToString(bytes, 0, count).Replace('-', ' ')
            + " | TEXT " + Utf8.GetString(bytes, 0, count));
    }

    public void Event(string message)
    {
        EnsureOpen();
        events.WriteLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
            + "  " + SingleLine(message));
        events.Flush();
    }

    public void Finish(string reason)
    {
        if (closed) return;
        Exception error = null;
        Attempt(delegate { Event("CLOSE " + reason); }, ref error);
        Attempt(delegate { WriteReadable(readable.Finish()); }, ref error);
        CloseFiles(ref error);
        if (error != null) throw new IOException("关闭会话文件时出错，已有数据已保留。", error);

        string[] evidence = { "rx.raw", "rx.txt", "events.log" };
        string manifest = "SHA256SUMS.txt";
        string partial = manifest + ".partial";
        using (var hash = SHA256.Create())
        using (var writer = NewTextFile(partial))
        {
            foreach (string filename in evidence)
            {
                using (var input = File.OpenRead(Path.Combine(Folder, filename)))
                {
                    string hex = BitConverter.ToString(hash.ComputeHash(input))
                        .Replace("-", "").ToLowerInvariant();
                    writer.WriteLine(hex + "  " + filename);
                }
            }
        }
        File.Move(Path.Combine(Folder, partial), Path.Combine(Folder, manifest));
    }

    public void Dispose()
    {
        // Best effort for exceptions before Finish; do not mask the primary failure.
        Exception ignored = null;
        CloseFiles(ref ignored);
    }

    private void CloseFiles(ref Exception error)
    {
        if (closed) return;
        closed = true;
        Attempt(delegate { if (transcript != null) transcript.Dispose(); }, ref error);
        Attempt(delegate { if (raw != null) raw.Dispose(); }, ref error);
        Attempt(delegate { if (events != null) events.Dispose(); }, ref error);
    }

    private FileStream NewFile(string name)
    {
        return new FileStream(Path.Combine(Folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    private StreamWriter NewTextFile(string name)
    {
        return new StreamWriter(NewFile(name), Utf8);
    }

    private void WriteReadable(string text)
    {
        if (text.Length == 0) return;
        // SerialText emits normalized LF for the terminal. Use CRLF for the Windows transcript.
        transcript.Write(text.Replace("\n", "\r\n"));
        transcript.Flush();
    }

    private void EnsureOpen()
    {
        if (closed) throw new ObjectDisposedException("CaptureFiles");
    }

    private static void ValidateCount(byte[] bytes, int count)
    {
        if (bytes == null) throw new ArgumentNullException("bytes");
        if (count < 0 || count > bytes.Length) throw new ArgumentOutOfRangeException("count");
    }

    private static void Attempt(Action action, ref Exception error)
    {
        try { action(); }
        catch (Exception ex) { if (error == null) error = ex; }
    }

    private static string FileLabel(string port)
    {
        var label = new StringBuilder();
        foreach (char c in port ?? "")
        {
            if (label.Length == 24) break;
            label.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') ? c : '_');
        }
        return label.Length == 0 ? "port" : label.ToString();
    }

    private static string SingleLine(string message)
    {
        var line = new StringBuilder();
        foreach (char c in message ?? "")
        {
            switch (c)
            {
                case '\\': line.Append(@"\\"); break;
                case '\r': line.Append(@"\r"); break;
                case '\n': line.Append(@"\n"); break;
                case '\t': line.Append(@"\t"); break;
                default:
                    if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                        line.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        line.Append(c);
                    break;
            }
        }
        return line.ToString();
    }
}
