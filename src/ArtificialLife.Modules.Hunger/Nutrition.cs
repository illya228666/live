namespace ArtificialLife.Modules.Hunger;

/// <summary>Edible nutrition on any entity, independent of its visual appearance.</summary>
public sealed record Nutrition
{
    public double HungerReduction { get; }

    public Nutrition(double hungerReduction)
    {
        if (!double.IsFinite(hungerReduction) || hungerReduction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(hungerReduction));
        HungerReduction = hungerReduction;
    }
}
