using System.Text.Json;
using System.Text.Json.Serialization;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

[JsonSerializable(typeof(MapPoolLibrary))]
[JsonSerializable(typeof(PresetMapPool))]
internal sealed partial class MapPoolStorageJsonContext : JsonSerializerContext
{
    internal static MapPoolStorageJsonContext CamelCase { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<ServerMapKind>(JsonNamingPolicy.CamelCase) }
    });
}
