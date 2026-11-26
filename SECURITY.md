# Security

TabLink listens on IPv4 loopback only. An authorized USB device reaches that
listener through ADB reverse and authenticates with a random per-PC token.
There is no Internet-facing listener, cloud pairing service or telemetry.

This is a local trust boundary, not isolation from malicious software already
running as your Windows or Android user. The pairing token is stored in the
Windows user's local application data and in Android private preferences.
ADB supplies it to the Android activity during pairing; privileged tools and
local process inspection may observe that operation. The protocol does not
provide a separate encrypted channel. Do not expose the listener through a
proxy or enable ADB over a network for use with TabLink.

USB debugging grants broader development access than TabLink alone needs.
Authorize trusted computers only. Revoke USB debugging authorization in
Android when it is no longer needed. Touch input can control your desktop.
Disconnect the device or disable touch when it should not have that access.

Screen images are streamed in memory. Logs remain local and may contain
system paths, codec details and monitor geometry. Captures made by hardware
tests can contain visible desktop content. Review diagnostics before sharing.
Never commit pairing files, keystores, passwords, logs or desktop captures.

Dependency archives are pinned by SHA-256. The VDD catalog signature is
checked before installation. TabLink does not install a root certificate,
disable Secure Boot or enable driver test signing. Origin and integrity
checks are not a full security audit of third-party software.

If you find an issue, avoid publishing credentials or private screen captures
in a public issue. Use GitHub's private vulnerability reporting if available;
otherwise open a minimal issue requesting a private contact channel without
including sensitive details.
