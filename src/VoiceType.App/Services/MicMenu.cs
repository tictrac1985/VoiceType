using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace VoiceType.App.Services;

/// <summary>قوائم الزر الأيمن (زر المايك وأيقونة الساعة) بشكل حديث: خلفية بيضاء، تحديد ناعم، ومسافات مريحة.</summary>
internal static class MicMenu
{
    public static Font MenuFont { get; } = new("Segoe UI", 10f);

    private static readonly Font BoldFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Image TickImage = Tick();
    private static readonly Dictionary<string, Image> Swatches = [];

    public static Forms.ToolStripRenderer Renderer { get; } = new ModernRenderer();

    public static Forms.ContextMenuStrip Create() => new()
    {
        RightToLeft = Forms.RightToLeft.Yes,
        Renderer = Renderer,
        Font = MenuFont,
        ShowImageMargin = true,
        ShowCheckMargin = false,
        Padding = new Forms.Padding(4),
    };

    public static Forms.ToolStripMenuItem Item(string text, Action? onClick, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text) { Padding = new Forms.Padding(4, 5, 4, 5) };
        if (bold)
        {
            item.Font = BoldFont;
        }

        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        item.DropDownOpening += (_, _) =>
        {
            if (item.DropDown is Forms.ToolStripDropDownMenu drop)
            {
                drop.RightToLeft = Forms.RightToLeft.Yes;
                drop.Renderer = Renderer;
                drop.Font = MenuFont;
            }
        };
        return item;
    }

    public static Forms.ToolStripMenuItem Check(string text, bool on, Action onClick)
    {
        var item = Item(text, onClick);
        item.Image = on ? TickImage : null;
        return item;
    }

    public static Forms.ToolStripMenuItem Radio(string text, bool on, Action onClick)
    {
        var item = Item(text, onClick);
        item.Image = on ? TickImage : null;
        if (on)
        {
            item.Font = BoldFont;
        }

        return item;
    }

    /// <summary>دائرة صغيرة بلون الزر بجانب اسم اللون.</summary>
    public static Image Swatch(string id, string topHex, string bottomHex)
    {
        if (Swatches.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new LinearGradientBrush(new Rectangle(0, 0, 16, 16), ColorTranslator.FromHtml(topHex), ColorTranslator.FromHtml(bottomHex), 45f);
        g.FillEllipse(brush, 1, 1, 14, 14);
        Swatches[id] = bitmap;
        return bitmap;
    }

    private static Image Tick()
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(79, 70, 229), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new PointF(3.5f, 8.5f), new PointF(6.8f, 11.8f), new PointF(12.5f, 4.5f)]);
        return bitmap;
    }

    private sealed class ModernRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.White;
        private static readonly Color Hover = Color.FromArgb(238, 242, 255);
        private static readonly Color Border = Color.FromArgb(226, 232, 240);
        private static readonly Color Text = Color.FromArgb(17, 24, 39);

        public ModernRenderer()
            : base(new Colors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            using (var back = new SolidBrush(Back))
            {
                e.Graphics.FillRectangle(back, bounds);
            }

            if (e.Item.Selected && e.Item.Enabled)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(bounds.X + 2, bounds.Y + 1, bounds.Width - 4, bounds.Height - 2);
                using var path = Rounded(r, 6);
                using var brush = new SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(100, 116, 139);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var back = new SolidBrush(Back);
            e.Graphics.FillRectangle(back, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class Colors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.White;

        public override Color ImageMarginGradientBegin => Color.White;

        public override Color ImageMarginGradientMiddle => Color.White;

        public override Color ImageMarginGradientEnd => Color.White;

        public override Color MenuBorder => Color.FromArgb(226, 232, 240);

        public override Color MenuItemBorder => Color.Transparent;

        public override Color MenuItemSelected => Color.FromArgb(238, 242, 255);
    }
}
