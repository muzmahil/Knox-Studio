// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// knox-scanworker — out-of-process plugin scanner (M3-1, FR-33/NFR-5).
//
// The host (PluginHost) launches this once per candidate plugin so a crashing
// or hanging plugin cannot take down the DAW: a crash here is just a non-zero
// child exit, a hang is killed by the parent's timeout. We instantiate the
// requested plugin's type(s) and print their PluginDescription(s) as XML to
// stdout; the parent parses that back into its KnownPluginList.
//
// Usage: knox-scanworker <formatName> <fileOrIdentifier> [--no-validate]
//   formatName       e.g. "AudioUnit", "VST3", or "VST"
//   fileOrIdentifier the format-specific plugin id/path from searchPathsForPlugins
//   --no-validate    optional flag to skip deep instantiation validation

#include <juce_audio_processors/juce_audio_processors.h>

#if JUCE_WINDOWS || defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace {
bool isValidVst2Dll(const juce::File& file) {
    if (!file.hasFileExtension(".dll")) return false;
    // Fast check: inspect DLL exports for VST entry points without executing code.
    HMODULE hMod = LoadLibraryExW(file.getFullPathName().toWideCharPointer(), NULL, DONT_RESOLVE_DLL_REFERENCES);
    if (!hMod) return false;
    bool hasVstEntry = (GetProcAddress(hMod, "VSTPluginMain") != NULL) ||
                       (GetProcAddress(hMod, "main") != NULL) ||
                       (GetProcAddress(hMod, "main_macho") != NULL);
    FreeLibrary(hMod);
    return hasVstEntry;
}
} // namespace
#endif

#include <iostream>

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
            if (c.getAlpha() > 20 && (c.getRed() > 15 || c.getGreen() > 15 || c.getBlue() > 15)) {
                nonBlackPixels++;
            }
        }
    }
    return totalSampled > 0 && ((float)nonBlackPixels / (float)totalSampled > 0.02f);
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

    if (mainHwnd != nullptr) {
        // 1. Direct Screen DC BitBlt (captures GPU Direct2D / DirectX / OpenGL surfaces)
        RECT rc;
        if (GetWindowRect(mainHwnd, &rc)) {
            int winW = rc.right - rc.left;
            int winH = rc.bottom - rc.top;

            if (winW > 20 && winH > 20) {
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
                        row[x * bmData.pixelStride + 3] = 255;
                    }
                }

                SelectObject(hdcMem, hOld);
                DeleteObject(hbm);
                DeleteDC(hdcMem);
                ReleaseDC(NULL, hdcScreen);

                if (isImageNonBlank(img)) return img;
            }
        }

        // 2. PrintWindow PW_RENDERFULLCONTENT or Window DC fallback
        if (GetClientRect(mainHwnd, &rc)) {
            int winW = rc.right - rc.left;
            int winH = rc.bottom - rc.top;
            if (winW > 20 && winH > 20) {
                HDC hdcWnd = GetDC(mainHwnd);
                HDC hdcMem = CreateCompatibleDC(hdcWnd);
                HBITMAP hbm = CreateCompatibleBitmap(hdcWnd, winW, winH);
                HGDIOBJ hOld = SelectObject(hdcMem, hbm);

                if (PrintWindow(mainHwnd, hdcMem, 2 /* PW_RENDERFULLCONTENT */) ||
                    PrintWindow(mainHwnd, hdcMem, 0) ||
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
                            row[x * bmData.pixelStride + 3] = 255;
                        }
                    }

                    SelectObject(hdcMem, hOld);
                    DeleteObject(hbm);
                    DeleteDC(hdcMem);
                    ReleaseDC(mainHwnd, hdcWnd);

                    if (isImageNonBlank(img)) return img;
                } else {
                    SelectObject(hdcMem, hOld);
                    DeleteObject(hbm);
                    DeleteDC(hdcMem);
                    ReleaseDC(mainHwnd, hdcWnd);
                }
            }
        }
    }
#endif

    // 3. JUCE Component snapshot fallback
    auto compSnap = editor->createComponentSnapshot(editor->getLocalBounds(), false, 1.0f);
    if (compSnap.isValid() && compSnap.getWidth() > 20 && compSnap.getHeight() > 20)
        return compSnap;

    return {};
}

