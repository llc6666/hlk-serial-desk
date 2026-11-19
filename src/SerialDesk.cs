using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    internal const string Version = "1.3.0";
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SerialDesk(args.Length == 2 && args[0] == "--replay" ? args[1] : null));
    }
}

internal sealed class SerialDesk : Form
{
    private static readonly Color GreenBack = Color.FromArgb(222, 244, 231), GreenInk = Color.FromArgb(15, 98, 62),
        Lilac = Color.FromArgb(237, 230, 254), VioletInk = Color.FromArgb(77, 36, 160),
        GreyBack = Color.FromArgb(234, 237, 245), GreyInk = Color.FromArgb(63, 74, 99),
        OrangeBack = Color.FromArgb(255, 238, 208), OrangeInk = Color.FromArgb(124, 70, 10),
        CrimsonBack = Color.FromArgb(255, 229, 235), CrimsonInk = Color.FromArgb(143, 39, 62);
    private const int TailLimit = 220000;
    private readonly ConcurrentQueue<Notice> inbox = new ConcurrentQueue<Notice>();
    private readonly ComboBox port = Choice("串口"), baud = Choice("波特率"),
        suffix = Choice("行尾"), presets = Choice("只读预设");
    private readonly TextBox entry = DraftInput(), search = Input("在接收日志内查找", 80);
    private readonly CheckBox tx = new CheckBox { AutoSize = true, Text = "启用发送", Margin = new Padding(10, 5, 0, 0) };
    private readonly CheckBox follow = new CheckBox { AutoSize = true, Checked = true, Text = "跟随日志", Margin = new Padding(10, 9, 1, 0) };
    private readonly RichTextBox terminal = new RichTextBox {
        Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, HideSelection = false,
        BackColor = DeskStyle.Console, ForeColor = DeskStyle.ConsoleText, Font = new Font("Consolas", 10.5f),
        BorderStyle = BorderStyle.None, Margin = new Padding(14, 10, 8, 8), AccessibleName = "接收日志" };
    private readonly Button refresh = new DeskButton("找串口"), connect = new DeskButton("开始监听", true),
        disconnect = new DeskButton("断开"), replay = new DeskButton("离线回放"), send = new DeskButton("发送", true),
        enter = new DeskButton("发回车"), presetSend = new DeskButton("发预设"), folderButton = new DeskButton("打开会话目录"),
        find = new DeskButton("查找");
    private readonly Label state = LabelText(), boot = LabelText(), note = LabelText(), totals = LabelText();
    private readonly Label consoleMode = LabelText(), emptyTitle = LabelText(), emptyTip = LabelText();
    private readonly DeskPill badge = new DeskPill("等待连接"), phaseBoot = new DeskPill("U-Boot"),
        phaseKernel = new DeskPill("Linux 内核"), phaseInit = new DeskPill("init 完成"), phaseShell = new DeskPill("Shell 提示符");
    private readonly ToolTip tips = new ToolTip { ShowAlways = true };
    private readonly TableLayoutPanel emptyConsole = Stack(-1, 40, 32, -1);
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 };
    private readonly string outputRoot;
    private LiveSerial live;
    private BootMarkers markers = new BootMarkers();
    private bool opening, closing, replaying, refreshing, exitWhenDone, tailClipped, sendPending, offlineView, faulted;
    private string currentFolder, draftAwaitingAck, txFeedback = "";
    private long draftVersion, sentDraftVersion;
    private bool txProblem;
    private long rxBytes, txBytes;

    private sealed class Command
    {
        internal readonly string Value, Group;
        internal Command(string group, string value) { Group = group; Value = value; }
        public override string ToString() { return Group + "  ·  " + Value; }
    }
    private static readonly string[] G1 = {
        "cat /proc/cpuinfo", "head -6 /proc/meminfo", "free", "cat /proc/version", "cat /proc/cmdline",
        "cat /proc/mtd", "df -h", "ifconfig -a", "lsmod", "cat /proc/bus/pci/devices",
        "lspci", "iw dev", "iw list", "iwconfig", "cat /etc/openwrt_release"
    };
    private static readonly string[] G2 = { "route -n", "cat /proc/net/dev", "cat /sys/class/net/eth0/carrier", "dmesg | tail -n 40" };

    public SerialDesk(string replayOnStart)
    {
        Text = "HLK 串口台 · " + Program.Version;
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 776);
        MinimumSize = new Size(970, 660);
        StartPosition = FormStartPosition.CenterScreen;
        ForeColor = DeskStyle.Ink;
        BackColor = DeskStyle.Paper;
        outputRoot = CaptureRoot();

        baud.Items.AddRange(new object[] { "57600", "115200", "38400", "19200", "9600" });
        baud.SelectedIndex = 0;
        suffix.Items.AddRange(new object[] { "CR", "LF", "CRLF", "无" });
        suffix.SelectedIndex = 0;
        foreach (var s in G1) presets.Items.Add(new Command("G1", s));
        foreach (var s in G2) presets.Items.Add(new Command("G2", s));
        presets.SelectedIndex = 4;

        var frame = Stack(72, 111, 56, -1, 153, 43);
        frame.Padding = new Padding(22, 10, 22, 7);
        Controls.Add(frame);
        frame.Controls.Add(BuildHeader(), 0, 0);
        frame.Controls.Add(BuildConnection(), 0, 1);
        frame.Controls.Add(BuildStages(), 0, 2);
        frame.Controls.Add(BuildTerminal(), 0, 3);
        frame.Controls.Add(BuildSend(), 0, 4);
        frame.Controls.Add(BuildFooter(), 0, 5);

        state.Text = "先插 USB1 数据线，打开监听后再给板子上 12V。";
        currentFolder = outputRoot;
        refresh.Click += (s, e) => RefreshPorts();
        connect.Click += (s, e) => Connect();
        disconnect.Click += (s, e) => Disconnect();
        replay.Click += (s, e) => ChooseReplay();
        send.Click += (s, e) => SendText();
        enter.Click += (s, e) => SendPayload("", "\r");
        presetSend.Click += (s, e) => SendPreset();
        folderButton.Click += (s, e) => OpenFolder();
        find.Click += (s, e) => FindText();
        port.SelectedIndexChanged += (s, e) => UpdateControls();
        tx.CheckedChanged += (s, e) => {
            if (!tx.Checked && live != null && sendPending) live.CancelSend();
            UpdateControls();
        };
        entry.TextChanged += (s, e) => { ++draftVersion; };
        entry.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendText(); } };
        search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindText(); } };
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; search.Focus(); search.SelectAll(); } };
        timer.Tick += (s, e) => Drain();
        FormClosed += (s, e) => { timer.Dispose(); tips.Dispose(); };
        FormClosing += (s, e) => {
            if (live == null) return;
            e.Cancel = true;
            exitWhenDone = true;
            Disconnect();
        };
        Shown += (s, e) => { timer.Start(); RefreshPorts(); if (replayOnStart != null) ReplayFile(replayOnStart); };
        UpdateControls();
    }

    private Control BuildHeader()
    {
        var header = Inline(52, -1, 128);
        var mark = new DeskMark { Size = new Size(40, 40), Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 8) };
        var titles = Stack(37, 23);
        titles.Padding = new Padding(0, 3, 0, 0);
        titles.Controls.Add(Title("HLK 串口台", 22), 0, 0);
        titles.Controls.Add(Caption("HLK-7621A  /  OpenWrt", 9f), 0, 1);
        badge.Size = new Size(124, 29);
        badge.Anchor = AnchorStyles.Right;
        badge.Margin = new Padding(0, 0, 0, 8);
        header.Controls.Add(mark, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(badge, 2, 0);
        return header;
    }

    private Control BuildConnection()
    {
        var settings = Stack(25, 40, -1);
        var bar = Inline(37, 254, 80, 60, 65, 85, 105, 66, -1, 102);
        Place(bar, 0, Caption("端口"));
        Place(bar, 1, port);
        Place(bar, 2, refresh);
        Place(bar, 3, Caption("波特率"));
        Place(bar, 4, baud);
        Place(bar, 5, Caption("8N1 · 无流控", 8.5f));
        Place(bar, 6, connect);
        Place(bar, 7, disconnect);
        Place(bar, 9, replay, false);
        state.Dock = DockStyle.Fill;
        state.AutoEllipsis = true;
        settings.Controls.Add(Title("串口连接"), 0, 0);
        settings.Controls.Add(bar, 0, 1);
        settings.Controls.Add(state, 0, 2);
        return Card(settings, new Padding(14, 8, 14, 7), new Padding(0, 0, 0, 9));
    }

    private Control BuildStages()
    {
        var bar = Inline(88, 112, 112, 112, 128, -1);
        boot.Text = "启动阶段";
        boot.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        boot.ForeColor = DeskStyle.Ink;
        Place(bar, 0, boot);
        Place(bar, 1, phaseBoot); Place(bar, 2, phaseKernel); Place(bar, 3, phaseInit); Place(bar, 4, phaseShell);
        return Card(bar, new Padding(14, 7, 14, 7), new Padding(0, 0, 0, 9));
    }

    private Control BuildTerminal()
    {
        var shell = Stack(46, -1);
        shell.BackColor = DeskStyle.Console;
        shell.Margin = new Padding(0, 0, 0, 10);
        var bar = Inline(83, 102, -1, 152, 60, 90);
        bar.Padding = new Padding(14, 0, 8, 0);
        bar.BackColor = DeskStyle.ConsoleBar;
        var title = Title("接收日志", 10f);
        title.ForeColor = DeskStyle.ConsoleText;
        consoleMode.ForeColor = Color.FromArgb(175, 189, 224);
        search.BackColor = Color.FromArgb(40, 51, 76);
        search.ForeColor = DeskStyle.ConsoleText;
        follow.ForeColor = DeskStyle.ConsoleText;
        Place(bar, 0, title);
        Place(bar, 1, consoleMode);
        Place(bar, 3, search); Place(bar, 4, find); Place(bar, 5, follow, false);
        tips.SetToolTip(search, "输入关键字，在当前日志中查找（Ctrl+F）");

        var body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(14, 11, 8, 9) };
        emptyConsole.BackColor = DeskStyle.Console;
        emptyTitle.Dock = emptyTip.Dock = DockStyle.Fill;
        emptyTitle.TextAlign = emptyTip.TextAlign = ContentAlignment.MiddleCenter;
        emptyTitle.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
        emptyTitle.ForeColor = DeskStyle.ConsoleText;
        emptyTip.ForeColor = Color.FromArgb(175, 189, 224);
        emptyConsole.Controls.Add(emptyTitle, 0, 1);
        emptyConsole.Controls.Add(emptyTip, 0, 2);
        body.Controls.Add(emptyConsole);
        body.Controls.Add(terminal);
        shell.Controls.Add(bar, 0, 0); shell.Controls.Add(body, 0, 1);
        return shell;
    }

    private Control BuildSend()
    {
        var cards = Inline(-1, 12, 320);
        var manual = Stack(26, 74, -1);
        var manualHead = Inline(78, -1);
        Place(manualHead, 0, Title("手动发送"));
        Place(manualHead, 1, tx, false);
        var input = Inline(-1, 67, 80, 86);
        entry.Dock = DockStyle.Fill;
        entry.Margin = new Padding(0, 5, 9, 5);
        input.Controls.Add(entry, 0, 0);
        Place(input, 1, suffix); Place(input, 2, send); Place(input, 3, enter, false);
        note.AutoEllipsis = true;
        manual.Controls.Add(manualHead, 0, 0);
        manual.Controls.Add(input, 0, 1);
        manual.Controls.Add(note, 0, 2);
        tips.SetToolTip(entry, "输入一条命令，长命令会自动折行显示；按 Enter 或点发送。");
        tips.SetToolTip(suffix, "发送文本时附加的行尾");
        tips.SetToolTip(enter, "只发送一个 CR（回车）");

        var quick = Stack(26, 74, -1);
        var selection = Inline(-1, 73);
        Place(selection, 0, presets);
        Place(selection, 1, presetSend, false);
        quick.Controls.Add(Title("只读预设"), 0, 0);
        quick.Controls.Add(selection, 0, 1);
        quick.Controls.Add(Caption("G1 系统取证 / G2 有线排查", 8.5f), 0, 2);
        cards.Controls.Add(Card(manual, new Padding(14, 8, 14, 7), new Padding(0)), 0, 0);
        cards.Controls.Add(Card(quick, new Padding(14, 8, 14, 7), new Padding(0)), 2, 0);
        return cards;
    }

    private Control BuildFooter()
    {
        var footer = Inline(-1, 126);
        footer.Padding = new Padding(0, 5, 0, 0);
        totals.Font = new Font("Consolas", 10f);
        Place(footer, 0, totals);
        Place(footer, 1, folderButton, false);
        return footer;
    }

    private static string CaptureRoot()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (String.IsNullOrWhiteSpace(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(folder, "HLK-SerialDesk", "Sessions");
    }

    private static ComboBox Choice(string label)
    {
        return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 274,
            IntegralHeight = true, MaxDropDownItems = 14, AccessibleName = label, ForeColor = DeskStyle.Ink };
    }
    private static TextBox Input(string label, int limit)
    {
        return new TextBox { MaxLength = limit, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = label, ForeColor = DeskStyle.Ink };
    }
    private static TextBox DraftInput()
    {
        var draft = Input("发送文本", 512);
        draft.Multiline = true;
        draft.WordWrap = true;
        draft.ScrollBars = ScrollBars.Vertical;
        draft.Font = new Font("Consolas", 13.5f);
        return draft;
    }
    private static Label LabelText()
    {
        return new Label { Dock = DockStyle.Fill, ForeColor = DeskStyle.Muted, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Margin = new Padding(0) };
    }
    private static Label Caption(string text, float size = 9f)
    {
        var c = LabelText(); c.Text = text;
        if (size != 9f) c.Font = new Font("Microsoft YaHei UI", size);
        return c;
    }
    private static Label Title(string text, float size = 10.5f)
    {
        var title = Caption(text);
        title.ForeColor = DeskStyle.Ink;
        title.Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold);
        return title;
    }
    private static TableLayoutPanel Stack(params int[] heights)
    {
        var box = new TableLayoutPanel { ColumnCount = 1, RowCount = heights.Length, Dock = DockStyle.Fill,
            Margin = new Padding(0), GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var value in heights) box.RowStyles.Add(value < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, value));
        return box;
    }
    private static TableLayoutPanel Inline(params int[] widths)
    {
        var box = new TableLayoutPanel { ColumnCount = widths.Length, RowCount = 1, Dock = DockStyle.Fill,
            Margin = new Padding(0), GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var value in widths) box.ColumnStyles.Add(value < 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, value));
        return box;
    }
    private static void Place(TableLayoutPanel bar, int col, Control item, bool gap = true)
    {
        if (!(item is Label) && !(item is CheckBox))
        {
            item.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            item.Margin = new Padding(0, 0, gap ? 7 : 0, 0);
            if (item is Button) item.Height = 34;
            if (item is DeskPill) item.Height = 27;
        }
        else if (item is CheckBox)
        {
            item.Anchor = AnchorStyles.Left; item.Margin = new Padding(0, 0, 0, 0);
        }
        bar.Controls.Add(item, col, 0);
    }
    private static Control Card(Control layout, Padding padding, Padding margin)
    {
        var panel = new DeskCard { Dock = DockStyle.Fill, Padding = padding, Margin = margin, BackColor = DeskStyle.Surface };
        panel.Controls.Add(layout);
        return panel;
    }
    private sealed class DeskCard : Panel
    {
        public DeskCard() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(DeskStyle.Paper);
            if (Width < 14 || Height < 14) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            const int d = 12;
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(DeskStyle.Surface))
            using (var edge = new Pen(DeskStyle.Border))
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(Width - d - 1, 0, d, d, 270, 90);
                path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
                path.AddArc(0, Height - d - 1, d, d, 90, 90);
                path.CloseFigure();
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }
        }
    }


    private void RefreshPorts()
    {
        if (refreshing) return;
        refreshing = true; faulted = false; UpdateControls();
        ThreadPool.QueueUserWorkItem(_ => {
            PortOption[] options;
            try { options = PortOption.Discover(); }
            catch (Exception ex) { inbox.Enqueue(new Notice("ports-error", ex.Message)); return; }
            try { BeginInvoke(new Action(() => AssignPorts(options))); }
            catch (InvalidOperationException) { /* Window is already closed. */ }
        });
    }
    private void AssignPorts(PortOption[] options)
    {
        refreshing = false;
        string prior = port.SelectedItem is PortOption ? ((PortOption)port.SelectedItem).Name : null;
        port.Items.Clear();
        port.Items.AddRange(options);
        int selected = Array.FindIndex(options, x => x.Name == prior);
        int count = Array.FindAll(options, x => x.IsCh340).Length;
        if (selected < 0 && count == 1) selected = Array.FindIndex(options, x => x.IsCh340);
        port.SelectedIndex = selected;
        if (live == null && !replaying && !state.Text.StartsWith("离线", StringComparison.Ordinal))
            state.Text = count == 1 ? "找到 CH340 候选，请确认接的是 HLK 的 USB1，再开始监听。" :
                count > 1 ? "发现多个 CH340，请拔插目标 USB 线并点“找串口”，核对新增端口。" :
                "未标出 CH340。换一根数据线、检查 USB1 和 CH341SER，再点“找串口”。外接转换器可手选。";
        UpdateControls();
    }

    private void Connect()
    {
        var choice = port.SelectedItem as PortOption;
        if (choice == null || live != null || replaying || refreshing) return;
        opening = true; faulted = false;
        tx.Checked = false;
        live = new LiveSerial(outputRoot, choice.Name, Int32.Parse((string)baud.SelectedItem), inbox);
        state.Text = "正在打开 " + choice.Name + "…";
        UpdateControls();
        live.Start();
    }
    private void Disconnect()
    {
        if (live == null || closing) return;
        closing = true;
        tx.Checked = false;
        live.Stop();
        state.Text = "正在断开并封存会话…";
        UpdateControls();
    }
    private bool CanSend() { return live != null && !opening && !closing && tx.Checked && !markers.InBootloader && !sendPending; }
    private void SendText()
    {
        if (!CanSend()) return;
        string line = entry.Text.TrimEnd('\r', '\n');
        if (String.IsNullOrWhiteSpace(line))
        {
            SetTxFeedback("请输入一条命令；只发回车请点「发回车」。", true);
            entry.Focus();
            UpdateControls();
            return;
        }
        var endings = new string[] { "\r", "\n", "\r\n", "" };
        SendPayload(line, endings[suffix.SelectedIndex], true);
    }
    private void SendPreset()
    {
        var command = presets.SelectedItem as Command;
        if (command != null && markers.Shell) SendPayload(command.Value, "\r");
    }
    private bool SendPayload(string line, string ending, bool trackDraft = false)
    {
        if (!CanSend()) return false;
        foreach (var ch in line)
        {
            if (!Char.IsControl(ch)) continue;
            SetTxFeedback("每次只发一条命令；请去掉中间的换行或制表符。", true);
            UpdateControls();
            return false;
        }
        if (!live.Send(Encoding.UTF8.GetBytes(line + ending)))
        {
            SetTxFeedback("串口未接受发送请求，内容已保留。", true);
            UpdateControls();
            return false;
        }
        sendPending = true;
        draftAwaitingAck = trackDraft ? entry.Text : null;
        sentDraftVersion = draftVersion;
        SetTxFeedback("正在写入串口…", false);
        if (trackDraft) entry.Focus();
        UpdateControls();
        return true;
    }

    private void SetTxFeedback(string text, bool problem)
    {
        txFeedback = text;
        txProblem = problem;
    }

    private void CompleteSend(Notice notice)
    {
        bool wrote = notice.Kind == "tx";
        sendPending = false;
        if (wrote)
        {
            txBytes += notice.Count;
            if (draftAwaitingAck != null && sentDraftVersion == draftVersion &&
                String.Equals(entry.Text, draftAwaitingAck, StringComparison.Ordinal)) entry.Clear();
            SetTxFeedback("已写入串口 " + notice.Count + " 字节，板端回显请看上方。", false);
        }
        else
        {
            SetTxFeedback(notice.Text, true);
        }
        draftAwaitingAck = null;
    }

    private void UpdateControls()
    {
        bool idle = live == null && !replaying;
        bool active = live != null && !opening && !closing;
        port.Enabled = baud.Enabled = idle && !refreshing;
        refresh.Enabled = idle && !refreshing;
        connect.Enabled = idle && !refreshing && port.SelectedItem != null;
        disconnect.Enabled = live != null && !closing;
        replay.Enabled = idle;
        tx.Enabled = active && !markers.InBootloader;
        bool writing = CanSend();
        send.Enabled = enter.Enabled = writing;
        send.Text = sendPending ? "发送中…" : "发送";
        presets.Enabled = presetSend.Enabled = writing && markers.Shell;
        string guidance = markers.InBootloader && active ? "U-Boot 期间锁定发送，等待系统启动。" :
            !active ? (offlineView ? "离线回放不连接串口，发送已锁定。" : "连接板子后可手动启用发送。") :
            !tx.Checked ? "当前只收。确认系统已启动后，勾选「启用发送」。" :
            !markers.Shell ? "可先发回车；看到 /# 后可用只读预设。" :
            "每次发送一条命令。";
        string feedback = txFeedback.Length == 0 ? guidance : txFeedback;
        if (txFeedback.Length != 0 && !sendPending && !writing)
            feedback += "  " + guidance;
        if (note.Text != feedback)
        {
            note.Text = feedback;
            tips.SetToolTip(note, feedback);
        }
        note.ForeColor = txProblem ? CrimsonInk : DeskStyle.Muted;
        UpdateDisplay(active);
    }

    private void UpdateDisplay(bool active)
    {
        if (opening) badge.Set("正在连接", Lilac, VioletInk);
        else if (closing) badge.Set("封存会话中", Lilac, VioletInk);
        else if (replaying) badge.Set("正在回放", Lilac, VioletInk);
        else if (faulted) badge.Set("会话异常", CrimsonBack, CrimsonInk);
        else if (active && markers.InBootloader) badge.Set("启动中 · 只收", OrangeBack, OrangeInk);
        else if (active && tx.Checked) badge.Set("收发已启用", OrangeBack, OrangeInk);
        else if (active) badge.Set("监听中 · 只收", GreenBack, GreenInk);
        else if (offlineView) badge.Set("离线回放", Lilac, VioletInk);
        else badge.Set("等待连接", GreyBack, GreyInk);

        SetPhase(phaseBoot, "U-Boot", markers.UBoot);
        SetPhase(phaseKernel, "Linux 内核", markers.Kernel);
        SetPhase(phaseInit, "init 完成", markers.Init);
        SetPhase(phaseShell, "Shell 提示符", markers.Shell);
        boot.AccessibleDescription = markers.Label();
        consoleMode.Text = offlineView ? "／  离线回放" : active ? "／  实时" : "／  串口终端";
        bool empty = terminal.TextLength == 0;
        terminal.Visible = !empty;
        emptyConsole.Visible = empty;
        if (empty)
        {
            emptyTitle.Text = replaying ? "正在读取日志" : active ? "正在等待串口数据" : "还没有接收日志";
            emptyTip.Text = replaying ? "历史日志将在这里显示。" : active ?
                "开始监听后给板子上电，可以保留最早的启动输出。" :
                "连接 CH340 开始监听，或使用「离线回放」打开历史日志。";
        }
        string counts = String.Format("RX  {0:N0} B      TX  {1:N0} B{2}", rxBytes, txBytes,
            tailClipped ? "     ·     窗口仅显示末段，完整数据见文件" : "");
        if (!String.Equals(totals.Text, counts, StringComparison.Ordinal)) totals.Text = counts;
        if (tips.GetToolTip(state) != state.Text) tips.SetToolTip(state, state.Text);
    }

    private static void SetPhase(DeskPill pill, string name, bool detected)
    {
        pill.Set(detected ? "✓  " + name : name, detected ? GreenBack : GreyBack, detected ? GreenInk : GreyInk);
    }

    private void ResetView()
    {
        terminal.Clear(); markers = new BootMarkers(); rxBytes = txBytes = 0; tailClipped = false;
    }
    private void Append(string value)
    {
        if (value.Length == 0) return;
        terminal.Visible = true;
        emptyConsole.Visible = false;
        int selection = terminal.SelectionStart, selected = terminal.SelectionLength;
        terminal.AppendText(value);
        int excess = Math.Max(0, terminal.TextLength - TailLimit);
        if (excess > 0)
        {
            terminal.Select(0, excess);
            terminal.SelectedText = "";
            tailClipped = true;
        }
        if (follow.Checked) { terminal.Select(terminal.TextLength, 0); terminal.ScrollToCaret(); }
        else terminal.Select(Math.Max(0, selection - excess), selected);
    }

    private void Drain()
    {
        Notice n;
        if (!inbox.TryDequeue(out n)) return;
        var chunk = new StringBuilder();
        for (int k = 0; ; )
        {
            switch (n.Kind)
            {
                case "open":
                    chunk.Clear();
                    opening = false; offlineView = false; ResetView(); currentFolder = n.Folder;
                    SetTxFeedback("", false);
                    if (!closing) state.Text = n.Text + " 已连接 · 正在记录，默认只收";
                    break;
                case "rx":
                    rxBytes += n.Count;
                    markers.Feed(n.Text);
                    if (markers.InBootloader && tx.Checked) tx.Checked = false;
                    chunk.Append(n.Text);
                    break;
                case "tx":
                case "tx-cancel":
                case "tx-error":
                    CompleteSend(n); break;
                case "closed":
                case "failed":
                    if (sendPending)
                    {
                        SetTxFeedback("会话已结束，发送结果未确认，输入内容已保留。", true);
                        draftAwaitingAck = null;
                    }
                    live = null; opening = closing = sendPending = false; tx.Checked = false;
                    faulted = n.Kind == "failed";
                    if (n.Folder != null) currentFolder = n.Folder;
                    state.Text = n.Text;
                    break;
                case "ports-error":
                    refreshing = false;
                    if (live == null && !replaying) { faulted = true; state.Text = "读取 COM 列表失败：" + n.Text; }
                    break;
                case "offline-rx":
                    rxBytes += n.Count; markers.Feed(n.Text); chunk.Append(n.Text); break;
                case "offline-done":
                    replaying = false; faulted = n.Text.StartsWith("离线回放失败", StringComparison.Ordinal); state.Text = n.Text; break;
            }
            if (++k >= 256 || !inbox.TryDequeue(out n)) break;
        }
        Append(chunk.ToString());
        UpdateControls();
        if (exitWhenDone && live == null) Close();
    }

    private void ChooseReplay()
    {
        using (var file = new OpenFileDialog { Title = "选择历史串口日志", Filter = "串口日志 (*.raw;*.txt)|*.raw;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true, InitialDirectory = Path.GetDirectoryName(outputRoot) })
            if (file.ShowDialog(this) == DialogResult.OK) ReplayFile(file.FileName);
    }
    private void ReplayFile(string file)
    {
        if (live != null || replaying) return;
        replaying = true; offlineView = true; faulted = false; ResetView();
        SetTxFeedback("", false);
        state.Text = "离线回放 · " + Path.GetFileName(file);
        UpdateControls();
        ThreadPool.QueueUserWorkItem(_ => {
            try
            {
                var decoder = new SerialText();
                var bytes = new byte[8192];
                using (var input = File.OpenRead(file))
                {
                    int n;
                    while ((n = input.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        while (inbox.Count >= 16) Thread.Sleep(25);
                        inbox.Enqueue(new Notice("offline-rx", decoder.Feed(bytes, n), n));
                    }
                    inbox.Enqueue(new Notice("offline-rx", decoder.Finish()));
                }
                inbox.Enqueue(new Notice("offline-done", "离线回放完成 · " + Path.GetFileName(file)));
            }
            catch (Exception ex) { inbox.Enqueue(new Notice("offline-done", "离线回放失败：" + ex.Message)); }
        });
    }

    private void FindText()
    {
        if (search.Text.Length == 0) return;
        int start = Math.Min(terminal.TextLength, terminal.SelectionStart + terminal.SelectionLength);
        string text = terminal.Text;
        int index = text.IndexOf(search.Text, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { System.Media.SystemSounds.Asterisk.Play(); return; }
        follow.Checked = false;
        terminal.Select(index, search.Text.Length); terminal.ScrollToCaret(); terminal.Focus();
    }
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(currentFolder);
            Process.Start(new ProcessStartInfo { FileName = currentFolder, UseShellExecute = true });
        }
        catch (Exception ex) { state.Text = "无法打开会话目录：" + ex.Message; }
    }
}
