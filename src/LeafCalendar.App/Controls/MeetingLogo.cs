using LeafCalendar.Core.Events;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// A Join button's 16 DIP icon: the meeting service's one-color logo, else a video glyph (any other link). Google Meet,
/// Webex, and Zoom (a wordmark, so it's wider: about the text's height) are from Simple Icons (CC0,
/// https://simpleicons.org); Microsoft Teams, which Simple Icons doesn't carry, is from Boxicons (MIT, see
/// Assets/ThirdPartyNotices.txt). Drawn as a vector in the button's text color, so it's sharp at any scale and follows
/// the theme.
/// </summary>
public sealed partial class MeetingLogo : UserControl
{
    // 24 x 24 Paths (Teams' dot is Boxicons' circle as two arcs)
    const string MeetPath  = "M5.53 2.13 0 7.75h5.53zm.398 0v5.62h7.608v3.65l5.47-4.45c-.014-1.22.031-2.25-.025-3.46-.148-1.09-1.287-1.47-2.236-1.36zM23.1 4.32c-.802.295-1.358.995-2.047 1.49-2.506 2.05-4.982 4.12-7.468 6.19 3.025 2.59 6.04 5.18 9.065 7.76 1.218.671 1.428-.814 1.328-1.64v-13a.828.828 0 0 0-.877-.825zM.038 8.15v7.7h5.53v-7.7zm13.577 8.1H6.008v5.62c3.864-.006 7.737.011 11.58-.009 1.02-.07 1.618-1.12 1.468-2.07v-2.51l-5.47-4.68v3.65zm-13.577 0c.02 1.44-.041 2.88.033 4.31.162.948 1.158 1.43 2.047 1.31h3.464v-5.62z";
    const string ZoomPath  = "M5.033 14.649H.743a.74.74 0 0 1-.686-.458.74.74 0 0 1 .16-.808L3.19 10.41H1.06A1.06 1.06 0 0 1 0 9.35h3.957c.301 0 .57.18.686.458a.74.74 0 0 1-.161.808L1.51 13.59h2.464c.585 0 1.06.475 1.06 1.06zM24 11.338c0-1.14-.927-2.066-2.066-2.066-.61 0-1.158.265-1.537.686a2.061 2.061 0 0 0-1.536-.686c-1.14 0-2.066.926-2.066 2.066v3.311a1.06 1.06 0 0 0 1.06-1.06v-2.251a1.004 1.004 0 0 1 2.013 0v2.251c0 .586.474 1.06 1.06 1.06v-3.311a1.004 1.004 0 0 1 2.012 0v2.251c0 .586.475 1.06 1.06 1.06zM16.265 12a2.728 2.728 0 1 1-5.457 0 2.728 2.728 0 0 1 5.457 0zm-1.06 0a1.669 1.669 0 1 0-3.338 0 1.669 1.669 0 0 0 3.338 0zm-4.82 0a2.728 2.728 0 1 1-5.458 0 2.728 2.728 0 0 1 5.457 0zm-1.06 0a1.669 1.669 0 1 0-3.338 0 1.669 1.669 0 0 0 3.338 0z";
    const string TeamsPath = "M18.581 8.344a1.707 1.707 0 1 0 3.414 0a1.707 1.707 0 1 0-3.414 0zM18.581 11.513h3.413v3.656c0 .942-.765 1.706-1.707 1.706h-1.706v-5.362zM2.006 4.2v15.6l11.213 1.979V2.221L2.006 4.2zm8.288 5.411-1.95.049v5.752H6.881V9.757l-1.949.098V8.539l5.362-.292v1.364zm3.899.439v8.288h1.95c.808 0 1.463-.655 1.463-1.462V10.05h-3.413zm1.463-4.875c-.586 0-1.105.264-1.463.673v2.555c.357.409.877.673 1.463.673a1.95 1.95 0 0 0 0-3.901z";
    const string WebexPath = "M21.78 7.376c.512 1.181.032 2.644-1.11 3.106-2.157.888-3-1.295-3-1.295-.236-.55-.727-1.496-1.335-1.496-.204 0-.503 0-.94.844-.229.443-.434 1.185-.616 1.84l-.09.32c-.373-1.587-.821-3.454-1.536-4.816-.195-.38-.42-.74-.673-1.08a5.135 5.135 0 0 1 1.743-1.337 4.891 4.891 0 0 1 2.112-.463c1.045 0 2.765.338 4.227 2.227.167.206.317.424.448.654.278.441.52.904.726 1.383l.043.113zM.02 8.4C-.15 7.105.8 5.845 1.953 5.755c1.794-.157 2.36 1.385 2.455 1.89l.022.137c.07.44.29 1.838.48 2.744.078.4.244 1.013.353 1.416l.006.022.026.092c.11.4.232.799.362 1.193.185.548.399 1.085.641 1.61.47.955.93 1.45 1.367 1.45.203 0 .512 0 .96-.878.283-.59.512-1.208.684-1.845.373 1.598.811 3.128 1.495 4.456.205.406.444.794.715 1.16a5.124 5.124 0 0 1-1.742 1.338 4.88 4.88 0 0 1-2.112.461c-1.548 0-3.727-.698-5.339-4.005a22.407 22.407 0 0 1-1.078-2.824 26.848 26.848 0 0 1-.693-2.656 48.56 48.56 0 0 1-.215-1.114C.191 9.603.074 8.872.02 8.4zm22.047-2.645-.202-.022h-.052c.222.392.421.797.597 1.215l.053.113c.322.76.346 1.614.068 2.391a3.079 3.079 0 0 1-1.552 1.749 2.93 2.93 0 0 1-1.228.28 3.115 3.115 0 0 1-.854-.135c-.299 1.182-.768 2.634-1.195 3.511-.427.877-.93 1.451-1.378 1.451-.192 0-.501 0-.95-.877a10.746 10.746 0 0 1-.683-1.845 38.722 38.722 0 0 1-.396-1.575 12.67 12.67 0 0 1-.136-.598l-.002-.01c-.406-1.778-.865-3.645-1.655-5.142A8.263 8.263 0 0 0 11.52 4.8a5.136 5.136 0 0 0-1.748-1.34A4.892 4.892 0 0 0 7.654 3c-1.036 0-2.754.338-4.217 2.228.466.223.867.562 1.164.984.305.433.499.933.565 1.458.076.563.256 1.654.47 2.688l.001.007c.021.11.042.221.073.342.126-.34.25-.642.38-.955l.112-.271.128-.293c.235-.55.726-1.496 1.324-1.496.213 0 .513 0 .95.844.296.606.532 1.239.706 1.89.138.507.276 1.047.394 1.587.04.148.07.296.101.444l.006.028c.427 1.879.875 3.69 1.644 5.187.159.317.34.622.545.911.15.215.31.422.48.62 1.27 1.45 2.733 1.8 3.843 1.8 1.548 0 3.738-.698 5.35-4.006.822-1.7 1.515-4.208 1.772-5.48.256-1.27.449-2.419.534-3.115.04-.307.023-.618-.051-.918-.075-.299-.205-.579-.382-.825a2.247 2.247 0 0 0-.653-.607 2.143 2.143 0 0 0-.826-.296z";

