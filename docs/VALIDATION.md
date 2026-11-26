# Validation

The original implementation was exercised on a physical Android tablet with
a 90 Hz panel and an NVIDIA Windows PC. This is evidence for that test setup,
not a guarantee for every device. Private desktop captures, system logs,
device identifiers and personal monitor arrangements are not published.

The public version adds capability negotiation rather than inferring support
from a model name. Its checks cover malformed and unauthenticated greetings,
different resolutions and refresh rates, 8-bit and 10-bit negotiation,
available-mode selection, APK signing, PowerShell syntax and publication
contents. See `tests/ProfileTests.cs` and `tests/test_publication.py`.

`tests/verify_video.py` exercises a real installed host with an authorized USB
device. It checks authentication before screen activation, the four-picture
ACK limit, dropping stale captures, independent HEVC pictures, encoded bit
depth and repeated reconnects. It compares physical monitor geometry before
and after each session. Results are written locally and excluded from Git.

Frame-rate and latency values depend on active content, link scheduling,
decoder behaviour, system load and temperature. Capture sampling ceilings
and nominal video metadata do not measure actual displayed fps. The Android
statistics use MediaCodec presentation callbacks. ACK timing excludes the
earlier capture/encoding stages and is not touch-to-photon latency.

Cross-brand compatibility still needs physical testing on additional devices.
Automated profile and transport tests do not replace that validation.

## Public release 0.6.0

Release checks passed: 23 negotiation and mode-selection assertions, source
and archive privacy checks, matching Android/VDD mode tables, dependency pins,
APK v2/v3 signature verification, and PowerShell syntax validation. All pinned
download URLs responded successfully, and a full dependency installation was
performed from the published upstream URLs.

The hardware integration run passed authentication rejection, the bounded
ACK window and stale-picture replacement, 10-bit / 8-bit / 10-bit HEVC
sessions, and three reconnect cycles with unchanged physical monitor geometry
and removal of the virtual monitor. The native Android receiver also presented
the negotiated 10-bit stream at its 90 Hz panel mode. Other manufacturers have
not yet been tested on physical hardware.
