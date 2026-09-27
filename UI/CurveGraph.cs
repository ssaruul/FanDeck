using System.Drawing.Drawing2D;
using System.Drawing.Text;
using FanDeck.Cooling;

namespace FanDeck.UI;

/// <summary>
/// Temperature → fan-speed chart. Drag a point to reshape the curve, double-click to add one,
/// right-click one to remove it. Dots show where each fan is right now.
/// </summary>
sealed class CurveGraph : Control
{
    /// <param name="Selected">The fan whose curve is being edited; its dot is drawn larger.</param>
    public sealed record LiveDot(string Name, float Temp, float Percent, bool Selected = false);

    const float FloorPercent = 20;
    const float PanicTemp = 95;
    static readonly Color[] DotColors =
        [Color.FromArgb(0x1F, 0x29, 0x37), Color.FromArgb(0x0E, 0x74, 0x90), Color.FromArgb(0x7C, 0x3A, 0xED)];

    List<CurvePoint>? _points;
    IReadOnlyList<IReadOnlyList<CurvePoint>> _others = [];
    IReadOnlyList<LiveDot> _live = [];
    bool _editable;
    Color _accent = Color.SeaGreen;
    int _drag = -1;
    int _hover = -1;

    /// <summary>Raised when a drag ends or a point is added or removed.</summary>
    public event Action<CurvePoint[]>? CurveEdited;

