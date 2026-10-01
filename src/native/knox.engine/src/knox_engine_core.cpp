// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// C ABI — the managed/native boundary (AR-7): engine-global slice.
// Lifecycle, transport, master mixer, realtime control (notes/recording/poll),
// undo/redo, offline render, meters, audio preview.
// Thin translation layer: validates handles, catches nothing across the line,
// returns integer result codes.

#include "knox_engine_internal.h"

#include <new>
#include <string>

#define NOTA_VERSION_STRING "0.1.0"

extern "C" {

const char* knox_engine_version(void) { return NOTA_VERSION_STRING; }

KnoxEngine* knox_engine_create(void) {
    return reinterpret_cast<KnoxEngine*>(new (std::nothrow) Engine());
}

void knox_engine_destroy(KnoxEngine* engine) {
    delete reinterpret_cast<Engine*>(engine);
}

NotaResult knox_engine_start(KnoxEngine* engine) {
    if (!engine) return NOTA_ERR_INVALID_ARG;
    return reinterpret_cast<Engine*>(engine)->start() ? NOTA_OK : NOTA_ERR_AUDIO_DEVICE;
}

NotaResult knox_engine_stop(KnoxEngine* engine) {
    if (!engine) return NOTA_ERR_INVALID_ARG;
    reinterpret_cast<Engine*>(engine)->stop();
    return NOTA_OK;
}

NotaResult knox_engine_set_test_tone(KnoxEngine* engine, int32_t enabled) {
    if (!engine) return NOTA_ERR_INVALID_ARG;
    reinterpret_cast<Engine*>(engine)->setToneEnabled(enabled != 0);
    return NOTA_OK;
}

NotaResult knox_engine_set_frequency(KnoxEngine* engine, float hz) {
    if (!engine) return NOTA_ERR_INVALID_ARG;
    if (!(hz > 0.0f) || hz > 20000.0f) return NOTA_ERR_INVALID_ARG;
    reinterpret_cast<Engine*>(engine)->setFrequency(hz);
    return NOTA_OK;
}

double knox_engine_sample_rate(const KnoxEngine* engine) {
    if (!engine) return 0.0;
    return reinterpret_cast<const Engine*>(engine)->sampleRate();
}

// ---- Audio preview / audition (M7-4a) -------------------------------------

NotaResult knox_engine_preview_file(KnoxEngine* e, const char* path) {
    if (!e || !path) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->previewFile(std::string(path)) ? NOTA_OK : NOTA_ERR_UNKNOWN;
}
NotaResult knox_engine_stop_preview(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->stopPreview();
    return NOTA_OK;
}
int32_t knox_engine_preview_active(const KnoxEngine* e) {
    return (e && CENG(e)->isPreviewActive()) ? 1 : 0;
}
int32_t knox_engine_preview_selftest(KnoxEngine* e) {
    return (e && ENG(e)->previewSelfTest()) ? 1 : 0;
}

int32_t knox_engine_xrun_count(const KnoxEngine* e) {
    return e ? CENG(e)->xrunCount() : 0;
}
int32_t knox_engine_xrun_selftest(KnoxEngine* e) {
    return (e && ENG(e)->xrunSelfTest()) ? 1 : 0;
}
float knox_engine_cpu_load(const KnoxEngine* e) {
    return e ? CENG(e)->cpuLoad() : 0.0f;
}

// ---- Transport ------------------------------------------------------------

NotaResult nota_transport_play(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->transportPlay(); return NOTA_OK;
}
NotaResult nota_transport_stop(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->transportStop(); return NOTA_OK;
}
NotaResult nota_transport_set_bpm(KnoxEngine* e, double bpm) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    if (!(bpm > 0.0) || bpm > 999.0) return NOTA_ERR_INVALID_ARG;
    ENG(e)->setBpm(bpm); return NOTA_OK;
}
double nota_transport_bpm(const KnoxEngine* e) { return e ? CENG(e)->bpm() : 120.0; }
NotaResult nota_transport_set_time_signature(KnoxEngine* e, int32_t num, int32_t denom) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    if (num <= 0 || denom <= 0) return NOTA_ERR_INVALID_ARG;
    ENG(e)->setTimeSignature(num, denom); return NOTA_OK;
}
NotaResult nota_transport_set_loop(KnoxEngine* e, int32_t enabled, double s, double en) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->setLoop(enabled != 0, s, en); return NOTA_OK;
}
NotaResult nota_transport_set_metronome(KnoxEngine* e, int32_t enabled) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->setMetronome(enabled != 0); return NOTA_OK;
}
NotaResult nota_transport_play_click(KnoxEngine* e, int32_t downbeat) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->playMetronomeClick(downbeat != 0); return NOTA_OK;
}
NotaResult knox_transport_play_click(KnoxEngine* e, int32_t downbeat) {
    return nota_transport_play_click(e, downbeat);
}
NotaResult nota_transport_seek(KnoxEngine* e, double beat) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->seekBeats(beat); return NOTA_OK;
}
double nota_transport_position_beats(const KnoxEngine* e) {
    return e ? CENG(e)->positionBeats() : 0.0;
}
int32_t nota_transport_is_playing(const KnoxEngine* e) {
    return (e && CENG(e)->isPlaying()) ? 1 : 0;
}
int32_t nota_transport_loop_enabled(const KnoxEngine* e) {
    return (e && CENG(e)->loopEnabled()) ? 1 : 0;
}
double nota_transport_loop_start(const KnoxEngine* e) {
    return e ? CENG(e)->loopStart() : 0.0;
}
double nota_transport_loop_end(const KnoxEngine* e) {
    return e ? CENG(e)->loopEnd() : 0.0;
}

