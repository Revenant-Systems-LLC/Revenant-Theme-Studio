namespace Revenant_Theme_Studio.Models
{
    public enum MatchStrength
    {
        Strong,
        Weak,
        None
    }

    public enum MatchStrictness
    {
        Strict,
        Balanced,
        Loose
    }

    public class MatchDecision
    {
        public MatchStrength Strength { get; init; }
        public string? IconPath { get; init; }
        public string? Reason { get; init; }
    }

    public class AutoMatchResult
    {
        public required string FolderName { get; init; }
        public required string TargetPath { get; init; }
        public string? SuggestedIcon { get; init; }
        public required string Reason { get; init; }
    }
}
