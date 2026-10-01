// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// M3 plugin-hosting module. THE ONLY place that includes JUCE — the core engine
// stays JUCE-free (see ARCHITECTURE.md § Plugin hosting). Built as a separate static
// library (nota_pluginhost) and whole-archive-linked into libknox_engine so its
// C ABI symbols survive.
//
// M3-0 spike: prove that a JUCE GUI/message loop coexists with the host app's
// run loop (Avalonia's NSApp). We initialise JUCE's GUI subsystem on the calling
// (main) thread WITHOUT running our own dispatch loop — JUCE piggybacks on the
// already-running CFRunLoop. If a plain DocumentWindow appears and stays live,
// the main M3 integration risk is retired.

#include "nota/knox_engine.h"
#include "Device.h"
#include "Instrument.h"
#include "PluginHostBridge.h"
#include "CommandQueue.h"   // SpscRingBuffer: message-thread -> audio-thread note injection

#include <juce_audio_processors/juce_audio_processors.h>
#include <juce_gui_basics/juce_gui_basics.h>

#if JUCE_WINDOWS || defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

#if JUCE_MAC
#import <AppKit/AppKit.h>   // NSEvent monitor for computer-keyboard MIDI (editor windows)
#endif

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <memory>
#include <string>

namespace {

class SpikeWindow : public juce::DocumentWindow {
public:
    SpikeWindow()
        : juce::DocumentWindow("Knox — JUCE spike",
                               juce::Colours::darkgrey,
                               juce::DocumentWindow::allButtons) {
        auto* label = new juce::Label({}, "JUCE window alive alongside Avalonia.\n"
                                          "M3-0 run-loop spike OK.");
        label->setJustificationType(juce::Justification::centred);
        setContentOwned(label, true);
        setUsingNativeTitleBar(true);
        centreWithSize(380, 140);
        setResizable(true, false);
        setVisible(true);
    }

    // Just hide — keep JUCE alive for the app's lifetime.
    void closeButtonPressed() override { setVisible(false); }
};

class NotaPluginHostLookAndFeel : public juce::LookAndFeel_V4 {
public:
    NotaPluginHostLookAndFeel() {
        setColour(juce::PopupMenu::backgroundColourId, juce::Colour(0xff18191d));
        setColour(juce::PopupMenu::textColourId, juce::Colour(0xffdcdfe8));
        setColour(juce::PopupMenu::highlightedBackgroundColourId, juce::Colour(0xff2d3039));
        setColour(juce::PopupMenu::highlightedTextColourId, juce::Colours::white);
        setColour(juce::PopupMenu::headerTextColourId, juce::Colour(0xff8a8e9b));
    }

    juce::Font getPopupMenuFont() override {
        return juce::Font(juce::Font::getDefaultSansSerifFontName(), 12.5f, juce::Font::plain);
    }

    void drawPopupMenuBackground(juce::Graphics& g, int width, int height) override {
        auto r = juce::Rectangle<float>(0.5f, 0.5f, (float)width - 1.0f, (float)height - 1.0f);
        g.setColour(juce::Colour(0xff18191d));
        g.fillRoundedRectangle(r, 5.0f);
        g.setColour(juce::Colour(0xff343740));
        g.drawRoundedRectangle(r, 5.0f, 1.0f);
    }

    void drawPopupMenuItem(juce::Graphics& g, const juce::Rectangle<int>& area,
                           bool isSeparator, bool isActive, bool isHighlighted, bool isTicked,
                           bool hasSubMenu, const juce::String& text, const juce::String& shortcutKeyText,
                           const juce::Drawable* icon, const juce::Colour* textColour) override {
        if (isSeparator) {
            auto r = area.reduced(5, 0);
            g.setColour(juce::Colour(0xff2a2c34));
            g.drawHorizontalLine(r.getCentreY(), (float)r.getX(), (float)r.getRight());
            return;
        }

        auto r = area.toFloat().reduced(3.0f, 1.0f);

        if (isHighlighted && isActive) {
            g.setColour(juce::Colour(0xff2c303c));
            g.fillRoundedRectangle(r, 3.5f);
            g.setColour(juce::Colour(0xffe59a3c));
            g.fillRoundedRectangle(r.getX() + 1.0f, r.getY() + 3.0f, 2.5f, r.getHeight() - 6.0f, 1.0f);
        }

        juce::Colour textCol = isActive ? (isHighlighted ? juce::Colours::white : juce::Colour(0xffdcdfe8))
                                        : juce::Colour(0xff606470);
        if (textColour != nullptr) textCol = *textColour;
        g.setColour(textCol);

        auto font = getPopupMenuFont();
        g.setFont(font);

        auto textArea = area.reduced(18, 0);
        if (isTicked) {
            g.setColour(isHighlighted ? juce::Colour(0xfff0b060) : juce::Colour(0xffe59a3c));
            juce::Path tick;
            float cx = (float)area.getX() + 9.0f;
            float cy = (float)area.getCentreY();
            tick.startNewSubPath(cx - 3.5f, cy);
            tick.lineTo(cx - 0.5f, cy + 3.0f);
            tick.lineTo(cx + 4.5f, cy - 3.5f);
            g.strokePath(tick, juce::PathStrokeType(1.6f, juce::PathStrokeType::curved, juce::PathStrokeType::rounded));
        }

        g.setColour(textCol);
        g.drawFittedText(text, textArea, juce::Justification::centredLeft, 1);

        if (shortcutKeyText.isNotEmpty()) {
            g.setColour(juce::Colour(0xff747886));
            g.setFont(font.withHeight(11.0f));
            g.drawFittedText(shortcutKeyText, textArea, juce::Justification::centredRight, 1);
        }

        if (hasSubMenu) {
            g.setColour(isHighlighted ? juce::Colours::white : juce::Colour(0xff8a8e9b));
            juce::Path arrow;
            float ax = (float)area.getRight() - 10.0f;
            float ay = (float)area.getCentreY();
            arrow.addTriangle(ax - 3.0f, ay - 3.5f, ax + 1.5f, ay, ax - 3.0f, ay + 3.5f);
            g.fillPath(arrow);
        }
    }

    void drawPopupMenuSectionHeader(juce::Graphics& g, const juce::Rectangle<int>& area,
                                    const juce::String& sectionName) override {
        g.setColour(juce::Colour(0xff8a8e9b));
        g.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 11.0f, juce::Font::bold));
        g.drawFittedText(sectionName, area.reduced(10, 0), juce::Justification::centredLeft, 1);
    }
};

// Leaked on purpose: JUCE stays initialised for the whole process (init once).
juce::ScopedJuceInitialiser_GUI* g_juceGui = nullptr;
NotaPluginHostLookAndFeel*       g_notaLookAndFeel = nullptr;
std::unique_ptr<SpikeWindow>     g_window;

void ensureJuceGuiInit() {
    if (g_juceGui == nullptr) {
        g_juceGui = new juce::ScopedJuceInitialiser_GUI();
        g_notaLookAndFeel = new NotaPluginHostLookAndFeel();
        juce::LookAndFeel::setDefaultLookAndFeel(g_notaLookAndFeel);
    }
}

} // namespace

extern "C" NOTA_API NotaResult nota_pluginhost_open_test_window(void) {
    if (g_juceGui == nullptr)
        g_juceGui = new juce::ScopedJuceInitialiser_GUI();

    if (g_window == nullptr)
        g_window = std::make_unique<SpikeWindow>();
    else
        g_window->setVisible(true);

    return NOTA_OK;
}

// ---- Plugin scanning & catalog (M3-1) --------------------------------------

namespace {

// AU first (macOS priority), then VST3, then VST2 (VST) — see ARCHITECTURE.md § Plugin hosting.
const char* const kFormatOrder[] = {"AudioUnit", "VST3", "VST"};
constexpr int kScanTimeoutMs = 30000;

juce::AudioPluginFormatManager& formatManager() {
    // AudioPluginFormatManager is non-copyable; init the static in place.
    static juce::AudioPluginFormatManager fm;
    static const bool once = [] { fm.addDefaultFormats(); return true; }();
    juce::ignoreUnused(once);
    return fm;
}

juce::KnownPluginList& catalog() {
    static juce::KnownPluginList list;
    return list;
}

// Per-user Knox Studio data dir (mirrors KnoxPaths.DataDir / cfgfile::dataDir):
//   macOS   -> ~/Library/Application Support/Knox Studio
//   Windows -> %APPDATA%\Knox Studio
//   Linux   -> ~/.config/knox-studio
juce::File notaSupportDir() {
#if JUCE_MAC
    auto dir = juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
                   .getChildFile("Application Support")
                   .getChildFile("Knox Studio");
#else
    // On Windows userApplicationDataDirectory resolves to %APPDATA%; on Linux to
    // ~/.config (matching cfgfile::dataDir's XDG default).
    auto dir = juce::File::getSpecialLocation(juce::File::userApplicationDataDirectory)
                   .getChildFile("Knox Studio");
#endif
    dir.createDirectory();
    return dir;
}

juce::File catalogFile()   { return notaSupportDir().getChildFile("plugins.xml"); }
juce::File scanPathsFile() { return notaSupportDir().getChildFile("scanpaths.txt"); }

// User-added search directories (one per line in scanpaths.txt).
juce::StringArray& extraScanPaths() {
    static juce::StringArray paths;
    return paths;
}

void saveScanPaths() {
    scanPathsFile().replaceWithText(extraScanPaths().joinIntoString("\n"));
}

void loadScanPathsOnce() {
    static bool loaded = false;
    if (loaded) return;
    loaded = true;

    auto f = scanPathsFile();
    if (f.existsAsFile()) {
        auto lines = juce::StringArray::fromLines(f.loadFileAsString());
        for (const auto& line : lines) {
            auto trimmed = line.trim();
            if (trimmed.isNotEmpty())
                extraScanPaths().addIfNotAlreadyThere(trimmed);
        }
    }

    // Seed standard VST and AudioUnit directories if missing or empty
#if JUCE_WINDOWS
    static const char* const kDefaultWinPaths[] = {
        "C:\\Program Files\\Common Files\\VST3",
        "C:\\Program Files (x86)\\Common Files\\VST3",
        "C:\\Program Files\\VSTPlugins",
        "C:\\Program Files\\Steinberg\\VSTPlugins",
        "C:\\Program Files\\Common Files\\VST2",
        "C:\\vstsdk2.4"
    };
    for (const char* p : kDefaultWinPaths) {
        juce::File dir(p);
        if (dir.isDirectory()) {
            if (!extraScanPaths().contains(p))
                extraScanPaths().add(p);
        }
    }
    if (extraScanPaths().isEmpty()) {
        for (const char* p : kDefaultWinPaths)
            extraScanPaths().addIfNotAlreadyThere(p);
    }
#elif JUCE_MAC
    static const char* const kDefaultMacPaths[] = {
        "/Library/Audio/Plug-Ins/VST3",
        "/Library/Audio/Plug-Ins/VST",
        "/Library/Audio/Plug-Ins/Components",
        "~/Library/Audio/Plug-Ins/VST3",
        "~/Library/Audio/Plug-Ins/VST",
        "~/Library/Audio/Plug-Ins/Components"
    };
    for (const char* p : kDefaultMacPaths) {
        extraScanPaths().addIfNotAlreadyThere(p);
    }
#endif
    if (!f.existsAsFile()) {
        saveScanPaths();
    }
}

void loadCatalogOnce() {
    static bool loaded = false;
    if (loaded) return;
    loaded = true;
    if (auto xml = juce::parseXML(catalogFile()))
        catalog().recreateFromXml(*xml);
}

void saveCatalog() {
    if (auto xml = catalog().createXml())
        xml->writeTo(catalogFile());
}

juce::AudioPluginFormat* findFormat(const juce::String& name) {
    for (auto* f : formatManager().getFormats())
        if (f->getName() == name)
            return f;
    return nullptr;
}

static std::atomic<bool> g_skipCurrentScan{false};
static std::atomic<bool> g_cancelScan{false};

// Run the worker for one plugin id; parse its PluginDescriptions into the
// catalog. Any failure (bad start, timeout, crash, garbage output) is swallowed
// so the scan moves on to the next plugin.
void scanOne(const juce::String& workerPath, const juce::String& formatName,
             const juce::String& fileOrId, bool validate) {
    g_skipCurrentScan.store(false);
    juce::ChildProcess proc;
    juce::StringArray cmd{workerPath, formatName, fileOrId};
    if (!validate)
        cmd.add("--no-validate");
    if (!proc.start(cmd, juce::ChildProcess::wantStdOut))
        return;

    juce::MemoryOutputStream captured;
    char buf[8192];
    const auto deadline = juce::Time::getMillisecondCounter() + (juce::uint32)kScanTimeoutMs;
    while (proc.isRunning()) {
        if (g_skipCurrentScan.load() || g_cancelScan.load()) {
            proc.kill();
            return;
        }
        const auto n = proc.readProcessOutput(buf, (int)sizeof(buf));
        if (n > 0) captured.write(buf, (size_t)n);
        if (juce::Time::getMillisecondCounter() > deadline) { proc.kill(); return; }
        if (n <= 0) juce::Thread::sleep(5);
    }
    if (g_skipCurrentScan.load() || g_cancelScan.load()) return;
    for (;;) {
        const auto n = proc.readProcessOutput(buf, (int)sizeof(buf));
        if (n <= 0) break;
        captured.write(buf, (size_t)n);
    }

    // The worker brackets its XML with sentinels because some plugins print their
    // own log lines to stdout while loading (e.g. Maschine's "[maschine_lib_logger]
    // … Logfile created"), which would corrupt a raw parse and silently drop an
    // otherwise-valid plugin. Slice out the bracketed text; fall back to the XML
    // prolog/root if the markers are missing (older worker / clean output).
    const juce::String out = captured.toString();
    juce::String xmlText;
    const int b = out.indexOf("<<<NOTA_PLUGINS_BEGIN>>>");
    const int e = out.lastIndexOf("<<<NOTA_PLUGINS_END>>>");
    if (b >= 0 && e > b) {
        xmlText = out.substring(b + (int)juce::String("<<<NOTA_PLUGINS_BEGIN>>>").length(), e);
    } else {
        int x = out.indexOf("<?xml");
        if (x < 0) x = out.indexOf("<plugins");
        if (x >= 0) xmlText = out.substring(x);
    }
    auto xml = juce::parseXML(xmlText);
    if (xml == nullptr) return;
    for (auto* child : xml->getChildIterator()) {
        juce::PluginDescription desc;
        if (desc.loadFromXml(*child))
            catalog().addType(desc);
    }
}

} // namespace

