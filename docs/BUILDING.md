# Build

Use Windows x64 with Java 21 on PATH, Python 3 and .NET Framework 4.8. The
Windows Framework C# compiler is used directly. Android Studio, Gradle and
the .NET SDK are not required.

```powershell
git clone https://github.com/feynnmark/TabLink.git
cd TabLink
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
python .\tests\test_publication.py
python .\scripts\package.py
```

The build downloads the pinned runtime and Android tools. Review the upstream
SDK terms before downloading. Dependencies and intermediate outputs stay in
the ignored `tools/` and `build/` directories. Release files are generated in
`dist/`; the ZIP is written to the project root. Neither is committed.

On the first build, a signing keystore and a random password are generated in
`tools/`. Keep them private and back them up if you need to update APKs signed
with that identity. A new keystore cannot update an APK signed by another key;
Android requires uninstalling the previous app first.

For an existing signing identity, set `TABLINK_KEYSTORE` and
`TABLINK_KEYSTORE_PASSWORD` in the build process environment. Do not put the
values in source, shell history, issue reports or workflow files. The password
is passed to signing tools through an environment variable. The release APK
contains the public signing certificate, never the private key.

The ZIP includes only an explicit allowlist in `scripts/package.py`.
`SHA256SUMS.txt` covers each packaged file. Hardware diagnostics, performance
captures, serial numbers, user directories, keys and vendor binaries are
excluded. Run the publication check before committing and after packaging.

For an end-to-end hardware check, install the local release, connect an
authorized Android device, then run `python tests/verify_video.py`. This test
temporarily closes the Android app and restores it afterward. Private test
outputs stay in the ignored `tests/local/` directory.
