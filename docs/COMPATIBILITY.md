# Compatibility

TabLink 0.6 supports one Android receiver at a time. It identifies authorized
USB transports without a device-brand or model allowlist. Android phones and
tablets use the same app, in landscape orientation.

Android must advertise a hardware HEVC decoder for the requested size, frame
rate and Main or Main 10 profile. The app queries MediaCodecList and checks
VideoCapabilities.areSizeAndRateSupported. It prefers a native common mode,
then a smaller mode with a similar aspect ratio. A device that lacks Main 10
can negotiate 8-bit Main. Software decoding is deliberately excluded because
it can add substantial latency.

The common display modes are defined in DeviceProfile.java and the VDD setup
script. They cover 640 x 360 through 3840 x 2160, with 30, 60, 75, 90 and
120 Hz. The selected mode must fit within the Android panel and decoder
limits. An unusual native resolution may use a smaller common mode. Add a
mode to both lists before claiming native support for it.

Windows selects the closest available VDD mode that does not exceed the
negotiated dimensions or refresh rate. Duplicating a physical monitor scales
its capture to fit the negotiated dimensions. Neither the physical monitor's
resolution nor its refresh rate is changed. Existing physical monitor
positions are preserved. The initial virtual display position avoids overlap;
Windows display settings can be used to move it afterward.

The PC pipeline currently requires Windows 11 x64 and NVIDIA HEVC NVENC.
10-bit negotiation also requires NVENC Main 10 support. Intel/AMD encoders,
Windows ARM, iOS and other desktop operating systems are not implemented.
This broader Android support does not imply universal hardware compatibility.

On devices before Android 11, explicit Surface frame-rate selection is not
available; Android's compositor decides the actual presentation frequency.
Power saving, thermal limits and vendor firmware can override requested
display modes. USB 2.0 is usable, but link quality and load matter.

References: [MediaCodecList](https://developer.android.com/reference/android/media/MediaCodecList),
[VideoCapabilities](https://developer.android.com/reference/android/media/MediaCodecInfo.VideoCapabilities),
[NVENC](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/).
