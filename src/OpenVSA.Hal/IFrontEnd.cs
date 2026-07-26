using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;

namespace OpenVSA.Hal
{
    /// <summary>Lifecycle state of a front end.</summary>
    public enum FrontEndState
    {
        /// <summary>Not connected to its source.</summary>
        Disconnected = 0,

        /// <summary>Connected but not yet configured with a plan.</summary>
        Connected,

        /// <summary>Configured with an <see cref="AcquisitionPlan"/>.</summary>
        Configured,

        /// <summary>Armed and ready to acquire.</summary>
        Armed,

        /// <summary>Acquiring.</summary>
        Acquiring,

        /// <summary>Failed; see the notification stream for the reason.</summary>
        Faulted,
    }

    /// <summary>Trigger styles a front end may offer.</summary>
    public enum TriggerStyle
    {
        /// <summary>Acquire immediately, without waiting for a condition.</summary>
        Immediate = 0,

        /// <summary>Trigger on input signal level.</summary>
        Level,

        /// <summary>Trigger on an external electrical input.</summary>
        External,

        /// <summary>Trigger on a periodic internal timebase.</summary>
        Periodic,

        /// <summary>
        /// Trigger when the spectrum crosses a frequency mask (<c>REQ-TRG-001</c>).
        /// </summary>
        /// <remarks>
        /// Needs real-time analysis in the front end: the mask has to be tested against every
        /// transform, gap-free, and an instrument that cannot do that would miss precisely the
        /// transient the mask was drawn to catch. <c>REQ-TRG-001</c> therefore requires this style
        /// to be declared unsupported unless the front end reports the capability.
        /// </remarks>
        FrequencyMask,
    }

    /// <summary>Kinds of out-of-band condition a front end can report.</summary>
    public enum FrontEndEventKind
    {
        /// <summary>Input overload; measurements are invalid (<c>REQ-UI-007</c>, <c>OVx</c>).</summary>
        Overload = 0,

        /// <summary>Frequency reference is not locked.</summary>
        ReferenceUnlocked,

        /// <summary>A hardware error occurred.</summary>
        HardwareError,

        /// <summary>The input range changed, whether by auto-ranging or externally.</summary>
        RangeChanged,

        /// <summary>Frames were dropped (<c>REQ-NFR-012</c>).</summary>
        FramesDropped,

        /// <summary>A requested parameter was coerced (<c>REQ-HAL-001</c>).</summary>
        ParameterCoerced,
    }

    /// <summary>An out-of-band condition reported by a front end.</summary>
    public sealed class FrontEndEvent : EventArgs
    {
        /// <summary>Creates an event.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="message">User-facing description; required.</param>
        public FrontEndEvent(FrontEndEventKind kind, string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                throw new ArgumentException("An event must carry a message.", nameof(message));
            }

            Kind = kind;
            Message = message;
        }

        /// <summary>What happened.</summary>
        public FrontEndEventKind Kind { get; }

