# Localization catalogs

The supported locale list is taken from the bundled Sunshine directory named in
`locales.json`. OpenStreamMS catalogs contain its own interface text; Sunshine's
translation JSON files are not imported or remapped into these catalogs.

Web catalogs live in `Core/wwwroot/locales`, tray catalogs in
`Services/TrayApp/Locales`, and installer custom messages in
`Core/Installer/messages`. English defines the keys and formatting tokens. Keep
placeholders, formatting tags, API identifiers, executable names and product names.

The original English, Spanish, French and Portuguese web translations remain
unchanged, and the previously validated Russian catalog is preserved. Added base
languages and Brazilian Portuguese were drafted with machine assistance from the
OpenStreamMS English text. Workstation/session terminology and selected critical
CJK labels and Japanese deletion prompts received an additional editorial pass.
This is not native-speaker acceptance: all newly added translations still need
review in their operating context, especially long hints and technical language.
Passing structural tests does not certify translation quality.

Regional catalogs have complete key sets, with runtime fallback to their parent
listed in `locales.json`, then English. English (US) uses the US-spelled reference;
English (GB) uses British spellings. Brazilian Portuguese and Traditional Chinese
have separate text. Browser tags accept hyphens/underscores; the DOM uses BCP 47
hyphens. Windows Tray follows Windows UI culture independently of browser choice.

To update installer output after editing custom-message JSON, run:

    python tools/generate-installer-locales.py

Commit both JSON and generated `.iss` includes. Existing language names such as
`english`, `russian` and `brazilianportuguese` remain stable for upgrades.

Standard installer translations use bundled official Inno Setup resources where
available. Additional resources, their pinned upstream commit and attribution are
in `Core/Installer/Languages/sources.json`. British English and Vietnamese are
maintained *unofficial* Inno resources; their standard text quality has not been
certified by this project. Chinese Simplified/Traditional are official resources
listed by Inno Setup. Unmodified attribution and the Inno Setup License are retained.

Test with `node --test tests/i18n.test.cjs`, then the Edge fixture harness
`tests/i18n-browser.cjs` and the published-file/HTTP tests. The fixture intercepts
API calls and cannot validate real streaming, installation, driver or RDP behavior.
The driver dialog translates OpenStreamMS-owned operation log lines from the
existing text API, including progress, exit codes and service states. Language
switching rerenders cached raw lines immediately. Paths, arguments, timestamps
and unrecognized vendor diagnostics remain intact and are rendered as plain text.
Backend log files and API payloads are unchanged. CLI/operator logs, API
documentation and arbitrary external error details are not certified localized
by these catalogs; unknown UI errors use a localized summary.
