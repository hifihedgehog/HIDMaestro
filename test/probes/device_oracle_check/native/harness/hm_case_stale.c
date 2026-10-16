/* Values a killed v2.0.0 program leaves, under a v1 controller's index
 * (design note, decision (c)): the plan Case5Driver.cs writes for this
 * mode holds dualsense-bt, amazon-luna-usb, xbox-360-wired and
 * dualshock-3-full with the instance configuration 9bd6279's SDK writes,
 * which is what a v1 program writes, plus FeatureAnswers, XusbAnswers,
 * XusbCompanion 1, LiveFeatureReport 1 and a cached input report ID list
 * (LeftValues.cs). Groups "stale" and, for a profile with the XUSB
 * companion, "stale-xusb". Case5Driver.cs runs this mode twice, on that
 * plan and on the same plan without the left values, and fails the case
 * when any stream differs between the two runs.
 *
 * Each runs with the version section absent, as a v1.12.0 program leaves
 * it, and at SdkProtocolMax 3, as a 9bd6279-era build leaves it, both of
 * which the version contract counts as a v1 program. A section at 4 is a
 * v2.0.0 program, which writes its own values, so no stale case runs
 * under it. Per profile: the device's start with frames through parked
 * reads and the polled input report (DriverProbe), the feature answers at
 * every declared id, the Sony table's ids and report 0 at each size
 * (FeatureSweep), and for a companion profile the companion's answers and
 * SET_STATE (XusbAnswerSweep). The 9bd6279 driver reads none of the left
 * values, so these streams equal the same plan's without them, and the
 * version contract asks the same of every later driver. */
#include "hm_scenario.h"

static void Driver(PlanProfile *p, const char *what, void (*probe)(Ctl *))
{
    Ctl c;
    StreamBegin("stale/%s/%s", p->id, what);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    probe(&c);
    CtlStop(&c);
}

void CaseStale(void)
{
    static const int params[] = { VERSION_ABSENT, VERSION_MAX3 };
    int n = 0;
    for (int k = 0; k < (int)ARRAYSIZE(params); k++) {
        g_versionParam = params[k];
        for (int i = 0; i < g_plan.profileCount; i++) {
            PlanProfile *p = &g_plan.profiles[i];
            int xusb = strcmp(p->group, "stale-xusb") == 0;
            if (!xusb && strcmp(p->group, "stale")) continue;
            n++;
            Driver(p, "start", DriverProbe);
            Driver(p, "feature", FeatureSweep);
            if (xusb) {
                Ctl c;
                StreamBegin("stale/%s/companion", p->id);
                if (!CtlStart(&c, p, 0, SDK_ALL, 1)) continue;
                XusbAnswerSweep(&c);
                CtlStop(&c);
            }
        }
    }
    g_versionParam = VERSION_SDK;
    if (n == 0) HmFail("the plan has no stale profiles");
}
