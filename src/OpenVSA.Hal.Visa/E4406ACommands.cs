namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// The SCPI this driver sends, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gathered here so the command set can be read against the programming guide without reading
    /// the driver, and so a test can assert the order commands were sent in — which is where an
    /// instrument driver usually goes wrong: every individual command is right and one of them is
    /// sent before the mode that makes it legal.
    /// </para>
    /// <para>
    /// Sources are the E4406A Programmer's Guide, chapter 5 (Language Reference) unless noted.
    /// </para>
    /// </remarks>
    internal static class E4406ACommands
    {
        /// <summary>Identification query.</summary>
        public const string Identify = "*IDN?";

        /// <summary>
        /// Clears the status registers and, with a device clear before it, the output queue.
        /// </summary>
        /// <remarks>
        /// Sent before anything else, because the instrument is not necessarily as the last
        /// program left it. A query whose response was never read leaves a reply waiting, and the
        /// next command then earns <c>-410 Query INTERRUPTED</c> — which looks like a fault in
        /// whatever command happened to be sent first rather than in the program that walked away.
        /// </remarks>
        public const string ClearStatus = "*CLS";

        /// <summary>Installed-option query, for the personalities of <c>REQ-E44-001</c>.</summary>
        public const string Options = "*OPT?";

        /// <summary>Queries the measurement mode currently selected.</summary>
        public const string SelectedMode = ":INSTrument:SELect?";

        /// <summary>Selects a measurement mode by name, for restoring the one found.</summary>
        /// <param name="mode">Mode name as <see cref="SelectedMode"/> reported it.</param>
        public static string SelectMode(string mode) => ":INSTrument:SELect " + mode;

        /// <summary>Selects Basic mode, in which the waveform (time-domain) measurement lives.</summary>
        public const string SelectBasicMode = ":INSTrument:SELect BASIC";

        /// <summary>
        /// Returns the seven scalars of the acquisition just taken.
        /// </summary>
        /// <remarks>
        /// <c>FETCh</c> rather than <c>READ</c>: it returns the results of the acquisition already
        /// in hand instead of arming a new one, so the scalars describe the very I/Q that was just
        /// read rather than a later capture that may differ. In order, per <c>REQ-E44-002</c>:
        /// sample interval, mean power, gated mean power, sample count, peak-to-mean ratio,
        /// maximum, minimum.
        /// </remarks>
        public const string FetchScalars = ":FETCh:WAVeform1?";

        /// <summary>Index of the sample interval within the scalar block, zero-based.</summary>
        public const int SampleIntervalScalar = 0;

        /// <summary>Index of the sample count within the scalar block, zero-based.</summary>
        public const int SampleCountScalar = 3;

        /// <summary>Selects the waveform measurement, whose <c>n=0</c> result is raw I/Q.</summary>
        public const string ConfigureWaveform = ":CONFigure:WAVeform";

        /// <summary>
        /// Selects 32-bit binary transfer.
        /// </summary>
        /// <remarks>
        /// The guide recommends REAL,64 where full resolution is needed and notes REAL,32 is
        /// "smaller and somewhat faster". The samples are volts from a 14-bit digitiser, so
        /// float32's 24-bit significand is far more than the data carries, and the transfer is the
        /// bottleneck on GPIB.
        /// </remarks>
        public const string BinaryFormat = ":FORMat:DATA REAL,32";

        /// <summary>Sends the least significant byte first; VISA's NORMal order is big-endian.</summary>
        public const string SwapByteOrder = ":FORMat:BORDer SWAP";

        /// <summary>Stops free-running measurement, so each acquisition is asked for.</summary>
        public const string SingleMeasurement = ":INITiate:CONTinuous OFF";

        /// <summary>Stops updating the front panel, which the guide lists first among speed measures.</summary>
        public const string DisableDisplay = ":DISPlay:ENABle OFF";

        /// <summary>Restores the front panel, so the instrument is not left looking broken.</summary>
        public const string EnableDisplay = ":DISPlay:ENABle ON";

        /// <summary>Flat-top digital filter: flat amplitude across the information bandwidth.</summary>
        public const string FlatTopFilter = ":SENSe:WAVeform:BANDwidth:RESolution:TYPE FLATtop";

        /// <summary>Turns off the waveform measurement's own averaging; OpenVSA averages its own traces.</summary>
        public const string AveragingOff = ":SENSe:WAVeform:AVERage:STATe OFF";

        /// <summary>Reads and clears the head of the instrument's error queue.</summary>
        public const string ErrorQuery = ":SYSTem:ERRor?";

        /// <summary>Blocks until the preceding commands have completed.</summary>
        public const string OperationComplete = "*OPC?";

        /// <summary>Sets the centre frequency, in hertz.</summary>
        public static string SetCenterFrequency(double hertz) =>
            ":SENSe:FREQuency:CENTer " + Number(hertz) + " Hz";

        /// <summary>Queries a centre-frequency limit.</summary>
        public static string CenterFrequencyLimit(bool maximum) =>
            ":SENSe:FREQuency:CENTer? " + (maximum ? "MAX" : "MIN");

        /// <summary>Sets the information bandwidth, which is this measurement's span.</summary>
        public static string SetBandwidth(double hertz) =>
            ":SENSe:WAVeform:BANDwidth:RESolution " + Number(hertz) + " Hz";

        /// <summary>Queries an information-bandwidth limit.</summary>
        public static string BandwidthLimit(bool maximum) =>
            ":SENSe:WAVeform:BANDwidth:RESolution? " + (maximum ? "MAX" : "MIN");

        /// <summary>
        /// Queries the bandwidth actually in use.
        /// </summary>
        /// <remarks>
        /// "Due to memory constraints the actual resolution bandwidth value may vary from the value
        /// entered by the user" — so the honoured span is asked for rather than assumed, which is
        /// the whole point of <c>REQ-HAL-001</c>'s negotiation.
        /// </remarks>
        public const string ActualBandwidth = ":SENSe:WAVeform:BANDwidth:RESolution:ACTual?";

        /// <summary>
        /// Queries the sample period.
        /// </summary>
        /// <remarks>
        /// "Returns the waveform sample period (aperture) based on current resolution bandwidth,
        /// filter type, and decimation factor. Sample rate is the reciprocal of period." This is
        /// the instrument's own answer and is never inferred from the span: the relationship
        /// between the two is this instrument's, not a law of the product.
        /// </remarks>
        public const string Aperture = ":SENSe:WAVeform:APERture?";

        /// <summary>Sets the capture length, in seconds.</summary>
        public static string SetSweepTime(double seconds) =>
            ":SENSe:WAVeform:SWEep:TIME " + Number(seconds) + " S";

        /// <summary>Queries a capture-length limit.</summary>
        public static string SweepTimeLimit(bool maximum) =>
            ":SENSe:WAVeform:SWEep:TIME? " + (maximum ? "MAX" : "MIN");

        /// <summary>
        /// Lets the instrument set its own input attenuation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The reference level is not commandable in Basic mode, and asking cost a
        /// timeout to discover.</strong> <c>[:SENSe]:POWer[:RF]:RANGe[:UPPer]</c> — "maximum
        /// expected total power level at the radio unit under test" — is documented as requiring
        /// "the Service, cdmaOne, EDGE(w/GSM), GSM, NADC, PDC, cdma2000, or W-CDMA (3GPP) mode",
        /// and Basic is not among them. Sent in Basic mode the instrument does not answer at all.
        /// </para>
        /// <para>
        /// <c>[:SENSe]:POWer[:RF]:ATTenuation:AUTO</c> is documented too, and this firmware
        /// (A.08.10) rejects it with <c>-113 Undefined header</c> — so the manual is not a
        /// substitute for asking the instrument. What Basic mode does have is the waveform
        /// measurement's own ADC ranging, which is what auto-ranges the digitiser here, and its
        /// factory default is already AUTO. Setting it explicitly makes the driver's assumption
        /// visible rather than inherited from whatever the last user left behind.
        /// </para>
        /// <para>
        /// So the input range is left to the instrument, and OpenVSA's reference level stays what
        /// it is for a front end that returns volts: the top of the graticule.
        /// </para>
        /// </remarks>
        public const string AutoAdcRange = ":SENSe:WAVeform:ADC:RANGe AUTO";

        /// <summary>Queries the input attenuation actually in use, in dB.</summary>
        public const string Attenuation = ":SENSe:POWer:RF:ATTenuation?";

        /// <summary>Takes a measurement and returns the raw I/Q trace, interleaved, in volts.</summary>
        public const string ReadIqTrace = ":READ:WAVeform0?";

        /// <summary>Invariant formatting, because a comma decimal separator is not SCPI.</summary>
        private static string Number(double value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
