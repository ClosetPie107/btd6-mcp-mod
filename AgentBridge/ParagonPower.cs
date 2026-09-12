using System;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static float CappedParagonPower(double amount, double units, int cap)
    {
        if (cap <= 0 || !double.IsFinite(amount) || amount <= 0d || !double.IsFinite(units) || units <= 0d)
            return 0f;
        return (float)Math.Min(amount / units, cap);
    }

    private static float ParagonCashPower(double sacrificePower, double sliderCash, double powerPerCash, int cap)
    {
        // Native InvestmentInfo retains fractional sacrifice power but rounds the
        // manual cash injection upward before combining it under the shared cap.
        float sliderPower = (float)Math.Ceiling(sliderCash * powerPerCash);
        return Math.Min(cap, (float)sacrificePower + sliderPower);
    }

    private static int? ParagonSliderCashNeeded(float targetMoneyPower, double sacrificePower,
        int currentCash, int maximumCash, double powerPerCash, int cap)
    {
        if (ParagonCashPower(sacrificePower, currentCash, powerPerCash, cap) >= targetMoneyPower)
            return 0;
        if (ParagonCashPower(sacrificePower, maximumCash, powerPerCash, cap) < targetMoneyPower)
            return null;

        // Search the native rounded function, not a continuous inverse. At most
        // 31 steps over whole-dollar inputs, including float rounding boundaries.
        int low = currentCash, high = maximumCash;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (ParagonCashPower(sacrificePower, middle, powerPerCash, cap) >= targetMoneyPower)
                high = middle;
            else
                low = middle + 1;
        }
        return low - currentCash;
    }

    private static long CeilingAdditionalCost(double targetPower, double currentRaw, double rawPerPower)
    {
        if (targetPower <= 0) return 0;
        if (!double.IsFinite(currentRaw) || !double.IsFinite(rawPerPower) || rawPerPower <= 0d)
            return long.MaxValue;
        double rawNeeded = targetPower * rawPerPower - currentRaw;
        if (rawNeeded <= 0d) return 0;
        double amount = Math.Ceiling(rawNeeded);
        return amount >= long.MaxValue ? long.MaxValue : (long)amount;
    }

    private static int DegreeForPower(float totalPower, int[] requirements, int degreeCount)
    {
        int degree = 1;
        for (int i = 0; i < degreeCount; i++)
        {
            if (totalPower < requirements[i]) break;
            degree = i + 1;
        }
        return degree;
    }
}
