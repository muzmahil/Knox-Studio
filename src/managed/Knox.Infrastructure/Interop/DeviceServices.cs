// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

namespace Knox.Infrastructure;

/// <summary>IAudioDeviceService over the engine's device enumeration.</summary>
public sealed class AudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioDevice> OutputDevices() => KnoxEngine.AudioOutputDevices();
    public IReadOnlyList<AudioDevice> InputDevices() => KnoxEngine.AudioInputDevices();
}

/// <summary>IMidiDeviceService over the engine's MIDI enumeration.</summary>
public sealed class MidiDeviceService : IMidiDeviceService
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", SetLastError = true)]
    private static extern uint midiOutGetNumDevs();

    [System.Runtime.InteropServices.DllImport("winmm.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint midiOutGetDevCaps(UIntPtr uDeviceID, ref MIDIOUTCAPS lpMidiOutCaps, uint cbMidiOutCaps);

    public IReadOnlyList<MidiDevice> InputDevices() => KnoxEngine.MidiInputDevices();

    public IReadOnlyList<MidiDevice> OutputDevices()
    {
        var list = new List<MidiDevice>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint numDevs = midiOutGetNumDevs();
                for (uint i = 0; i < numDevs; i++)
                {
                    var caps = new MIDIOUTCAPS();
                    if (midiOutGetDevCaps((UIntPtr)i, ref caps, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MIDIOUTCAPS))) == 0)
                    {
                        string name = string.IsNullOrWhiteSpace(caps.szPname) ? $"MIDI Out {i}" : caps.szPname.Trim();
                        list.Add(new MidiDevice(name, name));
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        if (list.Count == 0)
        {
            list.Add(new MidiDevice("Microsoft MIDI Mapper", "Microsoft MIDI Mapper"));
            list.Add(new MidiDevice("Microsoft GS Wavetable Synth", "Microsoft GS Wavetable Synth"));
        }
        return list;
    }

    public void RefreshDevices()
    {
        KnoxEngine.MidiInputDevices();
    }
}

/// <summary>IGamepadService over the engine's pad table. `PadsChanged` is fired
/// by the owning UI tick (MainWindow) after it polls events — pads connect/
/// disconnect events ride the edges, so there is no OS observer to bridge.</summary>
public sealed class GamepadService : IGamepadService
{
    private readonly KnoxEngine _engine;
    public GamepadService(KnoxEngine engine) => _engine = engine;

    public IReadOnlyList<GamepadDevice> Pads() => _engine.Gamepads();

    public event Action? PadsChanged;
    /// <summary>Hook for the UI tick: raise <see cref="PadsChanged"/>.</summary>
    public void RaisePadsChanged() => PadsChanged?.Invoke();

    public event Action<int, string>? Activity;
    /// <summary>Hook for the UI tick: raise <see cref="Activity"/> for a button press.</summary>
    public void RaiseActivity(int pad, string label) => Activity?.Invoke(pad, label);
}
