# Contributing

Issues and pull requests are welcome. For security problems, see [SECURITY.md](SECURITY.md) instead.

## Prerequisites

- .NET 10 SDK, plus the .NET 8 runtime (the library and its tests target net8.0 and net10.0)
- An OpenSSH client (`sftp` and `ssh-keygen` on the `PATH`) for the SFTP interop tests. Without it
  those tests are skipped, not failed, so a green local run without OpenSSH doesn't cover them.
  On CI (`CI=true`) a missing OpenSSH client fails them instead.
  Windows 10+ ships one as an optional feature; most Linux distributions and macOS have it already.

## Build and test

```
dotnet build Intertube.slnx -warnaserror
dotnet test Intertube.slnx
```

CI runs the same thing in Release (`-c Release`) and packs the library with
`dotnet pack src/Intertube.Stub -c Release --no-build`.

## Pull requests

- The build must have no warnings (`-warnaserror`).
- Tests must pass on all three CI operating systems (Ubuntu, Windows and macOS) and on both target
  frameworks. Socket behaviour differs between platforms (resets, Unix socket paths, timing), so
  avoid tests that depend on exact timing or on one OS's error codes.
- Add or update tests for behaviour changes, and update `README.md` when the public API changes.
- Add or update the matching requirement in `docs/requirements/*.sdoc` (see below).

## Requirements

The requirements are [StrictDoc](https://strictdoc.readthedocs.io/) documents in
`docs/requirements`: `core.sdoc`, `smtp.sdoc`, `sftp.sdoc` and `project.sdoc`, sharing the
grammar in `intertube.sgra`. Each requirement has a stable UID (`ITS-<AREA>-NNN`, never reused)
and a `VERIFIED_BY` list naming the tests that cover it as `TestClass.Method` (for F#,
`Module.text inside the double backticks`), or `Review: <file>` where no test can.
`RequirementsTraceTests` fails the test run if a UID is duplicated, an Active requirement has no
`VERIFIED_BY`, or a named test does not exist. CI also exports the HTML; to do it locally:

```
pip install -r docs/requirements/tools.txt
strictdoc export docs/requirements --output-dir requirements-html
```