// ---- Mixer ----------------------------------------------------------------

NotaResult knox_engine_set_master_volume(KnoxEngine* e, float v) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    if (v < 0.0f || v > 4.0f) return NOTA_ERR_INVALID_ARG;
    ENG(e)->setMasterVolume(v); return NOTA_OK;
}

// ---- Live MIDI, arming & recording (M2) -----------------------------------

NotaResult knox_engine_note_on(KnoxEngine* e, int32_t pitch, float velocity) {
    if (!e || pitch < 0 || pitch > 127) return NOTA_ERR_INVALID_ARG;
    ENG(e)->noteOn(pitch, velocity); return NOTA_OK;
}
NotaResult knox_engine_note_off(KnoxEngine* e, int32_t pitch) {
    if (!e || pitch < 0 || pitch > 127) return NOTA_ERR_INVALID_ARG;
    ENG(e)->noteOff(pitch); return NOTA_OK;
}
NotaResult knox_engine_set_midi_velocity_settings(KnoxEngine* e, int32_t sensitivity_enabled, float fixed_velocity, int32_t curve_type) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->setMidiVelocitySettings(sensitivity_enabled != 0, fixed_velocity, curve_type);
    return NOTA_OK;
}
void knox_engine_set_audition_track(KnoxEngine* e, int32_t track_id) {
    if (e) ENG(e)->setAuditionTrack(track_id);
}
NotaResult knox_engine_set_recording(KnoxEngine* e, int32_t enabled) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->setRecording(enabled != 0); return NOTA_OK;
}
int32_t knox_engine_is_recording(const KnoxEngine* e) {
    return (e && CENG(e)->isRecording()) ? 1 : 0;
}
int32_t knox_engine_record_start_status(const KnoxEngine* e) {
    return e ? CENG(e)->recordStartStatus() : 0;
}
int32_t knox_engine_audio_record_track(const KnoxEngine* e) {
    return e ? CENG(e)->audioRecordTrackId() : 0;
}
double knox_engine_audio_record_start_beat(const KnoxEngine* e) {
    return e ? CENG(e)->audioRecordStartBeat() : 0.0;
}
double knox_engine_audio_record_length_beats(const KnoxEngine* e) {
    return e ? CENG(e)->audioRecordLengthBeats() : 0.0;
}
int32_t knox_engine_master_track_id(const KnoxEngine* e) {
    return e ? CENG(e)->masterTrackId() : 0;
}
int32_t knox_engine_audio_record_peaks(const KnoxEngine* e, float* out, int32_t maxPoints) {
    return e ? CENG(e)->audioRecordPeaks(out, maxPoints) : 0;
}
int32_t nota_audio_record_selftest(KnoxEngine* e) {
    return (e && ENG(e)->audioRecordSelfTest()) ? 1 : 0;
}
NotaResult knox_engine_poll(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG; ENG(e)->poll(); return NOTA_OK;
}

