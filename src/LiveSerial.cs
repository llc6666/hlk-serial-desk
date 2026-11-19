using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

internal sealed class Notice
{
    public string Kind, Text, Folder;
    public int Count;
    public Notice(string kind, string text, int count = 0, string folder = null)
    { Kind = kind; Text = text; Count = count; Folder = folder; }
}

internal interface ISerialTransport : IDisposable
{
    void Open();
    int BytesToRead { get; }
    int Read(byte[] bytes, int offset, int count);
    void Write(byte[] bytes, int offset, int count);
    bool IsPresent { get; }
}

internal sealed class LiveSerial
{
    // Monitor wakeup handles outbound work immediately; the short timed wait polls RX.
    private const int IdleWaitMs = 10;
    private readonly ConcurrentQueue<Notice> output;
    private readonly string root, portName;
    private readonly int baud;
    private readonly Func<ISerialTransport> createTransport;
    private readonly object state = new object();
    private bool started, stopping, accepting, busy, inBootloader;
    private byte[] pending;

    public LiveSerial(string root, string portName, int baud, ConcurrentQueue<Notice> output)
        : this(root, portName, baud, output, () => new SerialPortTransport(portName, baud)) { }

    internal LiveSerial(string root, string portName, int baud, ConcurrentQueue<Notice> output,
        Func<ISerialTransport> createTransport)
    {
        this.root = root; this.portName = portName; this.baud = baud;
        this.output = output; this.createTransport = createTransport;
    }

    public void Start()
    {
        lock (state)
        {
            if (started) throw new InvalidOperationException("串口会话已启动");
            started = true;
            new Thread(Work) { IsBackground = true }.Start();
        }
    }

    public void Stop()
    {
        lock (state)
        {
            stopping = true;
            accepting = false;
            CancelPending("已断开：排队的请求未发送。");
            Monitor.PulseAll(state);
        }
    }

    public void CancelSend()
    {
        lock (state) CancelPending("已关闭发送：排队的请求未发送。");
    }

    public bool Send(byte[] data)
    {
        if (data == null || data.Length == 0) return false;
        lock (state)
        {
            if (!accepting || stopping || busy || inBootloader) return false;
            pending = (byte[])data.Clone();
            busy = true;
            Monitor.PulseAll(state);
            return true;
        }
    }

    // Called under state. If pending is null, a write may already be in flight.
    private void CancelPending(string reason)
    {
        if (pending == null) return;
        pending = null;
        busy = false;
        output.Enqueue(new Notice("tx-cancel", reason));
    }

    private bool Stopping
    {
        get { lock (state) return stopping; }
    }

    private byte[] PeekPending()
    {
        lock (state) return pending;
    }

    private bool BeginWrite(byte[] request)
    {
        lock (state)
        {
            if (stopping || inBootloader || pending != request) return false;
            // Commit point: cancellation after this waits for the outcome.
            pending = null;
            return true;
        }
    }

    private void CompleteWrite(Notice notice, bool usable)
    {
        lock (state)
        {
            if (!usable) accepting = false;
            output.Enqueue(notice);
            busy = false;
        }
    }

    private int ReadBuffered(ISerialTransport port, CaptureFiles capture,
        SerialText decoder, BootMarkers markers, byte[] block, bool finalDrain = false)
    {
        // Only request bytes known to be buffered. A finite snapshot avoids
        // starving TX when a fast target prints continuously.
        int total = 0;
        int remaining = port.BytesToRead;
        while (remaining > 0 && (finalDrain || !Stopping))
        {
            int n;
            try { n = port.Read(block, 0, Math.Min(remaining, block.Length)); }
            catch (TimeoutException) { break; }
            if (n == 0) break;

            capture.Receive(block, n);
            string shown = decoder.Feed(block, n);
            markers.Feed(shown);
            lock (state)
            {
                inBootloader = markers.InBootloader;
                output.Enqueue(new Notice("rx", shown, n));
                if (inBootloader) CancelPending("检测到 U-Boot：排队的请求未发送。");
            }
            remaining -= n;
            total += n;
        }
        return total;
    }

    private void WriteOne(ISerialTransport port, CaptureFiles capture, byte[] data)
    {
        try { port.Write(data, 0, data.Length); }
        catch (Exception ex)
        {
            string reason = "写串口失败，可能已部分发送。请先检查设备状态，勿自动重试：" + ex.Message;
            CompleteWrite(new Notice("tx-error", reason), false);
            try { capture.Event("TX 未确认，可能已部分发送；请求字节: " + BitConverter.ToString(data)); }
            catch (Exception logError) { reason += "；写入发送事件失败：" + logError.Message; }
            throw new IOException(reason, ex);
        }

        bool logged = false;
        try
        {
            capture.Sent(data, data.Length);
            logged = true;
        }
        catch (Exception ex)
        {
            throw new IOException("命令已写入串口，但发送事件保存失败：" + ex.Message, ex);
        }
        finally
        {
            CompleteWrite(new Notice("tx", "", data.Length), logged);
        }
    }

