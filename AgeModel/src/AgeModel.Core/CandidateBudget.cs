namespace AgeModel.Core;

public sealed class CandidateBudget
{
    private int remaining;

    public CandidateBudget(int maximum)
    {
        if (maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Candidate budget must be non-negative.");
        remaining = maximum;
    }

    public bool TryConsume()
    {
        if (remaining <= 0) return false;
        remaining--;
        return true;
    }
}
