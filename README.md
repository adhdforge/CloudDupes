# CloudDupes

Finds duplicate files and photos in **OneDrive** and **Google Drive**, both within each drive and across the two. It writes an HTML/CSV report and an editable plan, then moves the copies you don't want to the cloud's **recycle bin / trash**. It never deletes anything permanently.

## Match modes

You can pick one or more with `--mode exact,similar,namesize`, or set the default in `Matching:Modes`.

| Mode | How it matches | Cost | Default in plan |
|---|---|---|---|
| `exact` | Byte-identical files. It uses each cloud's own hashes (OneDrive quickXor/SHA-1/SHA-256, Google MD5/SHA-1/SHA-256). To compare across the two clouds, it downloads only files of equal size and hashes them with SHA-256. | Free within a cloud; downloads for cross-cloud candidates | proposed for deletion |
| `similar` | Photos that look the same (resized, re-compressed, lightly edited copies). It compares a perceptual hash (pHash) of each provider's thumbnail. | Fetches one thumbnail per image | all *Keep*, review first |
| `namesize` | Same file name (ignoring case) and same size. | Free | all *Keep*, review first |

The plan only proposes deletions for the modes listed in `Matching:AutoSelect` (or `--auto-select`), which is `exact` by default. Groups from the other modes are listed with every file set to `Keep`, so you can flip the ones you want in `plan.json`.

## One-time setup

### OneDrive (Microsoft Entra app registration)
1. Go to https://entra.microsoft.com → **App registrations** → **New registration**.
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**.
   - Redirect URI: platform **Public client/native (mobile & desktop)**, value `http://localhost`.
2. Under **API permissions**, add Microsoft Graph delegated permission **Files.ReadWrite.All**.
3. Copy the **Application (client) ID** into `OneDrive:ClientId`. You can put it in `appsettings.json`, set it with `dotnet user-secrets set OneDrive:ClientId <id> --project src/CloudDupes.Cli`, or use the environment variable `CLOUDDUPES_OneDrive__ClientId`.

### Google Drive (Google Cloud OAuth client)
1. Go to https://console.cloud.google.com and create a project.
2. Under **APIs & Services**, enable the **Google Drive API**.
3. Set up the **OAuth consent screen**: user type External, publishing status **Testing**, and add your own Google account as a test user. That's enough for personal use and needs no Google verification.
4. Go to **Credentials** → **Create credentials** → **OAuth client ID** → **Desktop app**, and download the JSON.
5. Save the JSON as `client_secret.json` in one of these places: the working directory, next to the exe, or `%LOCALAPPDATA%\CloudDupes`. You can also point `GoogleDrive:ClientSecretsPath` at it. Keep it out of git; `.gitignore` already excludes it.

## Usage

```powershell
dotnet run --project src/CloudDupes.Cli -- login onedrive
dotnet run --project src/CloudDupes.Cli -- login gdrive

# Read the inventory into the local cache. Later runs are incremental (OneDrive delta / Drive changes).
clouddupes scan
clouddupes status

# Find duplicates and write clouddupes-report\report.html, report.csv and plan.json
clouddupes find --mode exact,similar,namesize --keep oldest
clouddupes find --mode similar --path /Pictures --threshold 6
clouddupes find --keep prefer:onedrive --no-cross-download

# Review report.html, edit plan.json if you like ("action": "Keep" / "Delete"), then:
clouddupes apply clouddupes-report\plan.json --dry-run
clouddupes apply clouddupes-report\plan.json          # shows the preview and asks for confirmation
```

**Keep strategies** (`--keep`, `--keep-similar`): `oldest`, `newest`, `shortest-path`, `largest`, `prefer:onedrive`, `prefer:gdrive`. If `Keep:PathPriority` lists path prefixes (for example `["/Pictures/Originals"]`), files under them are kept first.

## Safety

- `apply` refuses a plan in which any group would lose every copy, or in which a file is marked *Keep* in one group and *Delete* in another.
- Before trashing a file, it checks that the file still exists and hasn't changed since the scan (eTag/version). Changed files are skipped.
- Files go to the OneDrive recycle bin or the Google Drive trash, where you can restore them. Each run writes a `trashed-*.jsonl` log next to the plan.
- Google Docs, Sheets and Slides, shortcuts and empty files are ignored. On Google Drive, only files you own are scanned.

## Local data

The `%LOCALAPPDATA%\CloudDupes` folder holds:
- `inventory.db`: a SQLite cache of the inventory, delta tokens, content hashes and thumbnails.
- `onedrive.msal.cache`: the OneDrive token, encrypted with DPAPI.
- `google-token\`: the Google refresh token.

To sign out, delete the token files. To force a full re-read, run `scan --full`.

## Project layout

```
src/CloudDupes.Core                  models, matchers, pHash, SQLite store, plan/apply, reports
src/CloudDupes.Providers.OneDrive    Microsoft Graph + MSAL
src/CloudDupes.Providers.GoogleDrive Drive API v3
src/CloudDupes.Cli                   System.CommandLine front end (exe name: clouddupes)
tests/CloudDupes.Core.Tests          xUnit tests
```
