using System.Text.Json.Serialization;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

internal sealed record EmptyPluginSelection(int FormatVersion, object[] Plugins);
internal sealed record EmptyAuthorization(int FormatVersion, object[] Rules);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstanceDefinition))]
[JsonSerializable(typeof(PresetDefinition))]
[JsonSerializable(typeof(EmptyPluginSelection))]
[JsonSerializable(typeof(EmptyAuthorization))]
internal sealed partial class InstanceStorageJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InstanceCatalog))]
internal sealed partial class InstanceSizeJsonContext : JsonSerializerContext;
