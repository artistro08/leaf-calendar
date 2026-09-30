# Leaf Calendar Design Standard

How every screen in Leaf should look and behave, so new UI matches what's already built and the owner's other Windows apps.

## Introduction

This standard is drawn from three shipped apps by the same owner, plus what's already in Leaf and what the owner has asked for directly:

| Short name | Project | Where |
| --- | --- | --- |
| **MA** | Music Assistant Native | `D:\music-assistant\MusicAssistant\` |
| **Layers** | Layers | `D:\layers\src\Layers.UI\`, spec `D:\layers\docs\superpowers\specs\2026-09-25-winui3-rewrite-design.md` |
| **Sony** | Sony Connect (Sony Control) | `D:\sony-control\src\SonyControl.App\`, spec `D:\sony-control\docs\superpowers\specs\2026-09-23-sony-control-design.md` |
| **Leaf** | This app | `src\LeafCalendar.App\`, spec `docs\superpowers\specs\2026-09-29-leaf-calendar-design.md`, brief `.superpowers\sdd\2026-09-30-m3-events\redesign-brief.md` |

Each rule names where it comes from. **Conflict** marks a rule where the reference apps disagree, and says which one wins and why. Owner instructions (the brief, the Leaf spec) beat every reference app.

> This standard describes. It doesn't ask for changes to existing Leaf screens. The last section lists where Leaf already follows it and the gaps, for new work only.

---

## 1. Principles

1. **Looks like Windows 11 made it.** Stock WinUI controls, stock theme brushes, stock type ramp. Build a custom control only when no stock one exists, and then style it from theme resources. (MA `CODING-STANDARDS.md` "Use WinUI common controls before building custom ones"; Layers spec "Stock everything"; Sony spec "Feel like a first-party Windows 11 flyout"; Leaf brief "use windows default elements".)
2. **Copy a real Windows surface when there's one to copy.** Settings copies the Windows 11 Settings app, the tray flyout copies Quick Settings and the battery flyout, the HUD copied the virtual-desktop switcher, the search box copied Task Manager. Measure the real thing and match it. (Leaf brief; Sony spec "Frame"; Layers spec "Section 3: HUD"; MA `MainWindow.xaml` "Task Manager style".)
3. **Quiet chrome, content first.** Icon-only buttons with no fill in title bars, no scrollbars until you scroll, one accent-filled button per surface at most. (Leaf `Styles/LeafTheme.xaml` `LeafBareIconButtonStyle`; Leaf `Controls/ScrollIndicator.cs`.)
4. **Pixel-exact alignment.** Things share left edges, glyphs line up with the text under them, and the reason for each odd number is written in a comment. (Leaf `Views/SidebarView.xaml` header comment; MA `Templates.xaml` `RowContentPadding`; Layers spec HUD sizes.)
5. **Light, dark, and contrast themes all work.** (MA `CODING-STANDARDS.md` "Support Light, Dark and contrast themes"; Layers `SettingsWindow.xaml` HighContrast dictionaries.)
6. **Native AOT safe.** `x:Bind` only, concrete `List<T>` for `ItemsSource` set in code, no typed read-backs of WinRT properties. (Leaf brief item 6; Layers `SettingsWindow.xaml.cs` "CsWinRT's AOT Mode Can't Cast The Native MenuItems Vector".)

---

## 2. Windows and Title Bars

### Every Window

| Rule | Value | Source |
| --- | --- | --- |
| Backdrop, normal windows | `MicaBackdrop` | Leaf `MainWindow.xaml`; MA `MainWindow.xaml`; Layers `SettingsWindow.xaml.cs`; Sony `SettingsWindow.xaml` |
| Backdrop, light-dismiss surfaces (tray flyout) | Desktop acrylic that stays "active" (`DesktopAcrylicController` with `IsInputActive = true`), inside a `SystemBackdropElement` with `CornerRadius="8"` | Sony `ActiveAcrylicBackdrop.cs`, `FlyoutWindow.xaml`; Layers `ActiveAcrylicBackdrop.cs`; Leaf spec 8.2 |
| Title bar | Stock `TitleBar` control, `ExtendsContentIntoTitleBar = true`, `SetTitleBar(AppTitleBar)` | Leaf, Layers, Sony |
| Title bar height | `AppWindow.TitleBar.PreferredHeightOption = Tall` and `<x:Double x:Key="TitleBarCompactHeight">48</x:Double>`, so caption buttons match the 48 DIP bar | Leaf `MainWindow.xaml(.cs)`; Layers `SettingsWindow.xaml` |
| Title bar icon | App icon via `TitleBar.IconSource` (`ImageIconSource`) and `AppWindow.SetIcon` (an `.ico`) | Leaf; Layers; Sony `SettingsWindow.xaml.cs` |
| Title text | App name for the main window, "`<App>` Settings" style for secondary windows (Leaf's settings window uses "Settings" per the brief) | Layers "Layers Settings"; Sony "Sony Control Settings"; Leaf brief item 4 |
| Theme on the caption buttons | `AppWindow.TitleBar.PreferredTheme` follows the app theme | Leaf `MainWindow.xaml.cs` |
| Instances | One window of each kind. Opening it again activates the existing one. Closing a secondary window destroys it to free memory. | Layers `SettingsWindow.Open`; Leaf brief item 4 |
| Placement of a new secondary window | Centered on the work area of the monitor under the cursor. Move it there first, then size it from that monitor's DPI (sizing first gets scaled twice). | Layers `SettingsWindow.xaml.cs` `Open` and spec "Opening on a monitor with another scale" |
| Sizes | Always written in DIPs and multiplied by the window's scale, plus the frame (`AppWindow.Size - ClientSize`) | Leaf `MainWindow.ApplyMinimumSize`; Layers `SettingsWindow.Open` |

> **Conflict (title bar height):** MA sizes its hand-built title bar row at runtime from the caption area; Sony uses the stock `TitleBar` at its default height. Leaf and Layers pin 48 DIP. **Use 48**, it's what the owner asked for ("caption buttons matching title-bar height").

### Title Bar Content

- Left to right: pane toggle (only where there's a pane), back button (only when there's somewhere to go), app icon, title, then content. (Leaf spec 6.2; Layers `SettingsWindow.xaml`.)
- Title bar buttons are icon-only with **no hover or press fill** (`LeafBareIconButtonStyle`, 32×32). Keyboard focus still shows. (Leaf `LeafTheme.xaml`, owner preference.)
- Glyph sizes in the title bar: 12 for chevrons (Previous/Next), 16 for everything else. (Leaf `MainWindow.xaml`.)
- Text buttons in the title bar (Today, the view picker) are stock `Button`/`DropDownButton` at `Height="32"` (`LeafToolbarButtonStyle`, padding `10,0`). (Leaf `LeafTheme.xaml`.)
- Space around buttons stays draggable. Call `AppTitleBar.RecomputeDragRegions()` after showing or hiding title bar content. (Leaf `MainWindow.xaml.cs`.)
- `TitleBarMinDragRegionWidth` 8 on the main window; 0 on fixed windows so Close sits on the edge. (Leaf; Layers.)

### Caption Buttons on Fixed-Size Windows

- A fixed window with **only Close** (onboarding): `OverlappedPresenter` with `IsResizable = false`, `IsMaximizable = false`, `IsMinimizable = false`. Windows then draws only the stock Close. Double-clicking the title bar must not maximize. (Leaf brief item 5; Windows behavior.)
- A fixed window that needs **Minimize and Close but no Maximize**: follow Layers exactly: `SetBorderAndTitleBar(true, false)`, two stock `Button`s 48×48 in `TitleBar.RightHeader`, restyled through lightweight resources (`SubtleFillColorSecondaryBrush` hover, `SubtleFillColorTertiaryBrush` press, Close hover `#C42B1C` with a white glyph), registered with `InputNonClientPointerSource.SetRegionRects`, glyphs dim while inactive, `TitleBarDeactivatedOpacity` 1. (Layers `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`.)

