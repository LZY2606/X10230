using System.Collections.Generic;
using Chrono.Core.Quantities;

namespace Chrono.Core.Model;

/// <summary>Measured age minus reservoir offset: both distributions sampled, not subtracted as means.</summary>
public sealed record ShiftedDistribution(IAgeDistribution Measured, IAgeDistribution Offset) : IAgeDistribution
{
    public string Kind => "reservoir_corrected";
    public string Datum => Measured.Datum;
    public IReadOnlyList<double> Parameters
    {
        get
        {
            var p = new List<double>(Measured.Parameters);
            p.AddRange(Offset.Parameters);
            return p;
        }
    }
    public double Draw(DeterministicRng rng) => Measured.Draw(rng) - Offset.Draw(rng);
}
