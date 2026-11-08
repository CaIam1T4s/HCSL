using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;

namespace HCSL.ServerTests;

public static class InstancePasswordTests
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux instance password configuration tests"); return; }
        var root = Path.Combine(Path.GetTempPath(), "hcsl-instance-password-" + Guid.NewGuid().ToString("N"));
        var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
        var store = new LocalInstanceStore();
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            var entry = store.Execute(workspace, new(InstanceAction.Create, Name: "Private server")).Instances.Single();
            var id = entry.Id;
            var revision = entry.Definition!.Revision;
            var instance = Path.Combine(workspace.DataRoot, "instances", id.ToString("D"));
            var preset = entry.Definition.SelectedPresetId;
            var command = new InstanceCommand(InstanceAction.SetPassword, id, Revision: revision, Password: "test pass?#% 世界");
            Check(!command.ToString().Contains("test pass"), "command debug representation exposed secret");
            entry = store.Execute(workspace, command).Instances.Single();
            Check(entry.HasPassword && entry.Definition!.Revision == revision, "setting password changed deployment identity or lost status");
            var source = Path.Combine(instance, "private", "server-password.cfg");
            Check(File.GetUnixFileMode(source) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "config must be 0600");
            Check(File.GetUnixFileMode(Path.GetDirectoryName(source)!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "secret directory must be 0700");
            Check(!File.ReadAllText(Path.Combine(instance, "instance.json")).Contains("test pass"), "metadata exposed secret");
            Check(!JsonSerializer.Serialize(entry).Contains("test pass"), "catalog exposed secret");
            Check(InstancePasswordConfig.Read(instance) == "test pass?#% 世界", "secret changed during encoding");
            using (var binding = InstancePasswordConfig.Bind(instance, preset.ToString("D")))
            {
                var link = Path.Combine(instance, "presets", preset.ToString("D"), "cfg", InstancePasswordConfig.ExecName);
                Check(new FileInfo(link).LinkTarget == source, "config not instance-owned");
                Check(File.ReadAllText(link).Contains("exec server.cfg\nsv_password \"test pass?#% 世界\""), "password not applied after normal server cfg");
                Check(binding.Redact("echo test pass?#% 世界") == "echo [redacted]", "log redaction failed");
                // Even a leftover generated reference must never be copied into a new preset.
                entry = store.Execute(workspace, new(InstanceAction.CopyPreset, id, preset, revision, Name: "Other rules")).Instances.Single();
                var copied = entry.Presets.Single(p => p.Id != preset);
                Check(!File.Exists(Path.Combine(instance, "presets", copied.Id.ToString("D"), "cfg", InstancePasswordConfig.ExecName)), "preset copy retained a private reference");
            }
            Check(!File.Exists(Path.Combine(instance, "presets", preset.ToString("D"), "cfg", InstancePasswordConfig.ExecName)), "normal cleanup left generated reference");
            var other = entry.Presets.Single(p => p.Id != preset);
            entry = store.Execute(workspace, new(InstanceAction.SelectPreset, id, other.Id, entry.Definition!.Revision)).Instances.Single();
            Check(entry.HasPassword && InstancePasswordConfig.Read(instance) == "test pass?#% 世界", "changing preset changed instance password");
            foreach (var bad in new[] { "x;quit", "x\"", "x\\", "x\n", "x+quit", new string('x', 129) })
            {
                try { store.Execute(workspace, new(InstanceAction.SetPassword, id, Revision: entry.Definition!.Revision, Password: bad)); throw new Exception("injection accepted"); }
                catch (InstanceException e) when (e.Code == "HCSL-INSTANCE-PASSWORD") { }
                Check(InstancePasswordConfig.Read(instance) == "test pass?#% 世界", "invalid request changed secret");
            }
            using (var lease = new LocalWorkspaceManager().Acquire(workspace))
            {
                try { store.Execute(workspace, new(InstanceAction.SetPassword, id, Revision: entry.Definition!.Revision, Password: "blocked")); throw new Exception("mutation ignored live workspace lease"); }
                catch (WorkspaceException e) when (e.Code == "HCSL-LOCK") { }
                Check(store.Execute(workspace, new(InstanceAction.List)).Instances.Single().HasPassword, "locked status unavailable");
            }
            entry = store.Execute(workspace, new(InstanceAction.SetPassword, id, Revision: entry.Definition!.Revision, Password: "")).Instances.Single();
            Check(!entry.HasPassword && InstancePasswordConfig.Read(instance) == "", "clear failed");
            Check(File.ReadAllText(source).EndsWith("sv_password \"\"\n"), "clear did not explicitly reset CS2 password");
            var second = store.Execute(workspace, new(InstanceAction.Create, Name: "Other instance", GamePort: 27016)).Instances.Single(e => e.Id != id);
            Check(!second.HasPassword, "another instance inherited the password setting");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            try { InstancePasswordConfig.Read(instance); throw new Exception("unsafe file permissions accepted"); }
            catch (InstanceException e) when (e.Code == "HCSL-INSTANCE-PASSWORD") { }
            Console.WriteLine("PASS instance.password.private-config-set-clear-preset-independence-lock-and-redaction");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
