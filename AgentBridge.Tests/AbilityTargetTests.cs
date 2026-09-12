using System.Text.Json;

namespace AgentBridge;

internal static class AbilityTargetTests
{
    public static void Run()
    {
        bool Accepts(string json)
        {
            using var document = JsonDocument.Parse(json);
            return AbilityTargetV1.TryRead(document.RootElement, out _);
        }
        foreach (string json in new[]
        {
            "{\"target\":null}",
            "{\"target\":{\"kind\":\"point\",\"x\":1}}",
            "{\"target\":{\"kind\":\"point\",\"x\":1,\"y\":2,\"towerId\":\"7\"}}",
            "{\"target\":{\"kind\":\"point\",\"x\":1,\"x\":2,\"y\":3}}",
            "{\"target\":{\"kind\":\"point\",\"x\":1e100,\"y\":2}}",
            "{\"target\":{\"kind\":\"tower\",\"towerId\":\" \"}}",
            "{\"target\":{\"kind\":\"tower_position\",\"towerId\":\"7\",\"x\":1}}",
            "{\"target\":{\"kind\":\"points\",\"points\":[]}}",
            "{\"target\":{\"kind\":\"points\",\"points\":[{\"x\":1,\"y\":2,\"z\":3}]}}"
        })
            if (Accepts(json)) throw new InvalidOperationException($"Accepted invalid ability target: {json}");

        string many = "{\"target\":{\"kind\":\"points\",\"points\":[" +
            string.Join(',', Enumerable.Repeat("{\"x\":1,\"y\":2}", 33)) + "]}}";
        if (Accepts(many)) throw new InvalidOperationException("Accepted unbounded ability point list");
        if (!Accepts("{}")) throw new InvalidOperationException("Untargeted abilities must remain valid");

        const string input = "{\"target\":{\"kind\":\"tower_position\",\"towerId\":\"42\",\"x\":-85,\"y\":40}}";
        using var valid = JsonDocument.Parse(input);
        if (!AbilityTargetV1.TryRead(valid.RootElement, out var target)) throw new InvalidOperationException("Rejected valid relocation");
        var serialized = JsonSerializer.Serialize(new { target }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var roundTrip = JsonDocument.Parse(serialized);
        if (!AbilityTargetV1.TryRead(roundTrip.RootElement, out var restored) ||
            restored?.TowerId != "42" || restored.X != -85 || restored.Y != 40)
            throw new InvalidOperationException("Scheduled target cannot round-trip through protocol JSON");
        Console.WriteLine("PASS strict ability target boundary and scheduled target serialization");
    }
}
