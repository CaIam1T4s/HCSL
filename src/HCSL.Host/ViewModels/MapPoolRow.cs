using HCSL.Core;
using TKCode.Arc;

namespace HCSL.Host.ViewModels;

/// <summary>One row of the instance map-pool library list. Rebuilt on every load, so a plain record is enough.</summary>
public sealed record MapPoolRow(MapPoolRecord Pool, string Count, string Presets, BadgeTone Tone)
{
    public string Name => Pool.Name;
}
