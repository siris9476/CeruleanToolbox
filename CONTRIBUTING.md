# Contributing

Thanks for wanting to help. The most useful things right now are **trying the app and telling what happens**, and **trying the actions that have never been tried** (see "Project status" in the [README](README.md)). Code, translations and documentation are welcome too.

## Before you start

- Open an issue to describe a bug or an idea, or comment on an existing one, before sending a large change.
- For a security problem do not open an issue: see [SECURITY.md](SECURITY.md).
- Never put a real client secret, token or password in an issue, a pull request or a screenshot. Hide tenant IDs, client IDs, subscription IDs and resource names that you do not want to be public.

## Build and run

You need Windows, the **.NET 8 SDK** and, for the desktop app, the WebView2 Runtime.

```bash
dotnet build CeruleanToolbox.sln
dotnet run --project src/CeruleanToolbox --urls http://localhost:5166          # web
dotnet run --project src/CeruleanToolbox.Desktop                                # desktop
```

To work without touching your saved connections, run the web app with a separate store: set the configuration key `ConnectionStoreFolder` (for example the environment variable `ConnectionStoreFolder=CeruleanToolboxTest`) and the connections and the language are kept in `%LOCALAPPDATA%\CeruleanToolboxTest`.

## Trying it safely

The app acts on real Azure resources with a service principal. When you test:

- use a **test subscription**, and give the service principal the smallest role that the page needs (for example Reader to look, Contributor on one resource group to change things);
- use throwaway resources for anything that deletes or changes state;
- say in your issue or pull request **what you tried and what happened**, even when everything works: that is how the "not tried yet" list in the README gets shorter.

## Switched-off delete actions

Ten delete/revoke actions are switched off with a `DEBUG-TEST` block that prints a message and returns (find them with `grep -rn "DEBUG-TEST" src`). To switch one on: remove the block so the real code below it runs, try it on a throwaway resource, and send a pull request that says what you tried. Keep the typed confirmation (the user retypes the resource name) in front of every delete.

## Code

- The code, the comments and the commit messages are in English.
- Every text the user sees is written in English as `T("Text")` (or `T("Loaded {0} items", count)`) and has its Italian translation in the `Locales/it.json` file of the project that owns it. Never keep a translated text in a `const`, a `static readonly` field or a field initializer: the language can change while the app runs. Compare values with `T("Yes")`, never with a literal word.
- To add a language, add its code and name to `Tr.Languages` (`src/Toolbox.Core/Localization/Tr.cs`) and a `<code>.json` file in each project's `Locales` folder. A text with no entry is shown in English, so a translation can be partial.
- Keep to the style of the surrounding code. Match its comment density and naming; do not reformat files you do not change.
- Read the Azure data you show as raw ARM JSON through `ArmRestClient` and the tolerant `ArmJson` helpers where the SDK types throw on real responses, and show a default value instead of a bare "-" when Azure omits a field.
- Operations that delete, scale or restart use the confirmation component and stay disabled while the resource is not in a stable state.

## Pull requests

- Keep a pull request to one subject. Describe what changes and why, and what you tried.
- Make sure `dotnet build CeruleanToolbox.sln` passes with no errors.
- If you change a page, look at it in the browser in English and in Italian (the language selector is at the top of the page).
- By contributing you agree that your work is released under the [MIT license](LICENSE) of this project.