// ---- Undo / redo (M6-6) ---------------------------------------------------

NotaResult knox_engine_undo(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->undo() ? NOTA_OK : NOTA_ERR_UNKNOWN;
}
NotaResult knox_engine_redo(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->redo() ? NOTA_OK : NOTA_ERR_UNKNOWN;
}
int32_t knox_engine_can_undo(const KnoxEngine* e) {
    return e ? (CENG(e)->canUndo() ? 1 : 0) : 0;
}
int32_t knox_engine_can_redo(const KnoxEngine* e) {
    return e ? (CENG(e)->canRedo() ? 1 : 0) : 0;
}

// ---- Project reset (M7-6) -------------------------------------------------

void knox_engine_reset(KnoxEngine* e) {
    if (e) ENG(e)->reset();
}

// ---- Meters (M6-2) --------------------------------------------------------

int32_t knox_engine_track_meter(const KnoxEngine* e, int32_t track_id, KnoxMeter* out) {
    if (!e || !out) return 0;
    return CENG(e)->trackMeter(track_id, out->peak_l, out->peak_r, out->rms_l, out->rms_r) ? 1 : 0;
}
NotaResult knox_engine_master_meter(const KnoxEngine* e, KnoxMeter* out) {
    if (!e || !out) return NOTA_ERR_INVALID_ARG;
    CENG(e)->masterMeter(out->peak_l, out->peak_r, out->rms_l, out->rms_r);
    return NOTA_OK;
}

// ---- Offline render -------------------------------------------------------

NotaResult knox_engine_render_offline(KnoxEngine* e, float* out, int32_t frames) {
    if (!e || !out || frames <= 0) return NOTA_ERR_INVALID_ARG;
    ENG(e)->renderOffline(out, frames);
    return NOTA_OK;
}

NotaResult knox_engine_render_offline_at(KnoxEngine* e, float* out, int32_t frames, double sr) {
    if (!e || !out || frames <= 0 || sr <= 0.0) return NOTA_ERR_INVALID_ARG;
    ENG(e)->renderOffline(out, frames, sr);
    return NOTA_OK;
}

// ---- Aliases for nota_engine_* C ABI --------------------------------------

