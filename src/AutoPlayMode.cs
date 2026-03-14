namespace CombatAutoHost;

internal enum AutoPlayMode
{
    Balanced,
    Defensive,
    Aggressive
}

internal static class AutoPlayModeExtensions
{
    public static AutoPlayMode Next(this AutoPlayMode mode)
    {
        return mode switch
        {
            AutoPlayMode.Balanced => AutoPlayMode.Defensive,
            AutoPlayMode.Defensive => AutoPlayMode.Aggressive,
            _ => AutoPlayMode.Balanced
        };
    }

    public static string GetShortLabel(this AutoPlayMode mode)
    {
        return mode switch
        {
            AutoPlayMode.Balanced => "BAL",
            AutoPlayMode.Defensive => "DEF",
            _ => "AGG"
        };
    }
}
