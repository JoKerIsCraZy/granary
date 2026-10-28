# Contributing to Granary

Thanks for helping. Granary is a native Windows client for Silo Server, built by volunteers. This guide covers how to
report a problem, how to propose and make a change, and the rules that come with the name and the license.

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Report a problem

- Search the open issues first. If one covers your problem, add what is new there.
- Open an issue with the **Bug report** form. It asks for:
  - what you saw, the exact steps, and what you expected
  - Granary's version (Settings → About)
  - how you installed Granary
  - your Windows version (run `winver`)
  - your Silo server's version
- One problem per issue. Describe what you observed before any theory about the cause.
- Attach the relevant lines of the log from `%LOCALAPPDATA%\Granary\logs`. Granary keeps tokens out of its logs,
  but check anyway, and remove server addresses, account names and anything private you would rather not share.
  Mark what you removed.
- Report a security vulnerability privately, as [SECURITY.md](SECURITY.md) describes, never in a public issue.
- A problem in Silo Server or its web app belongs in
  [Silo's issue tracker](https://github.com/Silo-Server/silo-server/issues), not here. If Silo's web app does the
  same thing, the problem is probably the server's.

## Propose a feature

Use the **Feature request** form. Describe the problem it solves and who it affects. Granary follows Silo's own
clients:

- **Behaviour** follows the Android and Apple apps; the web app decides how things **look**.
- If one of them already has the feature, say where.
- A feature no Silo client has needs a good reason to look and work differently.

## Make a change

1. For anything bigger than a small fix, open or comment on an issue first, so the approach is agreed before you
   spend time on it.
2. Set up your machine as the [README](README.md#building-from-source) describes: the .NET 10 SDK, then libmpv
   with `./scripts/fetch-native.ps1 -ZipPath <the pinned libmpv zip>`. The pinned download URL answers 404 until the
   pin points at a mirror; the README's note says how to get the archive.
3. Branch from `main`. Keep one change per branch and pull request.
4. Read [AGENTS.md](AGENTS.md): the layers, the code style, the test helpers and the known pitfalls. It is
   written for coding agents but applies to everyone.

A pull request is ready for review when:

- **Tests:** it comes with tests for the behaviour it adds or fixes, written first where possible. The whole suite
  passes: `dotnet test --solution Granary.slnx`.
- **Build:** it builds with **0 warnings**. Warnings are errors in this repository.
- **Docs:** it updates the docs it touches:
  - docs/api-contract.md for API use
  - docs/ui-design.md for screens
  - docs/architecture.md for structure
  - CHANGELOG.md under `[Unreleased]` for anything a user would notice
- **UI changes:** you opened every new or changed page in the running app, and the pull request shows a screenshot.
  Run the app with `GRANARY_DATA_DIR` set to an empty folder for checks, so your own servers and sign-ins
  stay out of it.
- **Commits:** they follow [Conventional Commits](https://www.conventionalcommits.org): `type(scope): what
  changes`, for example `fix(player): subtitles keep their delay after a seek`. Types: feat, fix, perf, refactor,
  docs, test, chore, ci.

Every pull request is reviewed before it is merged. Expect questions and requests for changes; that is normal.

If an AI tool wrote part of your change, say so in the pull request. You are responsible for every line you
submit, and for having run it.

## Silo's name and logo

Granary is not an official Silo app. Silo's code is open source, but its name and brand are protected by
[Silo's trademark policy](https://github.com/Silo-Server/siloserver.org/blob/main/TRADEMARK.md).

- **The name:** use "Silo" only to refer to Silo ("a client for Silo", "Connect to your Silo server"). Never use it
  as Granary's name, or in a way that suggests Silo made or endorses Granary.
- **Spelling:** write the name as **Silo**, with one capital S, never in capitals. The server software is
  **Silo Server**; someone's own instance is "your Silo server".
- **The logo:** Silo's logo files in `src/Granary.App/Assets/Brand` are Silo's, unmodified, and used only to refer
  to Silo. Do not change them or build them into Granary's own logo or icons.

## License

Granary is licensed under the [GNU Affero General Public License v3.0 or later](LICENSE). By contributing you
agree that your contribution is licensed under the same terms. There is no separate contributor agreement.

- Code adapted from Silo's web, Android or Apple clients keeps its AGPL license. Name its origin in a comment, and
  add it to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) if it is not listed yet.
- A new dependency must have a license compatible with AGPL-3.0-or-later. Add it to THIRD_PARTY_NOTICES.md in the
  same pull request.
