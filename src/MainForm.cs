using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LanTransfer
{
    public sealed class MainForm : Form
    {
        private readonly TextBox folder = new TextBox(), receiveCode = new TextBox(), sendCode = new TextBox();
        private readonly ComboBox host = new ComboBox();
        private readonly NumericUpDown receivePort = new NumericUpDown(), sendPort = new NumericUpDown();
        private readonly ListBox queue = new ListBox();
        private readonly TextBox logs = new TextBox();
        private readonly Label local = new Label(), receiveState = new Label(), sendState = new Label(), details = new Label();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Button listen = Button("开始接收"), send = Button("开始发送"), pause = Button("暂停发送"), search = Button("搜索设备");
        private readonly Button chooseFolder = Button("选择目录"), newCode = Button("换接收码");
        private readonly List<Control> queueControls = new List<Control>();
        private TransferServer server;
        private TransferClient sender;
        private readonly Stopwatch progressClock = Stopwatch.StartNew(), speedClock = new Stopwatch();
        private readonly object progressGate = new object();
        private string speedFile = "", speedStage = "";
        private long previousDone;
        private volatile bool closing;

        public MainForm()
        {
            Text = "轻传 · 局域网文件互传";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);
            ForeColor = Color.FromArgb(34, 43, 57);
            ClientSize = new Size(1020, 820); MinimumSize = new Size(940, 800);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);
            var title = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            title.Controls.Add(new Label { Text = "轻传  LAN TRANSFER", AutoSize = true, Font = new Font(Font.FontFamily, 19F, FontStyle.Bold) });
            title.Controls.Add(new Label { Text = "同一局域网 / 网线直连  ·  文件夹批量传输  ·  中断续传", AutoSize = true, ForeColor = Color.FromArgb(93, 107, 124) });
            root.Controls.Add(title, 0, 0);

            var receiveBox = new GroupBox { Text = "接收文件  /  把本机 IP 和接收码告诉对方", Dock = DockStyle.Fill, Padding = new Padding(12) };
            var receiving = Table(4);
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            receiving.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            receiving.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "轻传接收");
            folder.Dock = DockStyle.Fill;
            receiving.Controls.Add(Line(Label("保存到"), folder, chooseFolder), 0, 0);
            receiveCode.Text = Transfer.NewCode(); receiveCode.Width = 104;
            receivePort.Minimum = 1024; receivePort.Maximum = 65535; receivePort.Value = TransferServer.DefaultPort; receivePort.Width = 76;
            receiveState.Text = "尚未开启"; receiveState.ForeColor = Color.Gray; receiveState.AutoSize = true; receiveState.Padding = new Padding(8);
            receiving.Controls.Add(Flow(Label("接收码"), receiveCode, newCode, Label("端口"), receivePort, listen, receiveState), 0, 1);
            local.AutoSize = true; local.Padding = new Padding(0, 5, 0, 0);
            RefreshAddresses();
            var copy = Button("复制 IP"); copy.Click += delegate { Try(delegate { Clipboard.SetText(String.Join(" / ", Transfer.LocalAddresses())); }); };
            receiving.Controls.Add(Flow(local, copy), 0, 2);
            receiving.Controls.Add(new Label { Text = "开始接收后才接受文件；首次使用请允许 Windows 防火墙的专用网络访问。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(93, 107, 124), AutoSize = true }, 0, 3);
            receiveBox.Controls.Add(receiving); root.Controls.Add(receiveBox, 0, 1);

            var sendingBox = new GroupBox { Text = "发送文件  /  对方需要先开启接收", Dock = DockStyle.Fill, Padding = new Padding(12) };
            var sending = Table(6);
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            sending.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            sending.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var files = Button("添加文件"); var dirs = Button("添加文件夹"); var remove = Button("移除选中"); var clear = Button("清空");
            files.Click += delegate
            {
                using (var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) foreach (string path in dialog.FileNames) AddPath(path);
            };
            dirs.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择要发送的整个文件夹" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) AddPath(dialog.SelectedPath);
            };
            remove.Click += delegate { if (queue.SelectedIndex >= 0) queue.Items.RemoveAt(queue.SelectedIndex); };
            clear.Click += delegate { queue.Items.Clear(); };
            queueControls.AddRange(new Control[] { files, dirs, remove, clear, queue });
            sending.Controls.Add(Flow(files, dirs, remove, clear, Label("也可以把文件或文件夹拖到下方")), 0, 0);
            queue.Dock = DockStyle.Fill; queue.HorizontalScrollbar = true; queue.IntegralHeight = false; queue.AllowDrop = true;
            queue.DragEnter += delegate(object s, DragEventArgs e) { if (sender == null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            queue.DragDrop += delegate(object s, DragEventArgs e) { if (sender == null) foreach (string path in (string[])e.Data.GetData(DataFormats.FileDrop)) AddPath(path); };
            sending.Controls.Add(queue, 0, 1);
            host.Width = 220; host.DropDownStyle = ComboBoxStyle.DropDown; host.Text = "";
            host.SelectionChangeCommitted += delegate
            {
                string endpoint = host.Text.Split(' ')[0]; int colon = endpoint.LastIndexOf(':'); int port;
                if (colon > 0 && Int32.TryParse(endpoint.Substring(colon + 1), out port))
                { sendPort.Value = port; host.Text = endpoint.Substring(0, colon); }
            };
            sendPort.Minimum = 1; sendPort.Maximum = 65535; sendPort.Value = TransferServer.DefaultPort; sendPort.Width = 76;
            sendCode.Width = 96;
            sending.Controls.Add(Flow(Label("对方 IP"), host, search, Label("端口"), sendPort, Label("接收码"), sendCode), 0, 2);
            pause.Enabled = false;
            sending.Controls.Add(Flow(send, pause, Label("暂停后，再次发送原文件可续传；相同文件会跳过。")), 0, 3);
            bar.Dock = DockStyle.Fill; bar.Margin = new Padding(3, 4, 3, 5); sending.Controls.Add(bar, 0, 4);
            var status = Table(2); status.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); status.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            sendState.Text = "准备就绪"; sendState.Dock = DockStyle.Fill; sendState.AutoEllipsis = true;
            details.Text = "选择文件或文件夹，填写对方 IP 和接收码。"; details.Dock = DockStyle.Fill; details.AutoEllipsis = true; details.ForeColor = Color.FromArgb(93, 107, 124);
            status.Controls.Add(sendState, 0, 0); status.Controls.Add(details, 0, 1); sending.Controls.Add(status, 0, 5);
            sendingBox.Controls.Add(sending); root.Controls.Add(sendingBox, 0, 2);

            var logBox = new GroupBox { Text = "传输记录", Dock = DockStyle.Fill, Padding = new Padding(8) };
            logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.Dock = DockStyle.Fill; logs.BackColor = Color.White;
            logBox.Controls.Add(logs); root.Controls.Add(logBox, 0, 3);
            root.Controls.Add(new Label { Text = "v0.1.1  ·  离线直传  ·  SHA-256 完整校验  ·  同名不同内容不覆盖  ·  仅用于可信局域网（文件内容未加密）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(93, 107, 124) }, 0, 4);

            chooseFolder.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择接收目录", SelectedPath = folder.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            };
            newCode.Click += delegate { receiveCode.Text = Transfer.NewCode(); };
            listen.Click += delegate { Try(ToggleReceiver); };
            send.Click += delegate { Try(StartSend); };
            pause.Click += delegate { if (sender != null) { sender.Cancel(); Log("已请求暂停，接收方会保留未完成数据。"); } };
            search.Click += delegate { Search(); };
            FormClosing += delegate
            {
                closing = true;
                if (sender != null) sender.Cancel();
                if (server != null) server.Dispose();
            };
        }
        private static Button Button(string text)
        { return new Button { Text = text, AutoSize = true, Height = 29, MinimumSize = new Size(82, 29), FlatStyle = FlatStyle.System, Margin = new Padding(3, 0, 5, 0) }; }
        private static Label Label(string text)
        { return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0), Margin = new Padding(3, 0, 6, 0) }; }
        private static TableLayoutPanel Table(int rows)
        { return new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = rows, ColumnCount = 1, Margin = new Padding(0) }; }
        private static FlowLayoutPanel Flow(params Control[] controls)
        { var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) }; flow.Controls.AddRange(controls); return flow; }
        private static TableLayoutPanel Line(Control label, Control middle, Control button)
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            table.Controls.Add(label, 0, 0); table.Controls.Add(middle, 1, 0); table.Controls.Add(button, 2, 0); return table;
        }
        private void Try(Action action)
        { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "轻传", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        private void UI(Action action)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { if (InvokeRequired) BeginInvoke(action); else action(); } catch (InvalidOperationException) { }
        }
        private void Log(string message)
        {
            UI(delegate
            {
                if (logs.TextLength > 24000) logs.Text = logs.Text.Substring(logs.TextLength - 12000);
                logs.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            });
        }
        private void RefreshAddresses() { local.Text = "本机 IPv4：" + String.Join("  /  ", Transfer.LocalAddresses()); }
        private void AddPath(string path)
        {
            path = Path.GetFullPath(path);
            foreach (object item in queue.Items) if (String.Equals((string)item, path, StringComparison.OrdinalIgnoreCase)) return;
            queue.Items.Add(path);
        }
        private void ToggleReceiver()
        {
            if (server == null)
            {
                if (!Path.IsPathRooted(folder.Text)) throw new IOException("请选择完整的接收目录。");
                var next = new TransferServer(folder.Text, receiveCode.Text.Trim(), ReceiveProgress, Log);
                try { next.Start((int)receivePort.Value, true); server = next; }
                catch { next.Dispose(); throw; }
                receivePort.Value = server.Port;
                listen.Text = "停止接收"; receiveState.Text = "接收已开启"; receiveState.ForeColor = Color.SeaGreen;
                folder.Enabled = chooseFolder.Enabled = receiveCode.Enabled = newCode.Enabled = receivePort.Enabled = false;
                RefreshAddresses(); Log("接收已开启，端口 " + server.Port + "，目录：" + folder.Text);
            }
            else
            {
                server.Dispose(); server = null; listen.Text = "开始接收";
                receiveState.Text = "尚未开启"; receiveState.ForeColor = Color.Gray;
                folder.Enabled = chooseFolder.Enabled = receiveCode.Enabled = newCode.Enabled = receivePort.Enabled = true;
                Log("已停止接收；未完成文件保留，下次可续传。");
            }
        }
        private void StartSend()
        {
            if (queue.Items.Count == 0) throw new IOException("请先添加文件或文件夹。");
            string address = host.Text.Trim().Split(' ')[0]; int targetPort = (int)sendPort.Value;
            int colon = address.LastIndexOf(':');
            if (colon > 0) { targetPort = Int32.Parse(address.Substring(colon + 1)); address = address.Substring(0, colon); }
            IPAddress ip; if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new IOException("请填写对方的 IPv4 地址，或先搜索设备。");
            if (sendCode.Text.Trim().Length < 6) throw new IOException("请填写对方的接收码。");
            string pin = sendCode.Text.Trim(); string[] paths = new string[queue.Items.Count]; queue.Items.CopyTo(paths, 0);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (!names.Add(Path.GetFileName(path.TrimEnd('\\')))) throw new IOException("列表包含同名文件或根目录，请分批发送。");
            sender = new TransferClient(); var current = sender;
            SetSending(true); bar.Value = 0; speedFile = ""; speedClock.Reset();
            var thread = new Thread(delegate()
            {
                string result; bool complete = false;
                try
                {
                    current.Send(address, targetPort, pin, paths, SendProgress, Log);
                    complete = true;
                    result = "发送结束：成功/跳过 " + current.Completed + " 个文件，失败 " + current.Failed + " 个。";
                }
                catch (Exception ex) { result = "传输中断或暂停，可重新发送续传：" + ex.Message; }
                finally { current.Dispose(); }
                Log(result);
                UI(delegate
                {
                    sender = null; SetSending(false); sendState.Text = result; bar.Style = ProgressBarStyle.Continuous;
                    if (complete && current.Failed == 0) bar.Value = 100;
                    details.Text = "本次实际发送 " + Transfer.Size(current.PayloadBytes) + "  ·  已完成/跳过 " + current.Completed + " 个文件";
                });
            }); thread.IsBackground = true; thread.Start();
        }
        private void SetSending(bool busy)
        {
            send.Enabled = !busy; pause.Enabled = busy; host.Enabled = sendCode.Enabled = sendPort.Enabled = search.Enabled = !busy;
            foreach (Control control in queueControls) control.Enabled = !busy;
        }
        private void SendProgress(ProgressInfo p)
        {
            lock (progressGate)
            {
                if (progressClock.ElapsedMilliseconds < 120) return;
                progressClock.Restart();
            }
            UI(delegate
            {
                bool moving = p.Stage == "发送中";
                double speed = 0;
                if (moving && speedFile == p.FileName && speedStage == p.Stage && speedClock.Elapsed.TotalSeconds > 0)
                    speed = Math.Max(0, p.Done - previousDone) / speedClock.Elapsed.TotalSeconds;
                speedFile = p.FileName; speedStage = p.Stage; previousDone = p.Done; speedClock.Restart();
                bool hashing = p.Stage == "读取文件指纹" || p.Stage == "等待接收方校验" || p.Stage == "核对续传数据";
                bar.Style = hashing ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (!hashing) bar.Value = p.Length == 0 ? 100 : (int)Math.Min(100, p.Done * 100.0 / p.Length);
                sendState.Text = p.Stage + "：" + p.FileName;
                string eta = speed > 0 ? "  ·  约剩 " + TimeSpan.FromSeconds(Math.Min(86400000, (p.Length - p.Done) / speed)).ToString() : "";
                details.Text = Transfer.Size(p.Done) + " / " + Transfer.Size(p.Length) + "  ·  已完成 " + p.Files + " 个文件" + (speed > 0 ? "  ·  " + Transfer.Size((long)speed) + "/s" + eta : "");
            });
        }
        private readonly Stopwatch receiveClock = Stopwatch.StartNew();
        private void ReceiveProgress(ProgressInfo p)
        {
            if (receiveClock.ElapsedMilliseconds < 150) return; receiveClock.Restart();
            UI(delegate { receiveState.Text = p.Stage + "  " + Transfer.Size(p.Done) + "/" + Transfer.Size(p.Length); });
        }
        private void Search()
        {
            search.Enabled = false; Log("正在搜索已开启接收的设备…");
            var thread = new Thread(delegate()
            {
                try
                {
                    string[] peers = Transfer.Discover();
                    UI(delegate
                    {
                        host.Items.Clear(); host.Items.AddRange(peers);
                        if (peers.Length > 0) { host.SelectedIndex = 0; if (sender == null) host.DroppedDown = true; }
                        Log(peers.Length == 0 ? "未找到设备，可直接填写对方 IP。请检查接收状态和防火墙。" : "找到 " + peers.Length + " 个接收地址，请选择设备并填写接收码。");
                    });
                }
                catch (Exception ex) { Log("搜索失败，可使用 IP 连接：" + ex.Message); }
                finally { UI(delegate { search.Enabled = sender == null; }); }
            }); thread.IsBackground = true; thread.Start();
        }
    }
}
