namespace Veldrid
{
    /// <summary>
    /// DXGI output characteristics for the monitor attached to a swapchain (display-centric, not GPU vendor).
    /// </summary>
    public readonly struct MonitorDisplayCapabilities
    {
        public string MonitorName { get; init; }

        /// <summary>Windows advanced colour / DXGI reported bits per colour channel (often 8 or 10).</summary>
        public byte BitsPerColor { get; init; }

        /// <summary>Output colour space looks like an HDR signal (e.g. PQ / extended).</summary>
        public bool HdrSignal { get; init; }

        public float MaxLuminance { get; init; }

        public float MinLuminance { get; init; }

        public float MaxFullFrameLuminance { get; init; }

        /// <summary>Suggested SDR paper white in nits when following the monitor HDR metadata (clamped).</summary>
        public float SuggestedPaperWhiteNits => clamp(MaxFullFrameLuminance > 1 ? MaxFullFrameLuminance * 0.1f : 200f, 80f, 400f);

        private static float clamp(float value, float min, float max)
            => value < min ? min : value > max ? max : value;
    }
}
