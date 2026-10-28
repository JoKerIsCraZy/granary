# Security policy

## Supported versions

Granary is in early development, before 1.0. Only the latest release receives security fixes. An installed copy
updates itself, so staying on the latest release is the default.

## Report a vulnerability

Please report security problems privately. Do not open a public issue, pull request or discussion about them.

- Use **Report a vulnerability** on this repository's **Security** tab (GitHub private vulnerability reporting).
  <!-- TODO: enable private vulnerability reporting in the repository settings once it is public. -->
- Or email **TODO: security contact address**.

Please include:

- what the problem is and what an attacker could do with it
- the steps to reproduce it, and the Granary, Windows and Silo server versions involved
- any proof-of-concept, logs or screenshots. Remove tokens, passwords and other private data, and mark what you
  removed.

You will get a reply once the report has been read. A fix is released as soon as it is ready, and the report is
credited unless you ask otherwise.

## Scope

- **In scope:** this repository: the Granary app, its installer and updater, and the release workflow.
- **Out of scope:** the Silo server and Silo's own apps. Report problems in them to Silo, as
  [Silo's security policy](https://github.com/Silo-Server/silo-server/security) describes.
- **Third-party components:** report problems in libmpv, FFmpeg, the Windows App SDK or Velopack to their
  projects. Tell us as well if they affect Granary.

Granary keeps sign-in tokens in a file encrypted with Windows DPAPI for the current user, sends media
credentials only in headers, and keeps tokens out of its logs. A weakness in any of these counts as a
vulnerability.
