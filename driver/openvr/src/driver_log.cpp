#include "driver_log.h"

#include <cstdarg>
#include <cstdio>

#include "hm_openvr.h"

/* Mirrors DriverLogVarArgs in the openvr samples' driverlog.cpp: fixed
 * stack buffer, vsnprintf_s, one IVRDriverLog::Log call. Lines appear in
 * vrserver.txt prefixed "hidmaestro:". */
void HmVrLog(const char *fmt, ...)
{
    /* The VRDriverLog() accessor dereferences VRDriverContext() to
     * resolve the interface (openvr_driver.h:4473-4478), so the context
     * pointer must be checked FIRST for the pre-Init drop-the-line
     * guarantee to hold. */
    if (vr::VRDriverContext() == nullptr || vr::VRDriverLog() == nullptr)
        return;

    char buf[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, args);
    va_end(args);

    vr::VRDriverLog()->Log(buf);
}
