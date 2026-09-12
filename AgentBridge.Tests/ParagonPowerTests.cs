namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void RunParagonPowerTests()
    {
        // Native 56.3: Easy Dart Paragon, $2,510 sacrifice worth, 20 damage,
        // six non-T5 tiers and a $67,193 injection produced degree 20 / 11032.837 power.
        double sacrificePower = 2510d / 6.375d;
        double powerPerCash = 20000d / (127500d * 1.05d);
        float pops = CappedParagonPower(20, 180, 90000);
        float money = ParagonCashPower(sacrificePower, 67193, powerPerCash, 60000);
        float total = pops + money + 600f;
        int[] requirements = [0, 2000, 2324, 2666, 3027, 3408, 3808, 4228, 4669, 5131,
            5615, 6121, 6650, 7203, 7779, 8379, 9004, 9654, 10330, 11032];
        if (Math.Abs(total - 11032.837f) > 0.001f || DegreeForPower(total, requirements, 20) != 20)
            throw new InvalidOperationException($"Fractional native Paragon power lost: {total}.");

        float requiredMoney = 11032f - 600f - pops;
        int? additional = ParagonSliderCashNeeded(requiredMoney, sacrificePower, 67180, 401625, powerPerCash, 60000);
        if (additional != 12 ||
            ParagonCashPower(sacrificePower, 67180 + additional.Value - 1, powerPerCash, 60000) >= requiredMoney ||
            ParagonCashPower(sacrificePower, 67180 + additional.Value, powerPerCash, 60000) < requiredMoney)
            throw new InvalidOperationException("Paragon slider milestone must return the minimum additional whole-dollar injection.");

        if (ParagonSliderCashNeeded(requiredMoney, sacrificePower, 67193, 401625, powerPerCash, 60000) != 0 ||
            ParagonSliderCashNeeded(60001, sacrificePower, 0, 401625, powerPerCash, 60000) != null ||
            ParagonCashPower(60000, 100000, powerPerCash, 60000) != 60000)
            throw new InvalidOperationException("Paragon milestone reachability must respect current investment and the shared cash category cap.");
        Console.WriteLine("PASS native Paragon fractional power, ceiling slider, degree boundary and minimum additional investment");
    }
}
