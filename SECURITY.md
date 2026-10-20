# Security policy

CeruleanToolbox signs in to Azure with the credentials of a service principal that you save on your machine, so a security problem here can mean access to a real Azure environment. Please report problems privately.

## Reporting a vulnerability

**Do not open a public issue** for a security problem.

Use GitHub's private reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. If that button is not available, open a normal issue that only says "I would like to report a security problem" (no details) and a maintainer will arrange a private channel.

Please include:

- what you found and why it matters (what an attacker could read or do);
- the steps to reproduce it, and the version or commit you tried;
- the Windows and .NET versions, and whether you used the web or the desktop app.

Never put real secrets, tokens or client secrets in a report. Hide tenant IDs, client IDs and subscription IDs in screenshots and logs. If you think a real secret was exposed, rotate it first (create a new client secret in Microsoft Entra ID and delete the old one).

## What to expect

This is a small project maintained in spare time. I will try to acknowledge a report within about a week, to say whether I can reproduce it, and to fix confirmed problems in the main branch. There is no bug bounty.

## Supported versions

Only the latest commit on the `main` branch is supported. There are no maintained older releases.

## How the app handles credentials

This is useful to judge what counts as a problem:

- Saved connections are kept in `%LOCALAPPDATA%\CeruleanToolbox\connections.json`. The client secret is encrypted with Windows DPAPI for the current Windows user, so the file is only readable by that user on that machine. The secret is shown in clear only when the user presses **Show secret**.
- Exported connections (`.ceruleantoolbox` files) are encrypted with a passphrase chosen by the user.
- The web version listens on `localhost` only and has **no login of its own**. It must not be exposed to a network or the Internet: anyone who can reach it can use the active service principal.
- The PowerShell console runs real commands with the rights of the active service principal. It is meant for the local user only.
- The app sends the credentials only to Microsoft Entra ID and Azure, to sign in. It has no telemetry and does not send data to third parties.

Examples of problems worth reporting: a secret that is written to a log, a page or a file in clear; a way to read another user's saved connections; a way for a web page or another local process to use the app without the user (for example a cross-site request that reaches `localhost`); a command injection in the console or in a field that reaches a shell or a script.

Not a vulnerability by itself: the fact that a service principal with a broad role can do a lot in Azure (use the smallest role that does what you need), or that the web version has no login when you expose it yourself.
