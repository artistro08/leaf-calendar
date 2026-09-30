using LeafCalendar.Core.Auth;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Hosting;

namespace LeafCalendar.Tests;

public class OnboardingFlowTests
{
    static readonly OAuthClientCredentials Saved = new("123-abc.apps.googleusercontent.com", "GOCSPX-saved");

    // A flow moved forward to a step
    static OnboardingFlow At(OnboardingStep step)
    {
        var flow = new OnboardingFlow();
        while (flow.Step < step)
        {
            flow.Advance();
        }

        return flow;
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void IsNeeded_WithoutAClientOrAnAccount(bool hasClient, bool hasAccount, bool needed)
    {
        Assert.Equal(needed, OnboardingFlow.IsNeeded(hasClient, hasAccount));
    }

    [Fact]
    public void Advance_GoesThroughEveryStepInOrder_AndStopsAtDone()
    {
        var flow  = new OnboardingFlow();
        var steps = new List<OnboardingStep> { flow.Step };
        while (flow.Advance())
        {
            steps.Add(flow.Step);
        }

        Assert.Equal([OnboardingStep.Welcome, OnboardingStep.Client, OnboardingStep.SignIn, OnboardingStep.Syncing, OnboardingStep.Done], steps);
        Assert.Equal(OnboardingFlow.StepCount, steps.Count);
        Assert.Equal(OnboardingStep.Done, flow.Step);
    }

    [Fact]
    public void PageIndex_IsTheStepsPosition()
    {
        Assert.Equal(0, At(OnboardingStep.Welcome).PageIndex);
        Assert.Equal(2, At(OnboardingStep.SignIn).PageIndex);
        Assert.Equal(4, At(OnboardingStep.Done).PageIndex);
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome, false)]
    [InlineData(OnboardingStep.Client, true)]
    [InlineData(OnboardingStep.SignIn, true)]
    [InlineData(OnboardingStep.Syncing, false)]
    [InlineData(OnboardingStep.Done, false)]
    public void CanGoBack_OnlyBeforeSigningIn(OnboardingStep step, bool canGoBack)
    {
        Assert.Equal(canGoBack, At(step).CanGoBack);
    }

    [Fact]
    public void GoBack_WhileBusy_StaysPut()
    {
        var flow = At(OnboardingStep.SignIn);
        flow.IsBusy = true;

        Assert.False(flow.CanGoBack);
        Assert.False(flow.GoBack());
        Assert.Equal(OnboardingStep.SignIn, flow.Step);
    }

    [Fact]
    public void GoBack_FromSignIn_ReturnsToClient()
    {
        var flow = At(OnboardingStep.SignIn);

        Assert.True(flow.GoBack());
        Assert.Equal(OnboardingStep.Client, flow.Step);
    }

