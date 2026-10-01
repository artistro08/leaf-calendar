using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using LeafCalendar.UITests.Support;

namespace LeafCalendar.UITests;

public sealed class RichDescriptionTests : IDisposable
{
    const string Rich = "evt-rich";

    readonly FakeGoogleServer _google = new();
    readonly string _profile = SeededProfile.Create();

    public RichDescriptionTests() =>
        _google.AddEvent(SeededProfile.Email, JsonNode.Parse("""
            {
              "id": "evt-rich", "status": "confirmed", "summary": "Planning",
              "description": "<table><tr><td>Kept as Google wrote it</td></tr></table><b>Bold</b> <a href=\"https://example.com/doc\">Doc</a>",
              "start": { "dateTime": "2026-10-01T10:00:00-04:00" }, "end": { "dateTime": "2026-10-01T11:00:00-04:00" }
            }
            """)!.AsObject());

    public void Dispose()
    {
        LeafApp.DeleteProfile(_profile);
        _google.Dispose();
    }

    LeafApp OpenEditor()
    {
        var leaf = LeafApp.Launch(_profile, $"--fake-google {_google.BaseUri} --start-date 2026-10-01");
        leaf.WaitFor("Event_evt-rich_202610011400").Click();

        // The Edit button, not E: a key typed right after E is sent to the title (the E chord's typing rule)
        leaf.WaitFor("DetailsEditButton").AsButton().Invoke();

        // The editor's own first focus (the title) lands before a test moves it
        var title = leaf.WaitFor("EditorTitle");
        Assert.True(Retry.WhileFalse(() => title.Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(10)).Success, "The title never took focus.");
        return leaf;
    }

    // Focuses the description (which scrolls it into view under the toolbar) and waits until it has the keyboard
    static AutomationElement FocusDescription(LeafApp leaf)
    {
        var box = leaf.WaitFor("EditorDescription");
        box.Focus();
        Assert.True(Retry.WhileFalse(() => box.Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(10)).Success, "The description never took focus.");
        return box;
    }

    FakeWrite RichPatch() =>
        _google.WaitForWrite(w => w.Method == "PATCH" && w.Path.EndsWith("/events/" + Rich, StringComparison.Ordinal));

    // The description the patch sends (the body is JSON, so HTML characters arrive escaped)
    static string SentDescription(FakeWrite write) =>
        JsonNode.Parse(write.Body)?["description"]?.GetValue<string>() ?? throw new InvalidOperationException("The patch sent no description: " + write.Body);

    // Review Focus 2: Saving Another Field Leaves Google's Description Alone
    [Fact]
    public void SaveWithoutTouchingDescription_SendsNoDescription()
    {
        using var leaf = OpenEditor();
        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Planning v2";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write = RichPatch();
        Assert.Contains("Planning v2", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", write.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BoldButton_ThenType_SavesBoldText()
    {
        using var leaf = OpenEditor();
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.ENTER);
        leaf.WaitFor("DescriptionBold").Click();
        Keyboard.Type("Loud");
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var description = SentDescription(RichPatch());
        Assert.Contains("<b>Loud</b>", description, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/doc\">Doc</a>", description, StringComparison.Ordinal);
    }

    [Fact]
    public void BulletsButton_MakesAList()
    {
        using var leaf = OpenEditor();
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.ENTER);
        leaf.WaitFor("DescriptionBullets").Click();
        Keyboard.Type("First");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Keyboard.Type("Second");
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        Assert.Contains("<ul><li>First</li><li>Second</li></ul>", SentDescription(RichPatch()), StringComparison.Ordinal);
    }

