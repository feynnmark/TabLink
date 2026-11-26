# USB video protocol

The Windows host listens on `127.0.0.1:27839`. ADB reverse maps the Android
loopback port to this listener. Only one authenticated video session is active.

The receiver sends one ASCII line:

```text
TABLINK/5 <local-pairing-token> <width> <height> <rate> <bits>
```

The token is generated locally. Dimensions are even, landscape-oriented,
between 640 x 360 and 4096 x 2160. Rates are 30, 60, 75, 90 or 120; depth is
8 or 10. The host validates the entire greeting before activating a monitor.

The host responds with five signed big-endian 32-bit integers: magic
`0x544c5635`, encoded width, encoded height, target fps and bit depth. Each
picture follows as a 32-bit length, 64-bit presentation timestamp in
microseconds, and a complete Annex-B HEVC access unit. Pictures contain their
parameter sets and an IDR. The maximum access-unit size is 4 MiB.

The receiver returns `ACK <timestamp>` after MediaCodec reports presentation.
The host permits at most four unacknowledged pictures. A separate producer
drains the encoder into a latest-picture mailbox while waiting for ACKs.
Independent pictures can be skipped without breaking decoding dependencies.

Touch messages use `TOUCH <action> <action-index> <count> <id>:<x>:<y> ...`,
with normalized coordinates and up to ten contacts. `BYE` closes the session.
Touch state is released on disconnect. Input and ACKs share a separate
receiver path from encoded picture production.

This protocol intentionally rejects older greetings. Host and APK versions
must match; update both from the same release.
