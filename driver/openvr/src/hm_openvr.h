/*
 * hm_openvr.h: single include point for the vendored openvr_driver.h.
 * The Valve header's inline interface stubs trip C4100 (unreferenced
 * parameter) at this project's /W4; scope the suppression to the header
 * so our own code stays warning-clean.
 */
#pragma once

#pragma warning(push)
#pragma warning(disable : 4100)
#include "openvr_driver.h"
#pragma warning(pop)