### Window Sizes

| Window | Size (DIP) | Resizable | Source |
| --- | --- | --- | --- |
| Main window | Minimum 1086 × 540 (computed from the panes and the title bar toolbar, including its 32 DIP sync status slot, see `MainWindow.MinimumWidth`). Default when nothing is remembered: 1280 × 820. | Yes | Leaf `MainWindow.xaml.cs`; default from MA `MainWindow.RestorePlacement` |
| Settings | Opens at 1000 × 720 | Yes (see conflict) | Sony `SettingsWindow.xaml.cs` |
| Onboarding | About 520 × 640 client, centered | No, Close only | Leaf brief item 5 |
| Tray flyout | Fixed 360 wide, height fixed to the tallest page, capped to the work area; 12 DIP from the taskbar and screen edge | No | Sony spec "Frame" and "Placement" |
| Dialog content | Stock `ContentDialog` sizes | — | All |

> **Conflict (settings window size):** Layers is fixed 760 × 595, not resizable. Sony opens at 1000 × 720 and uses that as its minimum. The brief wants the pane to auto-collapse when narrow, which needs a resizable window. **Use Sony's 1000 × 720 opening size, resizable, with a minimum of 640 × 500** so the pane can collapse (see Settings Window pattern). Every page must still work at the minimum (MA rule: "Make every page work down to the minimum window size ... with scrolling wherever content can overflow").

---

## 3. Layout and Spacing

### Spacing Scale

Use these values only: **2, 4, 8, 12, 16, 24, 32** (plus 36 for the settings content inset, below). Odd values (5, 9, 11, 17) are allowed only for optical alignment with a comment saying why, like the sidebar's 9 DIP shared left edge.

