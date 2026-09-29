using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using LeafCalendar.Core.Auth;
using Windows.Management.Deployment;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace LeafCalendar.UITests.Support;

/// <summary>
/// Launches the registered Leaf package with a throwaway profile and finds elements by automation ID.
/// </summary>
public sealed class LeafApp : IDisposable
{
    const string PackageName = "LeafCalendar";

    readonly UIA3Automation _automation = new();

    LeafApp(Application app) => App = app;

    /// <summary>The running app.</summary>
    public Application App { get; }

    /// <summary>The main window (waits up to 20 s).</summary>
    public Window MainWindow => App.GetMainWindow(_automation, TimeSpan.FromSeconds(20))
        ?? throw new InvalidOperationException("Leaf's main window didn't appear.");

    /// <summary>The registered package.</summary>
    public static Windows.ApplicationModel.Package Package =>
        new PackageManager().FindPackagesForUser(string.Empty).FirstOrDefault(p => p.Id.Name == PackageName)
        ?? throw new InvalidOperationException("Leaf Calendar isn't registered. Run tools/dev-register.ps1 first.");

    /// <summary>True when the registered build is Native AOT (no managed LeafCalendar.dll).</summary>
    public static bool IsNativeAot => !File.Exists(Path.Combine(Package.InstalledLocation.Path, "LeafCalendar.dll"));

    /// <summary>Creates a unique throwaway profile name.</summary>
    public static string NewProfile() => "uitest-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Launches Leaf with <c>--profile</c> plus extra arguments.</summary>
    public static LeafApp Launch(string? profile = null, string extraArguments = "") =>
        new(Application.LaunchStoreApp($"{Package.Id.FamilyName}!App", $"--profile {profile ?? NewProfile()} {extraArguments}".Trim()));

    /// <summary>The package's local folder for <paramref name="profile"/>.</summary>
    public static string ProfileFolder(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        Package.Id.FamilyName,
        "LocalState",
        "profiles",
        profile);

    /// <summary>Deletes a profile's secrets and local files.</summary>
    public static void DeleteProfile(string profile)
    {
        new CredentialLockerTokenStore(profile).DeleteAll();

        var folder = ProfileFolder(profile);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Waits up to 15 s for an element by automation ID.</summary>
    public AutomationElement WaitFor(string automationId) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear.");

    /// <summary>Waits up to 15 s for an element by its accessible name.</summary>
    public AutomationElement WaitForName(string name) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element named '{name}' didn't appear.");

    /// <summary>Waits up to 15 s for an element in any of the app's windows (flyouts and dialogs can be separate).</summary>
    public AutomationElement WaitForAnywhere(string automationId) =>
        Retry.WhileNull(
            () => App.GetAllTopLevelWindows(_automation)
                .Select(w => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId)))
                .FirstOrDefault(e => e is not null),
            TimeSpan.FromSeconds(15)).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' didn't appear in any window.");

    /// <summary>True when an element with this ID is currently in the main window.</summary>
    public bool Exists(string automationId) => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null;

    /// <summary>Focuses the main window and presses a key chord (e.g. Control + Shift + E).</summary>
    public void Press(params VirtualKeyShort[] keys)
    {
        MainWindow.Focus();
        Keyboard.TypeSimultaneously(keys);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!App.HasExited)
        {
            App.Kill();
        }

        App.Dispose();
        _automation.Dispose();
    }
}