    private void Work()
    {
        ISerialTransport port = null;
        CaptureFiles capture = null;
        string folder = null, reason = "已断开，证据已封存";
        bool failed = false, openedPort = false;
        var decoder = new SerialText();
        var markers = new BootMarkers();
        var block = new byte[4096];
        try
        {
            if (Stopping) return;
            port = createTransport();
            port.Open();
            openedPort = true;
            // Do not discard input: the board may already be printing its boot banner.
            capture = new CaptureFiles(root, portName, baud);
            folder = capture.Folder;
            lock (state)
            {
                if (!stopping)
                {
                    output.Enqueue(new Notice("open", portName, 0, folder));
                    accepting = true;
                }
            }

            var cadence = Stopwatch.StartNew();
            while (!Stopping)
            {
                if (cadence.ElapsedMilliseconds >= 2000)
                {
                    if (!port.IsPresent) throw new IOException("Windows 已移除 " + portName);
                    cadence.Restart();
                }

                int received = ReadBuffered(port, capture, decoder, markers, block);
                byte[] request = PeekPending();
                if (request != null)
                {
                    // Recheck RX after observing a request so buffered boot text
                    // takes precedence even if Send raced with the first snapshot.
                    received += ReadBuffered(port, capture, decoder, markers, block);
                    if (BeginWrite(request))
                    {
                        WriteOne(port, capture, request);
                        continue;
                    }
                }

                if (received == 0)
                {
                    lock (state)
                        if (!stopping && pending == null) Monitor.Wait(state, IdleWaitMs);
                }
            }
            // Tail bytes already buffered at a normal disconnect belong to the capture.
            ReadBuffered(port, capture, decoder, markers, block, true);
        }
        catch (Exception ex)
        {
            failed = true;
            reason = ex is UnauthorizedAccessException && !openedPort
                ? "无法打开 " + portName + "；请关闭 SSCOM / 其他占用串口的工具。"
                : (capture == null ? "连接或创建日志失败：" : "会话中止：") + ex.Message;
        }
        finally
        {
            lock (state)
            {
                stopping = true;
                accepting = false;
                CancelPending(failed ? "会话异常结束：排队的请求未发送。" : "已断开：排队的请求未发送。");
            }
            if (port != null)
            {
                try { port.Dispose(); }
                catch (Exception ex) { failed = true; reason += "；释放串口失败：" + ex.Message; }
            }
            if (capture != null)
            {
                try
                {
                    output.Enqueue(new Notice("rx", decoder.Finish()));
                    capture.Finish(failed ? reason : "用户断开");
                }
                catch (Exception ex) { failed = true; reason += "；封存失败，保留已有文件：" + ex.Message; }
                finally
                {
                    try { capture.Dispose(); }
                    catch (Exception ex) { failed = true; reason += "；释放日志失败：" + ex.Message; }
                }
            }
            output.Enqueue(new Notice(failed ? "failed" : "closed", reason, 0, folder));
        }
    }

    private sealed class SerialPortTransport : ISerialTransport
    {
        private readonly SerialPort port;

        public SerialPortTransport(string portName, int baud)
        {
            port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) {
                Handshake = Handshake.None, ReadTimeout = IdleWaitMs, WriteTimeout = 800,
                DtrEnable = false, RtsEnable = false, ReadBufferSize = 65536
            };
        }

        public int BytesToRead { get { return port.BytesToRead; } }

        public bool IsPresent
        {
            get
            {
                return Array.FindIndex(SerialPort.GetPortNames(),
                    x => String.Equals(x, port.PortName, StringComparison.OrdinalIgnoreCase)) >= 0;
            }
        }

        public void Open() { port.Open(); }
        public int Read(byte[] bytes, int offset, int count) { return port.Read(bytes, offset, count); }
        public void Write(byte[] bytes, int offset, int count) { port.Write(bytes, offset, count); }
        public void Dispose() { port.Dispose(); }
    }
}

internal sealed class BootMarkers
{
    private string tail = "";
    public bool UBoot, Kernel, Init, Shell, InBootloader;
    public void Feed(string value)
    {
        if (value.Length == 0) return;
        string data = tail + value;
        int boundary = tail.Length;
        int boot = NewlySeen(data, "U-Boot 1.", boundary);
        int menu = NewlySeen(data, "Please choose the operation:", boundary);
        int kern = NewlySeen(data, "Linux version ", boundary);
        int init = NewlySeen(data, "- init complete -", boundary);
        int loader = Math.Max(boot, menu);
        if (boot >= 0) UBoot = true;
        if (loader >= 0) { InBootloader = true; Kernel = Init = Shell = false; }
        if (kern > loader) Kernel = true;
        if (init > loader) { Init = true; InBootloader = false; }
        if (!InBootloader && !Shell &&
            System.Text.RegularExpressions.Regex.IsMatch(data, @"(?:^|\n)(?:/[^\s#]*|root@[^\s:]+:[^\s#]*)#(?: |(?=\n|$))"))
            Shell = true;
        tail = data.Length <= 192 ? data : data.Substring(data.Length - 192);
    }
    private static int NewlySeen(string data, string marker, int boundary)
    {
        int i = data.LastIndexOf(marker, StringComparison.Ordinal);
        return i >= 0 && i + marker.Length > boundary ? i : -1;
    }
    public string Label()
    {
        return String.Format("启动线索   U-Boot {0}     Linux 内核 {1}     init 完成 {2}     Shell 提示符 {3}",
            UBoot ? "✓" : "·", Kernel ? "✓" : "·", Init ? "✓" : "·", Shell ? "✓" : "·");
    }
}
