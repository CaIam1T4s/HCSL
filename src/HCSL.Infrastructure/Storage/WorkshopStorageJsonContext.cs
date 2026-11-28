using System.Text.Json.Serialization;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

[JsonSerializable(typeof(WorkshopItem[]))]
internal sealed partial class WorkshopStorageJsonContext : JsonSerializerContext;
