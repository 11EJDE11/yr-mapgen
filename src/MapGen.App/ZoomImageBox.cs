using System.Drawing.Drawing2D;

namespace MapGen.App;

/// <summary>
/// Shows an image fitted to the control; the mouse wheel zooms around the cursor, dragging pans and
/// a double-click fits the whole image again.
/// </summary>
internal sealed class ZoomImageBox : Control
{
    private Image image;
    private float zoom = 1;         // screen pixels per image pixel
    private PointF offset;          // screen position of the image's top-left corner
    private bool fitted = true;     // follow the control's size until the user zooms or pans
    private Point? dragFrom;

    public ZoomImageBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public Image Image
    {
        get => image;
        set
        {
            image = value;
            Fit();
        }
    }

    private float FitZoom => image == null ? 1 : Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);

    private void Fit()
    {
        fitted = true;
        if (image != null)
        {
            zoom = FitZoom;
            offset = new PointF((ClientSize.Width - image.Width * zoom) / 2, (ClientSize.Height - image.Height * zoom) / 2);
        }
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (fitted)
            Fit();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (image == null)
            return;

        // Zoom about the point under the cursor; never smaller than fitting the whole map.
        float min = FitZoom, max = Math.Max(min, 6);
        float next = Math.Clamp(zoom * (e.Delta > 0 ? 1.25f : 0.8f), min, max);
        if (next == min)
        {
            Fit();
            return;
        }
        float ix = (e.X - offset.X) / zoom, iy = (e.Y - offset.Y) / zoom;
        zoom = next;
        offset = new PointF(e.X - ix * zoom, e.Y - iy * zoom);
        fitted = false;
        ClampOffset();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus(); // wheel events go to the focused control
        if (e.Button == MouseButtons.Left)
            dragFrom = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragFrom is not Point from || fitted)
            return;
        offset = new PointF(offset.X + e.X - from.X, offset.Y + e.Y - from.Y);
        dragFrom = e.Location;
        ClampOffset();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragFrom = null;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Fit();
    }

    /// <summary>Keeps the image covering the view (or centred along an axis where it is smaller).</summary>
    private void ClampOffset()
    {
        float w = image.Width * zoom, h = image.Height * zoom;
        float x = w <= ClientSize.Width ? (ClientSize.Width - w) / 2 : Math.Clamp(offset.X, ClientSize.Width - w, 0);
        float y = h <= ClientSize.Height ? (ClientSize.Height - h) / 2 : Math.Clamp(offset.Y, ClientSize.Height - h, 0);
        offset = new PointF(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (image == null)
            return;
        e.Graphics.InterpolationMode = zoom < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(image, new RectangleF(offset.X, offset.Y, image.Width * zoom, image.Height * zoom));
    }
}
