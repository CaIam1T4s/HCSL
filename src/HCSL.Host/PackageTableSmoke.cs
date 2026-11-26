using Avalonia.Controls;
using Avalonia.VisualTree;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host;

// Only used by --package-smoke. Keep every workspace-bearing request away from user data.
internal sealed class PackageTableSmoke : IDaemonClient, IDisposable
{
    private const string PresetName = "AOT Test Preset", MapName = "de_inferno", PoolName = "AOT Test Pool";
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hcsl-package-workspace-");
    private readonly DaemonClient client;
    private readonly WorkspacePaths paths;
    public string Pipe { get; } = $"hcsl-package-smoke-{Guid.NewGuid():N}";

    public PackageTableSmoke()
    {
        client = new(Pipe);
        paths = new(Path.Combine(root.FullName, "settings"), Path.Combine(root.FullName, "data"), Path.Combine(root.FullName, "library"));
    }

    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default) =>
        SendAsync(request, TimeSpan.FromSeconds(10), ct);

    public async Task<LocalResponse> SendAsync(LocalRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        var options = request.Options is { } supplied ? supplied with { Workspace = paths } : new ProbeOptions(paths);
        var response = await client.SendAsync(request with { Options = options }, timeout, ct);
        return request.Kind == RequestKind.Hello && response.Success ? response with { Defaults = paths } : response;
    }

    public async Task CheckTablesAsync(Window window, HostServerController servers)
    {
        var instances = new InstanceClient(this);
        var instance = (await instances.ExecuteAsync(new(InstanceAction.Create, Name: "Package Smoke"))).Instances.Single();
        instance = (await instances.ExecuteAsync(new(InstanceAction.CreatePreset, instance.Id,
            Revision: instance.Definition!.Revision, Name: PresetName,
            Preset: new(1, Guid.Empty, 1, PresetName, MapName)))).Instances.Single();
        var preset = instance.Presets.Single(p => p.Name == PresetName);
        var pools = await instances.MapPoolsAsync(new(MapPoolLibraryAction.Create, instance.Id,
            InstanceRevision: instance.Definition!.Revision, Name: PoolName, Maps: [new(ServerMapKind.Standard, MapName)]));
        await instances.MapPoolsAsync(new(MapPoolLibraryAction.AssignPreset, instance.Id,
            InstanceRevision: instance.Definition.Revision, PoolId: pools.Library.Pools.Single().Id, PresetId: preset.Id,
            ExpectedRevision: pools.Library.Revision, ExpectedHash: pools.Hash));

        await servers.InitializeAsync();
        servers.Selected = servers.Instances.Single(i => i.Entry.Id == instance.Id);
        var shell = (ShellViewModel)window.DataContext!;
        shell.NavigateCommand.Execute("instance");
        shell.SelectInstanceSection("presets");
        await ExpectCellsAsync(window, "PresetChoices", PresetName, MapName);
        shell.SelectInstanceSection("pools");
        await ExpectCellsAsync(window, "PoolChoices", PoolName, "1 张");
    }

    private static async Task ExpectCellsAsync(Window window, string tableName, params string[] expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        string[] actual = [];
        try
        {
            while (true)
            {
                var table = window.GetVisualDescendants().OfType<Table>().FirstOrDefault(t => t.Name == tableName && t.IsEffectivelyVisible);
                actual = table?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                    .Select(t => t.Text ?? "").ToArray() ?? [];
                if (expected.All(text => actual.Contains(text, StringComparer.Ordinal))) return;
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"{tableName}: expected visible text [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    public void Dispose() => root.Delete(recursive: true);
}
