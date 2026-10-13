# IGW S7 Plus native bridge

This Windows native library provides IGW with a small, versioned C ABI over the
`s7commplus` Rust crate. It supports TLS and legacy S7 Plus sessions, anonymous
connection, optional username/password legitimation, symbolic browsing, and
batched scalar reads/writes.

The ABI uses caller-owned buffers only. Credentials are accepted only for the
duration of an authentication call and are never returned or logged.

Build:

```powershell
cargo build --release --manifest-path native/igw-s7plus/Cargo.toml
```

The resulting `igw_s7plus.dll` is copied beside the managed driver when it is
present. Installer/publish verification must fail if the DLL is missing.

`s7commplus` and this bridge are distributed under LGPL-3.0-or-later. Real PLC
compatibility must be recorded separately for each tested model and firmware;
automated tests alone are not a production hardware validation.
