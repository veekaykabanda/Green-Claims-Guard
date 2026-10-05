using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class AnalysisLoggingTests
{
    private readonly ApiTestFactory _factory;

    public AnalysisLoggingTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private Task<int> CountLogsFor(string marker) =>
        _factory.WithDbAsync(db => db.AnalysisLogs.CountAsync(l => l.InputText.Contains(marker)));

    private static async Task<int> WaitForLogs(Func<Task<int>> count, int expected)
    {
        // logging happens in the background, so give it a sec to land
        for (var i = 0; i < 40; i++)
        {
            var current = await count();
            if (current >= expected) return current;
            await Task.Delay(100);
        }
        return await count();
    }

    [Fact]
    public async Task LiveTypingChecks_AreNotLogged()
    {
        const string marker = "live-marker-7431";
        var client = _factory.CreateCopywriter();

        foreach (var rulesOnly in new[] { true, false })
        {
            var response = await client.PostAsJsonAsync("/api/analyze", new
            {
                text = $"Sustainable bamboo fabric {marker}.",
                rulesOnly,
                trigger = "live"
            });
            response.EnsureSuccessStatusCode();
        }

        // A request that says nothing about its trigger is treated as live too.
        (await client.PostAsJsonAsync("/api/analyze", new { text = $"Bamboo {marker}.", rulesOnly = true }))
            .EnsureSuccessStatusCode();

        await Task.Delay(800);
        Assert.Equal(0, await CountLogsFor(marker));
    }

    [Fact]
    public async Task ManualChecks_AreLogged()
    {
        const string marker = "manual-marker-9152";
        var client = _factory.CreateCopywriter();

        var response = await client.PostAsJsonAsync("/api/analyze", new
        {
            text = $"Sustainable bamboo fabric {marker}.",
            rulesOnly = true,
            trigger = "manual"
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, await WaitForLogs(() => CountLogsFor(marker), 1));
    }
}
