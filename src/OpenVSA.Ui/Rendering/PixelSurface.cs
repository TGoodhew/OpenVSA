using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>An axis-aligned rectangle in whole pixels.</summary>
    public readonly struct PixelRect
    {
        /// <summary>Creates a rectangle.</summary>
        /// <param name="x">Left edge.</param>
        /// <param name="y">Top edge.</param>
        /// <param name="width">Width in pixels; must not be negative.</param>
        /// <param name="height">Height in pixels; must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">A dimension is negative.</exception>
        public PixelRect(int x, int y, int width, int height)
        {
            if (width < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width cannot be negative.");
            }

            if (height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height cannot be negative.");
            }

            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Left edge.</summary>
        public int X { get; }

        /// <summary>Top edge.</summary>
        public int Y { get; }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>One past the right edge.</summary>
        public int Right => X + Width;

        /// <summary>One past the bottom edge.</summary>
        public int Bottom => Y + Height;

        /// <summary>Whether the rectangle contains a point.</summary>
        /// <param name="x">Horizontal coordinate.</param>
        /// <param name="y">Vertical coordinate.</param>
        public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    }

    /// <summary>
    /// A BGRA32 pixel buffer that the rasteriser draws into.
    /// </summary>
    /// <remarks>
    /// The layout matches WPF's <c>Bgra32</c> so the WPF surface can hand the buffer straight to
    /// <c>WriteableBitmap.WritePixels</c> with no conversion pass. Nothing here depends on WPF,
    /// which is what lets a test render a frame and sample it without a window.
    /// </remarks>
    public sealed class PixelSurface
    {
        private readonly byte[] _pixels;

        /// <summary>Creates a surface.</summary>
        /// <param name="width">Width in pixels; must be positive.</param>
        /// <param name="height">Height in pixels; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
        public PixelSurface(int width, int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
            }

            Width = width;
            Height = height;
            Stride = width * 4;
            _pixels = new byte[Stride * height];
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Bytes per row.</summary>
        public int Stride { get; }

        /// <summary>The whole surface as a rectangle.</summary>
        public PixelRect Bounds => new PixelRect(0, 0, Width, Height);

        /// <summary>The pixel data, in a view that cannot be written through.</summary>
        /// <remarks>
        /// A view rather than the array, for the reason <c>REQ-DAT-001a</c> gives about
        /// <c>IqBlock</c>: handing out the buffer of something the render loop reuses invites a
        /// caller to hold it across frames and read a half-drawn one.
        /// </remarks>
        public ReadOnlySpan<byte> Pixels => new ReadOnlySpan<byte>(_pixels);

        /// <summary>Copies the pixel data to a caller-provided buffer.</summary>
        /// <param name="destination">Buffer of at least <c>Stride × Height</c> bytes.</param>
        /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
        public void CopyTo(byte[] destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destination.Length < _pixels.Length)
            {
                throw new ArgumentException(
                    "Destination needs at least " + _pixels.Length + " bytes.", nameof(destination));
            }

            Buffer.BlockCopy(_pixels, 0, destination, 0, _pixels.Length);
        }

        /// <summary>Reads one pixel.</summary>
        /// <param name="x">Horizontal coordinate.</param>
        /// <param name="y">Vertical coordinate.</param>
        /// <exception cref="ArgumentOutOfRangeException">The coordinate is outside the surface.</exception>
        public PlotColor GetPixel(int x, int y)
        {
            if (x < 0 || x >= Width)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, "Outside the surface.");
            }

            if (y < 0 || y >= Height)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, "Outside the surface.");
            }

            int offset = y * Stride + x * 4;
            return new PlotColor(_pixels[offset + 2], _pixels[offset + 1], _pixels[offset], _pixels[offset + 3]);
        }

        /// <summary>Writes one pixel, ignoring coordinates outside the surface.</summary>
        /// <param name="x">Horizontal coordinate.</param>
        /// <param name="y">Vertical coordinate.</param>
        /// <param name="color">Colour to write.</param>
        public void SetPixel(int x, int y, PlotColor color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return;
            }

            int offset = y * Stride + x * 4;
            _pixels[offset] = color.B;
            _pixels[offset + 1] = color.G;
            _pixels[offset + 2] = color.R;
            _pixels[offset + 3] = color.A;
        }

        /// <summary>Fills a rectangle, clipped to the surface.</summary>
        /// <param name="rect">Rectangle to fill.</param>
        /// <param name="color">Colour to fill with.</param>
        public void Fill(PixelRect rect, PlotColor color)
        {
            int left = Math.Max(0, rect.X);
            int top = Math.Max(0, rect.Y);
            int right = Math.Min(Width, rect.Right);
            int bottom = Math.Min(Height, rect.Bottom);

            for (int y = top; y < bottom; y++)
            {
                int offset = y * Stride + left * 4;
                for (int x = left; x < right; x++)
                {
                    _pixels[offset] = color.B;
                    _pixels[offset + 1] = color.G;
                    _pixels[offset + 2] = color.R;
                    _pixels[offset + 3] = color.A;
                    offset += 4;
                }
            }
        }

        /// <summary>Draws a vertical run of pixels, clipped to the surface.</summary>
        /// <param name="x">Column.</param>
        /// <param name="top">First row, inclusive.</param>
        /// <param name="bottom">Last row, inclusive.</param>
        /// <param name="color">Colour to draw with.</param>
        public void DrawVertical(int x, int top, int bottom, PlotColor color)
        {
            if (x < 0 || x >= Width)
            {
                return;
            }

            int first = Math.Max(0, Math.Min(top, bottom));
            int last = Math.Min(Height - 1, Math.Max(top, bottom));

            for (int y = first; y <= last; y++)
            {
                int offset = y * Stride + x * 4;
                _pixels[offset] = color.B;
                _pixels[offset + 1] = color.G;
                _pixels[offset + 2] = color.R;
                _pixels[offset + 3] = color.A;
            }
        }

        /// <summary>Draws a horizontal run of pixels, clipped to the surface.</summary>
        /// <param name="y">Row.</param>
        /// <param name="left">First column, inclusive.</param>
        /// <param name="right">Last column, inclusive.</param>
        /// <param name="color">Colour to draw with.</param>
        public void DrawHorizontal(int y, int left, int right, PlotColor color)
        {
            if (y < 0 || y >= Height)
            {
                return;
            }

            int first = Math.Max(0, Math.Min(left, right));
            int last = Math.Min(Width - 1, Math.Max(left, right));

            int offset = y * Stride + first * 4;
            for (int x = first; x <= last; x++)
            {
                _pixels[offset] = color.B;
                _pixels[offset + 1] = color.G;
                _pixels[offset + 2] = color.R;
                _pixels[offset + 3] = color.A;
                offset += 4;
            }
        }
    }
}
