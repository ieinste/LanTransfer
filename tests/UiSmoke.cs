using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using LanTransfer;

static class UiSmoke
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new MainForm())
        {
            form.CreateControl();
            form.PerformLayout();
            string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output"));
            Directory.CreateDirectory(output);
            // Detach the panel so child Visible is independent of a hidden top-level window.
            Control panel = form.Controls[0]; form.Controls.Remove(panel);
            panel.Size = new Size(1020, 820); panel.PerformLayout();
            using (var image = new Bitmap(panel.Width, panel.Height))
            { panel.DrawToBitmap(image, new Rectangle(Point.Empty, panel.Size)); image.Save(Path.Combine(output, "ui-preview.png"), ImageFormat.Png); }
            panel.Size = new Size(940, 800); panel.PerformLayout();
            using (var image = new Bitmap(panel.Width, panel.Height))
            { panel.DrawToBitmap(image, new Rectangle(Point.Empty, panel.Size)); image.Save(Path.Combine(output, "ui-minimum.png"), ImageFormat.Png); }
            panel.Dispose();
            Console.WriteLine("UI rendered at default and minimum sizes.");
        }
    }
}