extern "C" NOTA_API int32_t nota_pluginhost_scan_with_progress(const char* worker_path, int32_t validate, NotaPluginScanCallback callback) {
    if (worker_path == nullptr) return -1;
    g_cancelScan.store(false);
    g_skipCurrentScan.store(false);
    loadCatalogOnce();
    loadScanPathsOnce();

    const juce::String workerPath{juce::CharPointer_UTF8(worker_path)};
    if (!juce::File(workerPath).existsAsFile()) return -2;

    struct Candidate {
        juce::String formatName;
        juce::String id;
    };
    std::vector<Candidate> candidates;

    for (const char* formatName : kFormatOrder) {
        auto* format = findFormat(formatName);
        if (format == nullptr) continue;

        juce::FileSearchPath search = format->getDefaultLocationsToSearch();
        for (const auto& p : extraScanPaths())
            search.addIfNotAlreadyThere(juce::File(p));

        const auto ids = format->searchPathsForPlugins(
            search, /*recursive*/ true, /*allowAsync*/ false);

        for (const auto& id : ids) {
            candidates.push_back({formatName, id});
        }
    }

    int32_t total = (int32_t)candidates.size();
    for (int32_t i = 0; i < total; ++i) {
        if (g_cancelScan.load()) break;

        const auto& c = candidates[(size_t)i];
        if (callback != nullptr) {
            int32_t action = callback(c.formatName.toRawUTF8(), c.id.toRawUTF8(), i + 1, total);
            if (action == 2) {
                g_cancelScan.store(true);
                break;
            } else if (action == 1) {
                continue;
            }
        }

        bool known = false;
        for (const auto& t : catalog().getTypes()) {
            if (t.fileOrIdentifier == c.id) { known = true; break; }
        }
        if (!known) {
            scanOne(workerPath, c.formatName, c.id, validate != 0);
        }
    }

    saveCatalog();
    return catalog().getNumTypes();
}

extern "C" NOTA_API int32_t nota_pluginhost_scan_ex(const char* worker_path, int32_t validate) {
    return nota_pluginhost_scan_with_progress(worker_path, validate, nullptr);
}

extern "C" NOTA_API int32_t nota_pluginhost_scan(const char* worker_path) {
    return nota_pluginhost_scan_ex(worker_path, 1);
}

extern "C" NOTA_API void nota_pluginhost_skip_current_scan(void) {
    g_skipCurrentScan.store(true);
}

extern "C" NOTA_API void nota_pluginhost_cancel_scan(void) {
    g_cancelScan.store(true);
}

extern "C" NOTA_API int32_t nota_pluginhost_plugin_count(void) {
    loadCatalogOnce();
    return catalog().getNumTypes();
}

extern "C" NOTA_API const char* nota_pluginhost_plugin_desc(int32_t index) {
    loadCatalogOnce();
    const auto types = catalog().getTypes();
    if (index < 0 || index >= types.size())
        return nullptr;

    const auto& d = types.getReference(index);
    static std::string line; // owned by the engine, valid until next call
    line = (d.name + " | " + d.pluginFormatName + " | "
            + (d.isInstrument ? "inst" : "fx") + " | "
            + (d.manufacturerName.isEmpty() ? juce::String("?") : d.manufacturerName))
               .toStdString();
    return line.c_str();
}

extern "C" NOTA_API const char* nota_pluginhost_plugin_id(int32_t index) {
    loadCatalogOnce();
    const auto types = catalog().getTypes();
    if (index < 0 || index >= types.size())
        return nullptr;
    static std::string id; // owned by the engine, valid until next call
    id = types.getReference(index).createIdentifierString().toStdString();
    return id.c_str();
}

extern "C" NOTA_API const char* nota_pluginhost_plugin_file(int32_t index) {
    loadCatalogOnce();
    const auto types = catalog().getTypes();
    if (index < 0 || index >= types.size())
        return nullptr;
    static std::string file;
    file = types.getReference(index).fileOrIdentifier.toStdString();
    return file.c_str();
}

extern "C" NOTA_API const char* nota_pluginhost_plugin_format(int32_t index) {
    loadCatalogOnce();
    const auto types = catalog().getTypes();
    if (index < 0 || index >= types.size())
        return nullptr;
    static std::string fmt;
    fmt = types.getReference(index).pluginFormatName.toStdString();
    return fmt.c_str();
}

extern "C" NOTA_API const char* nota_pluginhost_plugin_name(int32_t index) {
    loadCatalogOnce();
    const auto types = catalog().getTypes();
    if (index < 0 || index >= types.size())
        return nullptr;
    static std::string name;
    name = types.getReference(index).name.toStdString();
    return name.c_str();
}

extern "C" NOTA_API int32_t nota_pluginhost_index_of_id(const char* identifier) {
    if (identifier == nullptr) return -1;
    loadCatalogOnce();
    const juce::String want{juce::CharPointer_UTF8(identifier)};
    const auto types = catalog().getTypes();
    // 1. Exact match on createIdentifierString
    for (int i = 0; i < types.size(); ++i) {
        if (types.getReference(i).createIdentifierString() == want) return i;
    }
    // 2. Match on fileOrIdentifier
    for (int i = 0; i < types.size(); ++i) {
        if (types.getReference(i).fileOrIdentifier == want) return i;
    }
    // 3. Match on plugin name
    for (int i = 0; i < types.size(); ++i) {
        if (types.getReference(i).name.equalsIgnoreCase(want)) return i;
    }
    return -1;
}

extern "C" NOTA_API NotaResult nota_pluginhost_add_scan_path(const char* dir) {
    if (dir == nullptr) return NOTA_ERR_INVALID_ARG;
    loadScanPathsOnce();
    const juce::String d{juce::CharPointer_UTF8(dir)};
    if (d.isNotEmpty() && !extraScanPaths().contains(d)) {
        extraScanPaths().add(d);
        saveScanPaths();
    }
    return NOTA_OK;
}

extern "C" NOTA_API NotaResult nota_pluginhost_remove_scan_path(int32_t index) {
    loadScanPathsOnce();
    if (index < 0 || index >= extraScanPaths().size())
        return NOTA_ERR_INVALID_ARG;
    extraScanPaths().remove(index);
    saveScanPaths();
    return NOTA_OK;
}

extern "C" NOTA_API int32_t nota_pluginhost_scan_path_count(void) {
    loadScanPathsOnce();
    return extraScanPaths().size();
}

extern "C" NOTA_API const char* nota_pluginhost_scan_path(int32_t index) {
    loadScanPathsOnce();
    if (index < 0 || index >= extraScanPaths().size())
        return nullptr;
    static std::string path; // owned by the engine, valid until next call
    path = extraScanPaths()[index].toStdString();
    return path.c_str();
}

// ---- Hosted-plugin adapters (M3-3) -----------------------------------------
// PluginInstrument/PluginEffect wrap a juce::AudioPluginInstance behind the
// core JUCE-free interfaces (Instrument/Device). All audio-thread methods are
// allocation-free after construction (buffers pre-sized). MIDI/DSP state is
// audio-thread-owned; prepare happens on the message thread before publish.

namespace {

constexpr int kMaxChans = 64;

// Message-thread producer -> audio-thread consumer. The editor window's computer
// keyboard pushes note events here; the plugin adapter drains them in its audio
// callback (keeps the plugin's own MidiBuffer audio-thread-only — no data race).
struct GuiNote { bool on; int32_t pitch; float velocity; };
using GuiNoteQueue = nota::SpscRingBuffer<GuiNote, 256>;

static nota::PluginHostNoteSink g_hostNoteSink = nullptr;
static std::atomic<int32_t>     g_typingOctave{3}; // 1..5; Default is 3 (Q = C4 = 60)
static std::atomic<bool>        g_typingActive{false};

class PluginEditorWindow;
#if JUCE_WINDOWS
static HHOOK                            g_winKeyHook = nullptr;
static std::vector<PluginEditorWindow*> g_activeEditorWindows;
static LRESULT CALLBACK WindowsPluginKeyHook(int nCode, WPARAM wParam, LPARAM lParam);

// Virtual typing keyboard mapping (offsets from Q = 0)
static int winKeyToPitchOffset(int vk) {
    switch (vk) {
        // Lower row (offsets relative to Q = 0)
        case 'Z': return -12;
        case 'S': return -11;
        case 'X': return -10;
        case 'D': return -9;
        case 'C': return -8;
        case 'V': return -7;
        case 'G': return -6;
        case 'B': return -5;
        case 'H': return -4;
        case 'N': return -3;
        case 'J': return -2;
        case 'M': return -1;
        case VK_OEM_COMMA: return 0;
        case 'L': return 1;
        case VK_OEM_PERIOD: return 2;
        case VK_OEM_1: return 3; // semicolon
        case VK_OEM_2: return 4; // slash

        // Upper row (offsets relative to Q = 0)
        case 'Q': return 0;
        case '2': case VK_NUMPAD2: return 1;
        case 'W': return 2;
        case '3': case VK_NUMPAD3: return 3;
        case 'E': return 4;
        case 'R': return 5;
        case '5': case VK_NUMPAD5: return 6;
        case 'T': return 7;
        case '6': case VK_NUMPAD6: return 8;
        case 'Y': return 9;
        case '7': case VK_NUMPAD7: return 10;
        case 'U': return 11;
        case 'I': return 12;
        case '9': case VK_NUMPAD9: return 13;
        case 'O': return 14;
        case '0': case VK_NUMPAD0: return 15;
        case 'P': return 16;
        case VK_OEM_4: return 17; // [
        case VK_OEM_PLUS: case VK_ADD: return 18; // = or +
        case VK_OEM_6: return 19; // ]
        default: return -999;
    }
}
#endif

#if JUCE_MAC
// macOS virtual keycode mapping (FL Studio 2-octave layout, offsets from Q = 0)
static int macKeycodeToPitchOffset(unsigned short kc) {
    switch (kc) {
        // Lower row
        case 0x06: return -12; // Z
        case 0x01: return -11; // S
        case 0x07: return -10; // X
        case 0x02: return -9;  // D
        case 0x08: return -8;  // C
        case 0x09: return -7;  // V
        case 0x05: return -6;  // G
        case 0x0B: return -5;  // B
        case 0x04: return -4;  // H
        case 0x2D: return -3;  // N
        case 0x26: return -2;  // J
        case 0x2E: return -1;  // M
        case 0x2B: return 0;   // ,
        case 0x25: return 1;   // L
        case 0x2F: return 2;   // .
        case 0x29: return 3;   // ;
        case 0x2C: return 4;   // /

        // Upper row
        case 0x0C: return 0;   // Q
        case 0x13: return 1;   // 2
        case 0x0D: return 2;   // W
        case 0x14: return 3;   // 3
        case 0x0E: return 4;   // E
        case 0x0F: return 5;   // R
        case 0x17: return 6;   // 5
        case 0x11: return 7;   // T
        case 0x16: return 8;   // 6
        case 0x10: return 9;   // Y
        case 0x1A: return 10;  // 7
        case 0x20: return 11;  // U
        case 0x22: return 12;  // I
        case 0x19: return 13;  // 9
        case 0x1F: return 14;  // O
        case 0x1D: return 15;  // 0
        case 0x23: return 16;  // P
        case 0x21: return 17;  // [
        case 0x18: return 18;  // =
        case 0x1E: return 19;  // ]
        default:   return -999;
    }
}
#endif

// Custom Apple macOS close button (DAW / macOS style: crisp red circle with cross)
class AppleCloseButton : public juce::Button {
public:
    AppleCloseButton() : juce::Button("Close") {
        setTooltip("Close Plugin Window");
    }

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat();
        float size = 12.0f;
        auto dot = r.withSizeKeepingCentre(size, size);

