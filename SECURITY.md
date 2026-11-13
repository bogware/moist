# Security Policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 1.x     | Yes — security fixes are released as 1.x patch versions |
| < 1.0   | No — please upgrade |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability**. That creates a private advisory visible only to the maintainers.

Include what you can of:

- the moist version (`moist --version`) and OS,
- which surface is affected (GUI, CLI, `moist serve`, Docker image, tool download, plugin loading),
- steps or a proof of concept that reproduces it,
- the impact you believe it has.

You can expect an acknowledgement within **7 days** and an assessment within **14 days**. Fixes are
released as a patch version, with a GitHub Security Advisory that credits the reporter unless you'd
rather stay anonymous. Please give us a reasonable window to ship a fix before disclosing publicly;
we aim for 90 days or less.

## Security model

moist is a local tool that runs other programs (FFmpeg, x264/x265, mkvmerge, …) on files you point
it at. Some boundaries are worth knowing before you deploy it.

### `moist serve` (HTTP API)

An encode request names its own input and output paths, so the API is a filesystem trust boundary.

- **Authenticated by default.** A bearer token is required (`--token`, or the `MOIST_TOKEN`
  environment variable, which keeps it out of the process list). `--no-auth` is refused anywhere
  but loopback. Tokens are compared in constant time.
- **Loopback by default.** Binding any other address requires at least one `--root <dir>`; every
  input, output, external-subtitle and probe path must resolve under a root. moist's own
  configuration folder is always refused, since writing there would be code execution on the next
  start.
- **Options are allow-listed.** Only options that shape the encode are accepted. Tool paths
  (`--ffmpeg`, `--ffprobe`), raw encoder arguments (`--video-args`, `--audio-args`, which reach a
  shell for the standalone encoders), `--temp-dir`, `--output-dir`, `--suffix` and
  `--filter-engine` are rejected. Filter parameters are validated, and free text that could open a
  second filtergraph or break out of a generated script is refused.
- **No TLS.** The server speaks plain HTTP. Off loopback, put it behind a TLS-terminating reverse
  proxy so the token is not sent in the clear.
- The Docker image runs as a non-root user and serves `/media` as its only root.

### Plugins

Every `*.dll` under `<config>/plugins` is loaded and run at startup, by the GUI and by every CLI
command, including `serve`. **Only put code you trust there.** Anyone who can write to that folder
can run code as you. The `serve` API refuses to write into the configuration folder for this reason.

### Tool downloads

Settings can download FFmpeg, MKVToolNix (Windows), dovi_tool, hdr10plus_tool, Real-ESRGAN and RIFE.
Downloads are HTTPS-only, pinned to immutable release URLs and **verified against a recorded
SHA-256 before extraction**. A mismatch aborts the install and deletes the file. Archive extraction
rejects entries that would land outside the target folder.

### Release signing and verification

Windows release binaries are Authenticode-signed through the
[SignPath Foundation](https://signpath.org) programme for open-source projects, once the project is
enrolled. Every release also publishes `SHA256SUMS.txt`; verify a download with:

```sh
sha256sum -c SHA256SUMS.txt --ignore-missing
```

#### For maintainers: enabling signing

1. Apply at <https://signpath.org> (the repository must be public and OSI-licensed).
2. In the SignPath project, upload `.signpath/artifact-configuration.xml` as the artifact
   configuration and create a signing policy (default slug `release-signing`).
3. In GitHub, add the secret `SIGNPATH_API_TOKEN` and the repository variable
   `SIGNPATH_ORGANIZATION_ID`. Optional variables `SIGNPATH_PROJECT_SLUG` (default `moist`) and
   `SIGNPATH_SIGNING_POLICY_SLUG` override the defaults.

The release workflow signs whenever `SIGNPATH_ORGANIZATION_ID` is set and fails if any shipped
executable isn't validly signed afterwards. Without it, releases build unsigned.
