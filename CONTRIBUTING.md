# Contributing

Issues and pull requests are welcome. For security problems, see [SECURITY.md](SECURITY.md) instead.

## Prerequisites

- .NET 10 SDK, plus the .NET 8 runtime (the library and its tests target net8.0 and net10.0)
- An OpenSSH client (`sftp` and `ssh-keygen` on the `PATH`) for the SFTP interop tests. Without it
  those tests are skipped, not failed, so a green local run without OpenSSH doesn't cover them.
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
