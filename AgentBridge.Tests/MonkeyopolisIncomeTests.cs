namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void RunMonkeyopolisIncomeTests()
    {
        // BTD6 56.3 native upgrade measurements: minimum increment, then truncation
        // on both sides of the second increment and for a Research Facility.
        foreach (var (worth, expectedIncome) in new[]
        {
            (905f, 1200f),
            (3870f, 1200f),
            (4120f, 1400f),
            (20820f, 3000f)
        })
        {
            if (!TryCalculateMonkeyopolisIncome(worth, 2000, 200, 1000, 10,
                    out _, out float income, out float crateCash) ||
                income != expectedIncome || crateCash * 10 != expectedIncome)
                throw new InvalidOperationException($"Monkeyopolis worth {worth}: expected native income {expectedIncome}, got {income}.");
        }

        if (TryCalculateMonkeyopolisIncome(float.MaxValue, 1, 200, 1000, 10, out _, out _, out _) ||
            TryCalculateMonkeyopolisIncome(905, 2000, 200, 1000, 0, out _, out _, out _))
            throw new InvalidOperationException("Monkeyopolis must reject unrepresentable increments and zero crate count.");
        Console.WriteLine("PASS Monkeyopolis minimum increment and floor boundaries match native 56.3 upgrades");
    }
}
