using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Core.Places;

namespace LeafCalendar.LiveTests;

// Real Google Maps with your own Maps API key: skipped until LEAF_LIVE_MAPS_KEY is set (see README.md)
public class LivePlacesTests
{
    [Fact]
    public async Task Search_RealKey_FindsAPlace()
    {
        var key = Environment.GetEnvironmentVariable("LEAF_LIVE_MAPS_KEY") is { Length: > 0 } fromProcess
            ? fromProcess
            : Environment.GetEnvironmentVariable("LEAF_LIVE_MAPS_KEY", EnvironmentVariableTarget.User) ?? "";
        if (key.Length == 0)
        {
            Assert.Skip("Set LEAF_LIVE_MAPS_KEY to a Maps API key with Places API (New) turned on.");
        }

        // A throwaway folder for the key and the log, never a real profile
        var folder = Directory.CreateTempSubdirectory("leaf-live-places-");
        try
        {
            using var http = new HttpClient();
            var search = new PlaceSearch(http, new ProtectedFileTokenStore(folder.FullName), new AppLog(folder.FullName, TimeProvider.System));
            search.SaveKey(key);

            Assert.Equal(KeyCheck.Works, await search.CheckKeyAsync(key, TestContext.Current.CancellationToken));
            Assert.NotEmpty(await search.SearchAsync("Eiffel Tower", TestContext.Current.CancellationToken));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