        /// <summary>User-facing description.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public override string ToString() => Kind + ": " + Message;
    }

    /// <summary>
    /// What a front end can do. The UI ranges and enables its controls from this and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>REQ-HAL-002</c>. No UI code may contain instrument-specific conditionals, so every limit
    /// a control needs has to be expressible here. A capability that cannot be stated in this
    /// interface is a gap in this interface, not a licence to special-case a model name.
    /// </remarks>
    public interface IFrontEndCapabilities
    {
        /// <summary>Supported centre-frequency range.</summary>
        FrequencyRange CenterFrequencyRange { get; }

        /// <summary>Largest supported span, in hertz.</summary>
        double MaxSpanHz { get; }

        /// <summary>Smallest supported span, in hertz.</summary>
        double MinSpanHz { get; }

        /// <summary>Highest supported sample rate, in hertz.</summary>
        double MaxSampleRateHz { get; }

        /// <summary>Largest block this front end will return, in complex samples.</summary>
        int MaxSamplesPerBlock { get; }

        /// <summary>Deepest single capture, in complex samples; may span many blocks.</summary>
        long MaxCaptureSamples { get; }

        /// <summary>Whether baseband (real) IQ is supported as well as complex zoom/IF.</summary>
        bool SupportsBasebandIq { get; }

        /// <summary>Number of input channels; at least one.</summary>
        int ChannelCount { get; }

        /// <summary>Whether multiple channels share a timebase and are phase coherent.</summary>
        bool SupportsPhaseCoherentChannels { get; }

        /// <summary>Trigger styles this front end offers.</summary>
        IReadOnlyList<TriggerStyle> TriggerStyles { get; }

        /// <summary>Supported reference-level range.</summary>
        AmplitudeRange ReferenceLevelRange { get; }

        /// <summary>Whether an external frequency reference can be used.</summary>
        bool SupportsExternalRef { get; }

        /// <summary>
        /// Whether the front end analyses gap-free in real time (<c>REQ-TRG-001</c>).
        /// </summary>
        /// <remarks>
        /// The condition <c>REQ-TRG-001</c> attaches to the frequency-mask trigger. Stated here
        /// rather than inferred from a model name, because <c>REQ-HAL-002</c> forbids the UI
        /// knowing which instrument it is talking to — a capability that cannot be expressed on
        /// this interface is a gap in the interface, not a licence to special-case.
        /// </remarks>
        bool SupportsRealTimeAnalysis { get; }

        /// <summary>
        /// How far before a trigger this front end can deliver samples, in complex samples; 0 if
        /// it cannot (<c>REQ-TRG-002</c>).
        /// </summary>
        /// <remarks>
        /// Pre-trigger is served from capture memory, so the limit is how much of it the front end
        /// keeps. An instrument with none can still be given a negative delay for a recording or a
        /// simulated source, where the samples before the trigger are simply already in hand.
        /// </remarks>
        long MaxPreTriggerSamples { get; }
    }

    /// <summary>
    /// A source of <see cref="IqBlock"/>s — live instrument, recording, or simulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-ARC-001</c>: the analysis layers see nothing but <see cref="IqBlock"/>, so they cannot
    /// tell which kind of source produced it. That is what lets the whole DSP stack run in CI with
    /// no hardware present (<c>REQ-NFR-032</c>).
    /// </para>
    /// <para>
    /// <strong>Pull, not push, and not <c>IAsyncEnumerable</c>.</strong> Async streams need C# 8.0;
    /// the mandated level is C# 7.3 because of the IVI/NI-VISA assemblies (RISK-05), under which
    /// <c>await foreach</c> does not compile. <see cref="AcquireNextAsync"/> returning <c>null</c>
    /// at end-of-source is the resulting idiom.
    /// </para>
    /// </remarks>
    public interface IFrontEnd : IDisposable
    {
        /// <summary>Stable identifier for this front end instance.</summary>
        FrontEndId Id { get; }

        /// <summary>Human-readable name, for the connection UI.</summary>
        string DisplayName { get; }

        /// <summary>What this front end can do.</summary>
        IFrontEndCapabilities Capabilities { get; }

        /// <summary>Current lifecycle state.</summary>
        FrontEndState State { get; }

        /// <summary>Connects to the underlying source.</summary>
        /// <param name="ct">Cancellation token.</param>
        Task ConnectAsync(CancellationToken ct);

        /// <summary>Disconnects from the underlying source.</summary>
        Task DisconnectAsync();

        /// <summary>
        /// Validates and coerces a request to what this front end can honour.
        /// </summary>
        /// <param name="request">What the user asked for.</param>
        /// <returns>A plan stating the honoured values and the reason for every coercion.</returns>
        /// <remarks>
        /// <strong>Pure.</strong> <c>REQ-HAL-001</c> requires that no hardware command is sent and
        /// no state changes, so this may be called on a disconnected front end and called
        /// repeatedly to explore what is achievable.
        /// </remarks>
        AcquisitionPlan Negotiate(AcquisitionRequest request);

        /// <summary>Applies a plan to the source.</summary>
        /// <param name="plan">A plan obtained from <see cref="Negotiate"/>.</param>
        /// <param name="ct">Cancellation token.</param>
        Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct);

        /// <summary>Arms the source, ready to acquire.</summary>
        /// <param name="ct">Cancellation token.</param>
        Task ArmAsync(CancellationToken ct);

        /// <summary>
        /// Pulls the next block.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The next block, or <c>null</c> when the source is exhausted.</returns>
        Task<IqBlock> AcquireNextAsync(CancellationToken ct);

        /// <summary>Abandons an acquisition in progress.</summary>
        Task AbortAsync();

        /// <summary>Out-of-band conditions: overload, unlock, hardware error, range change.</summary>
        event EventHandler<FrontEndEvent> Notification;
    }

    /// <summary>
    /// Marks a type as a front-end provider, for discovery at start-up.
    /// </summary>
    /// <remarks>
    /// <c>REQ-HAL-003</c>. Discovery is by attribute so that adding a front end never requires
    /// editing a registry in core code — which is also what keeps <c>REQ-HAL-002</c>'s prohibition
    /// on instrument-specific conditionals enforceable.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class FrontEndProviderAttribute : Attribute
    {
        /// <summary>Marks a provider.</summary>
        /// <param name="displayName">Name shown in the connection UI.</param>
        public FrontEndProviderAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                throw new ArgumentException("A provider needs a display name.", nameof(displayName));
            }

            DisplayName = displayName;
        }

        /// <summary>Name shown in the connection UI.</summary>
        public string DisplayName { get; }
    }
}