    // Review Focus 3: Rich Clipboard Content Pastes As Plain Text
    [Fact]
    public void PasteRichText_InsertsPlainText()
    {
        using var leaf = OpenEditor();
        Clipboard.SetHtml("<b>Pasted</b><img src=\"https://tracker.example/p.gif\"><script>x</script>", plainText: "Pasted");
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write       = RichPatch();
        var description = SentDescription(write);
        Assert.Contains("Pasted", description, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Pasted", description, StringComparison.Ordinal);
        Assert.DoesNotContain("img", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('￼', description);
    }

    // Shift+Insert Pastes Plain Text Too
    [Fact]
    public void ShiftInsertRichText_InsertsPlainText()
    {
        using var leaf = OpenEditor();
        Clipboard.SetHtml("<b>Pasted</b><img src=\"https://tracker.example/p.gif\">", plainText: "Pasted");
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.INSERT);
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var write       = RichPatch();
        var description = SentDescription(write);
        Assert.Contains("Pasted", description, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Pasted", description, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker", write.Body, StringComparison.Ordinal);
    }

    // Dropping Rich Text Or A File On The Description Does Nothing: The Box Refuses It (DoDragDrop reports no effect) And Its Text
    // Is Unchanged. ponytail: no Leaf control accepts an outside drop, so there is no positive control proving delivery; the
    // title TextBox refused this source too (2026-10-01). If a drop target is ever added, drop there first as the control.
    [Fact]
    public void DropRichTextOrFile_ChangesNothing()
    {
        using var leaf = OpenEditor();
        var box    = FocusDescription(leaf);
        var before = box.Patterns.Text.Pattern.DocumentRange.GetText(-1);
        var bounds = box.BoundingRectangle;
        var center = new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var file   = Path.Combine(Path.GetTempPath(), $"leaf-drop-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "Dropped file");

        try
        {
            // Rich Text
            var rich = new System.Windows.Forms.DataObject();
            rich.SetData(System.Windows.Forms.DataFormats.Html, "<b>Dropped</b>");
            rich.SetText("Dropped");
            Assert.Equal(System.Windows.Forms.DragDropEffects.None, DragSource.DropAt(center, rich));

            // A File
            var files = new System.Windows.Forms.DataObject();
            files.SetFileDropList([file]);
            Assert.Equal(System.Windows.Forms.DragDropEffects.None, DragSource.DropAt(center, files));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(before, box.Patterns.Text.Pattern.DocumentRange.GetText(-1));

        leaf.WaitFor("EditorTitle").AsTextBox().Text = "Planning v2";
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();
        Assert.DoesNotContain("description", RichPatch().Body, StringComparison.Ordinal);
    }

    // An RTF Field In A Description Is Literal Text, Never A Link: Every Character Of It Comes Back On Save. (Checked through
    // the save, not UIA: RichEdit's text pattern reads `HYPERLINK "url" text` out as just "text", though the box holds it all.)
    [Fact]
    public void RtfFieldDescription_StaysLiteralText()
    {
        _google.EditOnGoogle(SeededProfile.Email, Rich, e => e["description"] = """{\rtf1{\field{\*\fldinst HYPERLINK "https://x.example"}{\fldrslt Click}}}""");
        using var leaf = OpenEditor();
        var box = FocusDescription(leaf);
        Assert.Contains(@"{\rtf1", box.Patterns.Text.Pattern.DocumentRange.GetText(-1), StringComparison.Ordinal);

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type("!");
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var description = SentDescription(RichPatch());
        Assert.StartsWith(@"{\rtf1{\field{\*\fldinst HYPERLINK ", description, StringComparison.Ordinal);
        Assert.Contains("https://x.example", description, StringComparison.Ordinal);
        Assert.EndsWith(@"{\fldrslt Click}}}!", description, StringComparison.Ordinal);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }

    // Pasted Friendly-Link Syntax (HYPERLINK "url" text) Is Literal Text Too, And Saved Whole
    [Fact]
    public void PasteFriendlyLinkSyntax_IsSavedWhole()
    {
        using var leaf = OpenEditor();
        Clipboard.SetHtml("HYPERLINK \"https://x.example\" Click", plainText: "HYPERLINK \"https://x.example\" Click");
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        Thread.Sleep(500);
        leaf.WaitFor("EditorSaveButton").AsButton().Invoke();

        var description = SentDescription(RichPatch());
        Assert.Contains("HYPERLINK ", description, StringComparison.Ordinal);
        Assert.Contains("https://x.example", description, StringComparison.Ordinal);
        Assert.EndsWith(" Click", description, StringComparison.Ordinal);
    }

    // Ctrl+Enter Saves From The Description, It Doesn't Add A Line
    [Fact]
    public void CtrlEnterInDescription_Saves()
    {
        using var leaf = OpenEditor();
        FocusDescription(leaf);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        Keyboard.Type("x");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ENTER);

        Assert.StartsWith("xKept", SentDescription(RichPatch()), StringComparison.Ordinal);
    }

    // Hostile Description: Shown As Text, Nothing Launches
    [Fact]
    public void HostileDescription_ShowsInertTextAndLaunchesNothing()
    {
        _google.EditOnGoogle(SeededProfile.Email, Rich, e => e["description"] = "<script>alert(1)</script><a href=\"javascript:x\">Click</a>");
        using var leaf = OpenEditor();
        var box = FocusDescription(leaf);

        Assert.Contains("Click", box.Patterns.Text.Pattern.DocumentRange.GetText(-1), StringComparison.Ordinal);
        Assert.Empty(LeafApp.LaunchedLinks(_profile));
    }
}