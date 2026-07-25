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

        /// <summary>Selects Basic mode, in which the waveform (time-domain) measurement lives.</summary>
        public const string SelectBasicMode = ":INSTrument:SELect BASIC";

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

        /// <summary>Sets the input range's upper limit, in dBm.</summary>
        public static string SetReferenceLevel(double dbm) =>
            ":SENSe:POWer:RF:RANGe:UPPer " + Number(dbm) + " dBm";

        /// <summary>Queries an input-range limit.</summary>
        public static string ReferenceLevelLimit(bool maximum) =>
            ":SENSe:POWer:RF:RANGe:UPPer? " + (maximum ? "MAX" : "MIN");

        /// <summary>Takes a measurement and returns the raw I/Q trace, interleaved, in volts.</summary>
        public const string ReadIqTrace = ":READ:WAVeform0?";

        /// <summary>Invariant formatting, because a comma decimal separator is not SCPI.</summary>
        private static string Number(double value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
