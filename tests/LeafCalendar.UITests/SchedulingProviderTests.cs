using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Settings;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

/// <summary>
/// Sharing through Cal.com against the fake Cal.com under <c>--fake-google</c>: signing in from Settings › Accounts, the
/// Scheduling page, the share panel's switch and link, fitting picks to open times, linked copies, bookings found by the
/// booking check, and Approve… booking a time.
/// </summary>
public sealed class SchedulingProviderTests : IDisposable
{
    private const string Eastern = "America/New_York";
    private const string Provider = "calcom";
    private const string Jane = "jane@example.com";
    private const string Sam = "sam@example.com";
    private const string FirstSaved = "SavedSlot_1_0";
    private const string ThirtyMinutes = "30 min meeting · 30 min";
    private const string QuickChat = "Quick chat · 15 min";

    private readonly FakeGoogleServer _google = new();
    private string _profile = SignedInProfile();

    // Every test starts with 30-minute openings 9 AM-5 PM Eastern on Oct 1 and 2, unless it sets its own
    public SchedulingProviderTests() => _google.CalComOpen = [.. Open(1, 9, 17), .. Open(2, 9, 17)];

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    // The booking check reads the clock, so it's set to the start date's 8 AM too (else Oct 1's groups are long past)
    private LeafApp Launch() => LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01 --now 2026-10-01T08:00:00-04:00");

    // A profile signed in to the fake Google and to the fake Cal.com (its OAuth client and refresh token saved)
    private static string SignedInProfile(string defaultLink = "") => SignedInProfile(new LeafSettings
    {
        PrimaryTimeZone = Eastern,
        Scheduling = defaultLink.Length > 0 ? [new ProviderSettings(Provider, "", defaultLink)] : [],
    });

    /// <summary>A profile with these settings, signed in to the fake Google and to the fake Cal.com.</summary>
    internal static string SignedInProfile(LeafSettings settings)
    {
        var profile = SeededProfile.Create(settings);
        var store = new ProtectedFileTokenStore(LeafApp.ProfileFolder(profile));
        store.SetProviderSecret(Provider, "client_id", "calcom-uitest-client");
        store.SetProviderSecret(Provider, "client_secret", "calcom-uitest-secret");
        store.SetProviderSecret(Provider, "refresh_token", FakeGoogleServer.CalComRefreshToken);
        return profile;
    }

    // Swaps this test's profile for another (the old one is deleted)
    private void UseProfile(string profile)
    {
        LeafApp.DeleteProfile(_profile);
        _profile = profile;
    }

