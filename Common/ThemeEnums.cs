namespace Common
{
    public enum DayThemeType : ushort
    {
        ClassicPurple = 0,
        Red = 1,
        Blue = 2,
        Grey = 3,
        Modern = 4,
        Glass = 5,
    }

    public enum NightThemeType : ushort
    {
        ClassicPurple = 0,
        Grey = 1,
        Blue = 2,
        Red = 3,
        AmoledClassicPurple = 4,
        AmoledGrey = 5,
        Modern = 6,
        Glass = 7,
        AmoledGlass = 8,
    }

    /// <summary>SeekerOG accent colour overlays.</summary>
    public enum AccentColorType : ushort
    {
        ThemeDefault = 0,
        Ocean = 1,
        Violet = 2,
        Emerald = 3,
        Sunset = 4,
        Rose = 5,
        Graphite = 6,
    }

    /// <summary>SeekerOG glass surface intensity.</summary>
    public enum GlassIntensityType : ushort
    {
        Subtle = 0,
        Balanced = 1,
        Intense = 2,
    }
}
