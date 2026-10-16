/* Case 9, the physical corpus (Case9PhysicalCorpus.cs): every physical
 * descriptor and identity on disk, built into a profile as
 * HMDeviceExtractor.Extract builds one, with the instance configuration
 * the SDK's WriteInstanceConfig writes for it. The plan's groups are
 * "corpus" and, for a profile the SDK gives the XUSB companion
 * (ControllerProfile.RequiresXusbCompanion), "corpus-xusb". Each stream is
 * named after the corpus entry, which is named after its source file.
 *
 * Per entry, on the HID device the driver makes: its start (DriverProbe),
 * the feature answers at every declared id, the Sony table's ids and
 * report 0 at each size (FeatureSweep, which the Sony branch answers only
 * for vendor 0x054C, driver.c:3126-3129), and SDL's first Switch requests
 * (SwitchProbe, which the responder answers only for 057E:2009,
 * driver.c:480-481). For a companion profile, the companion's answers
 * (XusbAnswerSweep). */
#include "hm_scenario.h"

static void Driver(PlanProfile *p, const char *what, void (*probe)(Ctl *))
{
    Ctl c;
    StreamBegin("corpus/%s/%s", p->id, what);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    probe(&c);
    CtlStop(&c);
}

void CaseCorpus(void)
{
    int n = 0;
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        int xusb = strcmp(p->group, "corpus-xusb") == 0;
        if (!xusb && strcmp(p->group, "corpus")) continue;
        n++;
        Driver(p, "start", DriverProbe);
        Driver(p, "feature", FeatureSweep);
        Driver(p, "switch", SwitchProbe);
        if (xusb) {
            Ctl c;
            StreamBegin("corpus/%s/companion", p->id);
            if (!CtlStart(&c, p, 0, SDK_ALL, 1)) continue;
            XusbAnswerSweep(&c);
            CtlStop(&c);
        }
    }
    if (n == 0) HmFail("the plan has no corpus profiles");
}
