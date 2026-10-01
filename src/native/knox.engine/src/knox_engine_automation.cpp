// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// C ABI — parameter automation (M9): lane CRUD & points (read, A-phases),
// plugin-parameter lanes (B-phases), and write recording Touch/Latch/Write
// (C-phases), plus the automation self-tests.

#include "knox_engine_internal.h"

#include <string>

extern "C" {

int32_t knox_engine_automation_selftest(KnoxEngine* e) {
    return (e && ENG(e)->automationSelfTest()) ? 1 : 0;
}
int32_t knox_engine_automation_curve_selftest(KnoxEngine* e) {
    return (e && ENG(e)->automationCurveSelfTest()) ? 1 : 0;
}
int32_t knox_engine_plugin_automation_selftest(KnoxEngine* e) {
    return (e && ENG(e)->pluginAutomationSelfTest()) ? 1 : 0;
}
int32_t knox_engine_rack_selftest(KnoxEngine* e) {
    return (e && ENG(e)->rackSelfTest()) ? 1 : 0;
}
int32_t knox_engine_rack_device_selftest(KnoxEngine* e) {
    return (e && ENG(e)->rackDeviceSelfTest()) ? 1 : 0;
}
int32_t knox_engine_drum_rack_selftest(KnoxEngine* e) {
    return (e && ENG(e)->drumRackSelfTest()) ? 1 : 0;
}
int32_t nota_track_add_automation_lane(KnoxEngine* e, int32_t track_id,
        int32_t target, int32_t device_index, int32_t param_index) {
    return e ? ENG(e)->addAutomationLane(track_id, target, device_index, param_index) : -1;
}
int32_t nota_track_automation_lane_count(const KnoxEngine* e, int32_t track_id) {
    return e ? CENG(e)->automationLaneCount(track_id) : 0;
}
NotaResult nota_track_automation_lane_info(const KnoxEngine* e, int32_t track_id,
        int32_t lane_index, int32_t* out_target, int32_t* out_device_index,
        int32_t* out_param_index, int32_t* out_point_count) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return CENG(e)->automationLaneInfo(track_id, lane_index, out_target, out_device_index,
               out_param_index, out_point_count) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t nota_track_automation_get_points(const KnoxEngine* e, int32_t track_id,
        int32_t lane_index, NotaAutomationPoint* out, int32_t cap) {
    return e ? CENG(e)->getAutomationPoints(track_id, lane_index, out, cap) : 0;
}
NotaResult nota_track_automation_set_points(KnoxEngine* e, int32_t track_id,
        int32_t lane_index, const NotaAutomationPoint* pts, int32_t count) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setAutomationPoints(track_id, lane_index, pts, count) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
NotaResult nota_track_automation_set_points_live(KnoxEngine* e, int32_t track_id,
        int32_t lane_index, const NotaAutomationPoint* pts, int32_t count) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setAutomationPointsLive(track_id, lane_index, pts, count) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
NotaResult nota_track_remove_automation_lane(KnoxEngine* e, int32_t track_id, int32_t lane_index) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->removeAutomationLane(track_id, lane_index) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}
int32_t knox_engine_master_volume_automation_count(const KnoxEngine* e) {
    return e ? CENG(e)->masterVolumeAutomationCount() : 0;
}
int32_t knox_engine_master_volume_automation_get(const KnoxEngine* e, NotaAutomationPoint* out, int32_t cap) {
    return e ? CENG(e)->getMasterVolumeAutomation(out, cap) : 0;
}
NotaResult knox_engine_master_volume_automation_set(KnoxEngine* e, const NotaAutomationPoint* pts, int32_t count) {
    if (!e) return NOTA_ERR_INVALID_ARG;
    return ENG(e)->setMasterVolumeAutomation(pts, count) ? NOTA_OK : NOTA_ERR_INVALID_ARG;
}

// ---- plugin-parameter automation lanes (M9-B) -----------------------------

