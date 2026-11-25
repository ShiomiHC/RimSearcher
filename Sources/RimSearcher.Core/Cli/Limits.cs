namespace RimSearcher.Cli;

public static class Limits
{
    public const int ValueSpaceSample = 2000;

    public const int CodeSearchRegexTimeoutMs = 2000;

    public const int CodeSearchLineChars = 240;

    public const int AmbiguousFiles = 8;

    public const int MaxNoticeLines = 6;

    public const int MaxSuggestions = 3;

    public const int MaxShownShapes = MaxSuggestions * 2;

    public const int FuzzyFallbackThreshold = 1;
}