| Where | Value | Source |
| --- | --- | --- |
| Gap between items in a row of buttons | 4 (title bar toolbar), 8 (page actions) | Leaf `MainWindow.xaml`, `AccountsPage.xaml` |
| Icon to text inside a button | 8 | Layers Play Animation; MA Home Assistant button; Sony footer |
| Label stack (header, description) | 0 to 4 | Sony `AppPage.xaml`; MA `LoginPage.xaml` |
| Stack spacing inside a section | 8 | Layers `GeneralPage.xaml`; MA `SettingsPage.xaml`; Sony |
| Between sections, or form fields on a page | 16 | Layers; MA; Leaf `SetupPage.xaml` |
| Page padding, main window pages | 32 (current Leaf pages), 24 on dense pages | Leaf `SetupPage.xaml`, `AccountsPage.xaml`; MA pages `24,16,24,24` |
| Settings content inset | `Padding="36,24"` on the frame | Sony `SettingsWindow.xaml` |
| Card padding | 16 | Sony `SettingsCardStyle`; MA settings cards |
| Side panel inner inset | 16 | Leaf `DetailsPanel.xaml` |
| Flyout page padding | 20 (device page), 24 (empty state) | Sony `FlyoutView.xaml` |
| Readable column cap | `MaxWidth="560"` for forms and prose, 900 for settings pages | Leaf `SetupPage.xaml`; Sony settings pages |

### Structure

- Panes: sidebar 264, details 320, as inline `SplitView`s on the window's Mica; the calendar island is `LayerFillColorDefaultBrush` with no border (its fill is the edge). (Leaf `CalendarPage.xaml`.)
- Content under a 48 DIP title bar either starts in row 1, or runs under the title bar with `Padding="..,48,.."` to clear it. (Leaf `SidebarView.xaml`, `DetailsPanel.xaml`.)
- Only the part that can overflow scrolls; headers and footers stay pinned. (Leaf sidebar "Only the calendar list scrolls"; Sony spec "Footer stays pinned".)
- Size fixed windows so their main page doesn't scroll at 100%, but keep the `ScrollViewer` for small screens. (Layers spec SettingsWindow.)

### Corner Radii

| Element | Radius | Source |
| --- | --- | --- |
| Buttons, inputs, list rows, tiles | `{ThemeResource ControlCornerRadius}` (4) | Leaf `LeafIconButtonStyle`, sidebar rows `CornerRadius="4"`; Sony tiles; MA |
| Cards, flyout panels, dialogs, HUD-like surfaces | `{StaticResource OverlayCornerRadius}` (8) | MA settings cards; Sony `SettingsCardStyle`, flyout `CornerRadius="8"` |
| Event blocks, month chips | 4 | Leaf `EventBlock.cs`, `WeekRow.cs` |
| Circles (today, mini-month days) | Half the size (34 → 17, 28 → 14) | Leaf `DayHeaderCell.cs`, `LeafMiniDayButtonStyle` |
| Title bar icon buttons | `ControlCornerRadius` (only shows as a focus shape) | Leaf |

> **Conflict:** Layers' HUD used 6 to match the virtual-desktop switcher. That was copying a specific Windows surface; don't use 6 elsewhere.

---

## 4. Typography

The font is always the system font (Segoe UI Variable). Use the stock text styles by name, not raw sizes, except in code-built calendar visuals.

| Use | Style | Size / weight | Source |
| --- | --- | --- | --- |
| Settings page title, main page heading | `TitleTextBlockStyle` | 28 SemiBold, `Margin="0,0,0,16"` in settings | Sony `SettingsPageTitleStyle`; MA `SettingsPage.xaml`; Leaf `AccountsPage.xaml` |
| Dialog/card title, onboarding step title, details event title | `SubtitleTextBlockStyle` | 20 SemiBold | MA `LoginPage.xaml`; Leaf `DetailsPanel.xaml`; Layers About |
| Section header | `BodyStrongTextBlockStyle` | 14 SemiBold | Layers, MA, Sony (all three agree); Leaf "Upcoming" |
| Setting label, body text | `BodyTextBlockStyle` / default | 14 Regular | Sony `AppPage.xaml` |
| Description, status, metadata | `CaptionTextBlockStyle` + `TextFillColorSecondaryBrush` | 12 Regular | Sony `SecondaryTextStyle`; MA; Leaf |
| Sidebar group header (account email) | `LeafSectionHeaderStyle` (Caption, secondary) | 12 | Leaf `LeafTheme.xaml` |
| Error line in place of a status line | Caption + `SystemFillColorCautionBrush` | 12 | Sony `CautionTextStyle` |
| Period title (calendar) | Custom | 22 Bold | Leaf `CalendarPage.xaml` |
| Day number / weekday (day header) | Custom | 20 SemiBold / 12 | Leaf `DayHeaderCell.cs` |
| Event title / time | Custom | 12 SemiBold / 11 | Leaf `EventBlock.cs` |
| Title bar app name | `TitleBar.Title` (Caption-sized) | 12 | Leaf; MA `CaptionTextBlockStyle` |

- Two weights only in UI chrome: Regular and SemiBold. Bold is reserved for the calendar period title. (Leaf.)
- Long text trims with `CharacterEllipsis`; descriptions wrap. (Leaf, MA, Sony.)
- Don't use ALL CAPS labels. (MA's "REMOTE" badge and card tags are the only exceptions in the reference apps, and they're media-app flourishes.)

---

## 5. Color and Materials

