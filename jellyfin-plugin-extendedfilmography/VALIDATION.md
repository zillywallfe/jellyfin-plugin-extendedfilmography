# Validation for 1.0.1.0

- Passed bash syntax validation for install.sh.
- Passed isolated installer tests with simulated Docker commands: build failure leaves the existing installation/server untouched; successful installation backs up both old paths, installs the DLL and manifest, and preserves configuration files.
- Added regression checks for one-time default migration, preservation of credentials and feature state, custom thresholds, library provider-ID loading, index refresh, cached duplicate exclusion, and filling remaining slots after exclusion.
- Checked the DtoOptions constructor and manifest schema against Jellyfin v10.11.11 upstream source.
- C# execution and real-package compilation could not be completed in the editing environment: the downloaded .NET SDK failed to create CoreCLR (HRESULT 0x8007000E). These checks are mandatory in install.sh on the ThinkPad, before Jellyfin is stopped or installed files are touched.
- Jellyfin web/Neptune runtime behavior, including disabled-plugin dashboard visibility, still needs validation on the user's server. No client-side code was changed.