        juce::Colour baseColour = shouldDrawButtonAsDown ? juce::Colour(0xffbf3f38)
                                : (shouldDrawButtonAsHighlighted ? juce::Colour(0xffff6b63)
                                                                 : juce::Colour(0xffff5f56));
        g.setColour(baseColour);
        g.fillEllipse(dot);

        g.setColour(juce::Colour(0x60000000));
        g.drawEllipse(dot, 0.9f);

        g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xd04d0000) : juce::Colour(0x994d0000));
        float cross = 4.0f;
        auto cx = dot.getCentreX();
        auto cy = dot.getCentreY();
        g.drawLine(cx - cross * 0.5f, cy - cross * 0.5f, cx + cross * 0.5f, cy + cross * 0.5f, 1.2f);
        g.drawLine(cx + cross * 0.5f, cy - cross * 0.5f, cx - cross * 0.5f, cy + cross * 0.5f, 1.2f);
    }
};

// Minimal Header Collapse/Expand Toggle Button (with vector chevron indicator)
class CollapseToggleButton : public juce::Button {
public:
    CollapseToggleButton() : juce::Button("Collapse") {
        setTooltip("Collapse Plugin View");
    }

    void setCollapsed(bool c) {
        collapsed_ = c;
        setTooltip(collapsed_ ? "Expand Plugin View" : "Collapse Plugin View");
        repaint();
    }

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat().reduced(1.0f);
        auto cx = r.getCentreX();
        auto cy = r.getCentreY();

        juce::Colour bgCol = shouldDrawButtonAsDown ? juce::Colour(0x30ffffff)
                           : (shouldDrawButtonAsHighlighted ? juce::Colour(0x20ffffff) : juce::Colour(0x10ffffff));
        g.setColour(bgCol);
        g.fillRoundedRectangle(r, 3.0f);

        juce::Colour iconCol = shouldDrawButtonAsHighlighted ? juce::Colours::white : juce::Colour(0xff9ea2ad);
        g.setColour(iconCol);

        if (collapsed_) {
            // Draw Expand Icon (downward triangle chevron)
            juce::Path p;
            p.addTriangle(cx - 4.5f, cy - 2.0f, cx + 4.5f, cy - 2.0f, cx, cy + 3.5f);
            g.fillPath(p);
        } else {
            // Draw Collapse Icon (upward triangle chevron)
            juce::Path p;
            p.addTriangle(cx - 4.5f, cy + 2.0f, cx + 4.5f, cy + 2.0f, cx, cy - 3.5f);
            g.fillPath(p);
        }
    }

private:
    bool collapsed_ = false;
};

// Logic Pro Power / Bypass Button (large blue glowing circular power icon)
class LogicPowerButton : public juce::Button {
public:
    LogicPowerButton() : juce::Button("Power") {
        setClickingTogglesState(true);
        setToggleState(true, juce::dontSendNotification);
        setTooltip("Plugin Bypass / Power");
    }

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat().reduced(1.0f);
        const bool active = getToggleState();

        if (shouldDrawButtonAsDown) {
            g.setColour(juce::Colour(0x20ffffff));
            g.fillEllipse(r);
        } else if (shouldDrawButtonAsHighlighted) {
            g.setColour(juce::Colour(0x10ffffff));
            g.fillEllipse(r);
        }

        auto center = r.getCentre();
        float radius = 10.5f;

        if (active) {
            // Soft neon-blue glow around power icon
            g.setColour(juce::Colour(0x384aa0ff));
            juce::Path glowArc;
            glowArc.addCentredArc(center.x, center.y + 1.0f, radius + 2.0f, radius + 2.0f, 0.0f, 0.65f, 5.63f, true);
            g.strokePath(glowArc, juce::PathStrokeType(4.0f, juce::PathStrokeType::curved, juce::PathStrokeType::rounded));
        }

        juce::Colour iconCol = active ? (shouldDrawButtonAsHighlighted ? juce::Colour(0xff70c0ff) : juce::Colour(0xff3c96fe))
                                      : (shouldDrawButtonAsHighlighted ? juce::Colour(0xffadb2be) : juce::Colour(0xff555866));
        g.setColour(iconCol);

        juce::Path arc;
        arc.addCentredArc(center.x, center.y + 1.0f, radius, radius, 0.0f, 0.65f, 5.63f, true);
        g.strokePath(arc, juce::PathStrokeType(2.4f, juce::PathStrokeType::curved, juce::PathStrokeType::rounded));

        g.drawLine(center.x, center.y - 10.5f, center.x, center.y - 1.0f, 2.4f);
    }
};

// Preset Dropdown Capsule (vector dropdown arrow)
class LogicPresetDropdownButton : public juce::Button {
public:
    LogicPresetDropdownButton(juce::AudioPluginInstance& plugin)
        : juce::Button("PresetDropdown"), plugin_(plugin) {
        updateText();
        onClick = [this] { showPresetMenu(); };
    }

    void updateText() {
        int cur = plugin_.getCurrentProgram();
        int num = plugin_.getNumPrograms();
        juce::String name = plugin_.getName().isNotEmpty() ? plugin_.getName() : "Manual";
        if (num > 0 && cur >= 0 && cur < num) {
            juce::String pName = plugin_.getProgramName(cur);
            if (pName.isNotEmpty()) name = pName;
            else name = "Preset " + juce::String(cur + 1);
        } else if (num > 0 && cur >= 0) {
            name = "Preset " + juce::String(cur + 1);
        }
        text_ = name;
        repaint();
    }

    void showPresetMenu() {
        int num = plugin_.getNumPrograms();
        juce::PopupMenu menu;
        if (num > 0) {
            int cur = plugin_.getCurrentProgram();
            for (int i = 0; i < num; ++i) {
                juce::String pName = plugin_.getProgramName(i);
                if (pName.isEmpty()) pName = "Preset " + juce::String(i + 1);
                menu.addItem(i + 1, pName, true, i == cur);
            }
        } else {
            menu.addItem(1, "Manual (Internal Presets)", false);
            menu.addSeparator();
            menu.addItem(2, "Default Setting", true);
        }
        menu.showMenuAsync(juce::PopupMenu::Options().withTargetComponent(this),
            [this, num](int result) {
                if (num > 0 && result > 0) {
                    plugin_.setCurrentProgram(result - 1);
                    updateText();
                }
            });
    }

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat().reduced(0.5f);
        g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xff22242b) : juce::Colour(0xff18191d));
        g.fillRoundedRectangle(r, 3.5f);
        g.setColour(juce::Colour(0xff2e3038));
        g.drawRoundedRectangle(r, 3.5f, 0.8f);

        g.setColour(shouldDrawButtonAsHighlighted ? juce::Colours::white : juce::Colour(0xffdcdfe8));
        g.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 12.0f, juce::Font::bold));
        g.drawFittedText(text_, getLocalBounds().reduced(8, 0).withTrimmedRight(18), juce::Justification::centredLeft, 1);

        // Vector down arrow
        g.setColour(juce::Colour(0xff707482));
        float ax = (float)getWidth() - 11.0f;
        float ay = (float)getHeight() * 0.5f;
        juce::Path arrow;
        arrow.addTriangle(ax - 3.5f, ay - 2.0f, ax + 3.5f, ay - 2.0f, ax, ay + 2.5f);
        g.fillPath(arrow);
    }

private:
    juce::AudioPluginInstance& plugin_;
    juce::String text_;
};

// Compact Action Button (Compare, Copy, Paste)
class LogicActionButton : public juce::Button {
public:
    LogicActionButton(const juce::String& text, bool isCompare = false)
        : juce::Button("ActionBtn"), text_(text), isCompare_(isCompare) {}

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat().reduced(0.5f);
        const bool active = isCompare_ && getToggleState();

        if (active) {
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xff245692) : juce::Colour(0xff1c4474));
            g.fillRoundedRectangle(r, 3.0f);
            g.setColour(juce::Colour(0xff3d84e8));
            g.drawRoundedRectangle(r, 3.0f, 0.9f);
        } else {
            if (shouldDrawButtonAsDown)
                g.setColour(juce::Colour(0xff282a32));
            else if (shouldDrawButtonAsHighlighted)
                g.setColour(juce::Colour(0xff22242a));
            else
                g.setColour(juce::Colour(0xff18191d));

            g.fillRoundedRectangle(r, 3.0f);
            g.setColour(juce::Colour(0xff2c2e35));
            g.drawRoundedRectangle(r, 3.0f, 0.8f);
        }

        if (!isEnabled()) {
            g.setColour(juce::Colour(0xff555864));
        } else if (active) {
            g.setColour(juce::Colours::white);
        } else {
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colours::white : juce::Colour(0xffb8bcc8));
        }

        g.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 11.0f, active ? juce::Font::bold : juce::Font::plain));
        g.drawFittedText(text_, getLocalBounds().reduced(2, 0), juce::Justification::centred, 1);
    }

private:
    juce::String text_;
    bool isCompare_;
};

// Dual Arrow Buttons [ ◀ | ▶ ] with crisp vector triangles
class LogicArrowsButton : public juce::Component {
public:
    std::function<void()> onPrev;
    std::function<void()> onNext;

    LogicArrowsButton() {
        prevBtn_.onClick = [this] { if (onPrev) onPrev(); };
        nextBtn_.onClick = [this] { if (onNext) onNext(); };
        addAndMakeVisible(prevBtn_);
        addAndMakeVisible(nextBtn_);
    }

    void paint(juce::Graphics& g) override {
        auto r = getLocalBounds().toFloat().reduced(0.5f);
        g.setColour(juce::Colour(0xff18191d));
        g.fillRoundedRectangle(r, 3.0f);
        g.setColour(juce::Colour(0xff2c2e35));
        g.drawRoundedRectangle(r, 3.0f, 0.8f);

        g.setColour(juce::Colour(0xff282a30));
        g.drawVerticalLine(getWidth() / 2, 2.0f, (float)getHeight() - 2.0f);
    }

    void resized() override {
        int mid = getWidth() / 2;
        prevBtn_.setBounds(0, 0, mid, getHeight());
        nextBtn_.setBounds(mid, 0, getWidth() - mid, getHeight());
    }

private:
    class ArrowSubBtn : public juce::Button {
    public:
        ArrowSubBtn(bool isLeft) : juce::Button("Arrow"), isLeft_(isLeft) {}
        void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
            if (shouldDrawButtonAsDown) {
                g.setColour(juce::Colour(0x28ffffff));
                g.fillRect(getLocalBounds());
            } else if (shouldDrawButtonAsHighlighted) {
                g.setColour(juce::Colour(0x14ffffff));
                g.fillRect(getLocalBounds());
            }
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colours::white : juce::Colour(0xffc2c5cc));

            float cx = (float)getWidth() * 0.5f;
            float cy = (float)getHeight() * 0.5f;
            juce::Path p;
            if (isLeft_) {
                p.addTriangle(cx + 2.5f, cy - 3.5f, cx + 2.5f, cy + 3.5f, cx - 2.5f, cy);
            } else {
                p.addTriangle(cx - 2.5f, cy - 3.5f, cx - 2.5f, cy + 3.5f, cx + 2.5f, cy);
            }
            g.fillPath(p);
        }
    private:
        bool isLeft_;
    };

    ArrowSubBtn prevBtn_{true};
    ArrowSubBtn nextBtn_{false};
};

// Dropdown Button with Label (Side Chain, View) with vector double up/down arrows
class LogicDropdownWithLabel : public juce::Component {
public:
    LogicDropdownWithLabel(const juce::String& labelText, const juce::String& initialVal, std::function<void()> onClickMenu = nullptr)
        : labelText_(labelText), valueText_(initialVal), onClickMenu_(onClickMenu) {
        btn_.onClick = [this] { if (onClickMenu_) onClickMenu_(); };
        addAndMakeVisible(btn_);
    }

    void setValue(const juce::String& v) { valueText_ = v; btn_.repaint(); }

