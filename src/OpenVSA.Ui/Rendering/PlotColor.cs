using System;
using System.Globalization;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// A 32-bit colour, independent of any presentation framework.
    /// </summary>
    /// <remarks>
    /// The rasteriser deliberately does not use <c>System.Windows.Media.Color</c>. Keeping the
    /// render core free of WPF types is what lets <c>REQ-UI-010</c>'s acceptance criterion — set
    /// four colours, render, sample the frame — run headlessly in CI rather than needing a window
    /// and a message pump.
    /// </remarks>
    public readonly struct PlotColor : IEquatable<PlotColor>
    {
        /// <summary>Creates an opaque colour.</summary>
        /// <param name="r">Red channel.</param>
        /// <param name="g">Green channel.</param>
        /// <param name="b">Blue channel.</param>
        public PlotColor(byte r, byte g, byte b)
            : this(r, g, b, 255)
        {
        }

        /// <summary>Creates a colour.</summary>
        /// <param name="r">Red channel.</param>
        /// <param name="g">Green channel.</param>
        /// <param name="b">Blue channel.</param>
        /// <param name="a">Alpha channel.</param>
        public PlotColor(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>Red channel.</summary>
        public byte R { get; }

        /// <summary>Green channel.</summary>
        public byte G { get; }

        /// <summary>Blue channel.</summary>
        public byte B { get; }

        /// <summary>Alpha channel.</summary>
        public byte A { get; }

        /// <summary>Opaque black.</summary>
        public static PlotColor Black => new PlotColor(0, 0, 0);

        /// <summary>Opaque white.</summary>
        public static PlotColor White => new PlotColor(255, 255, 255);

        /// <summary>Creates a colour from a packed <c>0xAARRGGBB</c> value.</summary>
        /// <param name="argb">Packed colour.</param>
        public static PlotColor FromArgb(uint argb) => new PlotColor(
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF),
            (byte)((argb >> 24) & 0xFF));

        /// <inheritdoc />
        public bool Equals(PlotColor other) =>
            R == other.R && G == other.G && B == other.B && A == other.A;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is PlotColor other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;

        /// <summary>Equality.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        public static bool operator ==(PlotColor left, PlotColor right) => left.Equals(right);

        /// <summary>Inequality.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        public static bool operator !=(PlotColor left, PlotColor right) => !left.Equals(right);

        /// <inheritdoc />
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", A, R, G, B);
    }
}
