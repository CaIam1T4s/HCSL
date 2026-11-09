using System.Text.Json;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;
using HCSL.Application.Abstractions;
using HCSL.Frontend.Dev;

if (args.Contains("--help"))
{
    Console.WriteLine("HCSL offline instance deployment (Linux x64). Never starts CS2.\n" +
        "--instance <UUID> [--plan | --deploy | --recover] [--full-copy] [--revision <number>]\n" +
        "[--settings <absolute path>] [--data <absolute path>] [--library <absolute path>]\n" +
        "Default is --plan. The copy mode is probed automatically; pass --full-copy to force a full copy.\n" +
        "Ctrl+C cancels before publication; --recover preserves old deployment and partial staging.\n" +
        "Plan validates source hashes and space but does not create runtime or launch a server.\n" +
        "Examples: --plan --instance <UUID> | --deploy --instance <UUID> | --recover --instance <UUID>");
    return 0;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    var values = new Dictionary<string, string>();
    string action = "--plan"; bool actionSeen = false, fullCopy = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--plan" or "--deploy" or "--recover")
        {
            if (actionSeen) throw new ArgumentException("Specify one action.");
            actionSeen = true; action = args[i]; continue;
        }
        if (args[i] == "--full-copy" && !fullCopy) { fullCopy = true; continue; }
        if (args[i] == "--reflink" && !fullCopy) { fullCopy = false; continue; }
        if (args[i] is not ("--instance" or "--settings" or "--data" or "--library" or "--revision"))
            throw new ArgumentException($"Unknown option '{args[i]}'. Known: --instance --settings --data --library --revision. See --help.");
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option {args[i]} needs a value. Example: --instance 565653a2-28fe-4fce-8bb6-6e231d55ac61");
        if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException($"Duplicate option {args[i - 1]}.");
    }
    if (!Guid.TryParseExact(values.GetValueOrDefault("--instance"), "D", out var id) || id == Guid.Empty)
        throw new ArgumentException("--instance must be a nonempty D-format UUID.");
    long? revision = null;
    if (values.TryGetValue("--revision", out var text))
    {
        if (!long.TryParse(text, out var n) || n <= 0) throw new ArgumentException("Revision must be positive.");
        revision = n;
    }
    var defaults = LocalPaths.Defaults(values.GetValueOrDefault("--settings"));
    var workspace = new WorkspaceRequest(values.GetValueOrDefault("--settings", defaults.SettingsRoot), values.GetValueOrDefault("--data", defaults.DataRoot), values.GetValueOrDefault("--library", defaults.LibraryRoot));
    var copyMode = fullCopy ? DeploymentCopyMode.FullCopy
        : args.Contains("--reflink") ? DeploymentCopyMode.Reflink
        : RuntimeDeploymentService.ProbeCopyMode(Path.Combine(workspace.LibraryRoot, "runtime"));
    if (copyMode == DeploymentCopyMode.FullCopy && !fullCopy)
        Console.Error.WriteLine($"copy mode: reflink is not available on {workspace.LibraryRoot}; using a full copy. Pass --full-copy to silence this probe.");
    var options = new DeploymentOptions(id, copyMode, revision);
    var service = new RuntimeDeploymentService();
    var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    if (action == "--plan") Console.WriteLine(JsonSerializer.Serialize(service.Plan(workspace, options, cancel.Token), json));
    else if (action == "--recover") Console.WriteLine(service.Recover(workspace, id));
    else Console.WriteLine(JsonSerializer.Serialize(service.Deploy(workspace, options, new ConsoleProgress(), cancel.Token), json));
    return 0;
}
catch (DeploymentException e) { Console.Error.WriteLine("HCSL-DEPLOY-CHECK: " + DevHints.With(e.Code + ": " + e.Message, e.Code)); return 2; }
catch (InstanceException e) { Console.Error.WriteLine("HCSL-DEPLOY-CHECK: " + DevHints.With(e.Code + ": " + e.Message, e.Code)); return 2; }
catch (WorkspaceException e) { Console.Error.WriteLine("HCSL-DEPLOY-CHECK: " + DevHints.With(e.Code + ": " + e.Message, e.Code)); return e.Code == "HCSL-LOCK" ? 3 : 2; }
catch (OperationCanceledException) { Console.Error.WriteLine("HCSL-DEPLOY-CANCELLED: cancelled; use --recover if a deployment journal exists."); return 130; }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
{ Console.Error.WriteLine("HCSL-DEPLOY-ERROR: " + e.Message); return 2; }
sealed class ConsoleProgress : IProgress<DeploymentEvent>
{
    public void Report(DeploymentEvent e) => Console.Error.WriteLine($"{e.Stage} {e.FilesCompleted}/{e.TotalFiles}");
}