    void paint(juce::Graphics& g) override {
        if (labelW_ > 0) {
            g.setColour(juce::Colour(0xff8a8d96));
            g.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 11.0f, juce::Font::plain));
            g.drawFittedText(labelText_, 0, 0, labelW_ - 4, getHeight(), juce::Justification::centredRight, 1);
        }
    }

    void resized() override {
        int naturalLabelW = (int)juce::Font(juce::Font::getDefaultSansSerifFontName(), 11.0f, juce::Font::plain).getStringWidth(labelText_) + 6;
        if (getWidth() < naturalLabelW + 45) {
            labelW_ = 0;
        } else {
            labelW_ = naturalLabelW;
        }
        btn_.setBounds(labelW_, 0, getWidth() - labelW_, getHeight());
    }

private:
    class DropdownBtn : public juce::Button {
    public:
        DropdownBtn(LogicDropdownWithLabel& owner) : juce::Button("DD"), owner_(owner) {}
        void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
            auto r = getLocalBounds().toFloat().reduced(0.5f);
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xff22242b) : juce::Colour(0xff18191d));
            g.fillRoundedRectangle(r, 3.0f);
            g.setColour(juce::Colour(0xff2c2e35));
            g.drawRoundedRectangle(r, 3.0f, 0.8f);

            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colours::white : juce::Colour(0xffdcdfe8));
            g.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 11.0f, juce::Font::plain));
            g.drawFittedText(owner_.valueText_, getLocalBounds().reduced(5, 0).withTrimmedRight(14), juce::Justification::centredLeft, 1);

            // Vector up/down double triangles
            g.setColour(juce::Colour(0xff707482));
            float ax = (float)getWidth() - 8.0f;
            float cy = (float)getHeight() * 0.5f;
            juce::Path pUp, pDown;
            pUp.addTriangle(ax - 2.5f, cy - 1.5f, ax + 2.5f, cy - 1.5f, ax, cy - 4.5f);
            pDown.addTriangle(ax - 2.5f, cy + 1.5f, ax + 2.5f, cy + 1.5f, ax, cy + 4.5f);
            g.fillPath(pUp);
            g.fillPath(pDown);
        }
    private:
        LogicDropdownWithLabel& owner_;
    };

    juce::String labelText_;
    juce::String valueText_;
    int labelW_ = 60;
    std::function<void()> onClickMenu_;
    DropdownBtn btn_{*this};
};

// Purple Link Toggle Button (with vector chain link icon)
class LogicLinkButton : public juce::Button {
public:
    LogicLinkButton() : juce::Button("Link") {
        setClickingTogglesState(true);
        setToggleState(true, juce::dontSendNotification);
        setTooltip("Link Windows");
    }

    void paintButton(juce::Graphics& g, bool shouldDrawButtonAsHighlighted, bool shouldDrawButtonAsDown) override {
        auto r = getLocalBounds().toFloat().reduced(0.5f);
        const bool active = getToggleState();

        if (active) {
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xff60287c) : juce::Colour(0xff4c1e62));
            g.fillRoundedRectangle(r, 3.0f);
            g.setColour(juce::Colour(0xff8c3dae));
            g.drawRoundedRectangle(r, 3.0f, 0.9f);
        } else {
            g.setColour(shouldDrawButtonAsHighlighted ? juce::Colour(0xff22242a) : juce::Colour(0xff18191d));
            g.fillRoundedRectangle(r, 3.0f);
            g.setColour(juce::Colour(0xff2c2e35));
            g.drawRoundedRectangle(r, 3.0f, 0.8f);
        }

        g.setColour(active ? juce::Colours::white : (shouldDrawButtonAsHighlighted ? juce::Colour(0xffdcdfe8) : juce::Colour(0xff787c88)));

        // Draw crisp vector chain link
        float cx = r.getCentreX();
        float cy = r.getCentreY();
        juce::Path linkPath;
        linkPath.addRoundedRectangle(cx - 5.0f, cy - 2.5f, 6.0f, 5.0f, 2.0f);
        linkPath.addRoundedRectangle(cx - 1.0f, cy - 2.5f, 6.0f, 5.0f, 2.0f);
        g.strokePath(linkPath, juce::PathStrokeType(1.2f));
    }
};

// Full Logic Pro Plugin Window Header (76px Height)
class LogicPluginHeaderComponent : public juce::Component {
public:
    LogicPluginHeaderComponent(juce::DocumentWindow& window, juce::AudioPluginInstance& plugin,
                               std::function<void()> onToggleCollapse,
                               std::function<void(float)> onScaleChange)
        : window_(window),
          plugin_(plugin),
          presetDropdown_(plugin),
          sideChainDropdown_("Side Chain:", "None", [this] { showSideChainMenu(); }),
          viewDropdown_("View:", "100%", [this] { showViewMenu(); }),
          compareBtn_("Compare", true),
          copyBtn_("Copy"),
          pasteBtn_("Paste"),
          snapshotBtn_(juce::CharPointer_UTF8("\xF0\x9F\x93\xB7")),
          onScaleChange_(onScaleChange) {

        closeBtn_.onClick = [this] { window_.setVisible(false); };
        addAndMakeVisible(closeBtn_);

        collapseBtn_.onClick = [this, onToggleCollapse] {
            collapsed_ = !collapsed_;
            collapseBtn_.setCollapsed(collapsed_);
            if (onToggleCollapse) onToggleCollapse();
            updateVisibility();
            repaint();
        };
        addAndMakeVisible(collapseBtn_);

        // Power / Bypass
        powerBtn_.onClick = [this] {
            bool active = powerBtn_.getToggleState();
            if (auto* bp = plugin_.getBypassParameter())
                bp->setValueNotifyingHost(active ? 0.0f : 1.0f);
            else
                plugin_.suspendProcessing(!active);
        };
        addAndMakeVisible(powerBtn_);

        // Centered Plugin Title in subtle title grey
        titleLabel_.setText(plugin_.getName().isNotEmpty() ? plugin_.getName() : "Plugin", juce::dontSendNotification);
        titleLabel_.setFont(juce::Font(juce::Font::getDefaultSansSerifFontName(), 12.0f, juce::Font::bold));
        titleLabel_.setColour(juce::Label::textColourId, juce::Colour(0xff9ea2ad));
        titleLabel_.setJustificationType(juce::Justification::centred);
        titleLabel_.setInterceptsMouseClicks(false, false);
        addAndMakeVisible(titleLabel_);

        // Preset Capsule
        addAndMakeVisible(presetDropdown_);

        // Arrows [ ◀ | ▶ ]
        arrowsBtn_.onPrev = [this] { cycleProgram(-1); };
        arrowsBtn_.onNext = [this] { cycleProgram(1); };
        addAndMakeVisible(arrowsBtn_);

        // Compare Button
        compareBtn_.setClickingTogglesState(true);
        compareBtn_.onClick = [this] { toggleCompare(); };
        addAndMakeVisible(compareBtn_);

        // Copy Button
        copyBtn_.onClick = [this] {
            plugin_.getStateInformation(copiedState_);
            pasteBtn_.setEnabled(copiedState_.getSize() > 0);
            pasteBtn_.repaint();
        };
        addAndMakeVisible(copyBtn_);

        // Paste Button
        pasteBtn_.setEnabled(false);
        pasteBtn_.onClick = [this] {
            if (copiedState_.getSize() > 0) {
                plugin_.setStateInformation(copiedState_.getData(), (int)copiedState_.getSize());
                presetDropdown_.updateText();
            }
        };
        addAndMakeVisible(pasteBtn_);

        // Snapshot / Camera Button
        snapshotBtn_.setButtonText(juce::CharPointer_UTF8("\xF0\x9F\x93\xB7")); // 📷
        snapshotBtn_.setTooltip("Capture plug-in screenshot thumbnail");
        snapshotBtn_.onClick = [this] {
            if (onSnapshot_) onSnapshot_();
        };
        addAndMakeVisible(snapshotBtn_);

        // Side Chain & View & Link
        addAndMakeVisible(sideChainDropdown_);
        addAndMakeVisible(viewDropdown_);
        addAndMakeVisible(linkBtn_);

        // Initial snapshot for Compare
        plugin_.getStateInformation(compareOriginalState_);

        updateVisibility();
    }

    void cycleProgram(int delta) {
        int cur = plugin_.getCurrentProgram();
        int num = plugin_.getNumPrograms();
        if (num <= 0) return;
        int next = cur + delta;
        if (next < 0) next = 0;
        if (next >= num) next = num - 1;
        plugin_.setCurrentProgram(next);
        presetDropdown_.updateText();
    }

    void toggleCompare() {
        if (compareBtn_.getToggleState()) {
            plugin_.getStateInformation(compareModifiedState_);
            if (compareOriginalState_.getSize() > 0) {
                plugin_.setStateInformation(compareOriginalState_.getData(), (int)compareOriginalState_.getSize());
                presetDropdown_.updateText();
            }
        } else {
            if (compareModifiedState_.getSize() > 0) {
                plugin_.setStateInformation(compareModifiedState_.getData(), (int)compareModifiedState_.getSize());
                presetDropdown_.updateText();
            }
        }
    }

    void showSideChainMenu() {
        juce::PopupMenu menu;
        menu.addItem(1, "None", true, true);
        menu.addItem(2, "Audio In 1-2", true, false);
        menu.addItem(3, "Track 1 - Audio", true, false);
        menu.addItem(4, "Track 2 - Instrument", true, false);
        menu.showMenuAsync(juce::PopupMenu::Options().withTargetComponent(&sideChainDropdown_),
            [this](int res) {
                if (res <= 0) return;
                juce::String name = res == 1 ? "None" : res == 2 ? "Audio In 1-2" : res == 3 ? "Track 1" : "Track 2";
                sideChainDropdown_.setValue(name);
            });
    }

    void showViewMenu() {
        juce::PopupMenu menu;
        menu.addItem(1, "50%", true, currentScale_ == 0.5f);
        menu.addItem(2, "75%", true, currentScale_ == 0.75f);
        menu.addItem(3, "100%", true, currentScale_ == 1.0f);
        menu.addItem(4, "125%", true, currentScale_ == 1.25f);
        menu.addItem(5, "150%", true, currentScale_ == 1.5f);
        menu.addItem(6, "200%", true, currentScale_ == 2.0f);
        menu.showMenuAsync(juce::PopupMenu::Options().withTargetComponent(&viewDropdown_),
            [this](int res) {
                if (res <= 0) return;
                float s = res == 1 ? 0.5f : res == 2 ? 0.75f : res == 3 ? 1.0f : res == 4 ? 1.25f : res == 5 ? 1.5f : 2.0f;
                currentScale_ = s;
                viewDropdown_.setValue(juce::String((int)(s * 100)) + "%");
                if (onScaleChange_) onScaleChange_(s);
            });
    }

    void setCollapsed(bool c) {
        collapsed_ = c;
        collapseBtn_.setCollapsed(c);
        updateVisibility();
        resized();
        repaint();
    }

    void updateVisibility() {
        bool showControls = !collapsed_;
        powerBtn_.setVisible(showControls);
        presetDropdown_.setVisible(showControls);
        arrowsBtn_.setVisible(showControls);
        compareBtn_.setVisible(showControls);
        copyBtn_.setVisible(showControls);
        pasteBtn_.setVisible(showControls);
        snapshotBtn_.setVisible(showControls);
        sideChainDropdown_.setVisible(showControls);
        viewDropdown_.setVisible(showControls);
        linkBtn_.setVisible(showControls);
    }

    void paint(juce::Graphics& g) override {
        juce::ColourGradient grad(juce::Colour(0xff25272c), 0.0f, 0.0f,
                                  juce::Colour(0xff1a1b1e), 0.0f, (float)getHeight(), false);
        g.setGradientFill(grad);
        g.fillAll();

        g.setColour(juce::Colour(0xff3a3c44));
        g.drawHorizontalLine(0, 0.0f, (float)getWidth());

        g.setColour(juce::Colour(0xff121316));
        g.drawHorizontalLine(getHeight() - 1, 0.0f, (float)getWidth());

        if (!collapsed_) {
            g.setColour(juce::Colour(0x18ffffff));
            g.drawHorizontalLine(24, 0.0f, (float)getWidth());
        }
    }

