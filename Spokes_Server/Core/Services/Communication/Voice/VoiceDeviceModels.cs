using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class CameraDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public class CameraInitResult
{
    [JsonPropertyName("cameras")]
    public List<CameraDevice> Cameras { get; set; } = [];

    [JsonPropertyName("activeCameraDeviceId")]
    public string ActiveCameraDeviceId { get; set; } = "";
}

public class AudioDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}
