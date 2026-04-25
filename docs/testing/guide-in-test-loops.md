# `HMButton.Guide` in test pulse loops — banned by default, allowlist for intentional use

## Why this is enforced

When a HIDMaestro virtual reports Guide pressed, Windows Xbox UI fires a
Guide-long-press haptic ack (`hi=0x7F` short burst) to the controller's
XInput slot. If a continuous test-pulse loop cycles Guide, that haptic ack
floods any haptic-trace tooling — the shell-emitted SET_STATE bytes
overlap and mask real haptic dispatch (e.g. browser vibration) under
investigation.

Two investigation hours were lost to this in 2026-04 when the author
misread the shell haptic pattern as "driver-side dispatch signal." See
[docs/investigations/wgi-silent-sink-2026-04/finding.md](../investigations/wgi-silent-sink-2026-04/finding.md)
methodology note.

## The guard

[`scripts/check_no_guide_in_pulse.ps1`](../../scripts/check_no_guide_in_pulse.ps1)
scans `example/` and `test/` for any `HMButton.Guide` reference and fails
if one appears without an explicit allowlist comment on the same line.

## Allowlist syntax

If you legitimately need `HMButton.Guide` in a test — the common case is
testing Guide-long-press handling itself, or one-shot snapshot frames that
demonstrate Guide routing — add an `ALLOW-GUIDE:` trailing comment on the
same line with a short justification:

```cs
// Single snapshot: Guide button demonstrates the 0x0400 wButtons routing
// path on Xbox 360 descriptors. Not looped, so no continuous haptic ack.
Buttons = HMButton.A | HMButton.Guide,  // ALLOW-GUIDE: one-shot tour snapshot, not looped
```

```cs
// Test for Guide-long-press handling specifically.
ctrl.SubmitState(state with { Buttons = HMButton.Guide });  // ALLOW-GUIDE: exercising the Guide-haptic code path on purpose
Thread.Sleep(2000);
```

The allowlist comment MUST be on the same line as the `HMButton.Guide`
reference. Comments on the line above or below do not count — the regex
is line-scoped to keep the check simple and auditable.

## What you should NOT do

- Do not wrap `HMButton.Guide` in a string / macro / helper that hides
  the literal token from the guard. If the guard can't see it, future
  investigations can't see it either, and they end up chasing phantom
  haptic acks again.
- Do not disable the guard. It runs as a standalone script (not wired
  into the main build yet), so it's already opt-in for anyone who
  remembers to run it. Disabling it would require removing it from
  `scripts/`, which would show up in code review.

## Running the guard

```
powershell -NoProfile -File scripts/check_no_guide_in_pulse.ps1
```

Exit 0 on pass, exit 1 on violation. Wire it into your pre-commit hook
or CI if you want continuous enforcement.

## See also

- [scripts/check_no_guide_in_pulse.ps1](../../scripts/check_no_guide_in_pulse.ps1) — the guard itself (header comment documents the same rules)
- [docs/investigations/wgi-silent-sink-2026-04/finding.md](../investigations/wgi-silent-sink-2026-04/finding.md#three-standing-rules-that-emerged) — investigation that produced this rule
- [docs/investigations/wgi-silent-sink-2026-04/investigation-history.md](../investigations/wgi-silent-sink-2026-04/investigation-history.md#first-silent-sink-conclusion--retracted) — history of the Guide regression and why the strict form of this guard exists