- Only `{ThemeResource}` brushes in XAML. Code-built visuals pick per-theme values from `LeafBrushes` and re-apply on `ActualThemeChanged`. (Leaf `Controls/LeafBrushes.cs`; MA rule "re-apply any color set in code when ActualThemeChanged fires".)
- Accent comes from the system. Use `AccentFillColorDefaultBrush` / `TextOnAccentFillColorPrimaryBrush`, never a brand color. (Sony spec "system accent color"; Leaf today circle.)
- Accent-filled things per surface: the one primary action (`AccentButtonStyle`), the "on" state of toggles, today, and the selected step pip. (MA `LoginPage.xaml` Sign in; Layers Play Animation; Sony `SceneButtonActiveStyle`.)
- Surface brushes:

| Surface | Brush |
| --- | --- |
| Card / setting row | `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`, 1 px |
| Calendar island / content layer on Mica | `LayerFillColorDefaultBrush` |
| Hover fill on subtle things | `SubtleFillColorSecondaryBrush`; pressed `SubtleFillColorTertiaryBrush` |
| Divider | 1 DIP `Rectangle`/`Border` with `DividerStrokeColorDefaultBrush` |
| Flyout border | `SurfaceStrokeColorDefaultBrush`, 1 px, `ThemeShadow` with `Translation="0,0,32"` |
| Flyout footer strip | Theme dictionary brush: Light `#0F000000`, Dark `#33000000`, HighContrast `SystemColorWindowColor` |
| Status dots | `SystemFillColorSuccessBrush` / `CautionBrush` / `CriticalBrush` |
| Inactive/disabled text | `TextFillColorDisabledBrush`, or 0.4 opacity on custom templates |

  Sources: MA `SettingsPage.xaml`; Leaf `CalendarPage.xaml`, `LeafTheme.xaml`; Layers `GeneralPage.xaml`, `TrayMenuHost.xaml` spec; Sony `AppStyles.xaml`, `FlyoutWindow.xaml`.
- Calendar colors are Google's. Leaf's current-time line is `#E5484D`. (Leaf `LeafBrushes.cs`.)
- Theme setting: System / Light / Dark, labeled "Use Windows setting", "Light", "Dark". Apply with `RequestedTheme` on each window's root, to every open window. (Leaf `MainWindow.xaml` theme menu; Sony `ApplyTheme`.)
- Contrast themes: any brush you override for Light/Dark gets a `HighContrast` entry using `SystemColor*` resources, so borders and hovers stay visible. (MA `MainWindow.xaml` search box; Layers caption buttons.)

---

## 6. Icons

- Segoe Fluent Icons via `FontIcon Glyph="&#xE713;"` (XAML) or `char.ConvertFromUtf32(0xE713)` (code). Stock `SymbolIcon` is fine where the symbol exists. (MA rule; Layers spec "Icons"; Sony spec.)
- When Segoe Fluent Icons lacks a glyph, draw a `PathIcon` on the 16 DIP grid with a 1 px stroke to match, and say so in a comment. (MA Discover compass; Layers layers glyph.)
- Sizes:

| Where | Size |
| --- | --- |
| Title bar, nav items, menu items, setting row icons | 16 |
| Chevrons in the title bar | 12 |
| Mini-month chevrons | 10 |
| Icon inside a text button | 14 |
| About / empty-state / onboarding hero icon | 32 (onboarding may go to 48) |
| Menu item icon box | exactly 16×16 (set `Width`/`Height` on `PathIcon`) |

  Sources: Leaf `MainWindow.xaml`, `SidebarView.xaml`; Layers `GeneralPage.xaml` and spec TrayMenu; MA `LoginPage.xaml`; Sony `AboutPage.xaml`, `FlyoutView.xaml`.
- Standard glyphs so the apps agree: Settings `E713`, About/Info `E946`, Back `E72B`, Quit/Power `E7E8`, Search `E721`, Edit `E70F`, Delete `E74D`, Previous/Next `E76B`/`E76C`, Pane `E90D`, Minimize `E921`, Close `E8BB`, Refresh/Sync `E72C`. (Layers `SettingsWindow.xaml.cs`, `TrayMenuHost.xaml`; MA; Leaf; Sony.)
- Icons carry meaning together with text or a tooltip, never on their own. Decorative icons get `AutomationProperties.AccessibilityView="Raw"`. (Layers `GeneralPage.xaml`.)

---

## 7. Controls

### Buttons

| Kind | Control / style | When | Source |
| --- | --- | --- | --- |
| Primary action | `Button` + `AccentButtonStyle` | One per surface: Save, Sign in, Next, Open Leaf Calendar | Leaf `SetupPage.xaml`; MA `LoginPage.xaml` |
| Secondary action | `Button` (default style) | Cancel, Sync now, Disconnect | Leaf `AccountsPage.xaml`; Sony |
| Title bar icon | `LeafBareIconButtonStyle` 32×32 | Title bar only, no fill ever | Leaf |
| Icon button elsewhere | `LeafIconButtonStyle` (32, or 28 in the mini month): 83 ms fade to `SubtleFillColorSecondary` | Sidebar, panels | Leaf |
| Footer / flyout icon button | `SubtleButtonStyle`, 36×36 | Flyout footers | Sony `FooterIconButtonStyle`; MA back button |
| Link | `HyperlinkButton`, or `Hyperlink` inside a `TextBlock` to sit flush with text | Fixed URLs only | Layers About; Leaf `DetailsPanel.xaml` |
| Split action | `SplitButton` (main action + menu) | "Join meeting" + copy link | Leaf `DetailsPanel.xaml` |
| Tiles | `ToggleButton` 48 tall, stretch, `ControlCornerRadius`; label underneath | Quick Settings–style choices in the flyout | Sony `TileButtonStyle` |

