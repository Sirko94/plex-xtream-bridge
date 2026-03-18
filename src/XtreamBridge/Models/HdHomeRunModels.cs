using System.Text.Json.Serialization;

namespace XtreamBridge.Models;

/// <summary>
/// Response for GET /discover.json
/// Plex uses this to identify the device as an HDHomeRun tuner.
/// </summary>
public sealed class HdHomeRunDiscover
{
    [JsonPropertyName("FriendlyName")]    public string FriendlyName { get; set; } = string.Empty;
    [JsonPropertyName("Manufacturer")]    public string Manufacturer { get; set; } = "Silicondust";
    [JsonPropertyName("ModelNumber")]     public string ModelNumber { get; set; } = "HDTC-2US";
    [JsonPropertyName("FirmwareName")]    public string FirmwareName { get; set; } = "hdhomerun3_atsc";
    [JsonPropertyName("TunerCount")]      public int TunerCount { get; set; } = 6;
    [JsonPropertyName("FirmwareVersion")] public string FirmwareVersion { get; set; } = "20190621";
    [JsonPropertyName("DeviceID")]        public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("DeviceAuth")]      public string DeviceAuth { get; set; } = "test";
    [JsonPropertyName("BaseURL")]         public string BaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("LineupURL")]       public string LineupUrl { get; set; } = string.Empty;
}

/// <summary>
/// Response for GET /lineup_status.json
/// </summary>
public sealed class HdHomeRunLineupStatus
{
    [JsonPropertyName("ScanInProgress")] public int ScanInProgress { get; set; } = 0;
    [JsonPropertyName("ScanPossible")]   public int ScanPossible { get; set; } = 1;
    [JsonPropertyName("Source")]         public string Source { get; set; } = "Cable";
    [JsonPropertyName("SourceList")]     public List<string> SourceList { get; set; } = new() { "Cable" };
}

/// <summary>
/// One entry in GET /lineup.json — represents a single live channel.
/// StreamURL points back to our /stream/live/{id} proxy endpoint.
/// </summary>
public sealed class HdHomeRunLineupEntry
{
    [JsonPropertyName("GuideNumber")] public string GuideNumber { get; set; } = string.Empty;
    [JsonPropertyName("GuideName")]   public string GuideName { get; set; } = string.Empty;
    [JsonPropertyName("StreamURL")]   public string StreamUrl { get; set; } = string.Empty;
    [JsonPropertyName("HD")]          public int Hd { get; set; } = 1;
}
