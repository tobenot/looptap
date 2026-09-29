using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LoopTap
{
    // ponytail: 标题栏和滚动条用系统暗色（Win10 2004+ 的 DWM / uxtheme）。按钮和列表自绘，不依赖那两个未公开序号。
    static class UiTheme
    {
        public static readonly Color Bg = Color.FromArgb(28, 28, 30);
        public static readonly Color PanelBg = Color.FromArgb(37, 37, 40);
        public static readonly Color Field = Color.FromArgb(24, 24, 26);
        public static readonly Color Header = Color.FromArgb(32, 32, 36);
        public static readonly Color Text = Color.FromArgb(232, 232, 234);
        public static readonly Color Muted = Color.FromArgb(154, 154, 162);
        public static readonly Color Line = Color.FromArgb(62, 62, 68);
        public static readonly Color RowLine = Color.FromArgb(46, 46, 50);
        public static readonly Color ButtonBg = Color.FromArgb(50, 50, 54);
        public static readonly Color ButtonHot = Color.FromArgb(66, 66, 72);
        public static readonly Color ButtonDown = Color.FromArgb(42, 42, 46);
        public static readonly Color Accent = Color.FromArgb(46, 110, 176);
        public static readonly Color AccentHot = Color.FromArgb(58, 128, 198);
        public static readonly Color AccentDown = Color.FromArgb(36, 88, 146);
        public static readonly Color AccentText = Color.FromArgb(244, 248, 255);
        public static readonly Color AccentLine = Color.FromArgb(110, 170, 220);
        public static readonly Color Wave = Color.FromArgb(120, 190, 255);
        public static readonly Color Keep = Color.FromArgb(36, 48, 64);
        public static readonly Color Gold = Color.FromArgb(255, 196, 0);
        public static readonly Color Playing = Color.FromArgb(255, 204, 64);
        public static readonly Color PlayingText = Color.FromArgb(32, 24, 6);
        public static readonly Color Dim = Color.FromArgb(160, 0, 0, 0);

        static readonly Pen _selPen = new Pen(Wave);
        static readonly Pen _selBlurPen = new Pen(Color.FromArgb(140, 150, 165));
        static readonly Pen _rowPen = new Pen(RowLine);
        static readonly Pen _headerPen = new Pen(Line);
        static readonly HashSet<Control> _styled = new HashSet<Control>();
        static readonly Dictionary<Button, bool> _accent = new Dictionary<Button, bool>();
        static readonly HashSet<IntPtr> _hooked = new HashSet<IntPtr>();
        static readonly List<NativeWindow> _roots = new List<NativeWindow>();
        static bool _enabled;
        static IntPtr _caretBmp;
        static int _caretH;

        public static void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            try
            {
                SetPreferredAppMode(2);
                FlushMenuThemes();
            }
            catch { }
        }

        public static void Apply(Form form)
        {
            Enable();
            form.BackColor = Bg;
            form.ForeColor = Text;
            for (int i = 0; i < form.Controls.Count; i++)
                Paint(form.Controls[i]);
            EventHandler native = delegate
            {
                TryDarkTitle(form.Handle);
                ApplyNative(form);
            };
            if (form.IsHandleCreated) native(null, EventArgs.Empty);
            form.HandleCreated += native;
            form.Shown += native;
        }

        public static void MarkAccent(Button button)
        {
            _accent[button] = true;
            StyleButton(button);
        }

        static void Paint(Control c)
        {
            if (c is Panel)
            {
                c.BackColor = PanelBg;
                c.ForeColor = Text;
            }
            else if (c is Button)
                StyleButton((Button)c);
            else if (c is TextBox)
                StyleText((TextBox)c);
            else if (c is Label)
            {
                Label label = (Label)c;
                label.ForeColor = Text;
                label.BackColor = PanelBg;
            }
            else if (c is CheckBox)
            {
                CheckBox box = (CheckBox)c;
                box.ForeColor = Text;
                box.BackColor = PanelBg;
                box.UseVisualStyleBackColor = false;
            }
            else if (c is ListView)
                StyleList((ListView)c);

            for (int i = 0; i < c.Controls.Count; i++)
                Paint(c.Controls[i]);
        }

        static void StyleButton(Button b)
        {
            if (_styled.Add(b))
            {
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.FlatAppearance.BorderSize = 1;
                b.EnabledChanged += delegate { RefreshButton(b); };
            }
            RefreshButton(b);
        }

        static void RefreshButton(Button b)
        {
            bool accent = _accent.ContainsKey(b) && _accent[b];
            if (!b.Enabled)
            {
                b.BackColor = ButtonBg;
                b.ForeColor = Muted;
                b.FlatAppearance.BorderColor = Line;
                b.FlatAppearance.MouseOverBackColor = ButtonBg;
                b.FlatAppearance.MouseDownBackColor = ButtonBg;
                return;
            }
            if (accent)
            {
                b.BackColor = Accent;
                b.ForeColor = AccentText;
                b.FlatAppearance.BorderColor = AccentLine;
                b.FlatAppearance.MouseOverBackColor = AccentHot;
                b.FlatAppearance.MouseDownBackColor = AccentDown;
                return;
            }
            b.BackColor = ButtonBg;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = ButtonHot;
            b.FlatAppearance.MouseDownBackColor = ButtonDown;
        }

        static void StyleText(TextBox tb)
        {
            if (!_styled.Add(tb)) return;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.BackColor = Field;
            tb.ForeColor = Text;
        }

        static void StyleList(ListView lv)
        {
            if (!_styled.Add(lv)) return;
            lv.BackColor = Field;
            lv.ForeColor = Text;
            lv.BorderStyle = BorderStyle.None;
            lv.GridLines = false;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += OnHeader;
            lv.DrawItem += OnItem;
            lv.DrawSubItem += OnSub;
            lv.GotFocus += delegate { lv.Invalidate(); };
            lv.LostFocus += delegate { lv.Invalidate(); };
        }

        static void OnItem(object sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
            Color back = e.Item.BackColor;
            if (back.A == 0) back = Field;
            using (SolidBrush brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);
        }

        static void OnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            ListView lv = (ListView)sender;
            Rectangle fill = e.Bounds;
            if (e.ColumnIndex == lv.Columns.Count - 1 && fill.Right < lv.ClientSize.Width)
                fill.Width = lv.ClientSize.Width - fill.Left;
            using (SolidBrush brush = new SolidBrush(Header))
                e.Graphics.FillRectangle(brush, fill);
            e.Graphics.DrawLine(_headerPen, fill.Left, fill.Bottom - 1, fill.Right, fill.Bottom - 1);
            Rectangle text = e.Bounds;
            text.X += 6;
            text.Width -= 10;
            if (text.Width < 1) text.Width = 1;
            Font font = e.Font != null ? e.Font : lv.Font;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, font, text, Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        static void OnSub(object sender, DrawListViewSubItemEventArgs e)
        {
            ListView lv = (ListView)sender;
            Color back = e.Item.BackColor;
            Color fore = e.Item.ForeColor;
            if (back.A == 0) back = Field;
            if (fore.A == 0) fore = Text;

            Rectangle fill = e.Bounds;
            if (e.ColumnIndex == 0 && fill.Left > 0)
            {
                fill.Width += fill.Left;
                fill.X = 0;
            }
            if (e.ColumnIndex == lv.Columns.Count - 1 && fill.Right < lv.ClientSize.Width)
                fill.Width = lv.ClientSize.Width - fill.Left;
            using (SolidBrush brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, fill);
            if (fill.Height > 1)
                e.Graphics.DrawLine(_rowPen, fill.Left, fill.Bottom - 1, fill.Right, fill.Bottom - 1);

            Rectangle text = e.Bounds;
            text.X += 6;
            text.Width -= 10;
            if (text.Width < 1) text.Width = 1;
            Font font = e.Item.Font != null ? e.Item.Font : lv.Font;
            string s = e.SubItem.Text ?? "";
            TextRenderer.DrawText(e.Graphics, s, font, text, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (!e.Item.Selected) return;
            Pen pen = lv.Focused ? _selPen : _selBlurPen;
            int top = fill.Top;
            int bot = fill.Bottom - 1;
            int left = fill.Left;
            int right = fill.Right - 1;
            e.Graphics.DrawLine(pen, left, top, right, top);
            e.Graphics.DrawLine(pen, left, bot, right, bot);
            if (e.ColumnIndex == 0)
                e.Graphics.DrawLine(pen, left, top, left, bot);
            if (e.ColumnIndex == lv.Columns.Count - 1)
                e.Graphics.DrawLine(pen, right, top, right, bot);
        }

        static void ApplyNative(Control c)
        {
            if (!c.IsHandleCreated)
            {
                for (int i = 0; i < c.Controls.Count; i++)
                    ApplyNative(c.Controls[i]);
                return;
            }
            try
            {
                AllowDarkModeForWindow(c.Handle, true);
                if (c is Button)
                    SetWindowTheme(c.Handle, "", "");
                else if (c is TextBox || c is ListView || c is CheckBox || c is Form)
                    SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            }
            catch { }

            TextBox tb = c as TextBox;
            if (tb != null)
            {
                tb.BackColor = Field;
                tb.ForeColor = Text;
                if (Claim(tb.Handle))
                    _roots.Add(new CaretHook(tb));
            }
            Button button = c as Button;
            if (button != null) RefreshButton(button);
            ListView lv = c as ListView;
            if (lv != null)
            {
                IntPtr hdr = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                if (hdr != IntPtr.Zero)
                {
                    try
                    {
                        AllowDarkModeForWindow(hdr, true);
                        SetWindowTheme(hdr, "", "");
                    }
                    catch { }
                }
            }

            for (int i = 0; i < c.Controls.Count; i++)
                ApplyNative(c.Controls[i]);
        }

        static bool Claim(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            return _hooked.Add(hwnd);
        }

        static void Drop(NativeWindow window)
        {
            _roots.Remove(window);
        }

        static void TryDarkTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int use = 1;
                DwmSetWindowAttribute(hwnd, 20, ref use, 4);
                DwmSetWindowAttribute(hwnd, 19, ref use, 4);
            }
            catch { }
        }

        static void ShowLightCaret(Control owner)
        {
            if (owner.IsDisposed || !owner.IsHandleCreated || !owner.Focused) return;
            int h = owner.Font.Height;
            if (h < 8) h = 8;
            IntPtr bmp = CaretBitmap(h);
            if (bmp == IntPtr.Zero) return;
            CreateCaret(owner.Handle, bmp, 0, 0);
            ShowCaret(owner.Handle);
        }

        static IntPtr CaretBitmap(int height)
        {
            if (_caretBmp != IntPtr.Zero && _caretH == height) return _caretBmp;
            if (_caretBmp != IntPtr.Zero)
            {
                DeleteObject(_caretBmp);
                _caretBmp = IntPtr.Zero;
            }
            using (Bitmap bmp = new Bitmap(2, height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Text);
                _caretBmp = bmp.GetHbitmap();
                _caretH = height;
            }
            return _caretBmp;
        }

        sealed class CaretHook : NativeWindow
        {
            readonly Control _owner;
            public CaretHook(Control owner)
            {
                _owner = owner;
                AssignHandle(owner.Handle);
            }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x0002)
                {
                    ReleaseHandle();
                    Drop(this);
                    return;
                }
                if (m.Msg == 0x0007 || m.Msg == 0x0101 || m.Msg == 0x0202)
                {
                    try { _owner.BeginInvoke(new Action(Light)); }
                    catch { }
                }
            }
            void Light()
            {
                ShowLightCaret(_owner);
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);

        [DllImport("user32.dll")]
        static extern bool ShowCaret(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);
    }
}