- Every icon-only button has `ToolTipService.ToolTip` and `AutomationProperties.Name`. Tooltips add the shortcut in parentheses: "Today (T)", "Edit event (E)". (Leaf `MainWindow.xaml`.)

### Toggles and Choices

- On/off setting: `ToggleSwitch` at the right edge of a setting row, with **no On/Off text** (`OnContent=""`, `OffContent=""`, `MinWidth="0"`, `HorizontalAlignment="Right"`). The label is the row's header. (Sony `CardToggleSwitchStyle`.)
- Visibility of a thing in a list: `CheckBox` (the sidebar calendars, tinted with the calendar color). (Leaf `SidebarView.xaml`, brief ruling 1.)
- One of a few (≤ 5): `ComboBox`, `MinWidth="180"`, right edge of the row. One of 2–3 inline: `RadioButton`s (dialogs) or `RadioMenuFlyoutItem` (menus). (Sony `AppPage.xaml`; Leaf `ScopeDialog.cs`, theme menu.)
- Numbers: `NumberBox` with `SpinButtonPlacementMode="Compact"`; ranges: `Slider` with ticks. (Leaf `EventEditorView.xaml`; Layers duration slider.)

> **Conflict (toggle labels):** MA uses `ToggleSwitch Header="..."` with "On/Off" beside the knob; Layers sets `OnContent`/`OffContent` to the label ("Run on Startup"). **Use Sony's card switch with no text**, because it's how Windows 11 Settings does it and the brief says to copy Windows Settings.

### Inputs

- `TextBox`/`PasswordBox` with `Header` (sentence case) and an example `PlaceholderText` ("name@example.com", "Add a city or zone (Tokyo, NYC, UTC)"). Secrets always in `PasswordBox`. (Leaf `SetupPage.xaml`, `TimeZonePanel.xaml`; MA `LoginPage.xaml`.)
- Enter submits the form's primary action. (MA `OnPasswordKeyDown`.)

### Lists