    MeetingProvider? _provider;

    /// <summary>Creates the icon (the video glyph until <see cref="Provider"/> is set).</summary>
    public MeetingLogo()
    {
        IsTabStop = false;
        Height    = 16;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Show();
    }

    /// <summary>The meeting service, or null for a link Leaf doesn't recognize.</summary>
    public MeetingProvider? Provider
    {
        get => _provider;
        set
        {
            if (_provider != value)
            {
                _provider = value;
                Show();
            }
        }
    }

    /// <summary>The logo's Simple Icons path for a service, or null when it gets the video glyph.</summary>
    public static string? PathFor(MeetingProvider? provider) => provider switch
    {
        MeetingProvider.GoogleMeet => MeetPath,
        MeetingProvider.Webex      => WebexPath,
        MeetingProvider.Zoom       => ZoomPath,
        MeetingProvider.Teams      => TeamsPath,
        _                          => null,
    };

    // Zoom's wordmark fills 24 x 5.46 of its frame, from y 9.27; it's drawn 30 DIP wide (about 7 tall, like the text)
    const double ZoomTop    = 9.27;
    const double ZoomHeight = 5.46;
    const double ZoomWidth  = 30;

    // The path in its 24 x 24 frame (Zoom's cropped to its wordmark), scaled to fit, filled nonzero like the SVGs (F1;
    // XAML's default even-odd would punch holes where shapes overlap); the icon takes the button's text color
    void Show()
    {
        Width = _provider == MeetingProvider.Zoom ? ZoomWidth : 16;
        if (PathFor(_provider) is { } path)
        {
            var zoom  = _provider == MeetingProvider.Zoom;
            var icon  = new PathIcon { Width = 24, Height = 24, Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), "F1 " + path) };
            var frame = new Grid { Width = 24, Height = zoom ? ZoomHeight : 24, Children = { icon } };
            if (zoom)
            {
                icon.Margin = new Microsoft.UI.Xaml.Thickness(0, -ZoomTop, 0, -(24 - ZoomTop - ZoomHeight));
            }

            Content = new Viewbox { Child = frame, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        }
        else
        {
            Content = new FontIcon { Glyph = "\uE714", FontSize = 14 };
        }
    }
}
