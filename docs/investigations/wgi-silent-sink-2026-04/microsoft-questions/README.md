# Microsoft-Facing Questions (Drafted, Ready to Send)

Two open technical questions derived from the WGI Silent Sink investigation (2026-04-18). Each file is a standalone, sendable form of the question — copy-paste into the named channel.

## Target channels

| File | Channel | When to send |
|---|---|---|
| [driver-dev-feedback.md](driver-dev-feedback.md) | Windows Driver Developer feedback (Feedback Hub → "Developer Platform" category, or via MSDN forums → Windows Hardware Dev Center) | Send after confirming the finding via one round of user testing on a clean Win11 build (not immediately — wait ~1 week to ensure finding survives a Windows Update cycle and isn't fixed incidentally) |
| [gdk-team-question.md](gdk-team-question.md) | GDK Discord server (`#developer-support` channel) or Microsoft GDK developer forums | Send same time as driver-dev question; GDK team may route it differently but context is identical |

**Who sends:** HIDMaestro project maintainer. Not automated.

**Expected response window:** Microsoft dev channels typically respond within 1–3 weeks for well-formed technical questions. If no response within 4 weeks, escalate via MVP contacts or re-post.

## What was dropped

A Chromium `input-dev@chromium.org` question was considered and dropped. Post-parser-fix instrumentation showed Chromium uses upstream WGI `put_Vibration` dispatch (probe-arrival pattern matches; no Edge-proprietary divergence evident). The remaining Chromium angle would be a feature request ("please prefer XInputDataFetcher for non-USB-enumerated XUSB-class devices") rather than a technical question, and belongs in crbug.com as a separate issue if the Microsoft responses indicate an architectural dead end.

## Evidence to include when sending

Attach or link:
- The three-layer log excerpt: `../evidence/chromium-silent-sink-log.txt`
- The post-fix evidence matrix from `../finding.md`
- If requested: raw ETW trace excerpt from `../evidence/etw-extract/`

Do NOT attach:
- The full parser-bug retraction history (irrelevant to Microsoft's scope; belongs in our investigation notes only).
- Details of the ROOT-enumerator constraint's origin in HIDMaestro (project constraint, not relevant to the technical question).

## Status

- [ ] Draft finalized → **DONE**
- [ ] Reviewed by project maintainer
- [ ] Sent to driver-dev channel
- [ ] Sent to GDK channel
- [ ] Response received / escalated
