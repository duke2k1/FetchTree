using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FetchTree
{
    public sealed class BrandPanel : Panel
    {
        public BrandPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(9, 24, 47), Color.FromArgb(13, 68, 94), 12f)) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(24, 103, 145), 1))
            {
                for (int x = Width / 2; x < Width + 140; x += 55) e.Graphics.DrawLine(pen, x, 0, x - 110, Height);
            }
            using (var line = new Pen(Color.FromArgb(20, 181, 207), 2)) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }
}
