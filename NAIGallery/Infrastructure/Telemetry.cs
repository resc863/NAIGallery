using System.Diagnostics.Metrics;

namespace NAIGallery;

/// <summary>
/// App-wide telemetry (OpenTelemetry-friendly) using System.Diagnostics.Metrics.
/// </summary>
internal static class Telemetry
{
    private static readonly Meter Meter = new("NAIGallery", "1.0.0");

    // Decode error counters
    public static readonly Counter<long> DecodeIoErrors = Meter.CreateCounter<long>("decode.io_errors");
    public static readonly Counter<long> DecodeFormatErrors = Meter.CreateCounter<long>("decode.format_errors");
    public static readonly Counter<long> DecodeCanceled = Meter.CreateCounter<long>("decode.canceled");
    public static readonly Counter<long> DecodeUnknownErrors = Meter.CreateCounter<long>("decode.unknown_errors");

    // COM categories
    public static readonly Counter<long> ComUnsupportedFormat = Meter.CreateCounter<long>("decode.com.unsupported_format");
    public static readonly Counter<long> ComOutOfMemory = Meter.CreateCounter<long>("decode.com.out_of_memory");
    public static readonly Counter<long> ComAccessDenied = Meter.CreateCounter<long>("decode.com.access_denied");
    public static readonly Counter<long> ComWrongState = Meter.CreateCounter<long>("decode.com.wrong_state");
    public static readonly Counter<long> ComDeviceLost = Meter.CreateCounter<long>("decode.com.device_lost");
    public static readonly Counter<long> ComOther = Meter.CreateCounter<long>("decode.com.other");

    // Decode latency (ms)
    public static readonly Histogram<double> DecodeLatencyMs = Meter.CreateHistogram<double>("decode.latency_ms");
}
