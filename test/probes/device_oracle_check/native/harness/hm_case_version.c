/* Case 5 under the version-section parameters, for the version contract
 * of the design note's decision (c): the Sony table, the Switch
 * responder, the DualShock 3 form and the companion, each with
 * Global\HIDMaestroVersion<N> absent beside a live input section, with
 * SdkProtocolMax 3, with 4, at 4 then cleared or closed part way through,
 * and at protocols 4 to 4 with this devnode's identity tag or another
 * token's (hm_case_identity.c). Each stream carries its parameter in its name
 * ("/version-<label>") and records the section's 64 bytes after the add,
 * at the midpoint and around the removal.
 *
 * What the 9bd6279 driver does with the section: it writes its half,
 * DriverVersion, DriverCapabilities and DriverProtocol 3, when it opens
 * the input section (driver.c:600-643, AnnounceVersion), and reads none
 * of the SDK's half. The companion opens no version section. */
#include "hm_scenario.h"

void CaseVersion(void)
{
    for (int param = VERSION_ABSENT; param < VERSION_PARAMS; param++) {
        g_versionParam = param;
        CaseSonyVersion();
        CaseSwitchVersion();
        CaseDs3Version();
        CaseXusbVersion();
    }
    g_versionParam = VERSION_SDK;
}
