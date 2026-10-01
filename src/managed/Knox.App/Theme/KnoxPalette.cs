// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The single source of truth for palette brushes used by C# custom-drawn controls
// (Render overrides can't cheaply resolve a XAML StaticResource). Every value here
// MIRRORS a Brush.* key in Theme/NotaTheme.axaml — keep the two in sync. Views used
// to each re-declare these hex literals privately; they now alias into this class so
// a colour changes in exactly one place. File-specific tints (low-alpha washes,
// one-off grid shades) intentionally stay local to their view.

using Avalonia.Media;

namespace Knox.App;

internal static class KnoxPalette
{
    private static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    // Fonts (Helvetica embedded matching Font.UI in KnoxTheme.axaml with Inter, Poppins fallbacks)
    public static readonly FontFamily UiFont = new("avares://Knox.App/Assets/Fonts#Helvetica, Inter, Helvetica, Helvetica Neue, Poppins, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif");
    public static readonly FontFamily MonoFont = new("avares://Knox.App/Assets/Fonts#Helvetica, Inter, Helvetica, Helvetica Neue, Poppins, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif");

    // Surfaces (Brush.Bg* / Brush.Surface*) - Topbar 132;132;132 (#848484), BgApp 64;64;64 (#404040), BgSunken 46;46;46 (#2E2E2E)
    public static readonly IBrush BgSunken = Hex("#2E2E2E");     // 46, 46, 46 (Darker sunken / well / lanes)
    public static readonly IBrush BgApp = Hex("#404040");        // 64, 64, 64 (Main app window background)
    public static readonly IBrush LaneB = Hex("#363636");        // 54, 54, 54 (Alternating lane B)
    public static readonly IBrush SurfaceCard = Hex("#383838");  // 56, 56, 56 (Surface cards)
    public static readonly IBrush SurfaceRaised = Hex("#343434");// 52, 52, 52 (Raised tactile buttons & chips)
    public static readonly IBrush SurfaceHover = Hex("#4A4A4A"); // 74, 74, 74 (Surface hover)
    public static readonly IBrush SurfaceActive = Hex("#3D84E8");// Brush.SurfaceActive (Logic Blue active state)
    public static readonly IBrush ChromeBg = Hex("#848484");     // 132, 132, 132 (Top bar chrome)

    // Text (Brush.Text*) - Neutral
    public static readonly IBrush TextPrimary = Hex("#FFFFFF");  // Brush.TextPrimary
    public static readonly IBrush TextSecondary = Hex("#DCDCDC");// Brush.TextSecondary
    public static readonly IBrush TextTertiary = Hex("#AAAAAA"); // Brush.TextTertiary
    public static readonly IBrush TextDisabled = Hex("#787878"); // Brush.TextDisabled
    public static readonly IBrush TextOnAccent = Hex("#FFFFFF"); // Brush.TextOnAccent

    // Borders (Brush.Border*) - Pure Neutral
    public static readonly IBrush BorderDefault = Hex("#4E4E4E");// 78, 78, 78
    public static readonly IBrush BorderStrong = Hex("#686868"); // 104, 104, 104

    // Grid lines (Brush.Grid*) - Pure Neutral
    public static readonly IBrush GridBeat = Hex("#3C3C3C");     // 60, 60, 60
    public static readonly IBrush GridBar = Hex("#525252");      // 82, 82, 82

    // Accent (Brush.Accent*)
    public static readonly IBrush Accent = Hex("#3A75C4");       // Brush.Accent (Logic Blue)
    public static readonly IBrush AccentHover = Hex("#4A85D6");  // Brush.AccentHover
    public static readonly IBrush AccentBright = Hex("#5E98E6"); // Brush.AccentBright
    public static readonly IBrush AccentSubtle = new SolidColorBrush(Color.FromArgb(0x33, 0x3A, 0x75, 0xC4)); // Brush.AccentSubtle

    // Status (Brush.Success / Warning / Danger)
    public static readonly IBrush Success = Hex("#389E4D");      // Brush.Success (Logic Play Green)
    public static readonly IBrush Warning = Hex("#C98E20");      // Brush.Warning (Logic Solo Yellow)
    public static readonly IBrush Danger = Hex("#C93E3E");       // Brush.Danger (Logic Record Red)
    public static readonly IBrush DangerHover = Hex("#D64848");  // Brush.DangerHover

    // Track palette (Brush.Track*) — only the shades reused by custom-drawn views.
    public static readonly IBrush Sage = Hex("#388654");         // Brush.Track5
    public static readonly IBrush Teal = Hex("#32808A");         // Brush.Track8
    public static readonly IBrush LogicPurple = Hex("#7056B3");   // Logic Purple

    // Raw Colors — for the few call sites that need a Color rather than an IBrush.
    public static readonly Color AccentColor = Color.Parse("#3A75C4");       // Brush.Accent
    public static readonly Color AccentBrightColor = Color.Parse("#5E98E6"); // Brush.AccentBright
    public static readonly Color SuccessColor = Color.Parse("#389E4D");      // Brush.Success

    // The 8-track colour cycle (Brush.Track1..8) + the two return-bus tints. Views index
    // these to colour clips/tracks; keep the order in sync with NotaTheme.axaml.
    public static readonly Color[] TrackColors =
    {
        Color.Parse("#B88E30"), Color.Parse("#9E592F"), Color.Parse("#A84444"), Color.Parse("#6E4C8E"),
        Color.Parse("#388654"), Color.Parse("#943B71"), Color.Parse("#3A669E"), Color.Parse("#32808A"),
    };
    public static readonly Color[] ReturnColors = { Color.Parse("#5C687D"), Color.Parse("#7D685C") }; // ReturnA, ReturnB
}