- Stock `ListView`/`ItemsControl`. Rows at least 32 tall with `ControlCornerRadius`; hover fills the whole row, fading in 83 ms (`BrushTransition` on the row, and the item's own hover brushes set to `Transparent`). (Leaf `SidebarView.xaml`; Sony `PickerRowStyle`.)
- Long lists virtualize. Right-click on any item opens the same menu as its "..." button. (MA `CODING-STANDARDS.md`; Leaf `EventContextMenu.cs`.)

### Menus

- Stock `MenuFlyout` with Segoe Fluent icons. Items that open a window or dialog end with "…" (the single character): "Settings…", "Custom…". (Layers `TrayMenuHost.xaml`; Leaf `MainWindow.xaml`.)
- Tray menu items set `Padding="{ThemeResource MenuFlyoutItemThemePaddingNarrow}"`, or the first open is touch-sized. Placement follows the taskbar edge. (Layers spec TrayMenu; Leaf spec 8.3.)
- Separators group actions; Quit is last, alone. (Layers; Leaf spec 8.3.)

### Cards and Setting Rows

The Windows 11 Settings row, the one pattern for every setting:

```xml
<!-- Setting Row -->
<Border Style="{StaticResource SettingsCardStyle}">
    <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="16">
        <FontIcon Glyph="&#xE790;" FontSize="16" />
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
            <TextBlock Text="App theme" />
            <TextBlock Style="{StaticResource SecondaryTextStyle}" Text="Choose how Leaf looks." />
        </StackPanel>
        <ComboBox Grid.Column="2" MinWidth="180" VerticalAlignment="Center" />
    </Grid>
</Border>
```

- `SettingsCardStyle`: `CardBackgroundFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, 1 px, `OverlayCornerRadius`, `Padding="16"`, `MinHeight="68"`. (Sony `AppStyles.xaml`.)
- Icon is optional (16, left). Header is body text, description is Caption + secondary. The control sits right, vertically centered. (Sony `AppPage.xaml`; Leaf brief item 4.)
- A row with a sub-part (a path plus "Open log folder") stacks inside the same card with `Spacing="12"`. (Sony Logging card.)
- Use the CommunityToolkit `SettingsCard` only if it's AOT-safe with 0 IL warnings; otherwise this `Border` pattern or a small `SettingRow` control. (Leaf brief item 4.)

> **Conflict (settings layout):** MA puts a whole section in one card with stacked toggles and long descriptions under each; Layers uses no cards, just headers and dividers. **Use Sony's one-setting-per-card rows**: it's the Windows Settings look the brief asks for.

---

## 8. Settings Window Pattern

- Its own window, single instance, Mica, stock `TitleBar` with the app icon and title "Settings", pane toggle in the title bar (`IsPaneToggleButtonVisible="True"`, toggles `NavigationView.IsPaneOpen`). (Leaf brief item 4; Sony and Layers `SettingsWindow.xaml`.)
- Stock `NavigationView`: `IsSettingsVisible="False"`, `IsBackButtonVisible="Collapsed"`, its own pane toggle hidden, `OpenPaneLength="240"`. `PaneDisplayMode="Auto"` so it stays expanded at normal widths and collapses when narrow (set `ExpandedModeThresholdWidth` to about 800 so the 1000 wide window opens expanded). (Sony; brief item 4.)
- Menu items have a `FontIcon` (16) and a sentence-case name. **About** goes in `FooterMenuItems`. First item selected on open, shown without a transition (`SuppressNavigationTransitionInfo`), later pages with the stock drill-in. (Layers `SettingsWindow.xaml.cs`; Sony.)
- Items and pages set from concrete lists in code (AOT). (Layers.)
- Content: `Frame` with `Padding="36,24"`. Each page: `ScrollViewer` → `StackPanel MaxWidth="900" Spacing="4"` → page title (`TitleTextBlockStyle`, bottom margin 16) → groups. (Sony `SettingsWindow.xaml`, `AppPage.xaml`.)
- Groups: a `BodyStrongTextBlockStyle` header with `Margin="0,24,0,8"` (0 top on the first) over a stack of setting rows 4 apart. (Section headers: all three apps; 4 DIP row gap: Windows 11 Settings.)
- Every change saves immediately. No Save/Apply buttons. (Layers spec "Every change saves immediately"; Sony view model two-way bindings.)
- Pages for Leaf: General, Calendars, Time zones, Accounts, About (footer). (Brief item 4.)
- About page: app icon (or 32 glyph) + name + version in the first card, then cards for "View on GitHub", privacy, licenses and third-party notices, as fixed URLs. (Sony `AboutPage.xaml`; Layers `AboutPage.xaml` link set.)

> **Conflicts:** pane width is 160 in Layers, 220 in MA, 240 in Sony. **Use 240** (the one with five-plus pages). Layers has no page titles; Sony and MA do. **Show page titles** (Windows Settings does). Sony's card gap is 8; **use 4** to match Windows Settings, with group headers doing the separating.

---

## 9. Onboarding / First-Run Pattern

From the brief (item 5), with the reference apps filling in how it looks:

- Its own small window, about 520 × 640 client DIPs, centered on the monitor under the cursor, not resizable, Close only, Mica, stock `TitleBar` (icon + "Leaf Calendar"). Shown instead of the main window until an account exists. (Brief; Layers placement.)
- Steps in a `Frame`: Welcome → Google Cloud OAuth client → Sign in → Syncing → Done. Forward navigates with `SlideNavigationTransitionInfo { Effect = FromRight }`, Back with `FromLeft`. (Brief.)
- Step layout, top to bottom, `Padding="32"`, `Spacing="16"`: hero glyph (32 to 48), step title (`SubtitleTextBlockStyle`), one-line description (Caption, secondary), then the step's controls. (MA `LoginPage.xaml`.)
- Instructions are a short numbered list with the link inline ("1. Open Google Cloud Console and create a project."). Keep the existing `SetupPage` guide text as it is. (Leaf `SetupPage.xaml`.)
- Errors show in an `InfoBar` (`Severity="Error"`, `IsClosable="False"`) above the primary button; work in progress shows an indeterminate `ProgressBar` or a 20 DIP `ProgressRing` with a Caption status line. (MA `LoginPage.xaml`; Leaf `SetupPage.xaml`, `AccountsPage.xaml`.)
- Footer pinned at the bottom: a 1 DIP top divider (or the flyout footer strip brush), `Padding="24,16"`. Back (default button) on the left, the step's primary action (Accent) on the right, and a `PipsPager` centered, re-templated so each pip is a 24 × 3 rounded line: selected `AccentFillColorDefaultBrush`, others `ControlStrongFillColorDefaultBrush`. (Brief; Sony footer strip.)
- Primary actions: "Get started", "Next", "Sign in with Google", "Open Leaf Calendar" (enabled only when sync finishes). (Brief.)
- Closing early asks first (see Dialogs).

---

## 10. Dialogs and Warnings

- Stock `ContentDialog` only, built in code with `XamlRoot` set. (Leaf `ScopeDialog.cs`, `AccountsPage.xaml.cs`; MA `ItemMenu.cs`.)
- Title: a short question for confirmations ("Disconnect this account?", "Leave setup?"), a noun for pickers ("Number of days", "New playlist").
- Content: one or two plain sentences saying what happens and what doesn't ("Your Google Calendar isn't changed.").
- Buttons: the primary button repeats the action verb ("Disconnect", "Remove", "Create", "Show", "Leave"); the close button is "Cancel", or a "Keep ..." phrase when Cancel would be unclear ("Keep setting up").
- `DefaultButton`: **Close** for destructive actions, **Primary** for harmless ones. (Leaf `AccountsPage.xaml.cs` vs `MainWindow.xaml.cs`; MA "Remove from library" vs "New playlist".)
- Non-blocking messages use an `InfoBar`, not a dialog: inline for page errors, bottom-center of the island for undo notices ("Event deleted · Undo"). Transient command failures auto-dismiss after 5 s. (Leaf `CalendarPage.xaml`; Sony spec "States".)
- Real notifications are Windows toasts, never in-app popups. (Leaf spec 2.)

> **Conflict:** Leaf's `ScopeDialog` uses "OK"; MA and Leaf's other dialogs use verbs. **Use verbs** in new dialogs. Existing `ScopeDialog` stays as it is.

---

## 11. Motion

| Motion | Duration and easing | Source |
| --- | --- | --- |
| Hover and press fills | **83 ms** `BrushTransition` | Leaf `LeafTheme.xaml`, owner preference |
| Fades (show/hide) | 167 ms, decelerate in `(0,0)-(0,1)`, linear out | Sony `ImplicitMotion.cs` (Fluent) |
| Layout glides (element moves) | 250 ms, decelerate `(0,0)-(0,1)` | Sony `ImplicitMotion.cs` |
| Flyout enter / exit | Slide from the taskbar edge + fade: 250 ms decelerate in, 167 ms accelerate `(1,0)-(1,1)` out | Sony `FlyoutWindow.xaml.cs`; Leaf spec 8.2 |
| Side panes open / close | 200 ms / 100 ms, on the `SplitView`'s own curve | Leaf `MainWindow.xaml.cs` |
| Page changes in a `Frame` | Stock transitions: drill-in (settings), slide (onboarding), suppressed on first show | Layers; brief |
| Period paging | Slide with the next period preloaded, nothing flashes blank | Leaf spec 6.4 |

- Pause layout glides while a window resizes. (Sony `ImplicitMotion.Pause`.)
- No bounce, no elastic, no zoom in chrome. (MA's elastic heart and art zoom are media flourishes; don't bring them over.)
- Animations are built once and kept; don't read `RenderTransform` back under AOT. (Leaf `MainWindow.xaml.cs` comment.)

> **Conflict:** MA uses slower custom curves (320/240 ms queue slide, 280 ms paging, 250/180 ms back button). **Use the Fluent 83/167/250 set** (Sony, and the owner's 83 ms rule). Existing Leaf pane timings (200/100) stay.

---

## 12. States

| State | Rule | Source |
| --- | --- | --- |
| Hover | Subtle fill fades in over 83 ms (`SubtleFillColorSecondaryBrush`); whole row lights, not just the text. **None** on title bar icon buttons. | Leaf `LeafTheme.xaml`, `SidebarView.xaml` |
| Pressed | `SubtleFillColorTertiaryBrush`; glyph stays full strength | Leaf; Layers caption buttons |
| Focus | System focus visuals (`UseSystemFocusVisuals="True"`) on every custom template, including bare buttons | Leaf; MA `PlainCardButtonStyle` |
| Disabled | Stock disabled look; custom templates and previews drop to 0.4 opacity. Group a section in a `ContentControl` so one `IsEnabled` disables it all. | Leaf; Layers spec; Sony `SectionStyle` |
| Selected / on | Accent (toggle tiles checked, active scene, today, selected pip) | Sony; Leaf |
| Empty | One short sentence in secondary text, plus the next action as a link or button when there is one ("Nothing in the next 8 hours.", "No Sony headphones connected" + "Open Bluetooth settings") | Leaf `DetailsPanel.xaml`; Sony spec "Page Rules" |
| Loading | `ProgressRing` 20 DIP beside the action that's running, controls disabled; indeterminate `ProgressBar` for a whole step | Leaf `AccountsPage.xaml`; Sony spec "Connecting"; MA `LoginPage.xaml` |
| Error | `InfoBar Severity="Error"` inline, or a caution-colored status line; the control reverts to the real value | MA; Leaf; Sony `CautionTextStyle` |
| Scrollbars | Hidden until the pointer is over the scroller, then WinUI's auto-hiding indicator. Use `ScrollIndicator.ShowOnHover` on every new `ScrollViewer`. | Leaf `Controls/ScrollIndicator.cs`, owner preference |

> **Conflict (hover implementation):** MA forbids `VisualStateManager` setters in a `ControlTemplate` (they crashed WinUI on first hover) and does hover in code. Leaf's templates use VSM setters plus a `BrushTransition` and run fine under AOT. **Keep Leaf's approach**; switch to `PointerEntered`/`PointerExited` in code if a template ever crashes on hover.

---

## 13. Copy and Tone

- **US English** everywhere (UI, comments, docs). (MA `CODING-STANDARDS.md`; global instructions.)
- **Sentence case** for every visible label: headers, buttons, menu items, nav items, toggles, dialog titles ("Add Google account", "Show declined events", "Remote access", "Noise & scenes"). Proper nouns keep their caps (Google, Leaf Calendar, Windows).
- Buttons start with a verb and say what they do ("Sync now", "Open log folder", "Change OAuth client").
- Toggle labels read as statements, first person is fine: "Start when I sign in", "Keep running in the background when I close the window". (Sony; MA.)
- Descriptions: plain, second person, one or two sentences, ending with a period; include the constraint ("Notifies you once when your headphones drop below 20%."). (Sony; MA.)
- Paths in text use "›": "Settings › Apps › Startup". (Layers spec; MA `LoginPage.xaml`.)
- Middle dot " · " separates inline facts ("9:00 AM · in 12 min", "Event deleted · Undo"). (Leaf; Sony "AAC · DSEE Extreme".)
- "…" (one character) on items that open more UI. (Layers; Leaf.)
- Empty and error text says what's wrong and what to do, never blames the user, never shows raw exception text.
- Keep content the owner wrote verbatim; flag typos instead of fixing them. (Global instructions.)

> **Conflict (capitalization):** Layers uses Title Case on controls ("Run on Startup", "Play Animation", "HUD Show Duration (In Seconds)") and MA mixes ("Top Picks for You" next to "Recently played"). Sony, MA's settings, and all of Leaf use sentence case, as Windows does. **Use sentence case.** (Title Case stays for code comment labels, per the owner's code style.)

---

## 14. Accessibility

- `AutomationProperties.Name` on every icon-only control, link, nav item, and on the toggle/combobox of a setting row (same text as its label). (MA; Layers spec; Sony `AppPage.xaml`.)
- `AutomationProperties.AutomationId` on everything a UI test touches (`SettingsButton`, `ClientIdBox`). (Leaf, all views.)
- Landmarks: sidebar `Navigation`, calendar view `Main`. (Leaf `CalendarPage.xaml`.)
- Every action works from the keyboard with a visible focus ring; logical tab order; Esc closes flyouts and panels; access keys on settings controls where they help (Layers: Alt+S, Alt+H...). (MA; Layers spec; Leaf `CalendarPage.xaml` Escape.)
- Never color alone: calendar color plus name, status dot plus text. (MA rule; Layers status row.)
- Contrast themes keep borders and hover visible (see Color). Text sizes follow the system text scale; don't hard-code heights that clip text in chrome.
- Minimum sizes keep everything visible and unclipped at every DPI. (Leaf `MinimumWidth` comment; MA rule.)

---

## 15. Where Leaf Already Follows This / Known Gaps

Don't change existing work to close these. They're notes for new screens.

**Already follows it**

- Mica main window, stock `TitleBar` at 48 DIP with tall caption buttons, app icon, drag region kept. (`MainWindow.xaml(.cs)`)
- Bare title bar icon buttons, 32 DIP, 12/16 glyphs, tooltips with shortcuts. (`MainWindow.xaml`, `LeafTheme.xaml`)
- 83 ms hover fades on icon buttons and sidebar rows. (`LeafTheme.xaml`, `SidebarView.xaml`)
- Scrollbars hidden until hover. (`ScrollIndicator.cs`)
- Theme brushes throughout, per-theme code brushes with the system accent. (`LeafBrushes.cs`)
- Sentence-case copy, verb buttons, destructive dialog defaults to Close. (`AccountsPage.xaml(.cs)`)
- AutomationIds everywhere, landmarks, min window size computed from content.

**Known gaps (new work should do better, existing screens stay)**

1. No remembered window placement; MA restores size, position and maximized state and falls back to 1280 × 820.
2. Contrast-theme overrides exist only in `DetailsPanel.xaml`; new brush overrides need `HighContrast` entries.
3. `ScopeDialog` uses "OK" instead of a verb.
4. `SetupPage` and `AccountsPage` are main-window pages today; the brief moves them to the onboarding and settings windows.
5. Theme and view options live in the view picker menu; the brief moves the settings ones to Settings › General.
6. The tray flyout (acrylic, slide from the taskbar edge) isn't built yet; follow Sony's `FlyoutWindow` when it is.

---

## Reference Files

Screenshots worth opening before building a new screen:

- `D:\layers\assets\screenshots\settings-general.png`: fixed settings window, custom Minimize/Close, left nav, section headers.
- `D:\music-assistant\docs\screenshots\08-settings.png`: Mica, title bar with icon and back button, settings cards.
- `D:\sony-control\docs\images\flyout-wf-1000xm6.png`: acrylic tray flyout, tiles, footer strip.
- `D:\layers\assets\screenshots\tray-menu.png`: stock `MenuFlyout` tray menu with 16 DIP icons and status dot.

XAML to copy from:

- `D:\sony-control\src\SonyControl.App\Styles\AppStyles.xaml`: `SettingsCardStyle`, `CardToggleSwitchStyle`, `SettingsPageTitleStyle`, footer brushes.
- `D:\sony-control\src\SonyControl.App\Views\Settings\AppPage.xaml`: setting rows.
- `D:\sony-control\src\SonyControl.App\SettingsWindow.xaml(.cs)` and `D:\layers\src\Layers.UI\SettingsWindow.xaml(.cs)`: settings window, single instance, DPI-correct centering, custom caption buttons.
- `D:\music-assistant\MusicAssistant\Pages\LoginPage.xaml`: sign-in step layout.
- `D:\sony-control\src\SonyControl.App\ImplicitMotion.cs` and `FlyoutWindow.xaml(.cs)`: Fluent motion timings, acrylic flyout.
- `src\LeafCalendar.App\Styles\LeafTheme.xaml`: Leaf's button and hover styles.