    public CurveGraph()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
    }

    /// <summary>
    /// Shows a curve (null for none), with the other fans' curves faint behind it.
    /// Ignored mid-drag so the curve isn't pulled from under the mouse.
    /// </summary>
    public void SetCurve(IReadOnlyList<CurvePoint>? points, bool editable, Color accent,
        IReadOnlyList<IReadOnlyList<CurvePoint>>? others = null)
    {
        if (_drag >= 0)
            return;
        _points = points?.ToList();
        _others = others ?? [];
        _editable = editable && _points is not null;
        _accent = accent;
        if (_hover >= (_points?.Count ?? 0))
            _hover = -1;
        Invalidate();
    }

    public void SetLive(IReadOnlyList<LiveDot> dots)
    {
        _live = dots;
        Invalidate();
    }

    int Px(int logical) => LogicalToDeviceUnits(logical);

    Rectangle Plot => Rectangle.FromLTRB(Px(42), Px(12), Width - Px(16), Height - Px(26));

    float X(float temp) => Plot.Left + (temp - Profiles.MinTemp) / (Profiles.MaxTemp - Profiles.MinTemp) * Plot.Width;

    float Y(float percent) => Plot.Bottom - percent / 100f * Plot.Height;

    float TempAt(int x) => Profiles.MinTemp + (x - Plot.Left) * (Profiles.MaxTemp - Profiles.MinTemp) / Plot.Width;

    float PercentAt(int y) => (Plot.Bottom - y) * 100f / Plot.Height;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var plot = Plot;

        using var small = new Font(Font.FontFamily, 8f);
        using var grid = new Pen(Color.FromArgb(232, 232, 232));
        using var label = new SolidBrush(Color.DimGray);

        // Below the minimum speed: fans never run in this band.
        using (var band = new SolidBrush(Color.FromArgb(244, 244, 244)))
            g.FillRectangle(band, plot.Left, Y(FloorPercent), plot.Width, plot.Bottom - Y(FloorPercent));

        for (int t = 20; t <= 100; t += 10)
        {
            float x = X(t);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            if (t % 20 == 0)
                DrawText(g, $"{t}°C", small, label, x, plot.Bottom + Px(4), ContentAlignment.TopCenter);
        }
        for (int p = 0; p <= 100; p += 25)
        {
            float y = Y(p);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            DrawText(g, $"{p}%", small, label, plot.Left - Px(6), y, ContentAlignment.MiddleRight);
        }

        // Safety: at 95 °C every fan runs flat out, whatever the curve says.
        using (var panic = new Pen(Color.FromArgb(225, 110, 110), Px(1)) { DashStyle = DashStyle.Dash })
            g.DrawLine(panic, X(PanicTemp), plot.Top, X(PanicTemp), plot.Bottom);
        using (var border = new Pen(Color.FromArgb(210, 210, 210)))
            g.DrawRectangle(border, plot);

        using (var ghost = new Pen(Color.FromArgb(185, 185, 185), Px(1)) { DashStyle = DashStyle.Dash })
        {
            foreach (var other in _others.Where(o => o.Count > 0))
                g.DrawLines(ghost, CurveLine(other).ToArray());
        }

        if (_points is { Count: > 0 } pts)
            DrawCurve(g, pts, plot);

        // Fans at the same spot (often the CPU and chassis fan) share one dot and one label.
        var groups = _live
            .GroupBy(d => (Temp: MathF.Round(d.Temp), Percent: MathF.Round(d.Percent)))
            .Select(grp => new LiveDot(
                string.Join(" + ", grp.Select(d => d.Name)), grp.Key.Temp, grp.Key.Percent, grp.Any(d => d.Selected)))
            .ToList();
        var labels = new List<RectangleF>();
        for (int i = 0; i < groups.Count; i++)
            DrawDot(g, groups[i], DotColors[i % DotColors.Length], small, labels);

        int shown = _drag >= 0 ? _drag : _hover;
        if (_editable && _points is not null && shown >= 0 && shown < _points.Count)
        {
            var p = _points[shown];
            DrawBadge(g, $"{p.Temp:0} °C → {p.Percent:0}%", X(p.Temp), Y(p.Percent) - Px(12));
        }
    }

    /// <summary>The curve as screen points, extended flat to both edges of the chart.</summary>
    List<PointF> CurveLine(IReadOnlyList<CurvePoint> pts)
    {
        var line = new List<PointF> { new(X(Profiles.MinTemp), Y(pts[0].Percent)) };
        line.AddRange(pts.Select(p => new PointF(X(p.Temp), Y(p.Percent))));
        line.Add(new PointF(X(Profiles.MaxTemp), Y(pts[^1].Percent)));
        return line;
    }

    void DrawCurve(Graphics g, List<CurvePoint> pts, Rectangle plot)
    {
        var line = CurveLine(pts);

        var area = line.Concat([new PointF(plot.Right, plot.Bottom), new PointF(plot.Left, plot.Bottom)]).ToArray();
        using (var fill = new SolidBrush(Color.FromArgb(34, _accent)))
            g.FillPolygon(fill, area);
        using (var pen = new Pen(_accent, Px(2)) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, line.ToArray());

        if (!_editable)
            return;
        float r = Px(6);
        using var ring = new Pen(_accent, Px(2));
        using var solid = new SolidBrush(_accent);
        for (int i = 0; i < pts.Count; i++)
        {
            float x = X(pts[i].Temp), y = Y(pts[i].Percent);
            g.FillEllipse(i == _drag || i == _hover ? solid : Brushes.White, x - r, y - r, 2 * r, 2 * r);
            g.DrawEllipse(ring, x - r, y - r, 2 * r, 2 * r);
        }
    }

    void DrawDot(Graphics g, LiveDot dot, Color color, Font font, List<RectangleF> taken)
    {
        float x = X(Math.Clamp(dot.Temp, Profiles.MinTemp, Profiles.MaxTemp));
        float y = Y(Math.Clamp(dot.Percent, 0, 100));
        float r = Px(dot.Selected ? 7 : 5);
        using var brush = new SolidBrush(color);
        using var halo = new Pen(Color.White, Px(2));
        g.DrawEllipse(halo, x - r, y - r, 2 * r, 2 * r);
        g.FillEllipse(brush, x - r, y - r, 2 * r, 2 * r);

        string text = $"{dot.Name} {dot.Temp:0}°";
        var size = g.MeasureString(text, font);
        bool left = x + r + size.Width + Px(4) > Plot.Right;
        float tx = left ? x - r - Px(3) - size.Width : x + r + Px(3);
        var label = new RectangleF(tx, y + Px(2), size.Width, size.Height);
        // Step down past any label already drawn so two dots close together stay readable.
        while (taken.Any(t => t.IntersectsWith(label)))
            label.Y += size.Height;
        taken.Add(label);
        g.DrawString(text, font, brush, label.X, label.Y);
    }

    void DrawBadge(Graphics g, string text, float cx, float bottom)
    {
        var size = g.MeasureString(text, Font);
        var box = new RectangleF(cx - size.Width / 2 - Px(4), bottom - size.Height - Px(2), size.Width + Px(8), size.Height + Px(2));
        box.X = Math.Clamp(box.X, 0, Math.Max(0, Width - box.Width));
        box.Y = Math.Max(0, box.Y);
        using var back = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        using var border = new Pen(Color.FromArgb(200, 200, 200));
        g.FillRectangle(back, box);
        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
        g.DrawString(text, Font, Brushes.Black, box.X + Px(4), box.Y + Px(1));
    }

    static void DrawText(Graphics g, string text, Font font, Brush brush, float x, float y, ContentAlignment align)
    {
        var size = g.MeasureString(text, font);
        float left = align switch
        {
            ContentAlignment.TopCenter => x - size.Width / 2,
            ContentAlignment.MiddleRight => x - size.Width,
            _ => x,
        };
        float top = align == ContentAlignment.MiddleRight ? y - size.Height / 2 : y;
        g.DrawString(text, font, brush, left, top);
    }

    int HitTest(Point location)
    {
        if (_points is null)
            return -1;
        float reach = Px(10);
        int best = -1;
        float bestDistance = reach * reach;
        for (int i = 0; i < _points.Count; i++)
        {
            float dx = X(_points[i].Temp) - location.X, dy = Y(_points[i].Percent) - location.Y;
            float d = dx * dx + dy * dy;
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_editable || _points is null)
            return;
        int hit = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && hit >= 0)
        {
            _drag = hit;
            Capture = true;
        }
        else if (e.Button == MouseButtons.Right && hit >= 0 && _points.Count > 2)
        {
            _points.RemoveAt(hit);
            _hover = -1;
            Commit();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_editable || _points is null)
            return;

        if (_drag >= 0)
        {
            // A point stays between its neighbours: one degree apart, and never slower than the point before it.
            bool first = _drag == 0, last = _drag == _points.Count - 1;
            float lowT = first ? Profiles.MinTemp : _points[_drag - 1].Temp + 1;
            float highT = last ? Profiles.MaxTemp : _points[_drag + 1].Temp - 1;
            float lowP = first ? 0 : _points[_drag - 1].Percent;
            float highP = last ? 100 : _points[_drag + 1].Percent;
            float t = Clamp(MathF.Round(TempAt(e.X)), lowT, highT);
            float p = Clamp(MathF.Round(PercentAt(e.Y)), lowP, highP);
            _points[_drag] = new CurvePoint(t, p);
            Invalidate();
            return;
        }

        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0)
            return;
        _drag = -1;
        Capture = false;
        Commit();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!_editable || _points is null || e.Button != MouseButtons.Left || HitTest(e.Location) >= 0)
            return;
        if (_points.Count >= Profiles.MaxPoints || !Plot.Contains(e.Location))
            return;

        float t = MathF.Round(TempAt(e.X));
        if (_points.Any(p => Math.Abs(p.Temp - t) < 1))
            return;
        int index = _points.FindIndex(p => p.Temp > t);
        if (index < 0)
            index = _points.Count;
        float lowP = index > 0 ? _points[index - 1].Percent : 0;
        float highP = index < _points.Count ? _points[index].Percent : 100;
        _points.Insert(index, new CurvePoint(t, Clamp(MathF.Round(PercentAt(e.Y)), lowP, highP)));
        Commit();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0 && _drag < 0)
        {
            _hover = -1;
            Invalidate();
        }
    }

    void Commit()
    {
        Invalidate();
        if (_points is not null)
            CurveEdited?.Invoke(_points.ToArray());
    }

    /// <summary>Like Math.Clamp, but tolerates lo &gt; hi (neighbours already touching) instead of throwing.</summary>
    static float Clamp(float value, float lo, float hi) => hi < lo ? lo : Math.Min(Math.Max(value, lo), hi);
}