    void resized() override {
        const int w = getWidth();

        // Row 0: Title Bar (y: 0..24, height 24px)
        closeBtn_.setBounds(10, 6, 12, 12);
        collapseBtn_.setBounds(w - 28, 5, 18, 14);
        titleLabel_.setBounds(30, 2, juce::jmax(40, w - 65), 20);

        if (collapsed_) return;

        // Controls area (y: 26..76)
        // Left Power button (32x32)
        powerBtn_.setBounds(8, 34, 32, 32);

        const int leftX = 48;
        const int rightMargin = 10;
        const int availableW = w - leftX - rightMargin;

        // Row 1 (y: 28..48, height 20px)
        int sideChainW = juce::jlimit(110, 140, availableW / 3);
        int sideChainX = w - rightMargin - sideChainW;
        int presetW = juce::jmax(90, sideChainX - leftX - 10);

        presetDropdown_.setBounds(leftX, 28, presetW, 20);
        sideChainDropdown_.setBounds(sideChainX, 28, sideChainW, 20);

        // Row 2 (y: 52..72, height 20px)
        int linkW = 22;
        int linkX = w - rightMargin - linkW;
        int viewW = juce::jlimit(85, 105, availableW / 4);
        int viewX = linkX - 6 - viewW;

        int curX = leftX;
        int gap = 4;
        arrowsBtn_.setBounds(curX, 52, 36, 20); curX += 36 + gap;
        compareBtn_.setBounds(curX, 52, 56, 20); curX += 56 + gap;
        copyBtn_.setBounds(curX, 52, 42, 20); curX += 42 + gap;
        pasteBtn_.setBounds(curX, 52, 42, 20); curX += 42 + gap;
        snapshotBtn_.setBounds(curX, 52, 30, 20); curX += 30 + gap;

        if (viewX < curX + 6) {
            viewX = curX + 6;
            linkX = viewX + viewW + 4;
        }

        viewDropdown_.setBounds(viewX, 52, viewW, 20);
        linkBtn_.setBounds(linkX, 52, linkW, 20);
    }

    void mouseDown(const juce::MouseEvent& e) override {
        dragStartScreenPos_ = e.getScreenPosition();
        windowStartPos_ = window_.getPosition();
    }

    void mouseDrag(const juce::MouseEvent& e) override {
        auto delta = e.getScreenPosition() - dragStartScreenPos_;
        window_.setTopLeftPosition(windowStartPos_ + delta);
    }

    void setOnSnapshot(std::function<void()> cb) { onSnapshot_ = std::move(cb); }

private:
    juce::DocumentWindow& window_;
    juce::AudioPluginInstance& plugin_;
    bool collapsed_ = false;

    AppleCloseButton closeBtn_;
    CollapseToggleButton collapseBtn_;
    LogicPowerButton powerBtn_;
    juce::Label titleLabel_;

    LogicPresetDropdownButton presetDropdown_;
    LogicArrowsButton arrowsBtn_;
    LogicActionButton compareBtn_;
    LogicActionButton copyBtn_;
    LogicActionButton pasteBtn_;
    LogicActionButton snapshotBtn_;

    LogicDropdownWithLabel sideChainDropdown_;
    LogicDropdownWithLabel viewDropdown_;
    LogicLinkButton linkBtn_;

    float currentScale_ = 1.0f;
    std::function<void(float)> onScaleChange_;
    std::function<void()> onSnapshot_;

    juce::MemoryBlock compareOriginalState_;
    juce::MemoryBlock compareModifiedState_;
    static inline juce::MemoryBlock copiedState_;

    juce::Point<int> dragStartScreenPos_;
    juce::Point<int> windowStartPos_;
};

class PluginEditorContainer : public juce::Component {
public:
    PluginEditorContainer(juce::DocumentWindow& win, juce::AudioPluginInstance& p, juce::AudioProcessorEditor* editor)
        : window_(win),
          plugin_(p),
          header_(win, p, [this] { toggleCollapse(); }, [this](float s) { setScale(s); }),
          editor_(editor) {
        header_.setOnSnapshot([this] { captureDefaultThumbnails(); });
        addAndMakeVisible(header_);
        if (editor_ != nullptr) addAndMakeVisible(editor_.get());

        int ew = editor_ ? editor_->getWidth() : 450;
        int eh = editor_ ? editor_->getHeight() : 300;
        if (ew < 420) ew = 420;
        if (eh < 180) eh = 180;

        baseEditorW_ = ew;
        baseEditorH_ = eh;
        updateLayout();
    }

    void captureDefaultThumbnails() {
        juce::File thumbDir = notaSupportDir().getChildFile("thumbnails");
        thumbDir.createDirectory();
        auto desc = plugin_.getPluginDescription();
        juce::String cleanId = juce::File::createLegalFileName(desc.createIdentifierString());
        juce::String cleanName = juce::File::createLegalFileName(desc.name.isNotEmpty() ? desc.name : plugin_.getName());

        if (cleanId.isNotEmpty()) captureSnapshot(thumbDir.getChildFile(cleanId + ".png"));
        if (cleanName.isNotEmpty()) captureSnapshot(thumbDir.getChildFile(cleanName + ".png"));
    }

    bool captureSnapshot(const juce::File& targetPngFile) {
        if (editor_ == nullptr) return false;
        editor_->repaint();

        juce::Image img;

#if JUCE_WINDOWS
        HWND hwnd = (HWND)editor_->getWindowHandle();
        if (hwnd == nullptr) hwnd = (HWND)window_.getWindowHandle();
        if (hwnd != nullptr) {
            RECT rc;
            if (GetWindowRect(hwnd, &rc)) {
                int winW = rc.right - rc.left;
                int winH = rc.bottom - rc.top;
                if (winW > 10 && winH > 10) {
                    HDC hdcScreen = GetDC(NULL);
                    HDC hdcMem = CreateCompatibleDC(hdcScreen);
                    HBITMAP hbm = CreateCompatibleBitmap(hdcScreen, winW, winH);
                    HGDIOBJ hOld = SelectObject(hdcMem, hbm);

                    if (!PrintWindow(hwnd, hdcMem, 2 /* PW_RENDERFULLCONTENT */)) {
                        BitBlt(hdcMem, 0, 0, winW, winH, hdcScreen, rc.left, rc.top, SRCCOPY);
                    }

                    BITMAPINFO bmi = {0};
                    bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                    bmi.bmiHeader.biWidth = winW;
                    bmi.bmiHeader.biHeight = -winH;
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = BI_RGB;

                    img = juce::Image(juce::Image::ARGB, winW, winH, false);
                    juce::Image::BitmapData bmData(img, juce::Image::BitmapData::readWrite);
                    GetDIBits(hdcMem, hbm, 0, winH, bmData.data, &bmi, DIB_RGB_COLORS);

                    for (int y = 0; y < winH; ++y) {
                        uint8_t* row = bmData.getLinePointer(y);
                        for (int x = 0; x < winW; ++x) {
                            row[x * bmData.pixelStride + 3] = 255;
                        }
                    }

                    SelectObject(hdcMem, hOld);
                    DeleteObject(hbm);
                    DeleteDC(hdcMem);
                    ReleaseDC(NULL, hdcScreen);
                }
            }
        }
#endif

        if (!img.isValid()) {
            img = editor_->createComponentSnapshot(editor_->getLocalBounds(), false, 1.0f);
        }

        if (img.isValid()) {
            targetPngFile.getParentDirectory().createDirectory();
            juce::FileOutputStream stream(targetPngFile);
            if (stream.openedOk()) {
                juce::PNGImageFormat png;
                return png.writeImageToStream(img, stream);
            }
        }
        return false;
    }

    void setScale(float s) {
        scaleFactor_ = s;
        if (editor_ != nullptr) {
            editor_->setTransform(juce::AffineTransform::scale(s));
        }
        updateLayout();
    }

    void toggleCollapse() {
        isCollapsed_ = !isCollapsed_;
        header_.setCollapsed(isCollapsed_);
        if (editor_ != nullptr) {
            editor_->setVisible(!isCollapsed_);
            if (!isCollapsed_) {
                editor_->setBounds(0, 76, baseEditorW_, baseEditorH_);
                editor_->toFront(true);
                editor_->repaint();
            }
        }
        updateLayout();
        repaint();
    }

    void updateLayout() {
        int scaledW = (int)(baseEditorW_ * scaleFactor_);
        int scaledH = (int)(baseEditorH_ * scaleFactor_);
        int headerH = isCollapsed_ ? 26 : 76;
        int totalH = headerH + (isCollapsed_ ? 0 : scaledH);
        int totalW = juce::jmax(420, scaledW);

        window_.setResizeLimits(300, isCollapsed_ ? 24 : 100, 4000, 4000);
        setSize(totalW, totalH);
        window_.setSize(totalW, totalH);
    }

    void paint(juce::Graphics& g) override {
        g.fillAll(juce::Colour(0xff18191d));
    }

    void resized() override {
        if (isCollapsed_) {
            header_.setBounds(0, 0, getWidth(), getHeight());
        } else {
            header_.setBounds(0, 0, getWidth(), 76);
            if (editor_ != nullptr) {
                editor_->setVisible(true);
                editor_->setBounds(0, 76, baseEditorW_, baseEditorH_);
            }
        }
    }

private:
    juce::DocumentWindow& window_;
    juce::AudioPluginInstance& plugin_;
    bool isCollapsed_ = false;
    float scaleFactor_ = 1.0f;
    int baseEditorW_ = 450;
    int baseEditorH_ = 300;
    LogicPluginHeaderComponent header_;
    std::unique_ptr<juce::AudioProcessorEditor> editor_;
};

// A separate native window hosting a plugin's editor (M3-4).
class PluginEditorWindow : public juce::DocumentWindow {
public:
    std::function<void(int32_t, float)> onNoteOn;
    std::function<void(int32_t)>        onNoteOff;

    explicit PluginEditorWindow(juce::AudioPluginInstance& p)
        : juce::DocumentWindow(p.getName().isNotEmpty() ? p.getName() : juce::String("Plugin"),
                               juce::Colour(0xff18191b),
                               0) {
        ensureJuceGuiInit();
        setUsingNativeTitleBar(false);
        setTitleBarHeight(0);
        setResizeLimits(300, 24, 4000, 4000);
        juce::AudioProcessorEditor* ui = nullptr;
        if (p.hasEditor()) ui = p.createEditorIfNeeded();
        if (ui == nullptr) ui = new juce::GenericAudioProcessorEditor(p);
        auto* container = new PluginEditorContainer(*this, p, ui);
        setContentOwned(container, true);
        setResizable(ui->isResizable(), false);
        setTopLeftPosition(100, 100);
        juce::Process::makeForegroundProcess();
#if JUCE_MAC
        [NSApp activateIgnoringOtherApps:YES];
#endif
        setAlwaysOnTop(false);
        setVisible(true);
        toFront(true);
        installKeyMonitor();
    }
    bool captureSnapshot(const juce::File& targetPngFile) {
        if (auto* c = dynamic_cast<PluginEditorContainer*>(getContentComponent())) {
            return c->captureSnapshot(targetPngFile);
        }
        return false;
    }

    ~PluginEditorWindow() override { removeKeyMonitor(); clearContentComponent(); }
    void activeWindowStatusChanged() override {
        juce::DocumentWindow::activeWindowStatusChanged();
        if (isActiveWindow()) {
#if JUCE_WINDOWS
            auto it = std::find(g_activeEditorWindows.begin(), g_activeEditorWindows.end(), this);
            if (it != g_activeEditorWindows.end()) {
                g_activeEditorWindows.erase(it);
                g_activeEditorWindows.insert(g_activeEditorWindows.begin(), this);
            }
#endif
        }
    }

private:
    void installKeyMonitor() {
#if JUCE_MAC
        keyMonitor_ = [[NSEvent addLocalMonitorForEventsMatchingMask:(NSEventMaskKeyDown | NSEventMaskKeyUp)
            handler:^NSEvent* (NSEvent* ev) {
                void* handle = this->getWindowHandle();
                if (handle == nullptr || ev.window != ((NSView*)handle).window)
                    return ev;
                if (ev.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagOption))
                    return ev;
                if (!g_typingActive.load()) return ev;

                const int offset = macKeycodeToPitchOffset(ev.keyCode);
                if (offset == -999) return ev;

                const int basePitch = (g_typingOctave.load() + 2) * 12;
                const int pitch = juce::jlimit(0, 127, basePitch + offset);

                if (ev.type == NSEventTypeKeyDown) {
                    if (!ev.isARepeat) {
                        if (onNoteOn) onNoteOn(pitch, 0.85f);
                        else if (g_hostNoteSink) g_hostNoteSink(true, pitch, 0.85f);
                    }
                } else {
                    if (onNoteOff) onNoteOff(pitch);
                    else if (g_hostNoteSink) g_hostNoteSink(false, pitch, 0.0f);
                }
                return nil;
            }] retain];
#elif JUCE_WINDOWS
        g_activeEditorWindows.insert(g_activeEditorWindows.begin(), this);
        if (g_winKeyHook == nullptr) {
            g_winKeyHook = SetWindowsHookEx(WH_GETMESSAGE, WindowsPluginKeyHook, nullptr, GetCurrentThreadId());
        }
#endif
    }

    void removeKeyMonitor() {
#if JUCE_MAC
        if (keyMonitor_ != nil) { [NSEvent removeMonitor:keyMonitor_]; [keyMonitor_ release]; keyMonitor_ = nil; }
#elif JUCE_WINDOWS
        auto it = std::find(g_activeEditorWindows.begin(), g_activeEditorWindows.end(), this);
        if (it != g_activeEditorWindows.end()) g_activeEditorWindows.erase(it);
        if (g_activeEditorWindows.empty() && g_winKeyHook != nullptr) {
            UnhookWindowsHookEx(g_winKeyHook);
            g_winKeyHook = nullptr;
        }
#endif
    }

