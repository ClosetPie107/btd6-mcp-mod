using System;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static bool TryCalculateMonkeyopolisIncome(
        float totalEligibleFarmWorth,
        int valueRequiredForIncomeIncrement,
        int cashPerIncomeIncrement,
        int baseIncomePerRound,
        int cratesPerRound,
        out int incomeIncrements,
        out float projectedIncomePerRound,
        out float cashPerCrate)
    {
        incomeIncrements = 0;
        projectedIncomePerRound = 0f;
        cashPerCrate = 0f;
        if (!float.IsFinite(totalEligibleFarmWorth) || totalEligibleFarmWorth < 0f
            || valueRequiredForIncomeIncrement <= 0 || cashPerIncomeIncrement < 0
            || baseIncomePerRound < 0 || cratesPerRound <= 0)
            return false;

        double ratio = (double)totalEligibleFarmWorth / valueRequiredForIncomeIncrement;
        if (!double.IsFinite(ratio) || ratio < 0d || ratio > int.MaxValue)
            return false;
        // Native 56.3 upgrades truncate the worth ratio, with at least one increment
        // for an eligible farm. Measured at $905, $3,870, $4,120 and $20,820.
        incomeIncrements = Math.Max(1, (int)Math.Floor(ratio));
        long projected = (long)baseIncomePerRound + (long)incomeIncrements * cashPerIncomeIncrement;
        if (projected < 0L || projected > float.MaxValue)
            return false;
        projectedIncomePerRound = projected;
        cashPerCrate = projectedIncomePerRound / cratesPerRound;
        return float.IsFinite(cashPerCrate);
    }

}