    [Fact]
    public void GoBack_OnWelcome_StaysPut()
    {
        var flow = new OnboardingFlow();

        Assert.False(flow.GoBack());
        Assert.Equal(OnboardingStep.Welcome, flow.Step);
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome, "Get started")]
    [InlineData(OnboardingStep.Client, "Next")]
    [InlineData(OnboardingStep.SignIn, "Sign in with Google")]
    [InlineData(OnboardingStep.Syncing, "Next")]
    [InlineData(OnboardingStep.Done, "Open Leaf Calendar")]
    public void PrimaryText_PerStep(OnboardingStep step, string text)
    {
        Assert.Equal(text, At(step).PrimaryText);
    }

    [Fact]
    public void Syncing_CantMoveOnUntilItFails_ThenOffersTryAgain()
    {
        var flow = At(OnboardingStep.Syncing);
        flow.SyncStarted();

        Assert.False(flow.CanRunPrimary);

        flow.SyncFailed();

        Assert.True(flow.CanRunPrimary);
        Assert.Equal("Try again", flow.PrimaryText);
        Assert.Equal(OnboardingStep.Syncing, flow.Step);
    }

    [Fact]
    public void SyncSucceeded_MovesToDone_AndAllowsFinishing()
    {
        var flow = At(OnboardingStep.Syncing);
        flow.SyncStarted();
        flow.SyncFailed();
        flow.SyncStarted();

        flow.SyncSucceeded();

        Assert.Equal(OnboardingStep.Done, flow.Step);
        Assert.True(flow.CanFinish);
        Assert.True(flow.CanRunPrimary);
        Assert.False(flow.AsksBeforeClosing);
    }

    [Fact]
    public void Done_WithoutAFinishedSync_CantFinish()
    {
        var flow = At(OnboardingStep.Done);

        Assert.False(flow.CanFinish);
        Assert.False(flow.CanRunPrimary);
        Assert.True(flow.AsksBeforeClosing);
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome)]
    [InlineData(OnboardingStep.Client)]
    [InlineData(OnboardingStep.SignIn)]
    [InlineData(OnboardingStep.Syncing)]
    public void AsksBeforeClosing_UntilFinished(OnboardingStep step)
    {
        Assert.True(At(step).AsksBeforeClosing);
    }

    [Fact]
    public void CanRunPrimary_IsOffWhileBusy()
    {
        var flow = At(OnboardingStep.SignIn);
        flow.IsBusy = true;

        Assert.False(flow.CanRunPrimary);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ExitsOnLeave_OnlyWithoutAnAccount(bool hasAccount, bool exits)
    {
        Assert.Equal(exits, OnboardingFlow.ExitsOnLeave(hasAccount));
    }

    [Theory]
    [InlineData("123-abc.apps.googleusercontent.com", "", true)]
    [InlineData(" 123-abc.apps.googleusercontent.com ", "  ", true)]
    [InlineData("123-abc.apps.googleusercontent.com", "GOCSPX-new", false)]
    [InlineData("456-xyz.apps.googleusercontent.com", "", false)]
    public void KeepsSavedClient_WhenTheIdIsUnchangedAndNoSecretIsTyped(string clientId, string secret, bool keeps)
    {
        Assert.Equal(keeps, OnboardingFlow.KeepsSavedClient(clientId, secret, Saved));
    }

    [Fact]
    public void KeepsSavedClient_NothingSaved_IsFalse()
    {
        Assert.False(OnboardingFlow.KeepsSavedClient("123-abc.apps.googleusercontent.com", "", null));
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome, "Step 1 of 5")]
    [InlineData(OnboardingStep.Done, "Step 5 of 5")]
    public void StepName_ForScreenReaders(OnboardingStep step, string name)
    {
        Assert.Equal(name, At(step).StepName);
    }

    [Theory]
    [InlineData(2, AccountStatus.Ok, true)]
    [InlineData(0, AccountStatus.Ok, false)]
    [InlineData(2, AccountStatus.NeedsSignIn, false)]
    public void FirstSyncWorked_NeedsCalendarsAndASignedInAccount(int calendars, AccountStatus status, bool worked)
    {
        Assert.Equal(worked, OnboardingFlow.FirstSyncWorked(calendars, status));
    }

    [Theory]
    [InlineData(OnboardingStep.SignIn, true, true)]
    [InlineData(OnboardingStep.SignIn, false, false)]
    [InlineData(OnboardingStep.Client, true, false)]
    [InlineData(OnboardingStep.Syncing, true, false)]
    public void CanCancel_OnlyWhileSigningIn(OnboardingStep step, bool busy, bool canCancel)
    {
        var flow = At(step);
        flow.IsBusy = busy;

        Assert.Equal(canCancel, flow.CanCancel);
    }

    [Fact]
    public void SignInExpired_GoesBackToSignIn_ReadyToSignInAgain()
    {
        var flow = At(OnboardingStep.Syncing);
        flow.SyncStarted();

        flow.SignInExpired();

        Assert.Equal(OnboardingStep.SignIn, flow.Step);
        Assert.False(flow.HasSyncFailed);
        Assert.Equal("Sign in with Google", flow.PrimaryText);
        Assert.True(flow.CanRunPrimary);
    }

    [Theory]
    [InlineData(0, 0, "0 calendars · 0 events")]
    [InlineData(1, 1, "1 calendar · 1 event")]
    [InlineData(2, 6, "2 calendars · 6 events")]
    public void Summary_CountsCalendarsAndEvents(int calendars, int events, string summary)
    {
        Assert.Equal(summary, OnboardingFlow.Summary(calendars, events));
    }
}
