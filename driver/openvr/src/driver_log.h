/*
 * driver_log.h: printf-style wrapper over vr::IVRDriverLog.
 * Shape mirrors the Valve driver samples' utils/driverlog (driverlog.cpp),
 * trimmed to the one entry point this driver uses. Safe to call before
 * the driver context exists (drops the line).
 */
#pragma once

void HmVrLog(const char *fmt, ...);
