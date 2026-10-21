using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LanTransfer
{
    internal static class UiTheme
    {
        public static readonly Color Ink = Color.FromArgb(38, 44, 63);
        public static readonly Color Muted = Color.FromArgb(112, 121, 141);
        public static readonly Color Accent = Color.FromArgb(94, 80, 216);
        public static readonly Color Success = Color.FromArgb(34, 133, 107);
        public static readonly Color Border = Color.FromArgb(226, 230, 239);
        public static readonly Color Canvas = Color.FromArgb(245, 246, 251);
        public static readonly Color Surface = Color.White;
        public static readonly Color Field = Color.FromArgb(248, 249, 253);
        public static readonly Color AccentSoft = Color.FromArgb(240, 237, 253);

        public static Font BodyFont() { return new Font("Microsoft YaHei UI", 9F); }
        public static Font SmallFont() { return new Font("Microsoft YaHei UI", 8.5F); }

        public static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;
            radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (radius < .5F) { path.AddRectangle(bounds); path.CloseFigure(); return path; }
            float diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Color Behind(Control control)
        {
            Control parent = control.Parent;
            while (parent != null)
            {
                if (parent.BackColor.A == 255) return parent.BackColor;
                parent = parent.Parent;
            }
            return Canvas;
        }
    }

    internal sealed class UiButton : Button
    {
        private bool primary, hovering, mousePressed, keyboardPressed;

        public bool IsPrimary
        {
            get { return primary; }
            set { if (primary != value) { primary = value; Invalidate(); } }
        }

        public UiButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink;
            Font = UiTheme.BodyFont(); Cursor = Cursors.Hand;
            Height = 36; MinimumSize = new Size(36, 30);
            Padding = new Padding(10, 0, 10, 0);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        { e.Graphics.Clear(UiTheme.Behind(this)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool pressed = (mousePressed && hovering) || keyboardPressed;
            Color fill = primary ? UiTheme.Accent : UiTheme.Surface;
            Color foreground = primary ? Color.White : UiTheme.Ink;
            Color edge = UiTheme.Border;
            if (!Enabled)
            {
                fill = Color.FromArgb(244, 245, 249);
                foreground = Color.FromArgb(162, 169, 185);
                edge = Color.FromArgb(234, 237, 243);
            }
            else if (pressed)
            {
                fill = primary ? ControlPaint.Dark(UiTheme.Accent, .18F) : Color.FromArgb(228, 223, 250);
                edge = Color.FromArgb(204, 197, 239);
            }
            else if (hovering)
            {
                fill = primary ? ControlPaint.Dark(UiTheme.Accent, .08F) : UiTheme.AccentSoft;
                edge = Color.FromArgb(214, 208, 243);
            }
            var bounds = new RectangleF(.5F, .5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F));
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Rounded(bounds, 6))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
                if (!primary || !Enabled) using (var pen = new Pen(edge)) e.Graphics.DrawPath(pen, path);
            }
            e.Graphics.Restore(state);

            var textBounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, flags);
            if (Focused && ShowFocusCues && Enabled)
                ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Math.Max(0, Width - 8), Math.Max(0, Height - 8)), foreground, fill);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { mousePressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); mousePressed = false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { mousePressed = false; Invalidate(); } }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { keyboardPressed = true; Invalidate(); } }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) { keyboardPressed = false; Invalidate(); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); keyboardPressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) hovering = mousePressed = keyboardPressed = false; Invalidate(); }
    }

    internal sealed class InputSurface : Panel
    {
        private readonly Control input;
        private bool arranging;

        public InputSurface(Control input)
        {
            if (input == null) throw new ArgumentNullException("input");
            this.input = input;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = UiTheme.Field; Height = 36;
            Padding = input is NumericUpDown ? new Padding(6, 2, 3, 2) : new Padding(10, 2, 10, 2);
            Margin = Padding.Empty; TabStop = false;
            input.Dock = DockStyle.None; input.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            input.Margin = Padding.Empty; input.BackColor = UiTheme.Field;
            var textBox = input as TextBoxBase;
            if (textBox != null) textBox.BorderStyle = BorderStyle.None;
            var comboBox = input as ComboBox;
            if (comboBox != null) comboBox.FlatStyle = FlatStyle.Flat;
            var numeric = input as NumericUpDown;
            if (numeric != null) numeric.BorderStyle = BorderStyle.None;
            Controls.Add(input);
            input.Enter += InputVisualChanged; input.Leave += InputVisualChanged;
            input.EnabledChanged += InputVisualChanged;
            input.FontChanged += delegate { PerformLayout(); Invalidate(); };
        }

        private void InputVisualChanged(object sender, EventArgs e)
        {
            input.BackColor = input.Enabled ? UiTheme.Field : Color.FromArgb(244, 245, 249);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && input.CanFocus) input.Focus();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null || arranging) return;
            arranging = true;
            try
            {
                int height = input.PreferredSize.Height;
                var textBox = input as TextBox;
                var comboBox = input as ComboBox;
                var numeric = input as NumericUpDown;
                if (textBox != null && !textBox.Multiline) height = textBox.PreferredHeight;
                else if (comboBox != null) height = comboBox.PreferredHeight;
                else if (numeric != null) height = numeric.PreferredHeight;
                else height = Math.Max(0, ClientSize.Height - Padding.Vertical);
                int top = Math.Max(Padding.Top, (ClientSize.Height - height) / 2);
                input.SetBounds(Padding.Left, top, Math.Max(0, ClientSize.Width - Padding.Horizontal), height);
            }
            finally { arranging = false; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        { e.Graphics.Clear(UiTheme.Behind(this)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool enabled = Enabled && input.Enabled;
            Color fill = enabled ? UiTheme.Field : Color.FromArgb(244, 245, 249);
            Color edge = enabled && ContainsFocus ? UiTheme.Accent : UiTheme.Border;
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Rounded(new RectangleF(.5F, .5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F)), 6))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(edge))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            e.Graphics.Restore(state);
        }
    }

    internal sealed class CardSurface : Panel
    {
        public CardSurface()
        {
            BackColor = UiTheme.Surface; DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(UiTheme.Behind(this));
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Rounded(new RectangleF(.5F, .5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F)), 10))
            using (var brush = new SolidBrush(UiTheme.Surface)) e.Graphics.FillPath(brush, path);
            e.Graphics.Restore(state);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Rounded(new RectangleF(.5F, .5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F)), 10))
            using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawPath(pen, path);
            e.Graphics.Restore(state);
        }
    }

    internal sealed class TransferProgress : ProgressBar
    {
        private readonly Timer animation;
        private int marqueeOffset;

        public TransferProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = UiTheme.Surface; ForeColor = UiTheme.Accent;
            Height = 8; MinimumSize = new Size(16, 6);
            MarqueeAnimationSpeed = 0;
            animation = new Timer { Interval = 30 };
            animation.Tick += delegate { marqueeOffset = (marqueeOffset + 4) % Math.Max(1, Width + SegmentWidth); Invalidate(); };
        }

        public new ProgressBarStyle Style
        {
            get { return base.Style; }
            set { base.Style = value; MarqueeAnimationSpeed = 0; marqueeOffset = 0; UpdateAnimation(); Invalidate(); }
        }
        public new int Value { get { return base.Value; } set { base.Value = value; Invalidate(); } }
        public new int Minimum { get { return base.Minimum; } set { base.Minimum = value; Invalidate(); } }
        public new int Maximum { get { return base.Maximum; } set { base.Maximum = value; Invalidate(); } }

        private int SegmentWidth { get { return Math.Max(24, Width / 4); } }
        private void UpdateAnimation()
        { if (animation != null) animation.Enabled = !IsDisposed && IsHandleCreated && Visible && Enabled && base.Style == ProgressBarStyle.Marquee; }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateAnimation(); }
        protected override void OnHandleDestroyed(EventArgs e) { if (animation != null) animation.Stop(); base.OnHandleDestroyed(e); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnimation(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); UpdateAnimation(); Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs e)
        { e.Graphics.Clear(UiTheme.Behind(this)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0, 0, Width, Height);
            using (var track = UiTheme.Rounded(bounds, Height / 2F))
            using (var trackBrush = new SolidBrush(Color.FromArgb(234, 235, 245)))
            {
                e.Graphics.FillPath(trackBrush, track);
                e.Graphics.SetClip(track, CombineMode.Intersect);
                float left = 0, width;
                if (base.Style == ProgressBarStyle.Marquee)
                { width = SegmentWidth; left = marqueeOffset - width; }
                else
                {
                    double range = (double)base.Maximum - base.Minimum;
                    width = range <= 0 ? 0 : (float)(Width * Math.Max(0, Math.Min(1, (base.Value - (double)base.Minimum) / range)));
                }
                if (width > 0)
                    using (var fill = UiTheme.Rounded(new RectangleF(left, 0, width, Height), Height / 2F))
                    using (var brush = new SolidBrush(Enabled ? UiTheme.Accent : Color.FromArgb(187, 181, 224)))
                        e.Graphics.FillPath(brush, fill);
            }
            e.Graphics.Restore(state);
        }

        protected override void Dispose(bool disposing)
        { if (disposing && animation != null) animation.Dispose(); base.Dispose(disposing); }
    }
}
