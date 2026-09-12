using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    public static int Main(string[] args)
    {
        // Read metadata without loading any game/native dependencies into the host.
        if (args is ["--assembly-identity", var path])
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                moduleVersionId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D")
            }));
            return 0;
        }
        if (args.Length != 0) throw new ArgumentException("Expected no arguments or --assembly-identity <dll>");

        int passed = 0;
        void Check(string name, MapPoint[] a, MapPoint[] b, bool expected, float tolerance = 1f)
        {
            if (IsGeometryEquivalent(a, b, tolerance) != expected ||
                IsGeometryEquivalent(b, a, tolerance) != expected)
                throw new InvalidOperationException($"FAIL {name}");
            Console.WriteLine($"PASS {name}");
            passed++;
        }
        MapPoint[] P(params (float X, float Y)[] points) => points.Select(p => new MapPoint(p.X, p.Y)).ToArray();

        Check("nonuniform collinear sampling", P((0, 0), (100, 0)), P((0, 0), (1, 0), (2, 0), (100, 0)), true);
        Check("resampled bend", P((0, 0), (10, 0), (10, 10)), P((0, 0), (5, 0), (10, 0), (10, 1), (10, 10)), true);
        var above = Enumerable.Range(0, 41).Select(i => new MapPoint(i, i == 17 ? 4 : 0)).ToArray();
        var below = Enumerable.Range(0, 41).Select(i => new MapPoint(i, i == 17 ? -4 : 0)).ToArray();
        Check("equal-length branches between sparse sample indexes", above, below, false);
        Check("opposite orientation", P((0, 0), (100, 0)), P((100, 0), (0, 0)), false);
        Check("duplicate vertices", P((0, 0), (10, 0)), P((0, 0), (0, 0), (5, 0), (5, 0), (10, 0), (10, 0)), true);
        Check("degenerate point", P((1, 2)), P((1, 2), (1, 2)), true);
        Check("different endpoints", P((0, 0), (10, 0)), P((0, 0), (12, 0)), false);
        Check("inside tolerance", P((0, 0), (10, 0)), P((0, 0.9f), (10, 0.9f)), true);
        Check("outside tolerance", P((0, 0), (10, 0)), P((0, 1.1f), (10, 1.1f)), false);
        Check("nonfinite interior coordinate", P((0, 0), (10, 0)), P((0, 0), (float.NaN, 0), (10, 0)), false);
        Console.WriteLine($"{passed} geometry regressions passed");
        UiInterruptionTests.Run();
        AbilityTargetTests.Run();
        RunMonkeyopolisIncomeTests();
        RunParagonPowerTests();
        PlacementGeometryTests.Run();
        CheckpointBridgeRegressionTests.Run();
        return 0;
    }
}