    // An Eastern time on Oct (day) 2026 (Eastern daylight time, UTC-4)
    private static DateTimeOffset At(int day, double hour) => new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.FromHours(-4)).AddHours(hour);

    // 30-minute openings from one Eastern hour to another
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Open(int day, double from, double to)
    {
        for (var h = from; h < to; h += 0.5)
        {
            yield return (At(day, h), At(day, h + 0.5));
        }
    }

    // The slot= value a copied link carries for a start (a UTC instant; the link keeps its colons as they are)
    private static string Slot(int day, double hour) => "slot=" + At(day, hour).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    private static AutomationElement? Find(LeafApp leaf, string automationId) => leaf.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    // The link the share panel's Link box shows
    private static string SelectedLink(LeafApp leaf) => Find(leaf, "ShareLinkBox")?.AsComboBox().SelectedItem?.Name ?? "";

    // S, then wait until the panel shows the link and the link's openings are shaded (the closed stretch before 9 AM)
    private static void StartSharingWithLink(LeafApp leaf, string link = ThirtyMinutes)
    {
        ShareAvailabilityTests.StartSharing(leaf);
        WaitForLink(leaf, link);
        WaitForOpenTimes(leaf);
    }

    private static void WaitForLink(LeafApp leaf, string link) =>
        Assert.True(Retry.WhileFalse(() => SelectedLink(leaf) == link, TimeSpan.FromSeconds(15)).Success, $"The Link box shows \"{SelectedLink(leaf)}\", not \"{link}\".");

    private static void WaitForOpenTimes(LeafApp leaf) =>
        Assert.True(Retry.WhileFalse(() => Find(leaf, "ClosedTime_2026-10-01_0") is not null, TimeSpan.FromSeconds(15)).Success, "The closed times weren't shaded.");

    // The scheduling messages over the calendar right now, top first
    private static List<string> Messages(LeafApp leaf) =>
        [.. Enumerable.Range(0, 4).Select(i => Find(leaf, $"ShareMessage_{i}")?.Name).OfType<string>()];

    private static void AssertMessages(LeafApp leaf, params string[] expected) =>
        Assert.True(
            Retry.WhileFalse(() => expected.All(Messages(leaf).Contains), TimeSpan.FromSeconds(10)).Success,
            $"Messages were [{string.Join(" | ", Messages(leaf))}], expected [{string.Join(" | ", expected)}].");

    // Copy (& Save), then the clipboard's plain text once it holds the copy (sharing stops)
    private static string Copy(LeafApp leaf)
    {
        Clipboard.Clear();
        leaf.WaitFor("ShareCopyButton").AsButton().Invoke();
        var text = Retry.WhileNull(Clipboard.Text, TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(text);
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Copy & Save didn't stop sharing.");
        return text;
    }

    // Picks a time on the Cal.com link (guests added), then Copy & Save makes it the first saved group
    private static string SaveGroup(LeafApp leaf, double from, double to, string link = ThirtyMinutes, params string[] guests)
    {
        StartSharingWithLink(leaf, link);
        ShareAvailabilityTests.DragHours(leaf, from, to);
        leaf.WaitFor("ShareSlot_0");
        for (var i = 0; i < guests.Length; i++)
        {
            SavedShareGroupTests.AddGuest(leaf, guests[i], i);
        }

        var text = Copy(leaf);
        Assert.NotNull(leaf.WaitFor(FirstSaved));
        return text;
    }

    // Opens the first saved group and waits for its link to be read from Cal.com
    private static void OpenGroup(LeafApp leaf, string link = ThirtyMinutes)
    {
        SavedShareGroupTests.OpenFirst(leaf);
        WaitForLink(leaf, link);
    }

    // The open group's panel closed and its time is still on the grid (an open group's times show as picks)
    private static void AssertGroupStays(LeafApp leaf)
    {
        leaf.WaitFor("ShareCancelButton").AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(5)).Success, "Close left the panel up.");
        Assert.NotNull(leaf.WaitFor(FirstSaved));
    }

    // Sharing stopped and the group's time is gone from the grid
    private static void AssertGroupGone(LeafApp leaf)
    {
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "The panel stayed up.");
        Thread.Sleep(1000);
        Assert.False(leaf.Exists(FirstSaved), "The group stayed on the grid.");
    }

    // Approve… on the open group's first time
    private static void Approve(LeafApp leaf)
    {
        var approve = leaf.WaitFor("SharePanelApprove_0").AsButton();
        Assert.True(Retry.WhileFalse(() => approve.IsEnabled, TimeSpan.FromSeconds(5)).Success, "Approve… stayed off.");
        approve.Invoke();
    }

    // Every text in the booking dialog (its title, then the guests)
    private static List<string> DialogTexts(LeafApp leaf) =>
        [.. leaf.WaitForAnywhere("BookDialog").FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name)];

    private static void PressDialog(LeafApp leaf, string button)
    {
        leaf.WaitForAnywhere(button).AsButton().Invoke();
        Assert.True(Retry.WhileTrue(() => leaf.ExistsAnywhere("BookDialog"), TimeSpan.FromSeconds(5)).Success, "The booking dialog stayed open.");
    }

    private static bool InSettings(LeafApp leaf, string automationId) =>
        leaf.SettingsView.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null;

    // The provider card's error shows exactly this text
    private static bool ErrorSays(LeafApp leaf, string text) =>
        Retry.WhileFalse(
            () => leaf.SettingsView.FindFirstDescendant(cf => cf.ByAutomationId($"ProviderError_{Provider}")) is { } error
                && error.FindAllDescendants().Any(e => e.Properties.Name.ValueOrDefault == text),
            TimeSpan.FromSeconds(15)).Success;

    // Settings › Accounts: Add OAuth client…, type the client ID and secret like a person, Save
    private static void AddClient(LeafApp leaf)
    {
        leaf.OpenSettings("Accounts");
        leaf.WaitInSettings($"ProviderAddClient_{Provider}").AsButton().Invoke();
        foreach (var (id, value) in new[] { ($"ProviderClientId_{Provider}", "calcom-uitest-client"), ($"ProviderClientSecret_{Provider}", "calcom-uitest-secret") })
        {
            leaf.WaitInSettings(id).Focus();
            Thread.Sleep(300);
            Keyboard.Type(value);
        }

        var save = leaf.WaitInSettings($"ProviderSaveClient_{Provider}").AsButton();
        Assert.True(Retry.WhileFalse(() => save.IsEnabled, TimeSpan.FromSeconds(5)).Success, "Save stayed off with both boxes filled.");
        save.Invoke();
        Assert.NotNull(leaf.WaitInSettings($"ProviderSignIn_{Provider}"));
    }

    // The booking Leaf sent to the fake Cal.com (waits up to 10 s; a failure shows Leaf's scheduling log lines)
    private JsonNode BookingSent()
    {
        var body = Retry.WhileNull(() => _google.CalComBookings.LastOrDefault(), TimeSpan.FromSeconds(10)).Result;
        Assert.True(body is not null, "Nothing was booked. Log: " + string.Join(" | ", LogLines().Where(l => l.Contains("scheduling", StringComparison.Ordinal) || l.Contains("share", StringComparison.Ordinal))));
        return JsonNode.Parse(body)!;
    }

    private string[] LogLines()
    {
        var log = Path.Combine(LeafApp.ProfileFolder(_profile), "Logs", "leaf.log");
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    private string? SavedRefreshToken() => new ProtectedFileTokenStore(LeafApp.ProfileFolder(_profile)).GetProviderSecret(Provider, "refresh_token");

    // =========================================================================
    // SIGNING IN AND SETTINGS
    // =========================================================================

    // Before signing in nothing about Cal.com shows: no Scheduling page and no switch in the share panel
    [Fact]
    public void NotSignedIn_ShowsNoSwitchAndNoSchedulingPage()
    {
        UseProfile(SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern }));
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        Thread.Sleep(1000);
        Assert.False(leaf.Exists("ShareProviderSwitch"), "The share panel showed the Cal.com switch before signing in.");

        leaf.OpenSettings("Accounts");
        Assert.NotNull(leaf.WaitInSettings($"ProviderAddClient_{Provider}"));
        Assert.False(InSettings(leaf, "SettingsNav_Scheduling"), "Settings › Scheduling showed before signing in.");
    }

    // Add the OAuth client, sign in (the browser comes straight back), and Settings › Scheduling appears with both
    // links in its Default link box; the refresh token is saved
    [Fact]
    public void AddClient_SignIn_ShowsTheSchedulingPageWithTheLinks()
    {
        UseProfile(SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern }));
        using var leaf = Launch();
        AddClient(leaf);
        Assert.False(InSettings(leaf, "SettingsNav_Scheduling"), "Settings › Scheduling showed before signing in.");

        leaf.WaitInSettings($"ProviderSignIn_{Provider}").AsButton().Invoke();

        Assert.NotNull(leaf.WaitInSettings($"ProviderSignOut_{Provider}"));
        Assert.Equal(FakeGoogleServer.CalComRefreshToken, SavedRefreshToken());
        leaf.OpenSettings("Scheduling");
        var links = leaf.WaitInSettings($"SchedulingDefaultLink_{Provider}").AsComboBox();
        links.Expand();
        var names = new List<string>();
        Retry.WhileFalse(() => (names = [.. links.Items.Select(i => i.Name)]).Count > 0, TimeSpan.FromSeconds(5));
        links.Collapse();
        Assert.Equal([ThirtyMinutes, QuickChat], names);
    }

    // Cal.com refusing the sign-in says so, saves no refresh token and keeps Scheduling hidden
    [Fact]
    public void SignIn_Refused_SaysSoAndSavesNothing()
    {
        UseProfile(SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern }));
        _google.CalComRefuseToken = true;
        using var leaf = Launch();
        AddClient(leaf);

        leaf.WaitInSettings($"ProviderSignIn_{Provider}").AsButton().Invoke();

        Assert.True(ErrorSays(leaf, "Cal.com didn't accept this OAuth client. Check the client ID and secret, and that the redirect URI is registered."), "The refused sign-in didn't say so.");
        Assert.NotNull(leaf.WaitInSettings($"ProviderSignIn_{Provider}"));
        Assert.False(InSettings(leaf, "SettingsNav_Scheduling"), "Settings › Scheduling showed after a refused sign-in.");
        Assert.True(string.IsNullOrEmpty(SavedRefreshToken()), "A refused sign-in saved a refresh token.");
    }

    // Another app holding port 53817 stops the sign-in with the port-busy text
    [Fact]
    public void SignIn_PortBusy_SaysSo()
    {
        UseProfile(SeededProfile.Create(new LeafSettings { PrimaryTimeZone = Eastern }));
        using var leaf = Launch();
        AddClient(leaf);

        var busy = new TcpListener(IPAddress.Loopback, 53817);
        busy.Start();
        try
        {
            leaf.WaitInSettings($"ProviderSignIn_{Provider}").AsButton().Invoke();

            Assert.True(ErrorSays(leaf, "Port 53817 is busy, so Leaf can't finish signing in. Close the app using it and try again."), "The busy port didn't say so.");
            Assert.True(string.IsNullOrEmpty(SavedRefreshToken()), "A failed sign-in saved a refresh token.");
        }
        finally
        {
            busy.Stop();
            busy.Dispose();
        }
    }

    // The default link picked in Settings › Scheduling starts each new share
    [Fact]
    public void DefaultLink_StartsEachShare()
    {
        using var leaf = Launch();
        leaf.OpenSettings("Scheduling");
        var box = leaf.WaitInSettings($"SchedulingDefaultLink_{Provider}").AsComboBox();
        Assert.True(Retry.WhileFalse(() => box.SelectedItem?.Name == ThirtyMinutes, TimeSpan.FromSeconds(10)).Success, "The Default link box didn't load the links.");
        box.Select(QuickChat);
        Assert.True(Retry.WhileFalse(() => box.SelectedItem?.Name == QuickChat, TimeSpan.FromSeconds(5)).Success, $"The Default link box shows \"{box.SelectedItem?.Name}\".");
        leaf.CloseSettings();

        ShareAvailabilityTests.StartSharing(leaf);

        WaitForLink(leaf, QuickChat);
        Assert.True(leaf.WaitFor("ShareProviderSwitch").AsToggleButton().ToggleState == ToggleState.On, "The share didn't start with the switch on.");
    }

    // Sign out hides Settings › Scheduling and the panel's switch, and a saved Cal.com group copies plain text; Remove
    // OAuth client then goes back to Add OAuth client…
    [Fact]
    public void SignOut_HidesSchedulingAndTheSwitch_GroupsBecomePlain_RemoveClientGoesBack()
    {
        using var leaf = Launch();
        Assert.Contains(Slot(1, 10), SaveGroup(leaf, 10, 11), StringComparison.Ordinal);
        leaf.OpenSettings("Accounts");
        Assert.True(InSettings(leaf, "SettingsNav_Scheduling"), "Settings › Scheduling wasn't there while signed in.");

        leaf.WaitInSettings($"ProviderSignOut_{Provider}").AsButton().Invoke();

        Assert.NotNull(leaf.WaitInSettings($"ProviderSignIn_{Provider}"));
        Assert.True(Retry.WhileTrue(() => InSettings(leaf, "SettingsNav_Scheduling"), TimeSpan.FromSeconds(5)).Success, "Settings › Scheduling stayed after signing out.");
        Assert.True(string.IsNullOrEmpty(SavedRefreshToken()), "Signing out kept the refresh token.");

        leaf.WaitInSettings($"ProviderRemoveClient_{Provider}").AsButton().Invoke();
        Assert.NotNull(leaf.WaitInSettings($"ProviderAddClient_{Provider}"));
        leaf.CloseSettings();

        // The Group Is Plain Now
        OpenGroupPlain(leaf);
        Assert.False(leaf.Exists("ShareProviderSwitch"), "The switch showed after signing out.");
        Assert.DoesNotContain("slot=", Copy(leaf), StringComparison.Ordinal);
    }

    // Signing out in Settings while a Cal.com share is open turns the share plain: the switch goes, Copy & Save copies
    // plain text, and the group is saved without the link (after signing in again it opens with the switch off)
    [Fact]
    public void SignOut_WhileSharing_SharePlainAndSavedWithoutTheLink()
    {
        using (var leaf = Launch())
        {
            StartSharingWithLink(leaf);
            ShareAvailabilityTests.DragHours(leaf, 10, 11);
            leaf.WaitFor("ShareSlot_0");

            leaf.OpenSettings("Accounts");
            leaf.WaitInSettings($"ProviderSignOut_{Provider}").AsButton().Invoke();
            Assert.NotNull(leaf.WaitInSettings($"ProviderSignIn_{Provider}"));
            leaf.CloseSettings();

            Assert.NotNull(leaf.WaitFor("ShareSlotsPanel"));
            Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareProviderSwitch"), TimeSpan.FromSeconds(5)).Success, "The switch stayed after signing out.");
            Assert.DoesNotContain("slot=", Copy(leaf), StringComparison.Ordinal);
            Assert.NotNull(leaf.WaitFor(FirstSaved));
        }

        // Signed In Again, A Link Saved With The Group Would Turn Its Switch On
        new ProtectedFileTokenStore(LeafApp.ProfileFolder(_profile)).SetProviderSecret(Provider, "refresh_token", FakeGoogleServer.CalComRefreshToken);
        using var again = Launch();
        OpenGroupPlain(again);
        Assert.True(again.WaitFor("ShareProviderSwitch").AsToggleButton().ToggleState == ToggleState.Off, "The group was saved with the Cal.com link after signing out.");
    }

    // Cal.com refusing the sign-in while a saved group is open says to sign in again, Approve… says the same instead of
    // opening the event editor, and Save keeps the group's link (next run, signed in, it opens with the switch on)
    [Fact]
    public void NeedsReconnect_WhileSharing_SaysSoAndSaveKeepsTheLink()
    {
        using (var leaf = Launch())
        {
            SaveGroup(leaf, 10, 10.5, ThirtyMinutes, Jane);
        }

        // The First Cal.com Call Of This Run (reading the links) Is Refused
        _google.CalComRefuseToken = true;
        using (var leaf = Launch())
        {
            SavedShareGroupTests.OpenFirst(leaf);
            const string Reconnect = "Cal.com signed Leaf out. Sign in again to keep sharing booking links.";
            AssertMessages(leaf, Reconnect);

            // Approving In The Editor Would Close The Panel
            Approve(leaf);
            Thread.Sleep(1000);
            Assert.False(leaf.Exists("EditorTitle"), "Approve… opened the event editor.");
            Assert.True(leaf.Exists("ShareSlotsPanel"), "Approve… closed the panel.");
            AssertMessages(leaf, Reconnect);

            leaf.WaitFor("ShareSaveButton").AsButton().Invoke();
            Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Save didn't close the panel.");
        }

        _google.CalComRefuseToken = false;
        using var again = Launch();
        OpenGroup(again);
        Assert.True(again.WaitFor("ShareProviderSwitch").AsToggleButton().ToggleState == ToggleState.On, "Save dropped the Cal.com link while Cal.com needed signing in again.");
    }

    private static void OpenGroupPlain(LeafApp leaf)
    {
        SavedShareGroupTests.OpenFirst(leaf);
        Thread.Sleep(1000);
    }

    // =========================================================================
    // FITTING AND COPYING
    // =========================================================================

    // Open only 10-11 AM: a drag of 10-11:45 keeps both openings and says what happened, and the copy links exactly them
    [Fact]
    public void Drag_OverClosedTime_IsFittedAndExplained()
    {
        _google.CalComOpen = [.. Open(1, 10, 11)];
        using var leaf = Launch();
        StartSharingWithLink(leaf);

        ShareAvailabilityTests.DragHours(leaf, 10, 11.75);

        AssertMessages(leaf, "Split into 2 times of 30 minutes.", "45 minutes wasn't open on Cal.com.");
        var text = Copy(leaf);
        Assert.Equal(2, Count(text, "slot="));
        Assert.Contains(Slot(1, 10), text, StringComparison.Ordinal);
        Assert.Contains(Slot(1, 10.5), text, StringComparison.Ordinal);
    }

    // A drag made before Cal.com answers the open times is fitted once they arrive, and copies fitted
    [Fact]
    public void Drag_BeforeOpenTimesArrive_IsFittedWhenTheyDo()
    {
        _google.CalComOpen = [.. Open(1, 10, 11)];
        _google.CalComSlotsDelay = TimeSpan.FromSeconds(6);
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        WaitForLink(leaf, ThirtyMinutes);
        Assert.Null(Find(leaf, "ClosedTime_2026-10-01_0"));

        ShareAvailabilityTests.DragHours(leaf, 10, 11.75);
        leaf.WaitFor("ShareSlot_0");

        WaitForOpenTimes(leaf);
        AssertMessages(leaf, "Split into 2 times of 30 minutes.", "45 minutes wasn't open on Cal.com.");
        var text = Copy(leaf);
        Assert.Equal(2, Count(text, "slot="));
        Assert.Contains(Slot(1, 10.5), text, StringComparison.Ordinal);
    }

    // A drag made right after S, before Cal.com has even listed the links, is fitted once the link and its open times
    // arrive, and copies fitted
    [Fact]
    public void Drag_BeforeLinksArrive_IsFittedWhenTheyDo()
    {
        _google.CalComOpen = [.. Open(1, 10, 11)];
        _google.CalComLinksDelay = TimeSpan.FromSeconds(6);
        using var leaf = Launch();
        ShareAvailabilityTests.StartSharing(leaf);
        Assert.Equal("", SelectedLink(leaf));

        ShareAvailabilityTests.DragHours(leaf, 10, 11.75);
        leaf.WaitFor("ShareSlot_0");

        WaitForLink(leaf, ThirtyMinutes);
        WaitForOpenTimes(leaf);
        AssertMessages(leaf, "Split into 2 times of 30 minutes.", "45 minutes wasn't open on Cal.com.");
        var text = Copy(leaf);
        Assert.Equal(2, Count(text, "slot="));
        Assert.Contains(Slot(1, 10.5), text, StringComparison.Ordinal);
    }

    // Copy puts each time on the clipboard as a link with the guest filled in, as plain text and as HTML links
    [Fact]
    public void Copy_PutsLinksAndPlainText()
    {
        using var leaf = Launch();
        StartSharingWithLink(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 11);
        leaf.WaitFor("ShareSlot_0");
        SavedShareGroupTests.AddGuest(leaf, Jane, 0);

        var text = Copy(leaf);

        Assert.Contains("https://cal.com/leaf-tester/30min?date=2026-10-01&" + Slot(1, 10), text, StringComparison.Ordinal);
        Assert.Contains("email=jane%40example.com", text, StringComparison.Ordinal);
        var html = Retry.WhileNull(Clipboard.Html, TimeSpan.FromSeconds(5)).Result;
        Assert.NotNull(html);
        Assert.True(html.Contains("<a href=\"https://cal.com/leaf-tester/30min?date=2026-10-01&amp;" + Slot(1, 10).Replace(":", "%3A", StringComparison.Ordinal), StringComparison.Ordinal), html);
    }

    // With the switch off a pick isn't fitted and copies as plain text
    [Fact]
    public void SwitchOff_CopiesPlainText()
    {
        using var leaf = Launch();
        StartSharingWithLink(leaf);
        leaf.WaitFor("ShareProviderSwitch").AsToggleButton().Toggle();
        Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareLinkBox"), TimeSpan.FromSeconds(5)).Success, "The Link box stayed with the switch off.");

        ShareAvailabilityTests.DragHours(leaf, 15, 16);
        leaf.WaitFor("ShareSlot_0");
        var text = Copy(leaf);

        Assert.DoesNotContain("slot=", text, StringComparison.Ordinal);
        Assert.EndsWith("Thu Oct 1: 3–4 PM ET", text, StringComparison.Ordinal);
    }

    // On a link that takes no extra guests, Copy & Save fills in only the first guest, and the notice says so
    [Fact]
    public void GuestsNotAllowed_SaysSoAfterCopy()
    {
        UseProfile(SignedInProfile(FakeGoogleServer.CalComQuickChat));
        using var leaf = Launch();
        StartSharingWithLink(leaf, QuickChat);
        ShareAvailabilityTests.DragHours(leaf, 10, 11);
        leaf.WaitFor("ShareSlot_0");
        SavedShareGroupTests.AddGuest(leaf, Jane, 0);
        SavedShareGroupTests.AddGuest(leaf, Sam, 1);

        var text = Copy(leaf);

        Assert.True(ShareAvailabilityTests.NoticeSays(leaf, $"This Cal.com link doesn't allow extra guests, so only {Jane} is filled in."), "The notice didn't say only the first guest is filled in.");
        Assert.Contains("email=jane%40example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sam%40example.com", text, StringComparison.Ordinal);
    }

    // A pick moved onto a partly closed stretch is fitted to the openings there
    [Fact]
    public void MovePick_OntoPartlyClosedTime_IsFitted()
    {
        _google.CalComOpen = [.. Open(1, 10, 11), .. Open(1, 12, 13)];
        using var leaf = Launch();
        StartSharingWithLink(leaf);
        ShareAvailabilityTests.DragHours(leaf, 10, 11);
        var pick = leaf.WaitFor("ShareSlot_0").BoundingRectangle;
        var hour = pick.Height + 2;

        // Grabbed In The Middle And Moved 1.5 Hours Down: 11:30 AM-12:30 PM, of which only 12-12:30 is open
        var grab = new Point(pick.Left + pick.Width / 2, pick.Top + pick.Height / 2);
        LeafApp.Drag(grab, new Point(grab.X, grab.Y + (int)Math.Round(hour * 1.5)));

        AssertMessages(leaf, "Split into 1 time of 30 minutes.", "30 minutes wasn't open on Cal.com.");
        var text = Copy(leaf);
        Assert.Equal(1, Count(text, "slot="));
        Assert.Contains(Slot(1, 12), text, StringComparison.Ordinal);
    }

    // =========================================================================
    // SAVED GROUPS AND BOOKINGS
    // =========================================================================

    // A saved Cal.com group: a time resized over a closed stretch is fitted, a new link refits every time, Save keeps
    // the switch and link after a restart, and Copy & Save links the edited times
    [Fact]
    public void OpenSavedGroup_ResizeAndRelink_KeptAfterARestart()
    {
        _google.CalComOpen = [.. Open(1, 10, 12)];
        using (var leaf = Launch())
        {
            SaveGroup(leaf, 10, 11);
            OpenGroup(leaf);
            WaitForOpenTimes(leaf);

            // Bottom Edge Dragged From 11 To 11:45: 11-11:30 is kept, 11:30-12 (picked 15 of 30 minutes) is removed
            var slot = leaf.WaitFor("ShareSlot_0").BoundingRectangle;
            var hour = slot.Height + 2;
            var x = slot.Left + slot.Width / 2;
            LeafApp.Drag(new Point(x, slot.Bottom - 1), new Point(x, slot.Bottom - 1 + (int)Math.Round(hour * 0.75)));
            AssertMessages(leaf, "Split into 3 times of 30 minutes.\nLast time block removed to match the available time slots");

            // Quick Chat Opens Only 10-10:30
            _google.CalComOpen = [.. Open(1, 10, 10.5)];
            leaf.WaitFor("ShareLinkBox").AsComboBox().Select(QuickChat);
            AssertMessages(leaf, "Split into 1 time of 30 minutes.", "1 hour wasn't open on Cal.com.");

            leaf.WaitFor("ShareSaveButton").AsButton().Invoke();
            Assert.True(Retry.WhileTrue(() => leaf.Exists("ShareSlotsPanel"), TimeSpan.FromSeconds(10)).Success, "Save didn't close the panel.");
        }

        using var again = Launch();
        OpenGroup(again, QuickChat);
        Assert.True(again.WaitFor("ShareProviderSwitch").AsToggleButton().ToggleState == ToggleState.On, "The group's switch was off after a restart.");
        var text = Copy(again);
        Assert.Equal(1, Count(text, "slot="));
        Assert.Contains("https://cal.com/leaf-tester/quick?date=2026-10-01&" + Slot(1, 10), text, StringComparison.Ordinal);
    }

    // A booking made on Cal.com inside a saved group's time closes the group and shows a notification with who booked
    [Fact]
    public void Booking_RemovesTheGroupAndNotifies()
    {
        using var leaf = Launch();
        SaveGroup(leaf, 10, 11);

        _google.AddCalComBooking(At(1, 10.5), "Jane Doe");
        leaf.SyncNow();

        Assert.True(Retry.WhileTrue(() => leaf.Exists(FirstSaved), TimeSpan.FromSeconds(30)).Success, "The booked group stayed on the calendar.");
        var line = LeafApp.WaitForNotification(_profile, l => l.Contains("Jane Doe booked", StringComparison.Ordinal), 30);
        Assert.StartsWith("show\t", line, StringComparison.Ordinal);
    }

    // =========================================================================
    // APPROVE… ON A CAL.COM GROUP
    // =========================================================================

    // Approve… on one bookable time asks first: Cancel books nothing; Book sends the booker and the other guest (the link
    // allows guests), deletes the group, says so, and no notification follows for that booking
    [Fact]
    public void Approve_CancelBooksNothing_BookSendsEveryGuestAndDeletesTheGroup()
    {
        using var leaf = Launch();
        SaveGroup(leaf, 10, 10.5, ThirtyMinutes, Jane, Sam);
        OpenGroup(leaf);

        Approve(leaf);
        Assert.Equal(["Book Thu Oct 1, 10–10:30 AM ET on Cal.com?", Jane, Sam], DialogTexts(leaf));
        PressDialog(leaf, "CloseButton");
        Thread.Sleep(1000);
        Assert.Empty(_google.CalComBookings);
        Assert.True(leaf.Exists("ShareSlot_0"), "Cancel took the time away.");

        Approve(leaf);
        Assert.NotNull(leaf.WaitForAnywhere("BookDialog"));
        PressDialog(leaf, "PrimaryButton");

        var body = BookingSent();
        Assert.Equal(Jane, (string?)body["attendee"]!["email"]);
        Assert.Equal(Eastern, (string?)body["attendee"]!["timeZone"]);
        Assert.Equal(101, (long)body["eventTypeId"]!);
        Assert.Equal("2026-10-01T14:00:00.000Z", (string?)body["start"]);
        Assert.Equal([Sam], body["guests"]!.AsArray().Select(g => (string)g!));
        AssertGroupGone(leaf);
        Assert.True(ShareAvailabilityTests.NoticeSays(leaf, "Booked on Cal.com."), "The notice didn't say it was booked.");

        // The Booking Check Finds This Booking With No Group Left: no notification
        leaf.SyncNow();
        Thread.Sleep(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(LeafApp.NotificationLines(_profile), l => l.Contains(" booked ", StringComparison.Ordinal));
    }

    // On a link that takes no extra guests, the dialog lists only the booker and only the booker is sent
    [Fact]
    public void Approve_GuestsNotAllowed_ListsAndSendsOnlyTheBooker()
    {
        UseProfile(SignedInProfile(FakeGoogleServer.CalComQuickChat));
        using var leaf = Launch();
        SaveGroup(leaf, 10, 10.5, QuickChat, Jane, Sam);
        OpenGroup(leaf, QuickChat);

        Approve(leaf);
        Assert.Equal(["Book Thu Oct 1, 10–10:30 AM ET on Cal.com?", Jane], DialogTexts(leaf));
        PressDialog(leaf, "PrimaryButton");

        var body = BookingSent();
        Assert.Equal(Jane, (string?)body["attendee"]!["email"]);
        Assert.Null(body["guests"]);
        AssertGroupGone(leaf);
    }

    // A time taken meanwhile keeps the group and says so
    [Fact]
    public void Approve_Taken_KeepsTheGroupAndSaysSo()
    {
        using var leaf = Launch();
        SaveGroup(leaf, 10, 10.5, ThirtyMinutes, Jane);
        OpenGroup(leaf);
        _google.CalComTaken = true;

        Approve(leaf);
        PressDialog(leaf, "PrimaryButton");

        AssertMessages(leaf, "That time was just taken on Cal.com. Pick another.");
        Assert.Single(_google.CalComBookings);
        AssertGroupStays(leaf);
    }

    // A longer range holds several bookable times: a flyout lists them and the one picked is booked
    [Fact]
    public void Approve_LongerRange_PicksATimeInTheFlyout()
    {
        using var leaf = Launch();
        SaveGroup(leaf, 10, 11.5, ThirtyMinutes, Jane);
        OpenGroup(leaf);

        Approve(leaf);
        Assert.Equal("Thu Oct 1, 10:30–11 AM ET", leaf.WaitForAnywhere("ApproveTime_1").Name);
        Assert.NotNull(leaf.WaitForAnywhere("ApproveTime_2"));
        Assert.False(leaf.ExistsAnywhere("ApproveTime_3"), "The flyout listed more times than the range holds.");
        leaf.WaitForAnywhere("ApproveTime_1").AsMenuItem().Invoke();

        Assert.Equal("Book Thu Oct 1, 10:30–11 AM ET on Cal.com?", DialogTexts(leaf)[0]);
        PressDialog(leaf, "PrimaryButton");
        var body = BookingSent();
        Assert.Equal("2026-10-01T14:30:00.000Z", (string?)body["start"]);
    }

    // When Cal.com's links can't be read, Approve… books nothing and says so (no dialog)
    [Fact]
    public void Approve_LinksNotLoaded_BooksNothing()
    {
        using (var leaf = Launch())
        {
            SaveGroup(leaf, 10, 10.5, ThirtyMinutes, Jane);
        }

        _google.CalComLinksDown = true;
        using var again = Launch();
        SavedShareGroupTests.OpenFirst(again);

        Approve(again);

        AssertMessages(again, "Couldn't reach Cal.com. Nothing was booked.");
        Assert.False(again.ExistsAnywhere("BookDialog"), "The booking dialog opened without the links.");
        Assert.Empty(_google.CalComBookings);
        AssertGroupStays(again);
    }

    // The panel's Approve… text and its check icon, and the guest remove icon, render as one glyph each (a file once
    // saved in the wrong encoding garbled them into several characters)
    [Fact]
    public void Panel_ApproveTextAndIcons_RenderCorrectly()
    {
        using var leaf = Launch();
        SaveGroup(leaf, 10, 10.5, ThirtyMinutes, Jane);
        OpenGroup(leaf);
        var approve = leaf.WaitFor("SharePanelApprove_0");
        approve.Patterns.ScrollItem.Pattern.ScrollIntoView();
        Thread.Sleep(500);
        var label = Retry.WhileNull(() => approve.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == "Approve…"), TimeSpan.FromSeconds(5)).Result;
        Assert.NotNull(label);

        // The Check Mark: one stroke run left of the text, about as wide as tall
        var button = approve.BoundingRectangle;
        using (var ink = Ink.Capture(button))
        {
            var check = ink.Measure(inset: 2, toX: label.BoundingRectangle.Left - 2);
            Assert.NotNull(check);
            Assert.True(check.Value.Width <= (check.Value.Bottom - check.Value.Top) * 1.6, $"The check icon's ink is {check}.");
        }

        // The Remove Icon: one X, about as wide as tall
        leaf.WaitFor("ShareGuestRemove_0").Patterns.ScrollItem.Pattern.ScrollIntoView();
        Thread.Sleep(500);
        using var removeInk = Ink.Capture(leaf.WaitFor("ShareGuestRemove_0").BoundingRectangle);
        var runs = removeInk.Runs(inset: 2);
        var x = removeInk.Measure(inset: 2);
        Assert.Single(runs);
        Assert.NotNull(x);
        Assert.True(x.Value.Width <= (x.Value.Bottom - x.Value.Top) * 1.6, $"The remove icon's ink is {x}.");
    }
}
