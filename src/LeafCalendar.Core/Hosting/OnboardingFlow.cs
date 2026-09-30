using System.Globalization;
using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;

namespace LeafCalendar.Core.Hosting;

/// <summary>The first-run steps, in order.</summary>
public enum OnboardingStep
{
    /// <summary>What Leaf is, and "Get started".</summary>
    Welcome,

    /// <summary>The user's Google Cloud OAuth client (ID and secret).</summary>
    Client,

    /// <summary>Sign in with Google (PKCE loopback).</summary>
    SignIn,

    /// <summary>The first sync runs.</summary>
    Syncing,

    /// <summary>All set: "Open Leaf Calendar".</summary>
    Done,
}

/// <summary>
/// The onboarding window's step machine, kept free of UI so it's unit tested: which step shows, where Back and the
/// primary action go, what the primary button says and when it's enabled, and whether closing asks first. The window
/// only shows what this says. Back is offered on the client and sign-in steps only: once an account is signed in,
/// going back would sign in a second one. The first sync moves straight to Done when it succeeds; when it fails the
/// primary action becomes "Try again".
/// </summary>
public sealed class OnboardingFlow
{
    /// <summary>How many steps there are (the step indicator's page count).</summary>
    public const int StepCount = 5;

    /// <summary>The step showing now.</summary>
    public OnboardingStep Step { get; private set; }

    /// <summary>True while a save, sign-in, or sync runs (Back hides, the primary action is off).</summary>
    public bool IsBusy { get; set; }

    /// <summary>True when the last sync attempt failed.</summary>
    public bool HasSyncFailed { get; private set; }

    /// <summary>True once the first sync has finished.</summary>
    public bool HasSyncFinished { get; private set; }

    /// <summary>The step indicator's selected page.</summary>
    public int PageIndex => (int)Step;

    /// <summary>The step indicator's accessible name ("Step 2 of 5").</summary>
    public string StepName => string.Create(CultureInfo.InvariantCulture, $"Step {PageIndex + 1} of {StepCount}");

    /// <summary>True when Back shows: on the client and sign-in steps, while nothing runs.</summary>
    public bool CanGoBack => !IsBusy && Step is OnboardingStep.Client or OnboardingStep.SignIn;

    /// <summary>True when "Open Leaf Calendar" can close onboarding (the first sync finished).</summary>
    public bool CanFinish => Step == OnboardingStep.Done && HasSyncFinished;

    /// <summary>True when closing the window should ask "Leave setup?" first.</summary>
    public bool AsksBeforeClosing => !CanFinish;

    /// <summary>The primary button's text for this step.</summary>
    public string PrimaryText => Step switch
    {
        OnboardingStep.Welcome => "Get started",
        OnboardingStep.SignIn  => "Sign in with Google",
        OnboardingStep.Syncing => HasSyncFailed ? "Try again" : "Next",
        OnboardingStep.Done    => "Open Leaf Calendar",
        _                      => "Next",
    };

    /// <summary>True when the primary button is enabled.</summary>
    public bool CanRunPrimary => !IsBusy && Step switch
    {
        OnboardingStep.Syncing => HasSyncFailed,
        OnboardingStep.Done    => HasSyncFinished,
        _                      => true,
    };

    /// <summary>True when onboarding shows at launch: there's no OAuth client or no account yet.</summary>
    public static bool IsNeeded(bool hasClient, bool hasAccount) => !hasClient || !hasAccount;

    /// <summary>True when "Leave setup" exits the app; with an account already there, the main window opens instead.</summary>
    public static bool ExitsOnLeave(bool hasAccount) => !hasAccount;

    /// <summary>
    /// True when the client step can move on without saving: a client is saved, its ID is still in the box, and no
    /// new secret was typed (the saved secret is never shown, so an empty box means "keep it").
    /// </summary>
    public static bool KeepsSavedClient(string clientId, string clientSecret, OAuthClientCredentials? saved) =>
        saved is not null
        && string.Equals(clientId.Trim(), saved.ClientId, StringComparison.Ordinal)
        && clientSecret.Trim().Length == 0;

    /// <summary>
    /// True when the first sync worked. The sync engine logs and swallows Google and network failures, so success is
    /// judged by the result: every Google account has at least its primary calendar, and it must still be signed in.
    /// </summary>
    public static bool FirstSyncWorked(int calendarCount, AccountStatus status) => calendarCount > 0 && status == AccountStatus.Ok;

    /// <summary>Moves to the next step. False on the last one.</summary>
    public bool Advance()
    {
        if (Step == OnboardingStep.Done)
        {
            return false;
        }

        Step++;
        return true;
    }

    /// <summary>Moves back one step when <see cref="CanGoBack"/>. False otherwise.</summary>
    public bool GoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        Step--;
        return true;
    }

    /// <summary>A sync attempt started.</summary>
    public void SyncStarted()
    {
        HasSyncFailed   = false;
        HasSyncFinished = false;
    }

    /// <summary>The sync attempt failed: stay on Syncing and offer "Try again".</summary>
    public void SyncFailed() => HasSyncFailed = true;

    /// <summary>The first sync finished: move to Done, where "Open Leaf Calendar" is enabled.</summary>
    public void SyncSucceeded()
    {
        HasSyncFailed   = false;
        HasSyncFinished = true;
        Step            = OnboardingStep.Done;
    }
}
