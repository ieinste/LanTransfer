using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LanTransfer
{
    public sealed class MainForm : Form
    {
        private static readonly Color Ink = UiTheme.Ink, Muted = UiTheme.Muted;
        private static readonly Color Accent = UiTheme.Accent, Teal = UiTheme.Success, Border = UiTheme.Border;
        private readonly TextBox folder = new TextBox(), receiveCode = new TextBox(), sendCode = new TextBox();
        private readonly ComboBox host = new ComboBox(), addresses = new ComboBox();
        private readonly NumericUpDown receivePort = new NumericUpDown(), sendPort = new NumericUpDown();
        private readonly FileQueue queue = new FileQueue();
        private readonly DropSurface drop = new DropSurface();
        private readonly TextBox logs = new TextBox();
        private readonly Label receiveState = new Label(), sendState = new Label(), details = new Label(), queueCount = new Label();
        private readonly Label receiveBadge = new Label(), activityCount = new Label(), logEmpty = new Label();
        private readonly TransferProgress bar = new TransferProgress();
        private readonly Button listen = Button("开始接收"), send = Button("开始发送"), pause = Button("暂停"), search = Button("搜索设备");
        private readonly Button chooseFolder = Button("浏览"), remove = Button("移除"), clear = Button("清空");
        private readonly LinkLabel newCode = Link("换一个"), copyAddress = Link("复制地址");
        private readonly List<Control> queueControls = new List<Control>();
        private readonly ToolTip tips = new ToolTip();
        private TransferServer server;
        private TransferClient sender;
        private readonly Stopwatch progressClock = Stopwatch.StartNew(), speedClock = new Stopwatch();
        private readonly Stopwatch receiveClock = Stopwatch.StartNew();
        private readonly object progressGate = new object();
        private string speedFile = "", speedStage = "";
        private long previousDone;
        private int receiveSession, logCount;
        private bool searching;
        private volatile bool closing;

        public MainForm()
        {
            Text = "轻传 · 局域网文件互传";
            Font = UiTheme.BodyFont();
            BackColor = UiTheme.Canvas; ForeColor = Ink;
            ClientSize = new Size(1120, 748); MinimumSize = new Size(880, 680);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Font;
            var root = Table(4); root.BackColor = BackColor; root.Padding = new Padding(24, 18, 24, 12); root.Name = "Workspace";
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);

            var header = Grid(2); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            var brand = Table(2); brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            brand.Controls.Add(TextLabel("轻传", 22F, FontStyle.Bold, Ink), 0, 0);
            brand.Controls.Add(TextLabel("同一网络，轻松传递文件与文件夹。", 9F, FontStyle.Regular, Muted), 0, 1);
            header.Controls.Add(brand, 0, 0);
            var badge = TextLabel("●  局域网直传 · 无需联网", 9F, FontStyle.Regular, Accent); badge.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(badge, 1, 0); root.Controls.Add(header, 0, 0);

            var workspace = Grid(2); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            var receiveCard = new CardSurface { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 16), Margin = new Padding(0, 0, 16, 16) };
            var sendingCard = new CardSurface { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 16), Margin = new Padding(0, 0, 0, 16) };
            workspace.Controls.Add(receiveCard, 0, 0); workspace.Controls.Add(sendingCard, 1, 0); root.Controls.Add(workspace, 0, 1);

            var receiving = Table(8);
            foreach (int height in new[] { 52, 64, 64, 64 }) receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            receiving.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            receiveCard.Controls.Add(receiving);
            receiving.Controls.Add(SectionTitle("接收文件", "保存到这台设备"), 0, 0);
            folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "轻传接收"); StyleInput(folder);
            receiving.Controls.Add(Field("保存位置", InputAction(folder, chooseFolder, 62)), 0, 1);
            var credentials = Grid(2); credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); credentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            receiveCode.Text = Transfer.NewCode(); receiveCode.MaxLength = 64; StyleInput(receiveCode);
            receiveCode.Font = new Font("Consolas", 11F, FontStyle.Bold); receiveCode.ForeColor = Accent;
            ConfigurePort(receivePort, 1024); ConfigurePort(sendPort, 1);
            var codeField = Field("接收码", receiveCode, newCode);
            credentials.Controls.Add(codeField, 0, 0);
            var portField = Field("接收端口", receivePort); portField.Margin = new Padding(10, 0, 0, 6); credentials.Controls.Add(portField, 1, 0);
            receiving.Controls.Add(credentials, 0, 2);
            addresses.DropDownStyle = ComboBoxStyle.DropDownList; addresses.Dock = DockStyle.Fill; addresses.FlatStyle = FlatStyle.Standard;
            var addressField = Field("本机 IPv4", addresses, copyAddress);
            receiving.Controls.Add(addressField, 0, 3); RefreshAddresses();
            var receiveHint = TextLabel("开启接收后分享连接信息。", 9F, FontStyle.Regular, Muted);
            tips.SetToolTip(receiveHint, "先开启接收，再把地址、端口和接收码告诉对方。双方需连接同一局域网。");
            receiving.Controls.Add(receiveHint, 0, 4);
            Primary(listen); listen.Dock = DockStyle.Fill; listen.Margin = new Padding(0, 3, 0, 3); receiving.Controls.Add(listen, 0, 5);
            var receiveStatus = Table(2); receiveStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); receiveStatus.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            receiveBadge.Text = "接收未开启"; receiveBadge.ForeColor = Ink; receiveBadge.Font = new Font(Font, FontStyle.Bold); receiveBadge.Dock = DockStyle.Fill;
            receiveState.Text = "开启后等待对方连接"; receiveState.ForeColor = Muted; receiveState.Font = UiTheme.SmallFont(); receiveState.Dock = DockStyle.Fill; receiveState.AutoEllipsis = true;
            receiveStatus.Controls.Add(receiveBadge, 0, 0); receiveStatus.Controls.Add(receiveState, 0, 1); receiving.Controls.Add(receiveStatus, 0, 6);

            var sending = Table(7);
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            sending.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); sendingCard.Controls.Add(sending);
            var sendTitle = Grid(2); sendTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sendTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            sendTitle.Controls.Add(SectionTitle("发送文件", "发往另一台设备"), 0, 0);
            queueCount.Dock = DockStyle.Fill; queueCount.TextAlign = ContentAlignment.TopRight; queueCount.ForeColor = Muted; queueCount.Padding = new Padding(0, 5, 0, 0);
            sendTitle.Controls.Add(queueCount, 1, 0); sending.Controls.Add(sendTitle, 0, 0);
            var files = Button("+ 添加文件"); var dirs = Button("+ 添加文件夹");
            files.Width = 102; dirs.Width = 118; remove.Width = 68; clear.Width = 68;
            sending.Controls.Add(Flow(files, dirs, remove, clear), 0, 1);
            queue.Dock = DockStyle.Fill; queue.BorderStyle = BorderStyle.None; queue.IntegralHeight = false; queue.AllowDrop = true;
            drop.Dock = DockStyle.Fill; drop.Margin = new Padding(0, 0, 0, 8); drop.Padding = new Padding(4); drop.AllowDrop = true;
            drop.Controls.Add(queue); sending.Controls.Add(drop, 0, 2);
            queueControls.AddRange(new Control[] { files, dirs, remove, clear, queue });
            files.Click += delegate { Try(delegate { using (var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" }) if (dialog.ShowDialog(this) == DialogResult.OK) foreach (string path in dialog.FileNames) AddPath(path); }); };
            dirs.Click += delegate { Try(delegate { using (var dialog = new FolderBrowserDialog { Description = "选择要发送的整个文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) AddPath(dialog.SelectedPath); }); };
            remove.Click += delegate { if (queue.SelectedIndex >= 0) { int index = queue.SelectedIndex; queue.RemovePath(index); if (queue.Items.Count > 0) queue.SelectedIndex = Math.Min(index, queue.Items.Count - 1); UpdateQueue(); } };
            clear.Click += delegate { queue.ClearPaths(); UpdateQueue(); };
            queue.SelectedIndexChanged += delegate { UpdateQueue(); };
            string queueTip = "";
            queue.MouseMove += delegate(object source, MouseEventArgs e) { int index = queue.IndexFromPoint(e.Location); string path = index >= 0 ? (string)queue.Items[index] : ""; if (path != queueTip) { queueTip = path; tips.SetToolTip(queue, path); } };
            queue.DragEnter += QueueDragEnter; drop.DragEnter += QueueDragEnter;
            queue.DragDrop += QueueDragDrop; drop.DragDrop += QueueDragDrop;

            var target = Grid(3); target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); target.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); target.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            host.Dock = DockStyle.Fill; host.DropDownStyle = ComboBoxStyle.DropDown; host.FlatStyle = FlatStyle.Standard;
            host.SelectionChangeCommitted += delegate { Try(delegate { SelectPeer(host.Text); }); };
            target.Controls.Add(Field("对方 IPv4", InputAction(host, search, 86)), 0, 0);
            var targetPort = Field("端口", sendPort); targetPort.Margin = new Padding(10, 0, 0, 6); target.Controls.Add(targetPort, 1, 0);
            StyleInput(sendCode); sendCode.MaxLength = 64; sendCode.Font = new Font("Consolas", 11F, FontStyle.Bold); sendCode.ForeColor = Accent;
            var targetCode = Field("对方接收码", sendCode); targetCode.Margin = new Padding(10, 0, 0, 6); target.Controls.Add(targetCode, 2, 0); sending.Controls.Add(target, 0, 3);
            var actions = Grid(3); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Primary(send); send.Dock = DockStyle.Fill; send.Margin = new Padding(0, 3, 8, 5); pause.Dock = DockStyle.Fill; pause.Margin = new Padding(0, 3, 8, 5); pause.Enabled = false;
            actions.Controls.Add(send, 0, 0); actions.Controls.Add(pause, 1, 0);
            var resume = TextLabel("支持断点续传", 9F, FontStyle.Regular, Muted); resume.TextAlign = ContentAlignment.MiddleRight; actions.Controls.Add(resume, 2, 0); sending.Controls.Add(actions, 0, 4);
            bar.Dock = DockStyle.Fill; bar.Margin = new Padding(0, 3, 0, 5); sending.Controls.Add(bar, 0, 5);
            var status = Table(2); status.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); status.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sendState.Text = "准备就绪"; sendState.Dock = DockStyle.Fill; sendState.AutoEllipsis = true; sendState.Font = new Font(Font, FontStyle.Bold);
            details.Text = "添加文件，填写对方地址和接收码即可发送。"; details.Dock = DockStyle.Fill; details.AutoEllipsis = true; details.ForeColor = Muted; details.Font = UiTheme.SmallFont();
            status.Controls.Add(sendState, 0, 0); status.Controls.Add(details, 0, 1); sending.Controls.Add(status, 0, 6);

            var logCard = new CardSurface { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), Margin = new Padding(0, 0, 0, 4) };
            var logLayout = Table(2); logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logHeader = Grid(3); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            logHeader.Controls.Add(TextLabel("活动记录", 9F, FontStyle.Bold, Ink), 0, 0);
            activityCount.Text = "暂无记录"; activityCount.ForeColor = Muted; activityCount.Dock = DockStyle.Fill; logHeader.Controls.Add(activityCount, 1, 0);
            var clearLogs = Link("清空记录"); clearLogs.Dock = DockStyle.Fill; logHeader.Controls.Add(clearLogs, 2, 0);
            clearLogs.LinkClicked += delegate { logs.Clear(); logCount = 0; activityCount.Text = "暂无记录"; logs.Visible = false; logEmpty.Visible = true; };
            logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.Dock = DockStyle.Fill; logs.BackColor = UiTheme.Surface; logs.ForeColor = Muted; logs.BorderStyle = BorderStyle.None;
            var logContent = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiTheme.Surface };
            logEmpty.Text = "还没有传输记录，开始收发后会显示在这里。"; logEmpty.Dock = DockStyle.Fill; logEmpty.TextAlign = ContentAlignment.MiddleLeft; logEmpty.ForeColor = Muted; logEmpty.Font = UiTheme.SmallFont();
            logContent.Controls.Add(logs); logContent.Controls.Add(logEmpty); logs.Visible = false;
            logLayout.Controls.Add(logHeader, 0, 0); logLayout.Controls.Add(logContent, 0, 1); logCard.Controls.Add(logLayout); root.Controls.Add(logCard, 0, 2);
            var footer = TextLabel("v0.2.1  ·  SHA-256 完整校验  ·  同名冲突不覆盖  ·  仅用于可信局域网，文件内容未加密", 8.5F, FontStyle.Regular, Muted);
            footer.TextAlign = ContentAlignment.MiddleLeft; root.Controls.Add(footer, 0, 3);

            chooseFolder.Click += delegate { Try(delegate { using (var dialog = new FolderBrowserDialog { Description = "选择接收目录", SelectedPath = folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; }); };
            newCode.LinkClicked += delegate { receiveCode.Text = Transfer.NewCode(); };
            copyAddress.LinkClicked += delegate { Try(delegate { if (addresses.SelectedItem == null) throw new IOException("未找到可用的本机 IPv4 地址，请检查网络连接。"); Clipboard.SetText(addresses.Text + ":" + receivePort.Value); Log("已复制地址和端口；接收码请单独告诉对方。"); }); };
            listen.Click += delegate { Try(ToggleReceiver); }; send.Click += delegate { Try(StartSend); };
            pause.Click += delegate { if (sender != null) { sender.Cancel(); pause.Enabled = false; sendState.Text = "正在暂停…"; Log("已请求暂停，接收方会保留未完成数据。"); } };
            search.Click += delegate { Search(); };
            tips.SetToolTip(folder, "文件将保存到此目录；中断后保留 .lantransfer-cache 可续传。");
            tips.SetToolTip(receiveCode, "至少 6 位。仅在接收关闭时可以修改。");
            tips.SetToolTip(host, "可手动输入 IPv4 或 IPv4:端口，也可搜索已开启接收的设备。");
            tips.SetToolTip(addresses, "多块网卡时，请选择与对方处于同一网段的地址。");
            tips.SetToolTip(search, "搜索同一局域网内已开启接收的设备。");
            folder.AccessibleName = "接收保存位置"; receiveCode.AccessibleName = "本机接收码"; receivePort.AccessibleName = "接收端口";
            addresses.AccessibleName = "本机 IPv4 地址"; host.AccessibleName = "对方 IPv4 地址"; sendPort.AccessibleName = "对方端口"; sendCode.AccessibleName = "对方接收码";
            queue.AccessibleName = "待发送文件和文件夹"; logs.AccessibleName = "传输活动记录";
            UpdateQueue();
            Shown += delegate { Rectangle work = Screen.FromControl(this).WorkingArea; if (Height > work.Height) Height = Math.Max(MinimumSize.Height, work.Height); if (Width > work.Width) Width = Math.Max(MinimumSize.Width, work.Width); };
            FormClosing += delegate { closing = true; receiveSession++; if (sender != null) sender.Cancel(); if (server != null) server.Dispose(); };
        }

        private static Button Button(string text)
        {
            return new UiButton { Text = text, Height = 36, Width = 90, Margin = new Padding(0, 0, 8, 0) };
        }
        private static LinkLabel Link(string text)
        { return new LinkLabel { Text = text, AutoSize = false, LinkColor = Accent, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0), TabStop = true }; }
        private static void Primary(Button button)
        { ((UiButton)button).IsPrimary = true; button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold); }
        private static Label TextLabel(string text, float size, FontStyle style, Color color)
        { return new Label { Text = text, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, Margin = new Padding(0), AutoEllipsis = true }; }
        private static TableLayoutPanel SectionTitle(string title, string subtitle)
        { var table = Table(2); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); table.Controls.Add(TextLabel(title, 14F, FontStyle.Bold, Ink), 0, 0); table.Controls.Add(TextLabel(subtitle, 8.5F, FontStyle.Regular, Muted), 0, 1); return table; }
        private static TableLayoutPanel Table(int rows)
        { var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = rows, ColumnCount = 1, Margin = new Padding(0) }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return table; }
        private static TableLayoutPanel Grid(int columns)
        { var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = columns, Margin = new Padding(0) }; table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return table; }
        private static FlowLayoutPanel Flow(params Control[] controls)
        { var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) }; flow.Controls.AddRange(controls); return flow; }
        private static TableLayoutPanel Field(string caption, Control input, LinkLabel action = null)
        {
            var field = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); field.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); field.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            var label = TextLabel(caption, 9F, FontStyle.Regular, Muted); label.TextAlign = ContentAlignment.MiddleLeft; field.Controls.Add(label, 0, 0);
            if (action == null) field.SetColumnSpan(label, 2); else { field.Controls.Add(action, 1, 0); action.Dock = DockStyle.Fill; }
            Control surface = input is TextBox || input is ComboBox || input is NumericUpDown ? new InputSurface(input) : input;
            field.Controls.Add(surface, 0, 1); field.SetColumnSpan(surface, 2); surface.Dock = DockStyle.Fill; surface.Margin = new Padding(0); return field;
        }
        private static TableLayoutPanel InputAction(Control input, Button action, int width)
        { var table = Grid(2); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width)); var surface = new InputSurface(input) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }; table.Controls.Add(surface, 0, 0); table.Controls.Add(action, 1, 0); action.Dock = DockStyle.Fill; action.Margin = new Padding(0); return table; }
        private static void StyleInput(TextBox input)
        { input.BorderStyle = BorderStyle.None; input.Margin = new Padding(0); input.BackColor = UiTheme.Field; input.ForeColor = Ink; }
        private static void ConfigurePort(NumericUpDown input, int minimum)
        { input.Minimum = minimum; input.Maximum = 65535; input.Value = TransferServer.DefaultPort; input.BorderStyle = BorderStyle.None; input.Margin = new Padding(0); }
        private void Try(Action action)
        { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "轻传", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        private void UI(Action action)
        { if (closing || IsDisposed || !IsHandleCreated) return; try { if (InvokeRequired) BeginInvoke(action); else action(); } catch (InvalidOperationException) { } }
        private void Log(string message)
        { UI(delegate { logEmpty.Visible = false; logs.Visible = true; if (logs.TextLength > 24000) logs.Text = logs.Text.Substring(logs.TextLength - 12000); logs.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine); logCount++; activityCount.Text = logCount + " 条记录"; }); }
        private void RefreshAddresses()
        { string selected = addresses.Text; addresses.Items.Clear(); addresses.Items.AddRange(Transfer.LocalAddresses()); if (addresses.Items.Count > 0) { int index = addresses.Items.IndexOf(selected); addresses.SelectedIndex = index >= 0 ? index : 0; } copyAddress.Enabled = addresses.Items.Count > 0; }
        private void QueueDragEnter(object source, DragEventArgs e)
        { if (sender == null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; }
        private void QueueDragDrop(object source, DragEventArgs e)
        { if (sender == null) Try(delegate { foreach (string path in (string[])e.Data.GetData(DataFormats.FileDrop)) AddPath(path); }); }
        private void AddPath(string path)
        { path = Path.GetFullPath(path); bool directory = Directory.Exists(path); if (!directory && !File.Exists(path)) throw new IOException("文件或文件夹已不存在：" + path); foreach (object item in queue.Items) if (String.Equals((string)item, path, StringComparison.OrdinalIgnoreCase)) return; queue.AddPath(path, directory); UpdateQueue(); }
        private void UpdateQueue()
        { queueCount.Text = queue.Items.Count + " 项待发送"; queue.Visible = queue.Items.Count > 0; drop.Invalidate(); remove.Enabled = sender == null && queue.SelectedIndex >= 0; clear.Enabled = sender == null && queue.Items.Count > 0; }
        private void ToggleReceiver()
        {
            if (server == null)
            {
                if (!Path.IsPathRooted(folder.Text)) throw new IOException("请选择完整的接收目录。");
                int session = ++receiveSession;
                var next = new TransferServer(folder.Text, receiveCode.Text.Trim(), delegate(ProgressInfo p) { ReceiveProgress(session, p); }, delegate(string message) { Log(message); });
                try { next.Start((int)receivePort.Value, true); server = next; } catch { next.Dispose(); throw; }
                receivePort.Value = server.Port; listen.Text = "停止接收"; receiveBadge.Text = "接收已开启"; receiveBadge.ForeColor = Teal; receiveState.Text = "等待对方连接 · 端口 " + server.Port;
                folder.Enabled = chooseFolder.Enabled = receiveCode.Enabled = newCode.Enabled = receivePort.Enabled = false;
                RefreshAddresses(); Log("接收已开启，端口 " + server.Port + "，目录：" + folder.Text);
            }
            else
            {
                receiveSession++; server.Dispose(); server = null; listen.Text = "开始接收";
                receiveBadge.Text = "接收未开启"; receiveBadge.ForeColor = Ink; receiveState.Text = "未完成文件保留，可重新开启续传";
                folder.Enabled = chooseFolder.Enabled = receiveCode.Enabled = newCode.Enabled = receivePort.Enabled = true;
                Log("已停止接收；未完成文件保留，下次可续传。");
            }
        }
        private void SelectPeer(string peer)
        { string endpoint = peer.Split(' ')[0]; int colon = endpoint.LastIndexOf(':'); int port; if (colon > 0 && Int32.TryParse(endpoint.Substring(colon + 1), out port) && port >= 1 && port <= 65535) { sendPort.Value = port; host.Text = endpoint.Substring(0, colon); } }
        private void StartSend()
        {
            if (sender != null) return;
            if (queue.Items.Count == 0) throw new IOException("请先添加文件或文件夹。");
            string address = host.Text.Trim().Split(' ')[0]; int targetPort = (int)sendPort.Value;
            int colon = address.LastIndexOf(':');
            if (colon > 0) { int parsed; if (!Int32.TryParse(address.Substring(colon + 1), out parsed) || parsed < 1 || parsed > 65535) throw new IOException("对方端口应为 1–65535。"); targetPort = parsed; address = address.Substring(0, colon); }
            IPAddress ip; if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new IOException("请填写对方的 IPv4 地址，或先搜索设备。");
            if (sendCode.Text.Trim().Length < 6) throw new IOException("请填写对方的接收码（至少 6 位）。");
            string pin = sendCode.Text.Trim(); string[] paths = new string[queue.Items.Count]; queue.Items.CopyTo(paths, 0);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths) if (!names.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))) throw new IOException("列表包含同名文件或根目录，请分批发送。");
            sender = new TransferClient(); var current = sender;
            SetSending(true); bar.Value = 0; speedFile = ""; speedStage = ""; speedClock.Reset(); progressClock.Restart(); sendState.Text = "正在连接 " + address + "…"; details.Text = "请保持对方接收开启，首次连接请允许专用网络防火墙访问。";
            var thread = new Thread(delegate()
            {
                string result; bool complete = false;
                try { current.Send(address, targetPort, pin, paths, SendProgress, Log); complete = true; result = "发送结束：成功/跳过 " + current.Completed + " 个文件，失败 " + current.Failed + " 个。"; }
                catch (Exception ex) { result = "已中断或暂停，可重新发送续传：" + ex.Message; }
                finally { current.Dispose(); }
                Log(result);
                UI(delegate { sender = null; SetSending(false); sendState.Text = result; sendState.ForeColor = complete && current.Failed == 0 ? Teal : Ink; bar.Style = ProgressBarStyle.Continuous; if (complete && current.Failed == 0) bar.Value = 100; details.Text = "本次实际发送 " + Transfer.Size(current.PayloadBytes) + "  ·  已完成/跳过 " + current.Completed + " 个文件"; });
            }); thread.IsBackground = true; thread.Start();
        }
        private void SetSending(bool busy)
        { send.Enabled = !busy; pause.Enabled = busy; host.Enabled = sendCode.Enabled = sendPort.Enabled = !busy; search.Enabled = !busy && !searching; drop.AllowDrop = !busy; sendState.ForeColor = Ink; foreach (Control control in queueControls) control.Enabled = !busy; UpdateQueue(); }
        private void SendProgress(ProgressInfo p)
        {
            lock (progressGate) { if (progressClock.ElapsedMilliseconds < 120 && p.Stage != "文件完成") return; progressClock.Restart(); }
            UI(delegate
            {
                bool moving = p.Stage == "发送中"; double speed = 0;
                if (moving && speedFile == p.FileName && speedStage == p.Stage && speedClock.Elapsed.TotalSeconds > 0) speed = Math.Max(0, p.Done - previousDone) / speedClock.Elapsed.TotalSeconds;
                speedFile = p.FileName; speedStage = p.Stage; previousDone = p.Done; speedClock.Restart();
                bool hashing = p.Stage == "读取文件指纹" || p.Stage == "等待接收方校验" || p.Stage == "核对续传数据";
                bar.Style = hashing ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (!hashing) bar.Value = p.Length == 0 ? 100 : (int)Math.Min(100, p.Done * 100.0 / p.Length);
                sendState.Text = p.Stage + "：" + p.FileName; tips.SetToolTip(sendState, sendState.Text);
                string eta = speed > 0 ? "  ·  约剩 " + TimeSpan.FromSeconds(Math.Min(86400000, Math.Max(0, p.Length - p.Done) / speed)).ToString() : "";
                details.Text = Transfer.Size(p.Done) + " / " + Transfer.Size(p.Length) + "  ·  完成 " + p.Files + " 个" + (speed > 0 ? "  ·  " + Transfer.Size((long)speed) + "/s" + eta : ""); tips.SetToolTip(details, details.Text);
            });
        }
        private void ReceiveProgress(int session, ProgressInfo p)
        {
            if (session != receiveSession) return;
            if (receiveClock.ElapsedMilliseconds < 150 && p.Stage != "文件完成") return; receiveClock.Restart();
            UI(delegate { if (session != receiveSession || server == null) return; receiveState.Text = p.Stage + " · " + Transfer.Size(p.Done) + "/" + Transfer.Size(p.Length); tips.SetToolTip(receiveState, p.FileName + "  ·  " + receiveState.Text); });
        }
        private void Search()
        {
            if (searching || sender != null) return;
            searching = true; search.Enabled = false; search.Text = "搜索中…"; Log("正在搜索已开启接收的设备…");
            var thread = new Thread(delegate()
            {
                try
                {
                    string[] peers = Transfer.Discover();
                    UI(delegate { if (sender == null) { host.Items.Clear(); host.Items.AddRange(peers); if (peers.Length > 0) { host.SelectedIndex = 0; SelectPeer(peers[0]); if (peers.Length > 1) host.DroppedDown = true; } } Log(peers.Length == 0 ? "未找到设备，可直接填写对方 IP。请检查接收状态和防火墙。" : "找到 " + peers.Length + " 个接收地址，请选择设备并填写接收码。"); });
                }
                catch (Exception ex) { Log("搜索失败，可使用 IP 连接：" + ex.Message); }
                finally { UI(delegate { searching = false; search.Text = "搜索设备"; search.Enabled = sender == null; }); }
            }); thread.IsBackground = true; thread.Start();
        }
        protected override void Dispose(bool disposing)
        { if (disposing) tips.Dispose(); base.Dispose(disposing); }

        private sealed class DropSurface : Panel
        {
            public DropSurface() { BackColor = UiTheme.Field; DoubleBuffered = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = UiTheme.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 8F))
                using (var pen = new Pen(Border) { DashStyle = DashStyle.Dash }) e.Graphics.DrawPath(pen, path);
                if (Controls.Count == 0 || !Controls[0].Visible)
                {
                    bool roomy = Height >= 132;
                    int center = Height / 2;
                    if (roomy)
                    {
                        var tile = new Rectangle(Width / 2 - 21, center - 56, 42, 42);
                        using (var path = UiTheme.Rounded(tile, 10F))
                        using (var brush = new SolidBrush(UiTheme.AccentSoft)) e.Graphics.FillPath(brush, path);
                        DrawFileIcon(e.Graphics, new Rectangle(tile.X + 8, tile.Y + 8, 26, 26), true);
                    }
                    int top = roomy ? center - 5 : center - 24;
                    using (var font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, "拖放文件或文件夹到这里", font, new Rectangle(8, top, Math.Max(0, Width - 16), 26), Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, "或使用上方按钮添加，保留原始目录结构", Font, new Rectangle(8, top + 28, Math.Max(0, Width - 16), 22), Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
        }
        private static void DrawFileIcon(Graphics graphics, Rectangle bounds, bool directory)
        {
            using (var pen = new Pen(Accent, 1.5F) { LineJoin = LineJoin.Round })
            {
                float x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
                if (directory)
                    graphics.DrawPolygon(pen, new[] { new PointF(x + w * .08F, y + h * .24F), new PointF(x + w * .4F, y + h * .24F), new PointF(x + w * .5F, y + h * .36F), new PointF(x + w * .92F, y + h * .36F), new PointF(x + w * .92F, y + h * .82F), new PointF(x + w * .08F, y + h * .82F) });
                else
                {
                    graphics.DrawPolygon(pen, new[] { new PointF(x + w * .2F, y + h * .08F), new PointF(x + w * .62F, y + h * .08F), new PointF(x + w * .84F, y + h * .3F), new PointF(x + w * .84F, y + h * .92F), new PointF(x + w * .2F, y + h * .92F) });
                    graphics.DrawLines(pen, new[] { new PointF(x + w * .62F, y + h * .08F), new PointF(x + w * .62F, y + h * .3F), new PointF(x + w * .84F, y + h * .3F) });
                    graphics.DrawLine(pen, x + w * .34F, y + h * .53F, x + w * .68F, y + h * .53F);
                    graphics.DrawLine(pen, x + w * .34F, y + h * .7F, x + w * .62F, y + h * .7F);
                }
            }
        }
        private sealed class FileQueue : ListBox
        {
            private readonly HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public FileQueue() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 56; BackColor = UiTheme.Field; }
            public void AddPath(string path, bool directory) { if (directory) directories.Add(path); Items.Add(path); }
            public void RemovePath(int index) { directories.Remove((string)Items[index]); Items.RemoveAt(index); }
            public void ClearPaths() { directories.Clear(); Items.Clear(); }
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                string path = (string)Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(selected ? UiTheme.AccentSoft : UiTheme.Field)) e.Graphics.FillRectangle(brush, e.Bounds);
                var icon = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 9, 34, 34);
                using (var shape = UiTheme.Rounded(icon, 7F))
                using (var brush = new SolidBrush(UiTheme.AccentSoft)) e.Graphics.FillPath(brush, shape);
                DrawFileIcon(e.Graphics, new Rectangle(icon.X + 6, icon.Y + 6, 22, 22), directories.Contains(path));
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                TextRenderer.DrawText(e.Graphics, name.Length == 0 ? path : name, Font, new Rectangle(54, e.Bounds.Top + 6, Math.Max(0, e.Bounds.Width - 64), 22), Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics, path, Font, new Rectangle(54, e.Bounds.Top + 28, Math.Max(0, e.Bounds.Width - 64), 20), Muted, TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            }
        }
    }
}
