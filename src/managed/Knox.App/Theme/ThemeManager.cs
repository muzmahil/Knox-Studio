// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;
using Avalonia;
using Avalonia.Media;

namespace Knox.App;

public static class ThemeManager
{
    public static event Action? ThemeChanged;

    public static void Apply(string themeVariant, string themeAccent)
    {
        var app = Avalonia.Application.Current;
        if (app == null) return;

        var res = app.Resources;

        // 1. Base Theme Palette
        Color bgApp, bgSunken, laneB, card, raised, hover, active, chrome, borderDef, borderStr;

        switch (themeVariant)
        {
            case "Obsidian":
                bgApp = Color.Parse("#0E1013");
                bgSunken = Color.Parse("#08090A");
                laneB = Color.Parse("#131519");
                card = Color.Parse("#181A1F");
                raised = Color.Parse("#23262D");
                hover = Color.Parse("#2C3038");
                active = Color.Parse("#373C47");
                chrome = Color.Parse("#0B0C0E");
                borderDef = Color.Parse("#262930");
                borderStr = Color.Parse("#3C404B");
                break;

            case "WarmCharcoal":
                bgApp = Color.Parse("#242220");
                bgSunken = Color.Parse("#181615");
                laneB = Color.Parse("#2B2926");
                card = Color.Parse("#322E2B");
                raised = Color.Parse("#3F3B36");
                hover = Color.Parse("#4C4742");
                active = Color.Parse("#59534D");
                chrome = Color.Parse("#1D1B19");
                borderDef = Color.Parse("#3D3833");
                borderStr = Color.Parse("#544E47");
                break;

            case "MidnightNavy":
                bgApp = Color.Parse("#161A24");
                bgSunken = Color.Parse("#0F121A");
                laneB = Color.Parse("#1D2230");
                card = Color.Parse("#222838");
                raised = Color.Parse("#2E364A");
                hover = Color.Parse("#3A445C");
                active = Color.Parse("#475270");
                chrome = Color.Parse("#12151E");
                borderDef = Color.Parse("#2B3345");
                borderStr = Color.Parse("#404B63");
                break;

            case "SolarisAmber":
                bgApp = Color.Parse("#231E19");
                bgSunken = Color.Parse("#17130F");
                laneB = Color.Parse("#2B241E");
                card = Color.Parse("#332B24");
                raised = Color.Parse("#42382F");
                hover = Color.Parse("#51453B");
                active = Color.Parse("#615347");
                chrome = Color.Parse("#1B1612");
                borderDef = Color.Parse("#40352B");
                borderStr = Color.Parse("#594B3D");
                break;

            case "CyberpunkNeon":
                bgApp = Color.Parse("#110F18");
                bgSunken = Color.Parse("#09080E");
                laneB = Color.Parse("#161420");
                card = Color.Parse("#1D1A2B");
                raised = Color.Parse("#28243C");
                hover = Color.Parse("#353050");
                active = Color.Parse("#463F6A");
                chrome = Color.Parse("#0D0B13");
                borderDef = Color.Parse("#2E2745");
                borderStr = Color.Parse("#4C3F73");
                break;

            case "NordicSlate":
                bgApp = Color.Parse("#1C2026");
                bgSunken = Color.Parse("#13161B");
                laneB = Color.Parse("#222730");
                card = Color.Parse("#282F3A");
                raised = Color.Parse("#333C4A");
                hover = Color.Parse("#3F4A5C");
                active = Color.Parse("#4C5A70");
                chrome = Color.Parse("#161A20");
                borderDef = Color.Parse("#303846");
                borderStr = Color.Parse("#455266");
                break;

            case "StudioPlatinum":
                bgApp = Color.Parse("#2A2D35");
                bgSunken = Color.Parse("#1C1E24");
                laneB = Color.Parse("#31353E");
                card = Color.Parse("#383D48");
                raised = Color.Parse("#454B58");
                hover = Color.Parse("#535A6A");
                active = Color.Parse("#61697C");
                chrome = Color.Parse("#22242B");
                borderDef = Color.Parse("#424754");
                borderStr = Color.Parse("#596072");
                break;

            case "SpaceGrey":
            default:
                bgApp = Color.Parse("#404040");      // 64, 64, 64
                bgSunken = Color.Parse("#2E2E2E");   // 46, 46, 46
                laneB = Color.Parse("#363636");
                card = Color.Parse("#383838");
                raised = Color.Parse("#848484");     // 132, 132, 132 (Topbar)
                hover = Color.Parse("#4A4A4A");
                active = Color.Parse("#3D84E8");
                chrome = Color.Parse("#848484");     // 132, 132, 132 (Titlebar)
                borderDef = Color.Parse("#4E4E4E");
                borderStr = Color.Parse("#686868");
                break;
        }

        res["Brush.BgApp"] = new SolidColorBrush(bgApp);
        res["Brush.BgSunken"] = new SolidColorBrush(bgSunken);
        res["Brush.LaneB"] = new SolidColorBrush(laneB);
        res["Brush.SurfaceCard"] = new SolidColorBrush(card);
        res["Brush.SurfaceRaised"] = new SolidColorBrush(raised);
        res["Brush.SurfaceHover"] = new SolidColorBrush(hover);
        res["Brush.SurfaceActive"] = new SolidColorBrush(active);
        res["Brush.ChromeBg"] = new SolidColorBrush(chrome);
        res["Brush.BorderDefault"] = new SolidColorBrush(borderDef);
        res["Brush.BorderStrong"] = new SolidColorBrush(borderStr);

        // 2. Accent Colors
        Color accentCol, accentHov, accentBri;
        switch (themeAccent)
        {
            case "EmberGold":
            case "Gold":
                accentCol = Color.Parse("#C89B3C");
                accentHov = Color.Parse("#DCAD48");
                accentBri = Color.Parse("#F2C35E");
                break;
            case "CrimsonRed":
            case "Crimson":
            case "Red":
                accentCol = Color.Parse("#E03E3E");
                accentHov = Color.Parse("#F04D4D");
                accentBri = Color.Parse("#FF6B6B");
                break;
            case "EmeraldGreen":
            case "Emerald":
            case "Green":
                accentCol = Color.Parse("#2E9652");
                accentHov = Color.Parse("#3DB364");
                accentBri = Color.Parse("#50D27B");
                break;
            case "CyberPurple":
            case "PurpleNeon":
            case "Purple":
                accentCol = Color.Parse("#9333EA");
                accentHov = Color.Parse("#A855F7");
                accentBri = Color.Parse("#C084FC");
                break;
            case "NeonCyan":
            case "CyberCyan":
            case "Cyan":
                accentCol = Color.Parse("#00B4D8");
                accentHov = Color.Parse("#06D6A0");
                accentBri = Color.Parse("#48CAE4");
                break;
            case "Lavender":
                accentCol = Color.Parse("#A78BFA");
                accentHov = Color.Parse("#C4B5FD");
                accentBri = Color.Parse("#D8B4FE");
                break;
            case "StudioBlue":
            case "Blue":
            default:
                accentCol = Color.Parse("#3D84E8");
                accentHov = Color.Parse("#5495F0");
                accentBri = Color.Parse("#6EAAFA");
                break;
        }

        res["Brush.Accent"] = new SolidColorBrush(accentCol);
        res["Brush.AccentHover"] = new SolidColorBrush(accentHov);
        res["Brush.AccentBright"] = new SolidColorBrush(accentBri);
        res["Brush.AccentSubtle"] = new SolidColorBrush(Color.FromArgb(0x35, accentCol.R, accentCol.G, accentCol.B));
        res["Brush.SurfaceSelected"] = new SolidColorBrush(accentCol);

        // 3. Tactile Hardware Button Gradients (Dark hardware buttons: 56,56,56)
        res["Brush.ButtonGrad"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#3E3E3E"), 0.0),
                new GradientStop(Color.Parse("#343434"), 1.0),
            }
        };
        res["Brush.ButtonGradHover"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#4E4E4E"), 0.0),
                new GradientStop(Color.Parse("#424242"), 1.0),
            }
        };
        res["Brush.ButtonGradPressed"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#282828"), 0.0),
                new GradientStop(Color.Parse("#323232"), 1.0),
            }
        };

        // Primary / Active Gradient based on the chosen accent (Logic Blue)
        res["Brush.ButtonGradPrimary"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(accentCol, 0.0),
                new GradientStop(Color.Parse("#2465C0"), 1.0),
            }
        };
        res["Brush.ButtonGradActive"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(accentCol, 0.0),
                new GradientStop(Color.Parse("#2465C0"), 1.0),
            }
        };

        ThemeChanged?.Invoke();
    }

    public static void ApplyLcdTheme(string lcdTheme)
    {
        var app = Avalonia.Application.Current;
        if (app == null) return;

        var res = app.Resources;
        Color primary, ghost, secondary, border, bg;

        switch (lcdTheme)
        {
            case "Amber":
                primary = Color.Parse("#FFB84D"); // Classic Studio Amber
                ghost = Color.Parse("#3A2810");
                secondary = Color.Parse("#A67C28");
                border = Color.Parse("#382810");
                bg = Color.Parse("#0E0B06");
                break;
            case "Green":
                primary = Color.Parse("#34D399"); // Matrix Emerald Green
                ghost = Color.Parse("#0E2A1C");
                secondary = Color.Parse("#059669");
                border = Color.Parse("#0D2E1E");
                bg = Color.Parse("#040E08");
                break;
            case "White":
                primary = Color.Parse("#E2E8F0"); // Ice Platinum White
                ghost = Color.Parse("#252E3E");
                secondary = Color.Parse("#94A3B8");
                border = Color.Parse("#242F42");
                bg = Color.Parse("#0A0E15");
                break;
            case "Red":
                primary = Color.Parse("#F87171"); // Crimson Night Red
                ghost = Color.Parse("#3A1414");
                secondary = Color.Parse("#DC2626");
                border = Color.Parse("#3E1515");
                bg = Color.Parse("#100505");
                break;
            case "Purple":
                primary = Color.Parse("#C084FC"); // Cyber Violet / Neon
                ghost = Color.Parse("#2C1644");
                secondary = Color.Parse("#9333EA");
                border = Color.Parse("#2D1645");
                bg = Color.Parse("#0D0516");
                break;
            case "Gold":
                primary = Color.Parse("#FACC15"); // Solar Gold
                ghost = Color.Parse("#352B0C");
                secondary = Color.Parse("#CA8A04");
                border = Color.Parse("#362B0D");
                bg = Color.Parse("#0F0C04");
                break;
            case "Cyan":
            default:
                primary = Color.Parse("#BED4E8"); // Studio Ice Blue (Hardware OLED - Reference design)
                ghost = Color.Parse("#232E40");
                secondary = Color.Parse("#7E95B2");
                border = Color.Parse("#1F2738");
                bg = Color.Parse("#0C1017");
                break;
        }

        res["Brush.LcdAmber"] = new SolidColorBrush(primary);
        res["Brush.LcdPrimary"] = new SolidColorBrush(primary);
        res["Brush.LcdGhost"] = new SolidColorBrush(ghost);
        res["Brush.LcdMuted"] = new SolidColorBrush(secondary);
        res["Brush.LcdSecondary"] = new SolidColorBrush(secondary);
        res["Brush.LcdBorder"] = new SolidColorBrush(border);
        res["Brush.LcdBg"] = new SolidColorBrush(bg);

        ThemeChanged?.Invoke();
    }
}
