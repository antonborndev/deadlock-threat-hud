# Security policy

## Supported version

Only the latest published Threat HUD Bridge release and its matching source tag
are supported.

## Reporting a vulnerability

Do not post credentials, private logs, temporary broadcast URLs, or other
sensitive information in a public issue. Use GitHub's private vulnerability
reporting feature when it is enabled for this repository. General support is
available through https://discord.gg/MJcXrGXGt.

Include the affected release, reproduction steps, and the smallest useful
redacted log excerpt.

## Scope notes

The local HTTP service listens on `127.0.0.1:28741` without authentication.
Reports involving unintended access or mutation through that loopback API are
in scope. Reports should also cover unsafe changes to Deadlock files, parser
extraction, embedded-resource verification, and untrusted API or relay input.