#if JUCE_MAC
    id keyMonitor_ = nil;
#endif
};

#if JUCE_WINDOWS
static LRESULT CALLBACK WindowsPluginKeyHook(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0 && (wParam == PM_REMOVE || wParam == PM_NOREMOVE)) {
        MSG* msg = (MSG*)lParam;
        if (msg->message == WM_KEYDOWN || msg->message == WM_KEYUP ||
            msg->message == WM_SYSKEYDOWN || msg->message == WM_SYSKEYUP) {

            if (g_typingActive.load()) {
                HWND fg = GetForegroundWindow();
                HWND rootFg = fg ? GetAncestor(fg, GA_ROOT) : nullptr;
                HWND rootMsg = msg->hwnd ? GetAncestor(msg->hwnd, GA_ROOT) : nullptr;

                PluginEditorWindow* targetWindow = nullptr;
                for (auto* win : g_activeEditorWindows) {
                    if (win != nullptr) {
                        HWND winHwnd = (HWND)win->getWindowHandle();
                        if (winHwnd != nullptr) {
                            if (fg == winHwnd || rootFg == winHwnd || IsChild(winHwnd, fg) ||
                                msg->hwnd == winHwnd || rootMsg == winHwnd || IsChild(winHwnd, msg->hwnd)) {
                                targetWindow = win;
                                break;
                            }
                        }
                    }
                }
                // If focus was on an embedded control that didn't match directly, fall back to the most active editor
                if (targetWindow == nullptr && !g_activeEditorWindows.empty()) {
                    for (auto* win : g_activeEditorWindows) {
                        if (win != nullptr && win->isActiveWindow()) {
                            targetWindow = win;
                            break;
                        }
                    }
                    if (targetWindow == nullptr)
                        targetWindow = g_activeEditorWindows.front();
                }

                if (targetWindow != nullptr) {
                    bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool alt  = (GetKeyState(VK_MENU) & 0x8000) != 0;
                    bool winKey = (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

                    if (!ctrl && !alt && !winKey) {
                        int vk = (int)msg->wParam;

                        // Octave shift with - and +
                        if (msg->message == WM_KEYDOWN && (msg->lParam & (1 << 30)) == 0) {
                            if (vk == VK_OEM_MINUS || vk == VK_SUBTRACT) {
                                int oct = g_typingOctave.load();
                                if (oct > 1) g_typingOctave.store(oct - 1);
                            } else if (vk == VK_OEM_PLUS || vk == VK_ADD) {
                                int oct = g_typingOctave.load();
                                if (oct < 5) g_typingOctave.store(oct + 1);
                            }
                        }

                        int offset = winKeyToPitchOffset(vk);
                        if (offset != -999) {
                            int basePitch = (g_typingOctave.load() + 2) * 12; // Octave 3 => 60 (C4)
                            int pitch = juce::jlimit(0, 127, basePitch + offset);

                            if (msg->message == WM_KEYDOWN || msg->message == WM_SYSKEYDOWN) {
                                bool isRepeat = (msg->lParam & (1 << 30)) != 0;
                                if (!isRepeat) {
                                    if (targetWindow->onNoteOn) targetWindow->onNoteOn(pitch, 0.85f);
                                    else if (g_hostNoteSink) g_hostNoteSink(true, pitch, 0.85f);
                                }
                            } else if (msg->message == WM_KEYUP || msg->message == WM_SYSKEYUP) {
                                if (targetWindow->onNoteOff) targetWindow->onNoteOff(pitch);
                                else if (g_hostNoteSink) g_hostNoteSink(false, pitch, 0.0f);
                            }

                            msg->message = WM_NULL;
                            return 0;
                        }
                    }
                }
            }
        }
    }
    return CallNextHookEx(g_winKeyHook, nCode, wParam, lParam);
}
#endif

// Owns (at most one) editor window for a plugin; open() lazily creates it and
// re-shows on subsequent calls. Message thread only. The adapter wires
// onNoteOn/onNoteOff so the window's computer keyboard reaches its plugin.
struct EditorHost {
    std::unique_ptr<PluginEditorWindow> window;
    std::function<void(int32_t, float)> onNoteOn;
    std::function<void(int32_t)>        onNoteOff;

    void open(juce::AudioPluginInstance& p) {
        if (juce::MessageManager::getInstanceWithoutCreating() == nullptr) return;
        if (window == nullptr) {
            window = std::make_unique<PluginEditorWindow>(p);
            window->onNoteOn  = onNoteOn;
            window->onNoteOff = onNoteOff;
        } else {
            window->setVisible(true);
            window->toFront(true);
        }
    }
    void close() { if (window) window->setVisible(false); }

    bool captureSnapshot(const juce::File& targetPngFile) {
        if (window != nullptr) return window->captureSnapshot(targetPngFile);
        return false;
    }
};

// Opaque plugin state <-> byte vector (M3-5).
std::vector<uint8_t> readState(juce::AudioPluginInstance* p) {
    if (p == nullptr) return {};
    juce::MemoryBlock mb;
    p->getStateInformation(mb);
    const auto* bytes = static_cast<const uint8_t*>(mb.getData());
    return std::vector<uint8_t>(bytes, bytes + mb.getSize());
}

void writeState(juce::AudioPluginInstance* p, const uint8_t* data, int32_t size) {
    if (p != nullptr && data != nullptr && size > 0)
        p->setStateInformation(data, size);
}

// --- hosted-plugin parameter access (M9-B) ---------------------------------
// Shared by both adapters. Values are normalized 0..1; identity is the stable
// hosted paramID (juce::HostedAudioProcessorParameter::getParameterID), which
// survives plugin version changes where the bare index would not.
int32_t paramCountImpl(juce::AudioPluginInstance* p) {
    return p ? p->getParameters().size() : 0;
}
std::string paramIdImpl(juce::AudioPluginInstance* p, int32_t i) {
    if (p == nullptr) return {};
    if (auto* hp = p->getHostedParameter(i)) return hp->getParameterID().toStdString();
    return {};
}
std::string paramNameImpl(juce::AudioPluginInstance* p, int32_t i) {
    if (p == nullptr) return {};
    const auto& params = p->getParameters();
    if (i < 0 || i >= params.size()) return {};
    return params[i]->getName(64).toStdString();
}
float paramGetImpl(juce::AudioPluginInstance* p, int32_t i) {
    if (p == nullptr) return 0.0f;
    const auto& params = p->getParameters();
    if (i < 0 || i >= params.size()) return 0.0f;
    return params[i]->getValue();
}
void paramSetImpl(juce::AudioPluginInstance* p, int32_t i, float v) {
    if (p == nullptr) return;
    const auto& params = p->getParameters();
    if (i < 0 || i >= params.size()) return;
    // Audio-thread-safe value push: no host/GUI notification (message-thread only).
    params[i]->setValue(juce::jlimit(0.0f, 1.0f, v));
}
int32_t paramIndexOfIdImpl(juce::AudioPluginInstance* p, const std::string& id) {
    if (p == nullptr) return -1;
    const juce::String want(juce::String::fromUTF8(id.c_str()));
    const int n = p->getParameters().size();
    for (int i = 0; i < n; ++i)
        if (auto* hp = p->getHostedParameter(i))
            if (hp->getParameterID() == want) return i;
    return -1;
}

// Bridges Nota's JUCE-free TransportInfo to a juce::AudioPlayHead so hosted
// plugins (synced delays/LFOs, arps, loopers) follow the DAW clock. Both adapters
// own one and hand it to their plugin via setPlayHead(). `info` is written by the
// audio thread in setTransportInfo() and read by the same thread inside the
// plugin's processBlock() (via getPosition()) — no cross-thread sharing.
class NotaPlayHead : public juce::AudioPlayHead {
public:
    nota::TransportInfo info;

    juce::Optional<PositionInfo> getPosition() const override {
        PositionInfo p;
        p.setBpm(info.bpm);
        p.setTimeSignature(TimeSignature{ info.tsNum, info.tsDenom });
        p.setPpqPosition(info.ppqPosition);
        // Start of the current bar (quarter notes): many tempo-synced plugins
        // beat-align their playback to this rather than to the raw ppq.
        const double beatsPerBar = info.tsDenom > 0 ? info.tsNum * (4.0 / info.tsDenom)
                                                    : static_cast<double>(info.tsNum);
        if (beatsPerBar > 0.0)
            p.setPpqPositionOfLastBarStart(std::floor(info.ppqPosition / beatsPerBar) * beatsPerBar);
        p.setTimeInSamples(info.timeInSamples);
        p.setTimeInSeconds(info.timeInSeconds);
        p.setIsPlaying(info.isPlaying);
        p.setIsLooping(info.isLooping);
        if (info.isLooping)
            p.setLoopPoints(LoopPoints{ info.ppqLoopStart, info.ppqLoopEnd });

        // Diagnostic (NOTA_DEBUG_PLAYHEAD=1): proves the plugin is actually querying
        // our playhead and shows the values it sees. If nothing ever prints while a
        // plugin runs, that plugin ignores host transport (its own problem, not ours).
        static const bool dbg = std::getenv("NOTA_DEBUG_PLAYHEAD") != nullptr;
        if (dbg) {
            static std::atomic<int> n{0};
            if ((n.fetch_add(1, std::memory_order_relaxed) % 200) == 0)
                std::fprintf(stderr, "[nota playhead] bpm=%.2f ppq=%.3f playing=%d looping=%d\n",
                             info.bpm, info.ppqPosition, (int)info.isPlaying, (int)info.isLooping);
        }
        return p;
    }
};

class PluginInstrument : public nota::Instrument, private juce::AudioProcessorListener {
public:
    PluginInstrument(std::unique_ptr<juce::AudioPluginInstance> p, double sr, int maxBlock)
        : plugin_(std::move(p)), maxBlock_(maxBlock) {
        pending_.ensureSize(8192);
        if (plugin_) name_ = plugin_->getName().toStdString();   // cache for displayName()
        prepare(sr);
        if (plugin_) plugin_->addListener(this);   // "Learn" (M9-B3)
        // Editor-window computer keyboard -> this plugin (drained on the audio thread).
        editor_.onNoteOn  = [this](int32_t pitch, float vel) { guiNotes_.push({true, pitch, vel}); };
        editor_.onNoteOff = [this](int32_t pitch)            { guiNotes_.push({false, pitch, 0.0f}); };
    }
    const char* displayName() const override { return name_.empty() ? "Plugin" : name_.c_str(); }
    ~PluginInstrument() override { if (plugin_) plugin_->removeListener(this); }

    void setSampleRate(double sr) override { prepare(sr); }

    void noteOn(int32_t pitch, float velocity) override {
        pending_.addEvent(juce::MidiMessage::noteOn(1, pitch, (float)velocity), 0);
    }
    void noteOff(int32_t pitch) override {
        pending_.addEvent(juce::MidiMessage::noteOff(1, pitch), 0);
    }
    void allNotesOff() override {
        pending_.addEvent(juce::MidiMessage::allNotesOff(1), 0);
    }

    void render(float* out, int32_t frames) override {
        if (frames <= 0 || frames > maxBlock_ || plugin_ == nullptr) return;
        // Drain computer-keyboard notes from the editor window (message thread) into
        // this block's MIDI buffer — audio thread only touches pending_ here.
        for (GuiNote g; guiNotes_.pop(g); )
            pending_.addEvent(g.on ? juce::MidiMessage::noteOn(1, g.pitch, g.velocity)
                                   : juce::MidiMessage::noteOff(1, g.pitch), 0);
        const int nc = juce::jmin(storage_.getNumChannels(), kMaxChans);
        storage_.clear();
        float* ptrs[kMaxChans];
        for (int c = 0; c < nc; ++c) ptrs[c] = storage_.getWritePointer(c);
        juce::AudioBuffer<float> block(ptrs, nc, frames);
        plugin_->processBlock(block, pending_);
        pending_.clear();
        const float* l = block.getReadPointer(0);
        const float* r = nc > 1 ? block.getReadPointer(1) : l;
        for (int i = 0; i < frames; ++i) { out[i * 2] += l[i]; out[i * 2 + 1] += r[i]; }
    }

    // DAW transport sync (AudioPlayHead): store this block's snapshot; the plugin
    // reads it via getPosition() during processBlock. Audio thread.
    void setTransportInfo(const nota::TransportInfo& ti) override { playHead_.info = ti; }

