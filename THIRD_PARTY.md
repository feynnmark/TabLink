# Dependencies

TabLink source is MIT licensed. The downloadable TabLink package contains the
project's own binaries, documentation and setup scripts. It does not bundle
third-party executables. Setup downloads the pinned archives listed in
`dependencies.json` from their upstream providers and verifies SHA-256.

| Dependency | Upstream | License / notices |
| --- | --- | --- |
| Android Platform Tools 37.0.1 | [Google](https://developer.android.com/tools/releases/platform-tools) | Original `NOTICE.txt` is copied beside ADB. |
| FFmpeg 9.0.2 essentials | [Gyan builds](https://www.gyan.dev/ffmpeg/builds/) | GPL v3 build. Original license and build README, including source revision and configuration, are copied beside FFmpeg. [FFmpeg source](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b). |
| Virtual Display Driver 25.7.23 | [VirtualDrivers](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) | [MIT license](https://github.com/VirtualDrivers/Virtual-Display-Driver/blob/master/LICENSE). Original signed driver files are retained. The upstream package also supplies Microsoft's device installation utility. |
| Android SDK Platform 35 and Build Tools 35.0.0 | [Google](https://developer.android.com/studio/terms) | Build-time dependencies only; not redistributed by TabLink. Review Google's SDK terms before building. |

Java 21, Python 3 and the Windows .NET Framework compiler are build tools
provided separately by the developer's environment. No third-party source,
SDK archive, local signing identity or vendor controller UI is committed.