int32_t nota_track_add_plugin_automation_lane(KnoxEngine* e, int32_t track_id, int32_t device_index, const char* param_id) {
    return e ? ENG(e)->addPluginAutomationLane(track_id, device_index, param_id) : -1;
}
const char* nota_track_automation_lane_param_id(const KnoxEngine* e, int32_t track_id, int32_t lane_index) {
    static std::string s; // owned by the engine, valid until the next call
    s = e ? CENG(e)->automationLaneParamId(track_id, lane_index) : std::string{};
    return s.c_str();
}

// ---- automation write recording (M9-C) ------------------------------------

int32_t knox_engine_automation_write_selftest(KnoxEngine* e) {
    return (e && ENG(e)->automationWriteSelfTest()) ? 1 : 0;
}
void knox_engine_set_automation_write_mode(KnoxEngine* e, int32_t mode) {
    if (e) ENG(e)->setAutomationWriteMode(mode);
}
int32_t knox_engine_automation_write_mode(const KnoxEngine* e) {
    return e ? CENG(e)->automationWriteMode() : 0;
}
void nota_track_begin_automation_write(KnoxEngine* e, int32_t track_id,
        int32_t target, int32_t device_index, int32_t param_index, const char* param_id) {
    if (e) ENG(e)->beginAutomationWrite(track_id, target, device_index, param_index, param_id);
}
void nota_track_end_automation_write(KnoxEngine* e, int32_t track_id,
        int32_t target, int32_t device_index, int32_t param_index, const char* param_id) {
    if (e) ENG(e)->endAutomationWrite(track_id, target, device_index, param_index, param_id);
}
void nota_track_set_automation_arm(KnoxEngine* e, int32_t track_id,
        int32_t target, int32_t device_index, int32_t param_index, const char* param_id, int32_t armed) {
    if (e) ENG(e)->setAutomationArm(track_id, target, device_index, param_index, param_id, armed != 0);
}
int32_t nota_track_automation_armed(const KnoxEngine* e, int32_t track_id,
        int32_t target, int32_t device_index, int32_t param_index, const char* param_id) {
    return (e && CENG(e)->automationArmed(track_id, target, device_index, param_index, param_id)) ? 1 : 0;
}

// ---- Aliases for nota_engine_* C ABI --------------------------------------
int32_t nota_engine_automation_selftest(KnoxEngine* e) { return knox_engine_automation_selftest(e); }
int32_t nota_engine_automation_curve_selftest(KnoxEngine* e) { return knox_engine_automation_curve_selftest(e); }
int32_t nota_engine_plugin_automation_selftest(KnoxEngine* e) { return knox_engine_plugin_automation_selftest(e); }
int32_t nota_engine_rack_selftest(KnoxEngine* e) { return knox_engine_rack_selftest(e); }
int32_t nota_engine_rack_device_selftest(KnoxEngine* e) { return knox_engine_rack_device_selftest(e); }
int32_t nota_engine_drum_rack_selftest(KnoxEngine* e) { return knox_engine_drum_rack_selftest(e); }
int32_t nota_engine_master_volume_automation_count(const KnoxEngine* e) { return knox_engine_master_volume_automation_count(e); }
int32_t nota_engine_master_volume_automation_get(const KnoxEngine* e, NotaAutomationPoint* out, int32_t cap) { return knox_engine_master_volume_automation_get(e, out, cap); }
NotaResult nota_engine_master_volume_automation_set(KnoxEngine* e, const NotaAutomationPoint* pts, int32_t count) { return knox_engine_master_volume_automation_set(e, pts, count); }
int32_t nota_engine_automation_write_selftest(KnoxEngine* e) { return knox_engine_automation_write_selftest(e); }
void nota_engine_set_automation_write_mode(KnoxEngine* e, int32_t mode) { knox_engine_set_automation_write_mode(e, mode); }
int32_t nota_engine_automation_write_mode(const KnoxEngine* e) { return knox_engine_automation_write_mode(e); }

} // extern "C"
