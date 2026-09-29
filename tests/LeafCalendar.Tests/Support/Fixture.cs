namespace LeafCalendar.Tests.Support;

/// <summary>Reads recorded Google responses from the Fixtures folder.</summary>
public static class Fixture
{
    /// <summary>Returns the text of <paramref name="name"/> from the test output's Fixtures folder.</summary>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
