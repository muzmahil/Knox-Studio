using System;
using Avalonia.Interactivity;

namespace Knox.App;

public partial class MainWindow
{
    private MixerWindow? _mixerWindow;

    private void OnMenuMixer(object? sender, EventArgs e) => ToggleMixerWindow();
    private void OnMenuMixer(object? sender, RoutedEventArgs e) => OnMenuMixer(sender, (EventArgs)e);

    private void OnToggleMixerTop(object? sender, RoutedEventArgs e)
    {
        if (DetailPanel.IsVisible && DetailMixerBtn.IsChecked == true)
        {
            OnCloseDetail(null, null!);
            if (MixerTopBtn is not null) MixerTopBtn.IsChecked = false;
        }
        else
        {
            ShowMixerPanel();
            if (MixerTopBtn is not null) MixerTopBtn.IsChecked = true;
        }
    }

    /// <summary>Open the Mixer window, or close it if it's already open (⌘M / View → Mixer).</summary>
    internal void ToggleMixerWindow()
    {
        if (_mixerWindow is not null) { _mixerWindow.Close(); return; }
        if (_mixer is null) return;

        var win = new MixerWindow(this);
        win.EnableMidiLearn(_learn);   // faders/knobs stay MIDI-mappable in the window
        win.WindowClosed += () =>
        {
            if (_mixer is not null) _mixer.IsVisible = false;
            _mixerWindow = null;
            if (MixerTopBtn is not null) MixerTopBtn.IsChecked = DetailPanel.IsVisible && DetailMixerBtn.IsChecked == true;
        };
        _mixerWindow = win;

        _mixer.IsVisible = true;       // so the tick loop updates its meters
        SetHost(win.Host, _mixer);
        _mixer.Refresh();
        win.Show(this);
        if (MixerTopBtn is not null) MixerTopBtn.IsChecked = true;
    }
}