const char* nota_engine_version(void) { return knox_engine_version(); }
KnoxEngine* nota_engine_create(void) { return knox_engine_create(); }
void nota_engine_destroy(KnoxEngine* engine) { knox_engine_destroy(engine); }
NotaResult nota_engine_start(KnoxEngine* engine) { return knox_engine_start(engine); }
NotaResult nota_engine_stop(KnoxEngine* engine) { return knox_engine_stop(engine); }
NotaResult nota_engine_set_test_tone(KnoxEngine* engine, int32_t enabled) { return knox_engine_set_test_tone(engine, enabled); }
NotaResult nota_engine_set_frequency(KnoxEngine* engine, float hz) { return knox_engine_set_frequency(engine, hz); }
double nota_engine_sample_rate(const KnoxEngine* engine) { return knox_engine_sample_rate(engine); }
NotaResult nota_engine_set_master_volume(KnoxEngine* engine, float volume) { return knox_engine_set_master_volume(engine, volume); }
int32_t nota_engine_track_meter(const KnoxEngine* engine, int32_t track_id, KnoxMeter* out) { return knox_engine_track_meter(engine, track_id, out); }
NotaResult nota_engine_master_meter(const KnoxEngine* engine, KnoxMeter* out) { return knox_engine_master_meter(engine, out); }
NotaResult nota_engine_note_on(KnoxEngine* engine, int32_t pitch, float velocity) { return knox_engine_note_on(engine, pitch, velocity); }
NotaResult nota_engine_note_off(KnoxEngine* engine, int32_t pitch) { return knox_engine_note_off(engine, pitch); }
NotaResult nota_engine_set_midi_velocity_settings(KnoxEngine* engine, int32_t sensitivity_enabled, float fixed_velocity, int32_t curve_type) {
    return knox_engine_set_midi_velocity_settings(engine, sensitivity_enabled, fixed_velocity, curve_type);
}
void nota_engine_set_audition_track(KnoxEngine* engine, int32_t track_id) { knox_engine_set_audition_track(engine, track_id); }
NotaResult nota_engine_set_recording(KnoxEngine* engine, int32_t enabled) { return knox_engine_set_recording(engine, enabled); }
int32_t nota_engine_is_recording(const KnoxEngine* engine) { return knox_engine_is_recording(engine); }
int32_t nota_engine_record_start_status(const KnoxEngine* engine) { return knox_engine_record_start_status(engine); }
int32_t nota_engine_audio_record_track(const KnoxEngine* engine) { return knox_engine_audio_record_track(engine); }
double nota_engine_audio_record_start_beat(const KnoxEngine* engine) { return knox_engine_audio_record_start_beat(engine); }
double nota_engine_audio_record_length_beats(const KnoxEngine* engine) { return knox_engine_audio_record_length_beats(engine); }
int32_t nota_engine_master_track_id(const KnoxEngine* engine) { return knox_engine_master_track_id(engine); }
int32_t nota_engine_audio_record_peaks(const KnoxEngine* engine, float* out, int32_t maxPoints) { return knox_engine_audio_record_peaks(engine, out, maxPoints); }
NotaResult nota_engine_poll(KnoxEngine* engine) { return knox_engine_poll(engine); }
NotaResult nota_engine_undo(KnoxEngine* engine) { return knox_engine_undo(engine); }
NotaResult nota_engine_redo(KnoxEngine* engine) { return knox_engine_redo(engine); }
int32_t nota_engine_can_undo(const KnoxEngine* engine) { return knox_engine_can_undo(engine); }
int32_t nota_engine_can_redo(const KnoxEngine* engine) { return knox_engine_can_redo(engine); }
void nota_engine_reset(KnoxEngine* engine) { knox_engine_reset(engine); }
NotaResult nota_engine_render_offline(KnoxEngine* engine, float* out, int32_t frames) { return knox_engine_render_offline(engine, out, frames); }
NotaResult nota_engine_render_offline_at(KnoxEngine* engine, float* out, int32_t frames, double sr) { return knox_engine_render_offline_at(engine, out, frames, sr); }
NotaResult nota_engine_preview_file(KnoxEngine* engine, const char* path) { return knox_engine_preview_file(engine, path); }
NotaResult nota_engine_stop_preview(KnoxEngine* engine) { return knox_engine_stop_preview(engine); }
int32_t nota_engine_preview_active(const KnoxEngine* engine) { return knox_engine_preview_active(engine); }
int32_t nota_engine_preview_selftest(KnoxEngine* engine) { return knox_engine_preview_selftest(engine); }
int32_t nota_engine_xrun_count(const KnoxEngine* engine) { return knox_engine_xrun_count(engine); }
int32_t nota_engine_xrun_selftest(KnoxEngine* engine) { return knox_engine_xrun_selftest(engine); }
float nota_engine_cpu_load(const KnoxEngine* engine) { return knox_engine_cpu_load(engine); }

} // extern "C"

