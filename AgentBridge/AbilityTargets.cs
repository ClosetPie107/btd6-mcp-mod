using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge;

internal sealed class AbilityTargetPointV1
{
    public float X { get; init; }
    public float Y { get; init; }
}

internal sealed class AbilityTargetV1
{
    private static readonly string[] PointFields = { "kind", "x", "y" };
    private static readonly string[] TowerFields = { "kind", "towerId" };
    private static readonly string[] TowerPositionFields = { "kind", "towerId", "x", "y" };
    private static readonly string[] PointsFields = { "kind", "points" };
    private static readonly string[] PositionFields = { "x", "y" };

    public string Kind { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? X { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Y { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TowerId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AbilityTargetPointV1[]? Points { get; init; }

    // Validate the mailbox boundary too: MCP is not the only protocol client.
    public static bool TryRead(JsonElement payload, out AbilityTargetV1? target)
    {
        target = null;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty("target", out var value)) return true;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String)
            return false;

        string? kind = kindValue.GetString();
        string[] fields;
        switch (kind)
        {
            case "point": fields = PointFields; break;
            case "tower": fields = TowerFields; break;
            case "tower_position": fields = TowerPositionFields; break;
            case "points": fields = PointsFields; break;
            default: return false;
        }
        if (!HasExactFields(value, fields)) return false;

        float? x = null, y = null;
        if (kind == "point" || kind == "tower_position")
        {
            if (!TryPoint(value, out float px, out float py)) return false;
            x = px;
            y = py;
        }
        string? towerId = null;
        if (kind == "tower" || kind == "tower_position")
        {
            var idValue = value.GetProperty("towerId");
            if (idValue.ValueKind != JsonValueKind.String) return false;
            towerId = idValue.GetString();
            if (string.IsNullOrWhiteSpace(towerId) || towerId.Length > 128) return false;
        }
        AbilityTargetPointV1[]? points = null;
        if (kind == "points")
        {
            var array = value.GetProperty("points");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) return false;
            points = new AbilityTargetPointV1[array.GetArrayLength()];
            int i = 0;
            foreach (var point in array.EnumerateArray())
            {
                if (!HasExactFields(point, PositionFields) || !TryPoint(point, out float px, out float py)) return false;
                points[i++] = new AbilityTargetPointV1 { X = px, Y = py };
            }
        }
        target = new AbilityTargetV1 { Kind = kind!, X = x, Y = y, TowerId = towerId, Points = points };
        return true;
    }

    private static bool HasExactFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        int seen = 0;
        foreach (var property in value.EnumerateObject())
        {
            int index = Array.IndexOf(fields, property.Name);
            if (index < 0 || (seen & (1 << index)) != 0) return false;
            seen |= 1 << index;
        }
        return seen == (1 << fields.Length) - 1;
    }

    private static bool TryPoint(JsonElement value, out float x, out float y)
    {
        x = y = 0;
        return TryCoordinate(value, "x", out x) && TryCoordinate(value, "y", out y);
    }

    private static bool TryCoordinate(JsonElement value, string name, out float coordinate)
    {
        coordinate = 0;
        if (!value.TryGetProperty(name, out var number) || number.ValueKind != JsonValueKind.Number ||
            !number.TryGetDouble(out double parsed) || !double.IsFinite(parsed) ||
            parsed < -float.MaxValue || parsed > float.MaxValue) return false;
        coordinate = (float)parsed;
        return true;
    }
}

internal sealed class AbilityTargetingV1
{
    public string Kind { get; init; } = "unsupported";
    public string? InputClass { get; init; }
    public bool Supported { get; init; }
    public int? PointCount { get; init; }
}
