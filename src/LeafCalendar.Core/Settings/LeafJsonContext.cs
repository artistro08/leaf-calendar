using System.Text.Json.Serialization;

namespace LeafCalendar.Core.Settings;

/// <summary>Source-generated JSON metadata for Leaf's own stored types (AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LeafSettings))]
internal sealed partial class LeafJsonContext : JsonSerializerContext
{
}