int main(int argc, char** argv) {
    if (argc < 3) {
        std::cerr << "usage: knox-scanworker <formatName> <fileOrIdentifier> [--no-validate]\n"
                  << "   or: knox-scanworker --thumbnail <formatName> <fileOrIdentifier> <outPngPath> [optionalName]\n";
        return 2;
    }

    // --- Thumbnail Generation Mode (Crash-isolated child process) ---
    if (juce::String(argv[1]) == "--thumbnail" || juce::String(argv[1]) == "-t") {
        if (argc < 5) {
            std::cerr << "usage: knox-scanworker --thumbnail <formatName> <fileOrIdentifier> <outPngPath> [optionalName]\n";
            return 2;
        }

        const juce::String formatName{juce::CharPointer_UTF8(argv[2])};
        const juce::String fileOrId{juce::CharPointer_UTF8(argv[3])};
        const juce::String outPngPath{juce::CharPointer_UTF8(argv[4])};
        const juce::String optName = (argc > 5) ? juce::String{juce::CharPointer_UTF8(argv[5])} : juce::String();

        juce::ScopedJuceInitialiser_GUI juceInit;
        juce::AudioPluginFormatManager formats;
        formats.addDefaultFormats();

        juce::Image finalImg;

        for (auto* format : formats.getFormats()) {
            if (formatName != "ANY" && formatName.isNotEmpty() && format->getName() != formatName)
                continue;

            juce::OwnedArray<juce::PluginDescription> found;
            try {
                format->findAllTypesForFile(found, fileOrId);
                if (found.isEmpty() && !juce::File(fileOrId).exists()) {
                    juce::FileSearchPath searchPath = format->getDefaultLocationsToSearch();
                    auto files = format->searchPathsForPlugins(searchPath, true);
                    for (const auto& f : files) {
                        if (optName.isNotEmpty() && !f.containsIgnoreCase(optName)) continue;
                        format->findAllTypesForFile(found, f);
                        if (!found.isEmpty()) break;
                    }
                }
            } catch (...) {}

            for (auto* desc : found) {
                if (desc == nullptr) continue;

                // If multiple types were found in library/search, match by name or id if specified
                if (found.size() > 1 && optName.isNotEmpty() && !desc->name.equalsIgnoreCase(optName) && !desc->fileOrIdentifier.containsIgnoreCase(optName))
                    continue;

                try {
                    juce::String err;
                    auto inst = format->createInstanceFromDescription(*desc, 44100.0, 512, err);
                    if (inst != nullptr) {
                        inst->prepareToPlay(44100.0, 512);

                        // 1. Try plugin's custom GUI editor
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

                            // Message pump so VST3 / Direct2D / OpenGL initializes and paints
#if JUCE_WINDOWS || defined(_WIN32)
                            MSG msg;
                            DWORD start = GetTickCount();
                            while (GetTickCount() - start < 500) {
                                while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE)) {
                                    TranslateMessage(&msg);
                                    DispatchMessage(&msg);
                                }
                                Sleep(10);
                            }
#else
                            juce::MessageManager::getInstance()->runDispatchLoopUntil(500);
#endif

                            auto captured = captureEditorImage(editor.get(), win);
                            if (captured.isValid() && captured.getWidth() > 20 && captured.getHeight() > 20) {
                                finalImg = captured;
                            }

                            win.clearContentComponent();
                            win.setVisible(false);
                        }

                        // 2. If custom GUI failed or was empty, render real parameter controls via GenericAudioProcessorEditor
                        if (!finalImg.isValid() || finalImg.getWidth() <= 20) {
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
                                if (genCaptured.isValid() && genCaptured.getWidth() > 20 && genCaptured.getHeight() > 20) {
                                    finalImg = genCaptured;
                                }

                                win.clearContentComponent();
                                win.setVisible(false);
                            } catch (...) {}
                        }
                    }
                } catch (...) {}

                if (finalImg.isValid() && finalImg.getWidth() > 20) break;
            }
            if (finalImg.isValid() && finalImg.getWidth() > 20) break;
        }

        if (finalImg.isValid() && finalImg.getWidth() > 20 && finalImg.getHeight() > 20) {
            juce::File outFile(outPngPath);
            outFile.getParentDirectory().createDirectory();
            if (outFile.exists()) outFile.deleteFile();
            juce::FileOutputStream stream(outFile);
            if (stream.openedOk()) {
                juce::PNGImageFormat png;
                if (png.writeImageToStream(finalImg, stream)) {
                    stream.flush();
                    return 0;
                }
            }
        }
        return 1;
    }

    // --- Standard Plugin Scanner Mode ---
    bool validate = true;
    for (int i = 3; i < argc; ++i) {
        juce::String arg{juce::CharPointer_UTF8(argv[i])};
        if (arg == "--no-validate" || arg == "no-validate") {
            validate = false;
        }
    }

    const juce::String formatName{juce::CharPointer_UTF8(argv[1])};
    const juce::String fileOrId{juce::CharPointer_UTF8(argv[2])};

#if JUCE_WINDOWS || defined(_WIN32)
    if (formatName == "VST") {
        juce::File pluginFile(fileOrId);
        if (!isValidVst2Dll(pluginFile)) {
            std::cerr << "skipping non-vst or invalid DLL: " << fileOrId << "\n";
            return 1;
        }
    }
#endif

    // AU/VST/VST3 probing touches GUI/message-loop machinery.
    juce::ScopedJuceInitialiser_GUI juceInit;

    juce::AudioPluginFormatManager formats;
    formats.addDefaultFormats();

    for (auto* format : formats.getFormats()) {
        if (format->getName() != formatName)
            continue;

        juce::OwnedArray<juce::PluginDescription> found;
        format->findAllTypesForFile(found, fileOrId); // may crash for a bad plugin — that's the point

        juce::XmlElement root("plugins");
        for (auto* desc : found) {
            if (desc == nullptr) continue;

            if (validate) {
                // Strict validation: verify basic description & test instantiate
                if (desc->name.trim().isEmpty()) continue;

                juce::String err;
                auto instance = format->createInstanceFromDescription(*desc, 44100.0, 512, err);
                if (instance != nullptr) {
                    root.addChildElement(desc->createXml().release());
                } else {
                    std::cerr << "validation failed for: " << desc->name << " (" << err << ")\n";
                }
            } else {
                root.addChildElement(desc->createXml().release());
            }
        }

        // Bracket the XML with sentinels: some plugins (e.g. Maschine) write their
        // own log lines to stdout while loading, which would otherwise corrupt the
        // document the parent parses. The parent slices out the text between these.
        std::cout << "<<<NOTA_PLUGINS_BEGIN>>>\n"
                  << root.toString()
                  << "\n<<<NOTA_PLUGINS_END>>>" << std::endl;
        return 0;
    }

    std::cerr << "unknown format: " << formatName << "\n";
    return 3;
}
