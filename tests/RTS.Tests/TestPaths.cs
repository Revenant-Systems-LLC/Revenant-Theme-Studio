namespace RTS.Tests;

/// <summary>
/// Every test writes under the test build folder on M:, never under %TEMP% on C:
/// (Nomos Logos write zone). Each test class deletes its own folder when done.
/// </summary>
internal static class TestPaths
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "tmp");
}
