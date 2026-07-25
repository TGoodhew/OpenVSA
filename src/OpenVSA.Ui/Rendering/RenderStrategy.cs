using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// How a trace of a given size is drawn (<c>REQ-NFR-005</c>).
    /// </summary>
    public enum RenderStrategy
    {
        /// <summary>
        /// WPF <c>Polyline</c>/<c>Path</c> with per-point geometry. Permitted only at or below
        /// <see cref="RenderStrategySelector.PolylineLimit"/> points.
        /// </summary>
        PolylineGeometry = 0,

        /// <summary>
        /// <c>DrawingVisual</c> with <c>StreamGeometry</c>. Removes the per-element overhead but
        /// goes through the same tessellator, so it does not reach the top band.
        /// </summary>
        StreamGeometry,

        /// <summary>
        /// <c>WriteableBitmap</c> with the software rasteriser. The only strategy that scales to
        /// the point counts of <c>REQ-NFR-021</c>, and the one that survives RDP.
        /// </summary>
        SoftwareRasterizer,
    }

    /// <summary>
    /// Chooses a <see cref="RenderStrategy"/> from a trace's point count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-NFR-005</c> requires the choice to be <em>observable</em> rather than implicit in
    /// whichever drawing call a control happens to make. That is why this is a function returning
    /// a value a test can assert on, and not a private branch inside a rendering method.
    /// </para>
    /// <para>
    /// <strong>The reason the bands exist is not the one usually given.</strong> The cost at high
    /// point counts is MilCore's anti-aliased geometry tessellation on the render thread plus
    /// per-<c>Point</c> managed→native marshalling — not the size of the visual tree.
    /// <c>StreamGeometry</c> inside a <c>DrawingVisual</c> removes the per-<em>element</em>
    /// overhead and then meets the same tessellator, which is why it earns a middle band and not
    /// the top one.
    /// </para>
    /// </remarks>
    public static class RenderStrategySelector
    {
        /// <summary>Largest point count for which per-point <c>Polyline</c>/<c>Path</c> geometry is permitted.</summary>
        public const int PolylineLimit = 2000;

        /// <summary>Largest point count for which <c>DrawingVisual</c> + <c>StreamGeometry</c> is permitted.</summary>
        public const int StreamGeometryLimit = 20000;

        /// <summary>
        /// Selects the strategy for a trace.
        /// </summary>
        /// <param name="pointCount">Points to be drawn, after any decimation.</param>
        /// <returns>The permitted strategy for that size.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointCount"/> is negative.</exception>
        public static RenderStrategy Select(int pointCount)
        {
            if (pointCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pointCount), pointCount, "Point count cannot be negative.");
            }

            if (pointCount <= PolylineLimit)
            {
                return RenderStrategy.PolylineGeometry;
            }

            return pointCount <= StreamGeometryLimit
                ? RenderStrategy.StreamGeometry
                : RenderStrategy.SoftwareRasterizer;
        }

        /// <summary>
        /// Whether <paramref name="strategy"/> is permitted for <paramref name="pointCount"/> points.
        /// </summary>
        /// <param name="strategy">Proposed strategy.</param>
        /// <param name="pointCount">Points to be drawn.</param>
        /// <remarks>
        /// A strategy cheaper than the selected one is always a violation; a more expensive one is
        /// merely wasteful and is allowed, which is what makes the RDP fallback of
        /// <c>REQ-NFR-005</c> expressible — dropping to the software rasteriser at any size is
        /// legitimate, dropping <em>up</em> to per-point geometry never is.
        /// </remarks>
        public static bool IsPermitted(RenderStrategy strategy, int pointCount) =>
            strategy >= Select(pointCount);
    }
}
