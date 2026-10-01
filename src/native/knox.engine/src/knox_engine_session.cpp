// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// C ABI — Session view (M5): scenes/slots, launch/stop/record, quantization,
// session<->arrangement transfer, session notes, and session audio slots (M7-6b).

#include "knox_engine_internal.h"

#include <string>

extern "C" {

int32_t nota_session_scene_count(const KnoxEngine* e) {
    return e ? CENG(e)->sceneCount() : 0;
}
NotaResult nota_session_add_midi_clip(KnoxEngine* e, int32_t track_id, int32_t scene, double length_beats) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->addSessionMidiClip(track_id, scene, length_beats) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
double nota_session_slot_length(const KnoxEngine* e, int32_t track_id, int32_t scene) {
    return e ? CENG(e)->sessionSlotLength(track_id, scene) : 0.0;
}
NotaResult nota_session_set_slot_length(KnoxEngine* e, int32_t track_id, int32_t scene, double length_beats) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setSessionSlotLength(track_id, scene, length_beats) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t nota_session_slot_state(const KnoxEngine* e, int32_t track_id, int32_t scene) {
    return e ? CENG(e)->sessionSlotState(track_id, scene) : 0;
}
float nota_session_slot_gain(const KnoxEngine* e, int32_t track_id, int32_t scene) {
    return e ? CENG(e)->sessionSlotGain(track_id, scene) : 1.0f;
}
NotaResult nota_session_set_slot_gain(KnoxEngine* e, int32_t track_id, int32_t scene, float gain) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setSessionSlotGain(track_id, scene, gain) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
NotaResult nota_session_set_launch_quant(KnoxEngine* e, double beats) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->setLaunchQuant(beats);
    return NOTA_OK;
}
NotaResult nota_session_launch_slot(KnoxEngine* e, int32_t track_id, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->launchSlot(track_id, scene);
    return NOTA_OK;
}
NotaResult nota_session_stop_slot(KnoxEngine* e, int32_t track_id) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->stopSlot(track_id);
    return NOTA_OK;
}
NotaResult nota_session_launch_scene(KnoxEngine* e, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->launchScene(scene);
    return NOTA_OK;
}
NotaResult nota_session_stop_scene(KnoxEngine* e, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->stopScene(scene);
    return NOTA_OK;
}
NotaResult nota_session_stop_all(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->stopAllSession();
    return NOTA_OK;
}
NotaResult nota_session_back_to_arrangement(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->backToArrangement();
    return NOTA_OK;
}
int32_t nota_arrangement_active(const KnoxEngine* e) {
    return (e && CENG(e)->arrangementActive()) ? 1 : 0;
}
NotaResult nota_session_clear_slot(KnoxEngine* e, int32_t track_id, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->clearSessionSlot(track_id, scene) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t nota_session_add_scene(KnoxEngine* e) {
    return e ? ENG(e)->addScene() : -1;
}
NotaResult nota_session_remove_scene(KnoxEngine* e, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->removeScene(scene) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
NotaResult nota_session_record_slot(KnoxEngine* e, int32_t track_id, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->recordSessionSlot(track_id, scene);
    return NOTA_OK;
}
NotaResult nota_session_stop_record(KnoxEngine* e) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    ENG(e)->stopSessionRecord();
    return NOTA_OK;
}
int32_t nota_session_slot_to_arrangement(KnoxEngine* e, int32_t track_id, int32_t scene, double start_beat) {
    return e ? ENG(e)->sessionSlotToArrangement(track_id, scene, start_beat) : -1;
}
NotaResult nota_session_from_arrangement(KnoxEngine* e, int32_t track_id, int32_t clip_index, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->arrangementClipToSession(track_id, clip_index, scene) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
NotaResult nota_session_audio_from_arrangement(KnoxEngine* e, int32_t track_id, int32_t clip_index, int32_t scene) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->arrangementAudioClipToSession(track_id, clip_index, scene) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t nota_session_audio_record_selftest(KnoxEngine* e) {
    return (e && ENG(e)->sessionAudioRecordSelfTest()) ? 1 : 0;
}
NotaResult nota_session_set_notes(KnoxEngine* e, int32_t track_id, int32_t scene,
                                  const NotaNoteData* notes, int32_t count) {
    if (!e || (count > 0 && !notes) || count < 0) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setSessionNotes(track_id, scene, notes, count) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t nota_session_get_notes(const KnoxEngine* e, int32_t track_id, int32_t scene,
                               NotaNoteData* out, int32_t max_notes) {
    if (!e || !out || max_notes <= 0) return 0;
    return CENG(e)->getSessionNotes(track_id, scene, out, max_notes);
}
int32_t nota_session_note_count(const KnoxEngine* e, int32_t track_id, int32_t scene) {
    return e ? CENG(e)->sessionNoteCount(track_id, scene) : 0;
}

// ---- Session audio slots (M7-6b) ------------------------------------------

int32_t nota_session_get_audio_slot(const KnoxEngine* e, int32_t track_id, int32_t scene, KnoxSessionAudioSlot* out) {
    return (e && CENG(e)->sessionAudioSlotInfo(track_id, scene, out)) ? 1 : 0;
}
int32_t nota_session_add_audio_clip(KnoxEngine* e, int32_t track_id, int32_t scene, const char* path,
                                    double length_beats, double source_offset_frames,
                                    int64_t length_frames, float gain) {
    if (!e || !path) return 0;
    return ENG(e)->addSessionAudioClip(track_id, scene, std::string(path), length_beats,
                                       source_offset_frames, length_frames, gain) ? 1 : 0;
}
int32_t nota_session_add_audio_file(KnoxEngine* e, int32_t track_id, int32_t scene, const char* path) {
    if (!e || !path) return 0;
    return ENG(e)->addSessionAudioFile(track_id, scene, std::string(path)) ? 1 : 0;
}

} // extern "C"
