// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Ported from PowerToys: src/settings-ui/Settings.UI/SettingsXAML/Controls/ShortcutControl/ShortcutDialogContentControl.xaml.cs
// (https://github.com/microsoft/PowerToys). Leaf changes: Keys is a plain property whose setter draws the key caps (a
// list of boxed values can't go to a WinRT ItemsSource under Native AOT), with KeysName for screen readers and tests;
// no ignore-conflict or "Learn more" parts.

using LeafCalendar.App.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views.Settings;

/// <summary>The "Activation shortcut" dialog's content: the tip, the pressed keys as key caps, Reset and Clear, and the warnings.</summary>
public sealed partial class ShortcutDialogContentControl : UserControl
{
    /// <summary>Identifies <see cref="IsError"/>.</summary>
    public static readonly DependencyProperty IsErrorProperty = DependencyProperty.Register("IsError", typeof(bool), typeof(ShortcutDialogContentControl), new PropertyMetadata(false, OnIsErrorChanged));
    /// <summary>Identifies <see cref="IsWarningAltGr"/>.</summary>
    public static readonly DependencyProperty IsWarningAltGrProperty = DependencyProperty.Register("IsWarningAltGr", typeof(bool), typeof(ShortcutDialogContentControl), new PropertyMetadata(false));
    /// <summary>Identifies <see cref="HasConflict"/>.</summary>
    public static readonly DependencyProperty HasConflictProperty = DependencyProperty.Register("HasConflict", typeof(bool), typeof(ShortcutDialogContentControl), new PropertyMetadata(false));
    /// <summary>Identifies <see cref="ConflictMessage"/>.</summary>
    public static readonly DependencyProperty ConflictMessageProperty = DependencyProperty.Register("ConflictMessage", typeof(string), typeof(ShortcutDialogContentControl), new PropertyMetadata(string.Empty));

    private List<object>? _keys;

    /// <summary>Creates the content.</summary>
    public ShortcutDialogContentControl()
    {
        this.InitializeComponent();
    }

    /// <summary>Reset was clicked.</summary>
    public event RoutedEventHandler? ResetClick;

    /// <summary>Clear was clicked.</summary>
    public event RoutedEventHandler? ClearClick;

    /// <summary>The combination is taken (by the other shortcut, or by Windows or another app).</summary>
    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    /// <summary>Says who has the combination.</summary>
    public string ConflictMessage
    {
        get => (string)GetValue(ConflictMessageProperty);
        set => SetValue(ConflictMessageProperty, value);
    }

    /// <summary>The key caps (see <see cref="LeafCalendar.Core.Tray.HotkeySettings.GetKeysList"/>); setting it draws them.</summary>
    public List<object>? Keys
    {
        get => _keys;
        set
        {
            _keys = value;
            DrawKeys();
        }
    }

    /// <summary>The keys as text ("Ctrl+Alt+J", "No keys"), the key area's accessible name.</summary>
    public string KeysName
    {
        get => AutomationProperties.GetName(KeysControl);
        set => AutomationProperties.SetName(KeysControl, value);
    }

    /// <summary>The combination can't be used (the caps turn red and "Invalid shortcut" shows).</summary>
    public bool IsError
    {
        get => (bool)GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    /// <summary>Ctrl+Alt without Win: the Alt Gr warning shows.</summary>
    public bool IsWarningAltGr
    {
        get => (bool)GetValue(IsWarningAltGrProperty);
        set => SetValue(IsWarningAltGrProperty, value);
    }

    /// <summary>True when <paramref name="newValue"/> lists the same keys as <see cref="Keys"/>.</summary>
    public bool JudgeIfKeyValueSame(List<object> newValue)
    {
        List<object>? currentValue = _keys;

        if (currentValue == null && newValue == null)
        {
            return true;
        }

        if (currentValue == null || newValue == null)
        {
            return false;
        }

        if (currentValue.Count != newValue.Count)
        {
            return false;
        }

        for (int index = 0; index < currentValue.Count; index++)
        {
            if (!Equals(currentValue[index], newValue[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void OnIsErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ShortcutDialogContentControl)d).DrawKeys();

    // The Key Caps (PowerToys' KeysControl item template) And Clear, Shown Only With Keys To Clear
    private void DrawKeys()
    {
        KeysControl.Children.Clear();
        foreach (var key in _keys ?? [])
        {
            var keyVisual = new KeyVisual
            {
                Padding          = new Thickness(20, 16, 20, 16),
                Content          = key,
                CornerRadius     = new CornerRadius(8),
                FontSize         = 16,
                FontWeight       = FontWeights.SemiBold,
                IsTabStop        = false,
                RenderKeyAsGlyph = true,
                State            = IsError ? KeyVisualState.Error : KeyVisualState.Normal,
                Style            = (Style)Application.Current.Resources["AccentKeyVisualStyle"],
            };
            AutomationProperties.SetAccessibilityView(keyVisual, AccessibilityView.Raw);
            KeysControl.Children.Add(keyVisual);
        }

        ClearBtn.Visibility = _keys is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        ResetClick?.Invoke(this, new RoutedEventArgs());
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        ClearClick?.Invoke(this, new RoutedEventArgs());
    }
}
