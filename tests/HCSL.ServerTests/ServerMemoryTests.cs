using System.Text.Json;
using HCSL.Core;
using HCSL.Infrastructure.Server;

namespace HCSL.ServerTests;

public static class ServerMemoryTests
{
    public static void Run()
    {
        foreach (var consent in new[] { false, true })
        {
            Expect(0, consent, "HCSL-SERVER-MEMORY");
            Expect(ServerMemoryPolicy.MinimumBytes - 1, consent, "HCSL-SERVER-MEMORY");
            Expect(ServerMemoryPolicy.MinimumBytes, consent, consent ? null : "HCSL-SERVER-MEMORY-WARNING");
            Expect(ServerMemoryPolicy.RecommendedBytes - 1, consent, consent ? null : "HCSL-SERVER-MEMORY-WARNING");
            Expect(ServerMemoryPolicy.RecommendedBytes, consent, null);
        }
        // Consent must not persist into the next ordinary request.
        Expect(3500 * ServerMemoryPolicy.MiB, true, null);
        Expect(3500 * ServerMemoryPolicy.MiB, false, "HCSL-SERVER-MEMORY-WARNING");
        Expect(3598 * ServerMemoryPolicy.MiB, false, "HCSL-SERVER-MEMORY-WARNING");
        Expect(3598 * ServerMemoryPolicy.MiB, true, null);
        if (HCSL.Frontend.ServerStatusText.CodeHint("HCSL-SERVER-MEMORY-WARNING", false)?.Contains("GUI/TUI") != true)
            throw new Exception("Shared memory hint must not direct GUI users only to TUI.");
        // Revalidation can still reject a confirmed request after memory falls below the floor.
        Expect(3000 * ServerMemoryPolicy.MiB, true, "HCSL-SERVER-MEMORY");
        var command = JsonSerializer.Deserialize<ServerCommand>("{\"Action\":0}")!;
        if (command.AllowLowMemory) throw new Exception("Old clients must not implicitly consent.");
        var explicitConsent = new ServerCommand(ServerAction.Start, AllowLowMemory: true);
        if (!JsonSerializer.Deserialize<ServerCommand>(JsonSerializer.Serialize(explicitConsent))!.AllowLowMemory)
            throw new Exception("Consent lost across JSON.");
        Console.WriteLine("PASS memory.policy-boundaries-consent-floor-recheck-and-json-defaults");
    }
    private static void Expect(long available, bool consent, string? code)
    {
        try { ServerPreparation.EnforceMemory(available, consent); }
        catch (ServerException error)
        {
            if (error.Code == code) return;
            throw;
        }
        if (code is not null) throw new Exception("Memory gate did not reject: " + code);
    }
}