    void openEditor() override  { if (plugin_) editor_.open(*plugin_); }
    void closeEditor() override { editor_.close(); }
    bool captureEditorSnapshot(const char* out_png_path) override {
        if (out_png_path == nullptr) return false;
        return editor_.captureSnapshot(juce::File(out_png_path));
    }

    std::vector<uint8_t> getState() const override { return readState(plugin_.get()); }
    void setState(const uint8_t* data, int32_t size) override { writeState(plugin_.get(), data, size); }

    std::string pluginIdentifier() const override {
        return plugin_ ? plugin_->getPluginDescription().createIdentifierString().toStdString() : std::string{};
    }

    int32_t latencySamples() const override { return plugin_ ? plugin_->getLatencySamples() : 0; }

    // Hosted-plugin parameter automation (M9-B).
    int32_t     pluginParamCount() const override { return paramCountImpl(plugin_.get()); }
    std::string pluginParamId(int32_t i) const override { return paramIdImpl(plugin_.get(), i); }
    std::string pluginParamName(int32_t i) const override { return paramNameImpl(plugin_.get(), i); }
    float       pluginParamGet(int32_t i) const override { return paramGetImpl(plugin_.get(), i); }
    void        pluginParamSet(int32_t i, float v) override { paramSetImpl(plugin_.get(), i, v); }
    int32_t     pluginParamIndexOfId(const std::string& id) const override { return paramIndexOfIdImpl(plugin_.get(), id); }
    int32_t     lastTouchedPluginParam() override { return lastTouched_.exchange(-1, std::memory_order_relaxed); }
    int32_t     takePluginGestureBegin() override { return gestureBegin_.exchange(-1, std::memory_order_relaxed); }
    int32_t     takePluginGestureEnd() override { return gestureEnd_.exchange(-1, std::memory_order_relaxed); }

