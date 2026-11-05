# Sharing: registering the app with the services

YouTube, Google Drive, OneDrive and Dropbox need the app registered with each service. Until a copy has a registration, Settings › Destinations says so and doesn't offer that service. **This is the owner's job, once per product.** No account of the user's is involved: each user signs in for themselves in their own browser.

Nothing here is a secret an installed app could keep. OAuth for installed apps (RFC 8252) assumes that, and PKCE is what protects the sign-in.

## Where the ids go

```powershell
powershell -File tools\package.ps1 -GoogleClientId … -GoogleClientSecret … -MicrosoftClientId … -DropboxAppKey …
```

A build server can set `CASHGEAR_GOOGLE_CLIENT_ID`, `CASHGEAR_GOOGLE_CLIENT_SECRET`, `CASHGEAR_MICROSOFT_CLIENT_ID` and `CASHGEAR_DROPBOX_APP_KEY` instead. `settings.json` overrides any of them, for trying a registration out without building:

```json
{ "destinations": { "sharing": { "clients": { "google": "…", "googleSecret": "…", "microsoft": "…", "dropbox": "…" } } } }
```

Giving a Google id in `settings.json` without a secret doesn't borrow the built-in secret.

## Google (YouTube and Drive)

1. In the [Google Cloud console](https://console.cloud.google.com/), make a project and enable **YouTube Data API v3** and **Google Drive API**.
2. Set up the **OAuth consent screen**: the app's name, a support address and a privacy policy. Add the scopes below.
3. **Credentials › Create credentials › OAuth client ID › Desktop app.** The id and "client secret" are what `-GoogleClientId` and `-GoogleClientSecret` take. A desktop client accepts `http://127.0.0.1` on any port, with nothing to register.

| Scope | Why | What Google asks |
|---|---|---|
| `https://www.googleapis.com/auth/youtube` | Uploads, and adding to a playlist (the narrower `youtube.upload` can't) | **Sensitive: verification required** |
| `https://www.googleapis.com/auth/drive.file` | Only files this app made | None |

**Until the app is published and Google has verified it,** only the test users listed on the consent screen can sign in (up to 100), a warning page shows first, and in "Testing" status their refresh tokens last 7 days ([OAuth 2.0 for desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app)). Each user then signs in again every week.

**Videos from unaudited projects.** From 2020 these were locked as private until the project passed YouTube's audit. YouTube's [revision history](https://developers.google.com/youtube/data/revision_history) says that ended on 2 November 2026, though some of its other pages still describe the old rule. Check it with a real upload set to Unlisted. The app always says who can see a video, since YouTube's own default without that is public.

## Microsoft (OneDrive)

1. In the [Entra admin center](https://entra.microsoft.com/), **App registrations › New registration**.
2. **Supported account types:** personal Microsoft accounts and accounts in any organizational directory.
3. **Redirect URI:** platform **Mobile and desktop applications**, `http://localhost`. Entra then takes any port.
4. **Authentication › Allow public client flows: Yes.** No secret is made.
5. **API permissions:** Microsoft Graph, delegated: `Files.ReadWrite`, `User.Read`, `offline_access`.

The **Application (client) ID** is what `-MicrosoftClientId` takes. An organization's administrator may have to consent to the permissions, and may not allow links for anyone.

## Dropbox

1. In the [App Console](https://www.dropbox.com/developers/apps), **Create app**: Scoped access, Full Dropbox.
2. **Permissions:** `files.content.write`, `sharing.write`, `account_info.read`.
3. **OAuth 2 › Redirect URIs:** `http://127.0.0.1:53917/`. Dropbox compares the whole address, port included, so the app listens on that port alone for it (`DropboxDestination.RedirectPort`). If something else holds the port, signing in says so.
4. **PKCE and no secret:** the app key is all the app needs. It is what `-DropboxAppKey` takes.

A new app starts in development status, with a limit on how many users can link it. Apply for production in the App Console before that matters.

## What to check by hand once registered

Everything here is tested against local fake servers (`tests\CashGear.Capture.Outputs.Tests\Sharing\FakeSharingServer.cs`). These can only be checked against the real services; TESTING.md lists them as pending.

For each service:
1. Sign in from Settings › Destinations; the row shows the account. **Test** says signed in as that account.
2. Add it to the recording workflow with Copy the link. Record a few seconds: the capture toolbar stays free, the tray shows progress, a notification with the link comes when it is done, and the link opens the file.
3. Turn the network off in the middle of a large upload and back on: it goes on from where it was (the service's own file has the right length and plays).
4. Sleep the PC in the middle of a large upload, and wake it. Quit the app in the middle, and start it again. Both go on.
5. Sign out; the app is gone from nothing on the service's side (see below), and Test says not signed in.

For YouTube: title, description with chapter times, tags, privacy, playlist, thumbnail (a channel has to be phone-verified for custom thumbnails, else the video goes up and the note says why), and the chapters showing on the watch page.

## What signing out does

It forgets the tokens on this PC. It doesn't revoke the app's access at the service, which the user can do in their account's settings (Google: Third-party connections; Microsoft: account.live.com/consent; Dropbox: Connected apps).
