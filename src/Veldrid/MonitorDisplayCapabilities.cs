namespace Veldrid
{
    /// <summary>
    /// DXGI output characteristics for the monitor attached to a swapchain (display-centric, not GPU vendor).
    /// </summary>
    public readonly struct MonitorDisplayCapabilities
    {
        /// <summary>Win32 / DXGI output name (for example <c>\\.\DISPLAY2</c>).</summary>
        public string MonitorName { get; init; }

        /// <summary>Windows advanced colour / DXGI reported bits per colour channel (often 8 or 10).</summary>
        public byte BitsPerColor { get; init; }

        /// <summary>Output colour space looks like an HDR signal (e.g. PQ / extended).</summary>
        public bool HdrSignal { get; init; }

        /// <summary>Peak luminance in nits from DXGI output metadata.</summary>
        public float MaxLuminance { get; init; }

        /// <summary>Minimum luminance in nits from DXGI output metadata.</summary>
        public float MinLuminance { get; init; }

        /// <summary>Full-frame luminance in nits from DXGI output metadata.</summary>
        public float MaxFullFrameLuminance { get; init; }

        /// <summary>Paper white in nits: full-frame luminance, else peak, else 200.</summary>
        public float SuggestedPaperWhiteNits =>
            MaxFullFrameLuminance > 1 ? MaxFullFrameLuminance : MaxLuminance > 1 ? MaxLuminance : 200f;
    }
}
