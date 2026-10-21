using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LanTransfer;

static class UiSmoke
{
    private static int checks;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly HashSet<string> Captions = new HashSet<string>(new[]
    {
        "保存位置", "接收码", "接收端口", "本机 IPv4", "对方 IPv4", "端口", "对方接收码"
    });

    private static void Assert(bool value, string description)
    {
        if (!value) throw new Exception("FAIL: " + description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static T Field<T>(MainForm form, string name)
    { return (T)typeof(MainForm).GetField(name, PrivateInstance).GetValue(form); }

    private static void SetField(MainForm form, string name, object value)
    { typeof(MainForm).GetField(name, PrivateInstance).SetValue(form, value); }

    private static void Invoke(MainForm form, string name, params object[] args)
    { typeof(MainForm).GetMethod(name, PrivateInstance).Invoke(form, args); }

    private static void LayoutTree(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutTree(child);
    }

    private static bool Interactive(Control control)
    {
        return control is ButtonBase || control is TextBoxBase || control is ComboBox ||
            control is NumericUpDown || control is ListBox;
    }

    private static void CheckLayout(Control root, Control control, string context)
    {
        if (!control.Visible) return;
        if (Interactive(control))
        {
            string description = control.GetType().Name + " [" + control.Text + "]";
            if (control.Width <= 0 || control.Height <= 0)
                throw new Exception("FAIL: " + context + " empty interactive control: " + description);
            Rectangle bounds = control.Bounds;
            Control ancestor = control.Parent;
            while (ancestor != null)
            {
                if (!ancestor.ClientRectangle.Contains(bounds))
                    throw new Exception("FAIL: " + context + " clipped " + description + " in " + ancestor.GetType().Name + ": " + bounds + " / " + ancestor.ClientRectangle);
                if (ancestor == root) break;
                bounds.Offset(ancestor.Left, ancestor.Top);
                ancestor = ancestor.Parent;
            }
            // Native composite input children have their own platform-specific borders.
            return;
        }
        var label = control as Label;
        if (label != null && Captions.Contains(label.Text))
        {
            Size required = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.NoPadding);
            if (label.ClientSize.Width < required.Width || label.ClientSize.Height < required.Height)
                throw new Exception("FAIL: " + context + " clipped field caption [" + label.Text + "]: " + label.ClientSize + " needs " + required);
        }
        foreach (Control child in control.Controls) CheckLayout(root, child, context);
    }

    private static void Render(Control panel, Size size, string output, string name)
    {
        panel.Size = size;
        LayoutTree(panel);
        CheckLayout(panel, panel, name);
        int inputHeight = -1;
        CheckInputHeights(panel, ref inputHeight, name);
        using (var bitmap = new Bitmap(size.Width, size.Height))
        {
            panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
            bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
        }
        Assert(true, name + " controls fit at " + size.Width + "x" + size.Height);
    }

    private static void CheckInputHeights(Control control, ref int expected, string context)
    {
        if (!control.Visible) return;
        if (control is InputSurface)
        {
            if (expected < 0) expected = control.Height;
            if (control.Height != expected)
                throw new Exception("FAIL: " + context + " inconsistent input height: " + control.Height + " / " + expected);
        }
        foreach (Control child in control.Controls) CheckInputHeights(child, ref expected, context);
    }

    private static void CheckState(MainForm form, string output)
    {
        var addresses = Field<ComboBox>(form, "addresses");
        if (addresses.Items.Count > 0)
            Assert(addresses.SelectedIndex >= 0 && !String.IsNullOrEmpty(addresses.Text), "local address picker selects an available IPv4 address");
        Invoke(form, "SelectPeer", "192.168.50.2:51234  Office laptop");
        Assert(Field<ComboBox>(form, "host").Text == "192.168.50.2" && Field<NumericUpDown>(form, "sendPort").Value == 51234,
            "discovered peer applies its actual fallback port");

        SetField(form, "searching", true);
        Invoke(form, "SetSending", false);
        Assert(!Field<Button>(form, "search").Enabled, "ending send keeps active search disabled");
        using (var sender = new TransferClient())
        {
            SetField(form, "sender", sender);
            Invoke(form, "SetSending", true);
            Assert(!Field<Button>(form, "search").Enabled && !Field<ComboBox>(form, "host").Enabled &&
                !Field<ListBox>(form, "queue").Enabled && !Field<Panel>(form, "drop").AllowDrop,
                "sending locks destination and queue operations");
            SetField(form, "sender", null);
        }
        SetField(form, "searching", false);
        Invoke(form, "SetSending", false);
        Assert(Field<Button>(form, "search").Enabled && Field<ComboBox>(form, "host").Enabled,
            "idle restores destination and search operations");

        using (var receiver = new TransferServer(Path.Combine(output, "received"), "12345678", delegate { }, delegate { }))
        {
            // No listener is started: these checks exercise UI callback handling only.
            SetField(form, "server", receiver);
            SetField(form, "receiveSession", 7);
            Field<Stopwatch>(form, "receiveClock").Restart();
            Invoke(form, "ReceiveProgress", 7, new ProgressInfo("complete.txt", "文件完成", 42, 42, 1));
            Assert(Field<Label>(form, "receiveState").Text.StartsWith("文件完成"), "receive completion bypasses progress throttling");
            SetField(form, "receiveSession", 8);
            SetField(form, "server", null);
            Field<Label>(form, "receiveState").Text = "接收已停止";
            Invoke(form, "ReceiveProgress", 7, new ProgressInfo("stale.txt", "文件完成", 42, 42, 1));
            Assert(Field<Label>(form, "receiveState").Text == "接收已停止", "stale receive session cannot replace stopped status");
        }
        Field<Stopwatch>(form, "progressClock").Restart();
        Invoke(form, "SendProgress", new ProgressInfo("complete.txt", "文件完成", 42, 42, 1));
        Assert(Field<ProgressBar>(form, "bar").Value == 100 && Field<Label>(form, "sendState").Text.StartsWith("文件完成"),
            "send completion bypasses progress throttling");
        Field<Label>(form, "receiveState").Text = "开启后等待对方连接";
        Field<Label>(form, "sendState").Text = "准备就绪";
        Field<Label>(form, "details").Text = "添加文件，填写对方地址和接收码即可发送。";
        Field<ProgressBar>(form, "bar").Value = 0;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", "ui-v0.2.1"));
            Directory.CreateDirectory(output);
            using (var form = new MainForm())
            {
                // Create a hidden native handle so UI() callbacks can run synchronously.
                IntPtr handle = form.Handle;
                form.PerformLayout();
                Control panel = form.Controls[0];
                // Preserve inherited typography before detaching from the hidden form.
                panel.Font = form.Font;
                form.Controls.Remove(panel);
                using (panel)
                {
                    panel.CreateControl();
                    Render(panel, new Size(1120, 748), output, "ui-preview");
                    Render(panel, new Size(864, 641), output, "ui-minimum");
                    CheckState(form, output);

                    string fixtures = Path.Combine(output, "fixtures", "项目交付文件与设计参考资料_用于验证长路径省略展示");
                    Directory.CreateDirectory(fixtures);
                    string document = Path.Combine(fixtures, "产品界面优化及跨平台适配说明_最终版本.txt");
                    File.WriteAllText(document, "UI layout fixture.");
                    Invoke(form, "AddPath", document);
                    Invoke(form, "AddPath", fixtures);
                    Invoke(form, "AddPath", document);
                    var queue = Field<ListBox>(form, "queue");
                    Assert(queue.Items.Count == 2 && queue.Visible, "adding paths shows queue and rejects duplicates");
                    queue.SelectedIndex = 0;
                    Assert(Field<Button>(form, "remove").Enabled && Field<Button>(form, "clear").Enabled, "queued selection enables removal and clearing");
                    Render(panel, new Size(1120, 748), output, "ui-queue");
                    Render(panel, new Size(864, 641), output, "ui-queue-minimum");
                    // Preview the busy/disabled theme without connecting to another device.
                    using (var sender = new TransferClient())
                    {
                        SetField(form, "sender", sender);
                        Invoke(form, "SetSending", true);
                        Field<TransferProgress>(form, "bar").Value = 64;
                        Field<Label>(form, "sendState").Text = "发送中：界面说明.txt（预览）";
                        Field<Label>(form, "details").Text = "64 MB / 100 MB · 12 MB/s · 约剩 3 秒（示例数据）";
                        Render(panel, new Size(1120, 748), output, "ui-transfer");
                        SetField(form, "sender", null);
                        Invoke(form, "SetSending", false);
                    }
                }
            }
            Console.WriteLine("UI checks passed: " + checks + ". Artifacts: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
