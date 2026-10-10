using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SonarController
{
    /// <summary>
    /// The on-screen volume indicator shown when a media key is pressed, drawn
    /// in the shape of the Windows 11 volume flyout but naming the device the
    /// volume actually belongs to.
    ///
    /// It deliberately stays out of the way: topmost so it floats over other
    /// windows, click-through so it cannot swallow a click, and non-activating
    /// so it never pulls focus away from whatever the user is doing. All of that
    /// is available to an ordinary user process, so no elevation is involved.
    /// </summary>
    internal sealed class VolumeOverlay : Form
    {
        private const int HideDelayMs = 1400;

        // Laid out at 96 DPI and scaled by DeviceDpi at construction.
        private const int BaseWidth = 344;
        private const int BaseHeight = 92;
        private const int BasePadding = 16;
        private const int BaseIconSize = 24;
        private const int BaseBarHeight = 6;
        private const int BaseCornerRadius = 8;
        private const int BaseBottomMargin = 48;

        // DWM paints a light 1px frame onto the topmost row and leftmost column
        // of a window rather than outside it, which stacks on top of a border
        // drawn by hand and makes those two edges read as thicker. The window is
        // made this many pixels larger than the panel and offset the same amount
        // up and to the left, so the frame lands on pixels the rounded clip then
        // discards and never on the panel itself.
        private const int FrameBypass = 1;

        // Icons are the real Windows glyphs from Segoe MDL2 Assets, which is the
        // font the shell itself draws these with. Codepoints come from the
        // published glyph list: the neighbouring U+E996 is BatteryUnknown, not a
        // volume icon, which is easy to get wrong from memory.
        private const char MuteGlyph = '\uE74F';      // Mute
        private const char VolumeZeroGlyph = '\uE992'; // Volume0
        private const char VolumeLowGlyph = '\uE993';  // Volume1
        private const char VolumeMidGlyph = '\uE994';  // Volume2
        private const char VolumeHighGlyph = '\uE995'; // Volume3

        private static readonly string[] IconFonts = ["Segoe MDL2 Assets", "Segoe Fluent Icons"];
        private static readonly string? IconFont = FindIconFont();

        private static readonly Color PanelColor = Color.FromArgb(32, 32, 32);
        private static readonly Color BorderColor = Color.FromArgb(70, 70, 70);
        private static readonly Color BarTrackColor = Color.FromArgb(64, 64, 64);
        private static readonly Color BarFillColor = Color.FromArgb(96, 205, 255);
        private static readonly Color TextColor = Color.White;
        private static readonly Color SubTextColor = Color.FromArgb(170, 170, 170);

        private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = HideDelayMs };
        private readonly int _width;
        private readonly int _height;
        private readonly int _padding;
        private readonly int _iconSize;
        private readonly int _barHeight;
        private readonly int _cornerRadius;
        private readonly int _bottomMargin;

        private float _level;
        private bool _muted;
        private string _deviceName = "";

        public VolumeOverlay()
        {
            var scale = DeviceDpi / 96f;

            _width = Scale(BaseWidth, scale);
            _height = Scale(BaseHeight, scale);
            _padding = Scale(BasePadding, scale);
            _iconSize = Scale(BaseIconSize, scale);
            _barHeight = Scale(BaseBarHeight, scale);
            _cornerRadius = Scale(BaseCornerRadius, scale);
            _bottomMargin = Scale(BaseBottomMargin, scale);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;

            // Keeps the flyout translucent over whatever is behind it.
            Opacity = 0.94;

            // The overlay repaints itself on every key press. Without a back
            // buffer the window briefly shows through between the erase and the
            // paint, which reads as the panel flickering.
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;

            Size = new Size(_width + FrameBypass, _height + FrameBypass);

            _hideTimer.Tick += (_, _) => Hide();
        }

        /// <summary>
        /// Keeps the flyout out of the activation order so showing it never
        /// steals focus from the foreground window.
        /// </summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;

                // TRANSPARENT: clicks fall through. NOACTIVATE: no focus change.
                // TOOLWINDOW: stays out of the taskbar and Alt+Tab.
                cp.ExStyle |= 0x00000020 | 0x08000000 | 0x00000080;
                return cp;
            }
        }

        /// <summary>
        /// Closed rounded-rectangle outline. Used both for the window's clip and
        /// for the border drawn inside it, so the two always share a shape.
        /// </summary>
        private GraphicsPath RoundedRect(int x, int y, int w, int h, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();

            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Clipping the window to a rounded rectangle is what gives the panel
            // its shape; without it the corners would be square. The clip also
            // swallows the bypass strip DWM's frame is left to land on.
            using var path = RoundedRect(FrameBypass, FrameBypass, _width, _height, _cornerRadius);
            Region = new Region(path);
        }

        /// <summary>
        /// Updates the flyout and restarts its hide timer. Safe to call on every
        /// key press; the window is only created once and reused.
        /// </summary>
        public void Display(float level, bool muted, string deviceName)
        {
            _level = Math.Clamp(level, 0f, 1f);
            _muted = muted;
            _deviceName = deviceName;

            if (!Visible)
            {
                PositionAtBottomCentre();
                Show();
            }

            Invalidate();

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        /// <summary>
        /// Bottom-centre of whichever monitor the pointer is on, so it appears
        /// where the user is looking rather than on a possibly distant display.
        /// </summary>
        private void PositionAtBottomCentre()
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;

            // Positioned by where the panel must sit, not the window: the window
            // is a pixel larger and hugs the panel's top-left corner.
            Location = new Point(
                area.Left + (area.Width - _width) / 2 - FrameBypass,
                area.Bottom - _height - _bottomMargin - FrameBypass);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            // Drawn in panel coordinates throughout. The panel does not start at
            // the window's top-left corner, so the origin is moved once here
            // rather than every position below having to know about it.
            g.TranslateTransform(FrameBypass, FrameBypass);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ClearType's subpixel fringing assumes an opaque backdrop and shows
            // up as coloured halos once the window is translucent, so the text is
            // antialiased in grayscale instead.
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            using (var panel = new SolidBrush(PanelColor))
                g.FillRectangle(panel, 0, 0, _width, _height);

            var scale = DeviceDpi / 96f;

            var percentWidth = Scale(46, scale);
            var iconTop = _padding + Scale(2, scale);

            DrawIcon(g, new Rectangle(_padding, iconTop, _iconSize, _iconSize));

            var barLeft = _padding + _iconSize + Scale(10, scale);
            var barWidth = _width - barLeft - _padding - percentWidth;
            var barTop = _padding + _iconSize / 2 - _barHeight / 2;

            DrawVolumeBar(g, new Rectangle(barLeft, barTop, barWidth, _barHeight), _level);

            using (var percentFont = new Font("Segoe UI", Scale(15, scale), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var percentBrush = new SolidBrush(TextColor))
            using (var centred = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                g.DrawString($"{_level * 100:F0}%", percentFont, percentBrush,
                    new Rectangle(_width - _padding - percentWidth, iconTop - Scale(2, scale), percentWidth, _iconSize + Scale(4, scale)),
                    centred);
            }

            using (var deviceFont = new Font("Segoe UI", Scale(12, scale), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var deviceBrush = new SolidBrush(SubTextColor))
            using (var left = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            {
                var textWidth = _width - barLeft - _padding;

                g.DrawString(_deviceName, deviceFont, deviceBrush,
                    new RectangleF(barLeft, _height - _padding - Scale(15, scale), textWidth, Scale(18, scale)),
                    left);
            }

            // Drawn last so it sits on top of the fill. A stroked outline cannot
            // be used here: a 1px stroke sits either side of its path, so it
            // cannot cover the panel's first row and last row with the same line
            // and the two edges come out different thicknesses. Filling the gap
            // between the panel's outline and a 1px-inset copy keeps every edge
            // exactly one pixel, and shares the panel's rounded outline so the
            // corners are followed rather than clipped away.
            var ring = new GraphicsPath { FillMode = FillMode.Alternate };
            ring.AddPath(RoundedRect(0, 0, _width, _height, _cornerRadius), false);
            ring.AddPath(RoundedRect(1, 1, _width - 2, _height - 2, _cornerRadius - 1), false);

            using (var edge = new SolidBrush(BorderColor))
            using (ring)
            {
                // Filled without antialiasing on purpose. A one pixel outline is
                // thinner than the sampling grid can place cleanly, so softening
                // it spreads the same amount of ink over two rows and the border
                // reads as a blurry double line.
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;

                g.FillPath(edge, ring);

                g.SmoothingMode = smoothing;
            }
        }

        /// <summary>
        /// Picks the icon Windows would show: a crossed-out speaker when muted,
        /// otherwise one more sound wave for each step up the level.
        /// </summary>
        private void DrawIcon(Graphics g, Rectangle box)
        {
            if (IconFont is null)
                return;

            var glyph = _muted ? MuteGlyph
                : _level <= 0.001f ? VolumeZeroGlyph
                : _level < 0.34f ? VolumeLowGlyph
                : _level < 0.67f ? VolumeMidGlyph
                : VolumeHighGlyph;

            // The MDL2 glyphs sit close to the em square, so the point size is a
            // little larger than the box to fill it without being clipped.
            using var font = new Font(IconFont, box.Height * 1.22f, FontStyle.Regular, GraphicsUnit.Pixel);

            TextRenderer.DrawText(
                g,
                glyph.ToString(),
                font,
                Rectangle.Inflate(box, Scale(4, DeviceDpi / 96f), 0),
                TextColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding);
        }

        private void DrawVolumeBar(Graphics g, Rectangle bounds, float level)
        {
            using (var track = new SolidBrush(BarTrackColor))
                g.FillRectangle(track, bounds);

            if (level <= 0.001f)
                return;

            var width = (int)Math.Round(bounds.Width * level);

            using var fill = new SolidBrush(BarFillColor);

            // Rounding a short bar needs an ellipse at least as wide as it is
            // tall, so below that a plain rectangle is the honest rendering.
            if (width < bounds.Height)
            {
                g.FillRectangle(fill, bounds.X, bounds.Y, width, bounds.Height);
                return;
            }

            var filled = new Rectangle(bounds.X, bounds.Y, width, bounds.Height);
            var d = bounds.Height;

            // Two semicircular caps joined by straight edges: a pill.
            using var path = new GraphicsPath();
            path.AddArc(filled.X, filled.Y, d, d, 90, 180);
            path.AddArc(filled.Right - d, filled.Y, d, d, 270, 180);
            path.CloseFigure();

            g.FillPath(fill, path);
        }

        private static string? FindIconFont()
        {
            using var installed = new InstalledFontCollection();
            var families = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var found = IconFonts.FirstOrDefault(families.Contains);

            if (found is null)
                Log.Warn("Neither Segoe MDL2 Assets nor Segoe Fluent Icons is installed; the volume flyout will have no icon.");

            return found;
        }

        private static int Scale(int value, float scale) =>
            (int)Math.Round(value * scale);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hideTimer.Dispose();

            base.Dispose(disposing);
        }
    }
}