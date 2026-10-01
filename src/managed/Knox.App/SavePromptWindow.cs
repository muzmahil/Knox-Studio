// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Knox.App;

public enum SavePromptResult
{
    Save,
    DontSave,
    Cancel
}

public sealed class SavePromptWindow : KnoxWindow
{
    public SavePromptWindow(string projectName)
    {
        Title = "Knox";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.TryFindResource("Brush.BgApp", out var bg) && bg is IBrush b ? b : Brushes.DarkGray;

        var msg = new TextBlock
        {
            Text = $"Do you want to save the changes made to \"{projectName}\" before closing?",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = KnoxPalette.TextPrimary
        };

        var saveBtn = new Button { Content = "Save", Classes = { "primary" } };
        saveBtn.Click += (_, _) => Close(SavePromptResult.Save);

        var dontSaveBtn = new Button { Content = "Don't Save", Classes = { "ghost" } };
        dontSaveBtn.Click += (_, _) => Close(SavePromptResult.DontSave);

        var cancelBtn = new Button { Content = "Cancel", Classes = { "ghost" } };
        cancelBtn.Click += (_, _) => Close(SavePromptResult.Cancel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelBtn, dontSaveBtn, saveBtn }
        };

        SetBody(new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children = { msg, buttons }
        });
    }
}
