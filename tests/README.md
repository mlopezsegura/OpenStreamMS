# Localization validation

The tests do not install or start OpenStreamMS, drivers, Windows services, or
streaming sessions. Run from the repository root.

```powershell
node --test tests/i18n.test.cjs
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o Core/Installer/publish
node tests/verify-package.cjs
```

`i18n.test.cjs` checks every dictionary, the effective original values at source
commit `79a032e5d00efb37bb53a71991408acbd94c3a11`, placeholders, safe HTML,
asynchronous startup, storage, races, English fallback and error recovery.

Browser tests require the `playwright` Node package and an installed Microsoft
Edge. Use an existing package installation via `NODE_PATH` if appropriate.

```powershell
node tests/i18n-browser.cjs
$env:I18N_WEB_ROOT = (Resolve-Path Core/Installer/publish/wwwroot).Path
$env:I18N_TEST_OUTPUT = 'C:/path/to/test-results'
node tests/i18n-browser.cjs
```

All API requests in this suite are intercepted. The suite checks the five
languages on Login, Setup and Dashboard, language persistence, open session/seat
forms, preservation of unsaved fields, errors, fallback and Russian action labels
at 375/720/1280 pixels in light/dark themes. Screenshots and JSON results are saved.

For real ASP.NET static-file and public-path checks, generate a separate host
outside the product source tree:

```powershell
./tests/prepare-validation-host.ps1 -OutputDirectory C:/path/to/validation-host
dotnet build C:/path/to/validation-host/ValidationHost.csproj -c Release
dotnet C:/path/to/validation-host/bin/Release/net10.0/ValidationHost.dll C:/path/to/publish/wwwroot http://127.0.0.1:51972
```

This host compiles the current `IsPublicPath` method extracted from `Program.cs`
and the actual `TrayText.cs`, but links no streaming/system code. In another shell:

```powershell
node tests/verify-http.cjs http://127.0.0.1:51972
$env:I18N_BASE_URL = 'http://127.0.0.1:51972'
node tests/i18n-browser.cjs
dotnet C:/path/to/validation-host/bin/Release/net10.0/ValidationHost.dll --tray-test ru-RU
```

The validation cookie is test-only. This host does not validate real accounts,
register services, or reproduce the complete application's authentication flow.
`--tray-test` checks all tray dictionaries, format arguments, Windows language
detection and fallback; it does not test the real notification icon or service.

`preview-server.cjs <published-wwwroot> [port]` is an optional visual preview with
inert session and device fixtures. All write API calls fail without side effects.
It is not an integration test of Windows, RDP, devices or streaming.

Full installation, uninstallation, tray integration and driver behavior must be
tested separately in an explicitly permitted disposable Windows VM.
