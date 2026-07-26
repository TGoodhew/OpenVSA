using System;
using System.Globalization;
using OpenVSA.Dsp.Zoom;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// What happens to the frequency axis when the span changes (<c>REQ-DSP-023</c>'s
    /// <em>Zoom If Span Change</em>).
    /// </summary>
    public enum SpanChangeBehaviour
    {
        /// <summary>
        /// The centre frequency is held and the span narrows around it — a zoom.
        /// </summary>
        /// <remarks>
        /// What a reader almost always means by reducing the span: keep looking at the same thing,
        /// look at it more closely.
        /// </remarks>
        Zoom = 0,

        /// <summary>
        /// The start frequency is held and the centre moves to suit the new span.
        /// </summary>
        /// <remarks>
        /// A swept analyser's behaviour, and the one <c>REQ-DSP-023</c> names: for a baseband
        /// measurement the start is 0 Hz, so reducing the span keeps the display anchored there
        /// instead of walking up the axis. Stated as "hold the start" rather than "hold 0 Hz"
        /// because the rule is the same at any start frequency, and a rule written for the
        /// baseband case would quietly do nothing anywhere else.
        /// </remarks>
        HoldStartFrequency,
    }

    /// <summary>
    /// Where the analysis sits inside a captured band, and how far in it may go
    /// (<c>REQ-DSP-023</c>, <c>REQ-REC-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the policy; <see cref="DigitalDownconverter"/> is the arithmetic.</strong>
    /// The downconverter knows how to move a band to zero and decimate to it, and deliberately
    /// knows nothing about how narrow a zoom is allowed to be — that bound is a fraction of the
    /// <em>source span</em>, which the downconverter never sees. Keeping it here is what makes
    /// <c>REQ-REC-004</c>'s "the two must not disagree" structural rather than a matter of two
    /// call sites remembering to check the same thing.
    /// </para>
    /// <para>
    /// <strong>One bound and one message, for live blocks and playback alike.</strong> A recording
    /// re-analysed and a live block zoomed into are the same downconversion of the same kind of
    /// samples; both arrive here with a source centre and a source span, and there is no second
    /// path for either to take.
    /// </para>
    /// <para>
    /// <strong>The span is rejected; the centre is clamped.</strong> Not an inconsistency —
    /// <c>REQ-REC-004</c> requires a span past the bound to be refused with the bound named, and a
    /// zoom silently stopping at 1/256 would look exactly like a zoom that worked. A centre
    /// frequency that would push the analysis off the end of the captured band is a different
    /// case: there is simply no data out there, the nearest position that fits is unambiguous, and
    /// the move is written on the frequency axis where the reader is already looking.
    /// </para>
    /// </remarks>
    public sealed class ZoomControl
    {
        /// <summary>
        /// The deepest zoom allowed, as a ratio of source span to analysis span
        /// (<c>REQ-REC-004</c>).
        /// </summary>
        /// <remarks>
        /// The reference product's documented playback bound, adopted so that a measurement made
        /// here and one made there can be compared without a footnote. It is a product bound rather
        /// than a physical one: the downconverter will decimate far past it, and what runs out
        /// first in practice is the record — a zoom of 256 needs 256 times the samples to hold the
        /// same time record, plus the decimation filter's transient.
        /// </remarks>
        public const int MaximumZoomRatio = 256;

        private readonly double _sourceCenterHz;
        private readonly double _sourceSpanHz;

        private double _centerHz;
        private double _spanHz;

        // Where the user asked to look, before the captured band had its say. Kept so that setting
        // a centre and then narrowing the span arrives where it was asked to: at full span the
        // analysis cannot move at all, so a centre set first would otherwise be swallowed and the
        // two settings would have to be given in one particular order to work.
        private double _requestedCenterHz;

        /// <summary>
        /// Creates a control over a captured band, starting at full span.
        /// </summary>
        /// <param name="sourceCenterHz">Centre frequency of the captured band, in hertz.</param>
        /// <param name="sourceSpanHz">Span of the captured band, in hertz; positive and finite.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public ZoomControl(double sourceCenterHz, double sourceSpanHz)
        {
            if (double.IsNaN(sourceCenterHz) || double.IsInfinity(sourceCenterHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceCenterHz), sourceCenterHz, "A centre frequency must be finite.");
            }

            if (!(sourceSpanHz > 0.0) || double.IsInfinity(sourceSpanHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSpanHz), sourceSpanHz, "A span must be positive and finite.");
            }

            _sourceCenterHz = sourceCenterHz;
            _sourceSpanHz = sourceSpanHz;
            _centerHz = sourceCenterHz;
            _requestedCenterHz = sourceCenterHz;
            _spanHz = sourceSpanHz;
        }

        /// <summary>Centre frequency of the captured band, in hertz.</summary>
        public double SourceCenterHz => _sourceCenterHz;

        /// <summary>Span of the captured band, in hertz.</summary>
        public double SourceSpanHz => _sourceSpanHz;

        /// <summary>Lowest frequency in the captured band, in hertz.</summary>
        public double SourceStartHz => _sourceCenterHz - _sourceSpanHz / 2.0;

        /// <summary>Highest frequency in the captured band, in hertz.</summary>
        public double SourceStopHz => _sourceCenterHz + _sourceSpanHz / 2.0;

        /// <summary>Centre frequency of the analysis, in hertz.</summary>
        public double CenterFrequencyHz => _centerHz;

        /// <summary>Span of the analysis, in hertz.</summary>
        public double SpanHz => _spanHz;

        /// <summary>Lowest frequency analysed, in hertz.</summary>
        public double StartHz => _centerHz - _spanHz / 2.0;

        /// <summary>Highest frequency analysed, in hertz.</summary>
        public double StopHz => _centerHz + _spanHz / 2.0;

        /// <summary>What a span change does to the frequency axis.</summary>
        public SpanChangeBehaviour SpanChange { get; set; }

        /// <summary>
        /// The narrowest span this source may be analysed at, in hertz: a
        /// <see cref="MaximumZoomRatio"/>th of the source span.
        /// </summary>
        public double NarrowestSpanHz => _sourceSpanHz / MaximumZoomRatio;

        /// <summary>How far in the analysis currently is: source span divided by analysis span.</summary>
        public double ZoomRatio => _sourceSpanHz / _spanHz;

        /// <summary>Whether the analysis is at full span.</summary>
        public bool IsFullSpan => _spanHz >= _sourceSpanHz;

        /// <summary>Whether a span is within the zoom bound.</summary>
        /// <param name="spanHz">The span to test, in hertz.</param>
        public bool IsAvailable(double spanHz) =>
            spanHz >= NarrowestSpanHz * (1.0 - Tolerance) &&
            spanHz <= _sourceSpanHz * (1.0 + Tolerance);

        /// <summary>
        /// Sets the analysis span.
        /// </summary>
        /// <param name="spanHz">Wanted span, in hertz.</param>
        /// <returns>The centre frequency now in force, which may have moved.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The span is wider than the source or narrower than <see cref="NarrowestSpanHz"/>. The
        /// message names the bound — <c>REQ-REC-004</c>'s criterion.
        /// </exception>
        public double SetSpan(double spanHz)
        {
            if (!(spanHz > 0.0) || double.IsInfinity(spanHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanHz), spanHz, "A span must be positive and finite.");
            }

            if (!IsAvailable(spanHz))
            {
                throw new ArgumentOutOfRangeException(nameof(spanHz), spanHz, Explain(spanHz));
            }

            double startHz = StartHz;

            _spanHz = Math.Min(spanHz, _sourceSpanHz);

            if (SpanChange == SpanChangeBehaviour.HoldStartFrequency)
            {
                // The start is now the statement about where to look, so it replaces the request
                // rather than being overridden by it on the next span change.
                _requestedCenterHz = startHz + _spanHz / 2.0;
            }

            _centerHz = FitToSource(_requestedCenterHz);

            return _centerHz;
        }

        /// <summary>
        /// Sets the analysis centre frequency, moving it no further than the captured band allows.
        /// </summary>
        /// <param name="hz">Wanted centre frequency, in hertz.</param>
        /// <returns>The centre frequency now in force, which may be nearer the middle than asked.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="hz"/> is not finite.</exception>
        /// <remarks>
        /// The frequency asked for is remembered even when the current span cannot reach it, so
        /// that setting the centre and then narrowing the span arrives where it was asked to. At
        /// full span the analysis cannot move at all; without this, a centre set before the span
        /// would be silently discarded and the two settings would only work in one order.
        /// </remarks>
        public double SetCenterFrequency(double hz)
        {
            if (double.IsNaN(hz) || double.IsInfinity(hz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hz), hz, "A centre frequency must be finite.");
            }

            _requestedCenterHz = hz;
            _centerHz = FitToSource(hz);

            return _centerHz;
        }

        /// <summary>
        /// Zooms to a dragged region — the <em>Select Area</em> trace tool's model
        /// (<c>REQ-DSP-023</c>).
        /// </summary>
        /// <param name="firstHz">One edge of the region, in hertz.</param>
        /// <param name="secondHz">The other edge, in hertz.</param>
        /// <returns>The span now in force.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The edges are not finite, coincide, or enclose a span past the zoom bound.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The two edges are taken in either order, because a drag has no preferred direction and
        /// requiring one would make dragging right-to-left do nothing or do something surprising.
        /// </para>
        /// <para>
        /// The region is trimmed to the captured band before the bound is applied: a drag that
        /// starts inside the trace and ends past its edge is a perfectly ordinary gesture, and the
        /// part of it that lands on data is what the user meant. A region entirely outside the band
        /// is not, and is refused.
        /// </para>
        /// <para>
        /// <see cref="SpanChange"/> is not consulted. A drag states both edges, so there is no
        /// question of what to hold.
        /// </para>
        /// </remarks>
        public double SelectArea(double firstHz, double secondHz)
        {
            if (double.IsNaN(firstHz) || double.IsInfinity(firstHz) ||
                double.IsNaN(secondHz) || double.IsInfinity(secondHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(firstHz), "The edges of a selected area must be finite.");
            }

            double low = Math.Max(Math.Min(firstHz, secondHz), SourceStartHz);
            double high = Math.Min(Math.Max(firstHz, secondHz), SourceStopHz);

            if (!(high > low))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(firstHz), firstHz,
                    "The selected area encloses no part of the captured band, which runs from " +
                    Hz(SourceStartHz) + " to " + Hz(SourceStopHz) + ".");
            }

            double span = high - low;

            if (!IsAvailable(span))
            {
                throw new ArgumentOutOfRangeException(nameof(firstHz), span, Explain(span));
            }

            _spanHz = Math.Min(span, _sourceSpanHz);
            _requestedCenterHz = (low + high) / 2.0;
            _centerHz = FitToSource(_requestedCenterHz);

            return _spanHz;
        }

        /// <summary>
        /// Returns the analysis to the whole captured band — the <em>Full Span</em> control.
        /// </summary>
        public void FullSpan()
        {
            _centerHz = _sourceCenterHz;
            _requestedCenterHz = _sourceCenterHz;
            _spanHz = _sourceSpanHz;
        }

        /// <summary>
        /// Builds the downconverter this zoom needs, if it needs one.
        /// </summary>
        /// <param name="sourceSampleRateHz">Sample rate of the captured blocks, in hertz.</param>
        /// <param name="downconverter">The downconverter, when one is needed.</param>
        /// <returns>
        /// <c>false</c> when the captured blocks already deliver this span and should be analysed
        /// as they stand.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceSampleRateHz"/> is not positive.</exception>
        /// <remarks>
        /// Whether a downconverter is needed is a question about the sample rate rather than about
        /// the span: a front end that digitises well above its information bandwidth needs
        /// decimation even at full span, and one that does not needs none until the zoom is real.
        /// Asking the downconverter what it would do, rather than deciding here, keeps that
        /// judgement in the one place that knows how much of a sample rate is usable.
        /// </remarks>
        public bool TryCreateDownconverter(
            double sourceSampleRateHz, out DigitalDownconverter downconverter)
        {
            if (!(sourceSampleRateHz > 0.0) || double.IsInfinity(sourceSampleRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSampleRateHz), sourceSampleRateHz,
                    "A sample rate must be positive and finite.");
            }

            try
            {
                downconverter = DigitalDownconverter.ForSpan(
                    sourceSampleRateHz, _centerHz - _sourceCenterHz, _spanHz);

                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // ForSpan refuses a span too wide to decimate to, which is exactly the case where
                // the blocks are already what was asked for.
                downconverter = null;

                return false;
            }
        }

        /// <summary>
        /// A one-line description of the zoom, for the annotation band.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-041</c> wants the reader to be able to tell a zoomed measurement from a full-span
        /// one without counting axis labels. A zoom that is not announced is the same trace at a
        /// different scale, and the difference matters most exactly when it is least obvious.
        /// </remarks>
        public string Annotation() =>
            IsFullSpan
                ? "Full span " + Hz(_sourceSpanHz)
                : "Zoom " + ZoomRatio.ToString("0.#", CultureInfo.CurrentCulture) + ":1, " +
                  Hz(_spanHz) + " of " + Hz(_sourceSpanHz);

        private double FitToSource(double centerHz)
        {
            double half = _spanHz / 2.0;
            double lowest = SourceStartHz + half;
            double highest = SourceStopHz - half;

            // At full span the two meet; floating point can put them the wrong way round by an
            // ulp, and clamping to an inverted range would land wherever the comparisons fell.
            if (!(highest > lowest))
            {
                return _sourceCenterHz;
            }

            return centerHz < lowest ? lowest : (centerHz > highest ? highest : centerHz);
        }

        private string Explain(double spanHz)
        {
            if (spanHz > _sourceSpanHz)
            {
                return "A span of " + Hz(spanHz) + " is wider than the " + Hz(_sourceSpanHz) +
                       " that was captured. Full Span shows all of it; there is no data outside it " +
                       "to analyse without acquiring again.";
            }

            return "A span of " + Hz(spanHz) + " is deeper than the " + MaximumZoomRatio +
                   ":1 zoom bound. The narrowest analysis this " + Hz(_sourceSpanHz) +
                   " capture allows is " + Hz(NarrowestSpanHz) +
                   "; a narrower one needs a narrower acquisition.";
        }

        private static string Hz(double hertz) =>
            ResolutionBandwidthRange.EngineeringHertz(hertz);

        /// <summary>Fraction of a span within which a request counts as being on a bound.</summary>
        /// <remarks>
        /// The narrowest span is a source span divided by 256, and a user interface that offers
        /// that number back will offer it rounded. Refusing the bound it just displayed, by a part
        /// in 10^12, would be the least explicable rejection in the product.
        /// </remarks>
        private const double Tolerance = 1e-9;
    }
}