    std::shared_ptr<nota::Instrument> clone() const override {
        if (!plugin_) return nullptr;
        juce::String err;
        double sr = plugin_->getSampleRate() > 0 ? plugin_->getSampleRate() : 44100.0;
        auto inst = formatManager().createPluginInstance(plugin_->getPluginDescription(), sr, maxBlock_, err);
        if (!inst) return nullptr;
        juce::MemoryBlock mb; plugin_->getStateInformation(mb);
        inst->setStateInformation(mb.getData(), (int)mb.getSize());
        return std::make_shared<PluginInstrument>(std::move(inst), sr, maxBlock_);
    }

private:
    // AudioProcessorListener: GUI edits call setValueNotifyingHost -> here; our
    // automation uses setValue (no notify), so this reflects only user gestures.
    void audioProcessorParameterChanged(juce::AudioProcessor*, int index, float) override {
        lastTouched_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorParameterChangeGestureBegin(juce::AudioProcessor*, int index) override {
        gestureBegin_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorParameterChangeGestureEnd(juce::AudioProcessor*, int index) override {
        gestureEnd_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorChanged(juce::AudioProcessor*, const juce::AudioProcessorListener::ChangeDetails&) override {}

    void prepare(double sr) {
        plugin_->setPlayConfigDetails(0, 2, sr, maxBlock_);
        plugin_->prepareToPlay(sr, maxBlock_);
        plugin_->setPlayHead(&playHead_);   // DAW transport sync
        storage_.setSize(juce::jmax(2, plugin_->getTotalNumOutputChannels()), maxBlock_);
    }

    NotaPlayHead playHead_;   // declared first: outlives plugin_ (which points at it)
    std::unique_ptr<juce::AudioPluginInstance> plugin_;
    juce::AudioBuffer<float> storage_;
    juce::MidiBuffer pending_;
    int maxBlock_;
    std::string name_;   // cached plugin name for displayName()
    std::atomic<int32_t> lastTouched_{-1};   // "Learn" (M9-B3)
    std::atomic<int32_t> gestureBegin_{-1}, gestureEnd_{-1};   // write gestures (M9-C)
    GuiNoteQueue guiNotes_;   // editor-window computer keyboard -> render() (message->audio thread)
    EditorHost editor_; // declared last: destroyed before plugin_ (detaches editor)
};

class PluginEffect : public nota::Device, private juce::AudioProcessorListener {
public:
    PluginEffect(std::unique_ptr<juce::AudioPluginInstance> p, double sr, int maxBlock)
        : plugin_(std::move(p)) {
        if (plugin_) name_ = plugin_->getName().toStdString();
        empty_.ensureSize(256);
        setSampleRate(sr, maxBlock);
        if (plugin_) plugin_->addListener(this);   // "Learn" (M9-B3)
    }
    ~PluginEffect() override { if (plugin_) plugin_->removeListener(this); }

    const char* displayName() const override { return name_.empty() ? "Plugin" : name_.c_str(); }

    void setSampleRate(double sr, int32_t maxBlock) override {
        maxBlock_ = maxBlock;
        // Prefer stereo main I/O plus a sidechain input bus if the plugin exposes
        // one (input bus index 1, Phase C). Fall back to plain stereo I/O.
        hasSidechain_ = false; scChanOffset_ = -1; scNumChans_ = 0;
        if (plugin_->getBusCount(true) >= 2) {
            auto layout = plugin_->getBusesLayout();
            if (plugin_->getBusCount(true)  > 0) layout.inputBuses.getReference(0)  = juce::AudioChannelSet::stereo();
            if (plugin_->getBusCount(false) > 0) layout.outputBuses.getReference(0) = juce::AudioChannelSet::stereo();
            layout.inputBuses.getReference(1) = juce::AudioChannelSet::stereo();
            if (plugin_->setBusesLayout(layout)) hasSidechain_ = true;
            else {
                layout.inputBuses.getReference(1) = juce::AudioChannelSet::mono();
                if (plugin_->setBusesLayout(layout)) hasSidechain_ = true;
            }
        }
        if (!hasSidechain_)
            plugin_->setPlayConfigDetails(2, 2, sr, maxBlock_);
        plugin_->prepareToPlay(sr, maxBlock_);
        const int chans = juce::jmax(2, juce::jmax(plugin_->getTotalNumInputChannels(),
                                                   plugin_->getTotalNumOutputChannels()));
        storage_.setSize(chans, maxBlock_);
        if (hasSidechain_ && plugin_->getBus(true, 1) != nullptr) {
            scChanOffset_ = plugin_->getChannelIndexInProcessBlockBuffer(true, 1, 0);
            scNumChans_   = plugin_->getBus(true, 1)->getNumberOfChannels();
        }
        plugin_->setPlayHead(&playHead_);   // DAW transport sync
    }

    int32_t latencySamples() const override { return plugin_ ? plugin_->getLatencySamples() : 0; }

    void process(float* buf, int32_t frames) override {
        if (frames <= 0 || frames > maxBlock_ || plugin_ == nullptr) return;
        const int nc = juce::jmin(storage_.getNumChannels(), kMaxChans);
        storage_.clear();
        float* l = storage_.getWritePointer(0);
        float* r = nc > 1 ? storage_.getWritePointer(1) : l;
        for (int i = 0; i < frames; ++i) { l[i] = buf[i * 2]; r[i] = buf[i * 2 + 1]; }

        // Feed the sidechain input bus (Phase C) if the engine handed us a source
        // this block; otherwise it stays silent (cleared above).
        if (hasSidechain_ && scBuf_ != nullptr && scFrames_ >= frames && scChanOffset_ >= 0) {
            const float scGain = std::pow(10.0f, sidechainGainDb() / 20.0f);   // Phase D
            if (scNumChans_ >= 2 && scChanOffset_ + 1 < nc) {
                float* sl = storage_.getWritePointer(scChanOffset_);
                float* sr = storage_.getWritePointer(scChanOffset_ + 1);
                for (int i = 0; i < frames; ++i) { sl[i] = scBuf_[i * 2] * scGain; sr[i] = scBuf_[i * 2 + 1] * scGain; }
            } else if (scNumChans_ == 1 && scChanOffset_ < nc) {
                float* sm = storage_.getWritePointer(scChanOffset_);
                for (int i = 0; i < frames; ++i) sm[i] = 0.5f * (scBuf_[i * 2] + scBuf_[i * 2 + 1]) * scGain;
            }
        }
        scBuf_ = nullptr;   // consume once; the engine re-hands it each block if a source is set

        float* ptrs[kMaxChans];
        for (int c = 0; c < nc; ++c) ptrs[c] = storage_.getWritePointer(c);
        juce::AudioBuffer<float> block(ptrs, nc, frames);
        empty_.clear();
        plugin_->processBlock(block, empty_);

        const float* ol = block.getReadPointer(0);
        const float* orr = nc > 1 ? block.getReadPointer(1) : ol;
        const float mix = sidechainMix();   // dry/wet of the plugin (Phase D)
        for (int i = 0; i < frames; ++i) {
            const float dl = buf[i * 2], dr = buf[i * 2 + 1];
            buf[i * 2]     = dl * (1.0f - mix) + ol[i]  * mix;
            buf[i * 2 + 1] = dr * (1.0f - mix) + orr[i] * mix;
        }
    }

    // DAW transport sync (AudioPlayHead): store this block's snapshot; the plugin
    // reads it via getPosition() during processBlock. Audio thread.
    void setTransportInfo(const nota::TransportInfo& ti) override { playHead_.info = ti; }

    void openEditor() override  { if (plugin_) editor_.open(*plugin_); }
    void closeEditor() override { editor_.close(); }
    bool captureEditorSnapshot(const char* out_png_path) override {
        if (out_png_path == nullptr) return false;
        return editor_.captureSnapshot(juce::File(out_png_path));
    }

    std::vector<uint8_t> getState() const override { return readState(plugin_.get()); }
    void setState(const uint8_t* data, int32_t size) override { writeState(plugin_.get(), data, size); }

    std::string pluginIdentifier() const override {
        return plugin_ ? plugin_->getPluginDescription().createIdentifierString().toStdString() : std::string{};
    }

    // Hosted-plugin parameter automation (M9-B).
    int32_t     pluginParamCount() const override { return paramCountImpl(plugin_.get()); }
    std::string pluginParamId(int32_t i) const override { return paramIdImpl(plugin_.get(), i); }
    std::string pluginParamName(int32_t i) const override { return paramNameImpl(plugin_.get(), i); }
    float       pluginParamGet(int32_t i) const override { return paramGetImpl(plugin_.get(), i); }
    void        pluginParamSet(int32_t i, float v) override { paramSetImpl(plugin_.get(), i, v); }
    int32_t     pluginParamIndexOfId(const std::string& id) const override { return paramIndexOfIdImpl(plugin_.get(), id); }
    int32_t     lastTouchedPluginParam() override { return lastTouched_.exchange(-1, std::memory_order_relaxed); }
    int32_t     takePluginGestureBegin() override { return gestureBegin_.exchange(-1, std::memory_order_relaxed); }
    int32_t     takePluginGestureEnd() override { return gestureEnd_.exchange(-1, std::memory_order_relaxed); }

    // Sidechain / routing (Phase C). The engine drives these generically for any
    // Device; the aux input bus is fed in process() above.
    int32_t sidechainSourceTrackId() const override { return scTrackId_.load(std::memory_order_relaxed); }
    void    setSidechainSourceTrackId(int32_t id) override { scTrackId_.store(id, std::memory_order_relaxed); }
    void    setSidechain(const float* interleaved, int32_t frames) override { scBuf_ = interleaved; scFrames_ = frames; }
    bool    acceptsSidechain() const override { return hasSidechain_; }

    std::shared_ptr<nota::Device> clone() const override {
        if (!plugin_) return nullptr;
        juce::String err;
        double sr = plugin_->getSampleRate() > 0 ? plugin_->getSampleRate() : 44100.0;
        auto inst = formatManager().createPluginInstance(plugin_->getPluginDescription(), sr, maxBlock_, err);
        if (!inst) return nullptr;
        juce::MemoryBlock mb; plugin_->getStateInformation(mb);
        inst->setStateInformation(mb.getData(), (int)mb.getSize());
        auto d = std::make_shared<PluginEffect>(std::move(inst), sr, maxBlock_);
        d->setSidechainSourceTrackId(scTrackId_.load(std::memory_order_relaxed));
        return d;
    }

private:
    void audioProcessorParameterChanged(juce::AudioProcessor*, int index, float) override {
        lastTouched_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorParameterChangeGestureBegin(juce::AudioProcessor*, int index) override {
        gestureBegin_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorParameterChangeGestureEnd(juce::AudioProcessor*, int index) override {
        gestureEnd_.store(index, std::memory_order_relaxed);
    }
    void audioProcessorChanged(juce::AudioProcessor*, const juce::AudioProcessorListener::ChangeDetails&) override {}

    NotaPlayHead playHead_;   // declared first: outlives plugin_ (which points at it)
    std::unique_ptr<juce::AudioPluginInstance> plugin_;
    std::string name_;
    std::atomic<int32_t> lastTouched_{-1};   // "Learn" (M9-B3)
    std::atomic<int32_t> gestureBegin_{-1}, gestureEnd_{-1};   // write gestures (M9-C)
    std::atomic<int32_t> scTrackId_{-1};     // sidechain source track (Phase C)
    const float* scBuf_ = nullptr;           // engine-handed sidechain buffer (audio thread)
    int32_t scFrames_ = 0;
    bool hasSidechain_ = false;              // plugin exposes a usable sidechain input bus
    int  scChanOffset_ = -1, scNumChans_ = 0; // where its channels sit in the process buffer
    juce::AudioBuffer<float> storage_;
    juce::MidiBuffer empty_;
    int maxBlock_ = 0;
    EditorHost editor_; // declared last: destroyed before plugin_
};

// In-process instantiation of a catalog entry (message thread).
std::unique_ptr<juce::AudioPluginInstance> instantiate(int catalogIndex, double sr, int maxBlock) {
    loadCatalogOnce();
    ensureJuceGuiInit();
    const auto types = catalog().getTypes();
    if (catalogIndex < 0 || catalogIndex >= types.size())
        return nullptr;
    juce::String err;
    auto inst = formatManager().createPluginInstance(types.getReference(catalogIndex), sr, maxBlock, err);
    return inst; // null on failure
}

} // namespace

extern "C" NOTA_API void nota_pluginhost_set_typing_octave(int32_t octave) {
    g_typingOctave.store(juce::jlimit(1, 5, (int)octave));
}

extern "C" NOTA_API void nota_pluginhost_set_typing_active(int32_t active) {
    g_typingActive.store(active != 0);
}

static bool isImageNonBlank(const juce::Image& img) {
    if (!img.isValid() || img.getWidth() <= 10 || img.getHeight() <= 10) return false;
    juce::Image::BitmapData bmData(img, juce::Image::BitmapData::readOnly);
    int nonBlackPixels = 0;
    int totalSampled = 0;
    int stepX = juce::jmax(1, img.getWidth() / 40);
    int stepY = juce::jmax(1, img.getHeight() / 40);
    for (int y = 0; y < img.getHeight(); y += stepY) {
        for (int x = 0; x < img.getWidth(); x += stepX) {
            auto c = bmData.getPixelColour(x, y);
            totalSampled++;
            if (c.getAlpha() > 30 && (c.getRed() > 25 || c.getGreen() > 25 || c.getBlue() > 25)) {
                nonBlackPixels++;
            }
        }
    }
    return totalSampled > 0 && ((float)nonBlackPixels / (float)totalSampled > 0.03f);
}

class ThumbnailCaptureWindow : public juce::DocumentWindow {
public:
    ThumbnailCaptureWindow()
        : juce::DocumentWindow("ThumbnailCapture", juce::Colours::black, 0)
    {
        setUsingNativeTitleBar(false);
        setTitleBarHeight(0);
    }
    ~ThumbnailCaptureWindow() override {
        clearContentComponent();
    }
    void closeButtonPressed() override {}
};

static juce::Image captureEditorImage(juce::AudioProcessorEditor* editor, juce::DocumentWindow& win) {
    if (editor == nullptr) return {};
    editor->repaint();

#if JUCE_WINDOWS || defined(_WIN32)
    HWND mainHwnd = (HWND)win.getWindowHandle();
    if (mainHwnd == nullptr) mainHwnd = (HWND)editor->getWindowHandle();

    // Look for child HWND (many VST3 plugins embed a child HWND inside the container)
    HWND targetHwnd = mainHwnd;
    if (mainHwnd != nullptr) {
        HWND child = FindWindowEx(mainHwnd, NULL, NULL, NULL);
        if (child != nullptr) {
            targetHwnd = child;
        }
    }

    if (targetHwnd != nullptr) {
        RECT rc;
        GetWindowRect(targetHwnd, &rc);
        int winW = rc.right - rc.left;
        int winH = rc.bottom - rc.top;

        if (winW > 20 && winH > 20) {
            // 1. Direct Screen DC BitBlt (captures GPU Direct2D / DirectX / OpenGL / Metal surfaces)
            HDC hdcScreen = GetDC(NULL);
            HDC hdcMem = CreateCompatibleDC(hdcScreen);
            HBITMAP hbm = CreateCompatibleBitmap(hdcScreen, winW, winH);
            HGDIOBJ hOld = SelectObject(hdcMem, hbm);

            BitBlt(hdcMem, 0, 0, winW, winH, hdcScreen, rc.left, rc.top, SRCCOPY);

            BITMAPINFO bmi = {0};
            bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = winW;
            bmi.bmiHeader.biHeight = -winH;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            juce::Image img(juce::Image::ARGB, winW, winH, false);
            juce::Image::BitmapData bmData(img, juce::Image::BitmapData::readWrite);
            GetDIBits(hdcMem, hbm, 0, winH, bmData.data, &bmi, DIB_RGB_COLORS);

            for (int y = 0; y < winH; ++y) {
                uint8_t* row = bmData.getLinePointer(y);
                for (int x = 0; x < winW; ++x) {
                    row[x * 4 + 3] = 255;
                }
            }

            SelectObject(hdcMem, hOld);
            DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(NULL, hdcScreen);

            if (isImageNonBlank(img)) return img;
        }

        // 2. PrintWindow PW_RENDERFULLCONTENT or Window DC
        GetClientRect(targetHwnd, &rc);
        winW = rc.right - rc.left;
        winH = rc.bottom - rc.top;
        if (winW > 20 && winH > 20) {
            HDC hdcWnd = GetDC(targetHwnd);
            HDC hdcMem = CreateCompatibleDC(hdcWnd);
            HBITMAP hbm = CreateCompatibleBitmap(hdcWnd, winW, winH);
            HGDIOBJ hOld = SelectObject(hdcMem, hbm);

            if (PrintWindow(targetHwnd, hdcMem, 2 /* PW_RENDERFULLCONTENT */) ||
                PrintWindow(targetHwnd, hdcMem, 0) ||
                BitBlt(hdcMem, 0, 0, winW, winH, hdcWnd, 0, 0, SRCCOPY))
            {
                BITMAPINFO bmi = {0};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = winW;
                bmi.bmiHeader.biHeight = -winH;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                juce::Image img(juce::Image::ARGB, winW, winH, false);
                juce::Image::BitmapData bmData(img, juce::Image::BitmapData::readWrite);
                GetDIBits(hdcMem, hbm, 0, winH, bmData.data, &bmi, DIB_RGB_COLORS);

                for (int y = 0; y < winH; ++y) {
                    uint8_t* row = bmData.getLinePointer(y);
                    for (int x = 0; x < winW; ++x) {
                        row[x * 4 + 3] = 255;
                    }
                }

                SelectObject(hdcMem, hOld);
                DeleteObject(hbm);
                DeleteDC(hdcMem);
                ReleaseDC(targetHwnd, hdcWnd);

                if (isImageNonBlank(img)) return img;
            } else {
                SelectObject(hdcMem, hOld);
                DeleteObject(hbm);
                DeleteDC(hdcMem);
                ReleaseDC(targetHwnd, hdcWnd);
            }
        }
    }
#endif

    // 3. JUCE Component snapshot
    auto compSnap = editor->createComponentSnapshot(editor->getLocalBounds(), false, 1.0f);
    if (isImageNonBlank(compSnap)) return compSnap;

    return {};
}

extern "C" NOTA_API int32_t nota_pluginhost_capture_plugin_thumbnail(const char* identifier, const char* out_png_path) {
    if (identifier == nullptr || out_png_path == nullptr) return 0;
    ensureJuceGuiInit();
    loadCatalogOnce();

    int idx = nota_pluginhost_index_of_id(identifier);
    if (idx < 0 || idx >= catalog().getNumTypes()) {
        // Fallback search by name or substring
        juce::String want{juce::CharPointer_UTF8(identifier)};
        for (int i = 0; i < catalog().getNumTypes(); ++i) {
            auto* t = catalog().getType(i);
            if (t != nullptr && (t->name.equalsIgnoreCase(want) || t->fileOrIdentifier == want || t->createIdentifierString() == want)) {
                idx = i;
                break;
            }
        }
    }

    const juce::PluginDescription* desc = (idx >= 0 && idx < catalog().getNumTypes()) ? catalog().getType(idx) : nullptr;
    juce::Image finalImg;

    if (desc != nullptr) {
        try {
            juce::String err;
            auto inst = formatManager().createPluginInstance(*desc, 44100.0, 512, err);
            if (inst != nullptr) {
                inst->prepareToPlay(44100.0, 512);

                // 1. Try plugin's custom GUI
                std::unique_ptr<juce::AudioProcessorEditor> editor;
                if (inst->hasEditor()) {
                    try {
                        editor.reset(inst->createEditorIfNeeded());
                    } catch (...) {}
                }

                if (editor != nullptr) {
                    int w = editor->getWidth();
                    int h = editor->getHeight();
                    if (w <= 50) w = 800;
                    if (h <= 50) h = 500;
                    editor->setSize(w, h);

                    ThumbnailCaptureWindow win;
                    win.setContentNonOwned(editor.get(), true);
                    win.centreWithSize(w, h);
                    win.setVisible(true);
                    win.addToDesktop(juce::ComponentPeer::windowIsTemporary);

#if JUCE_WINDOWS || defined(_WIN32)
                    MSG msg;
                    DWORD start = GetTickCount();
                    while (GetTickCount() - start < 350) {
                        while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE)) {
                            TranslateMessage(&msg);
                            DispatchMessage(&msg);
                        }
                        Sleep(10);
                    }
#else
                    juce::MessageManager::getInstance()->runDispatchLoopUntil(350);
#endif

                    auto captured = captureEditorImage(editor.get(), win);
                    if (isImageNonBlank(captured)) {
                        finalImg = captured;
                    }

                    win.clearContentComponent();
                    win.setVisible(false);
                }

                // 2. If custom GUI was blank or failed, use GenericAudioProcessorEditor
                if (!isImageNonBlank(finalImg)) {
                    try {
                        auto genericEditor = std::make_unique<juce::GenericAudioProcessorEditor>(*inst);
                        int gw = 700;
                        int gh = 450;
                        genericEditor->setSize(gw, gh);

                        ThumbnailCaptureWindow win;
                        win.setContentNonOwned(genericEditor.get(), true);
                        win.centreWithSize(gw, gh);
                        win.setVisible(true);
                        win.addToDesktop(juce::ComponentPeer::windowIsTemporary);

#if JUCE_WINDOWS || defined(_WIN32)
                        MSG msg;
                        DWORD start = GetTickCount();
                        while (GetTickCount() - start < 150) {
                            while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE)) {
                                TranslateMessage(&msg);
                                DispatchMessage(&msg);
                            }
                            Sleep(5);
                        }
#else
                        juce::MessageManager::getInstance()->runDispatchLoopUntil(150);
#endif

                        auto genCaptured = captureEditorImage(genericEditor.get(), win);
                        if (isImageNonBlank(genCaptured)) {
                            finalImg = genCaptured;
                        }

                        win.clearContentComponent();
                        win.setVisible(false);
                    } catch (...) {}
                }
            }
        } catch (...) {
            // Safe catch on unexpected plugin initialization crash
        }
    }

    if (finalImg.isValid()) {
        juce::File outFile(out_png_path);
        outFile.getParentDirectory().createDirectory();
        juce::FileOutputStream stream(outFile);
        if (stream.openedOk()) {
            juce::PNGImageFormat png;
            if (png.writeImageToStream(finalImg, stream))
                return 1;
        }
    }
    return 0;
}

namespace nota {

std::shared_ptr<Instrument> createPluginInstrument(int32_t catalogIndex, double sr, int32_t maxBlock) {
    const auto types = catalog().getTypes();
    if (catalogIndex >= 0 && catalogIndex < types.size() && !types.getReference(catalogIndex).isInstrument)
        return nullptr; // not an instrument
    auto inst = instantiate(catalogIndex, sr, maxBlock);
    if (!inst) return nullptr;
    return std::make_shared<PluginInstrument>(std::move(inst), sr, maxBlock);
}

std::shared_ptr<Device> createPluginEffect(int32_t catalogIndex, double sr, int32_t maxBlock) {
    auto inst = instantiate(catalogIndex, sr, maxBlock);
    if (!inst) return nullptr;
    return std::make_shared<PluginEffect>(std::move(inst), sr, maxBlock);
}

void setPluginHostNoteSink(PluginHostNoteSink sink) {
    g_hostNoteSink = sink;
}

} // namespace nota
