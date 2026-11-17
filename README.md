# Propnight Revival Launcher

The Windows launcher for [Propnight Revival](https://propnightrevival.online).
Propnight's official servers have shut down, and the revival brings the game back
online with its own servers, fixes and content. The launcher installs and updates
the revival on top of your copy of the game, signs you in, and takes you into games.

It does **not** contain or download the game. Install Propnight from your own
Steam library first. Propnight Revival is a fan project, not affiliated with the
game's developers or publisher.

## Features

- **One-click play:** updates the revival files and starts the game.
- **Account:** sign in, keep your profile, progress, level and rank on the server. Your save is synced, and older copies are kept locally.
- **Play together:**
  - Friends, parties and invites.
  - Join a friend's game through the revival relay, with no port forwarding.
  - Public and custom games.
- **Maps & DLC:** install or remove map packs, such as the classic 2021, 2022 and 2023 maps and Reborn Beta. New packs are marked as new.
- **Community:**
  - Suggestions with votes and comments, and polls.
  - Patch notes and news.
  - Bug and player reports, with the game's logs attached on request.
- **In-game overlay:** friends, notifications and a cast HUD for streams (an OBS browser source).
- **Test channels:** beta and diagnostic builds, opened with a code in Settings.
- **14 languages:** English, French, German, Spanish, Italian, Portuguese (Brazil), Polish, Russian, Swedish, Turkish, Japanese, Korean, and Chinese (simplified and traditional).

## What happens when you press PLAY

1. **Checks the update list.** It downloads the update manifest from `https://api.propnightrevival.online/updates/`. It checks the manifest's signature (ECDSA P-256) against the public key in [`src/Common.cs`](src/Common.cs). An unsigned or modified manifest is refused and nothing is changed.
2. **Downloads what changed.** It compares the SHA-256 of each listed file with your install and downloads only what differs. Each download is checked against the manifest before it replaces anything. Game files are backed up once, in `steamapps\common\Propnight\revival-backups`, before they are first replaced.
3. **Patches the game.** It applies the byte patches listed in the manifest to `Propnight.exe`. They are applied only to the latest Steam build, and checked before and after.
4. **Syncs your save.** It sends your save to the server and writes back the server's copy when that one is newer. Older local saves are kept in `%APPDATA%\PropnightRevival\save-backups`.
5. **Starts the game.** The launcher stays open while you play.

Updates are always announced, never installed silently. A banner tells you when
the launcher or the revival needs an update, and nothing is downloaded until you
press the button.

The launcher never asks for administrator rights. Your sign-in token is stored
encrypted for your Windows user (DPAPI), in `%APPDATA%\PropnightRevival\settings.json`.

## Build

You need the .NET SDK (any recent version). The launcher targets .NET Framework
4.8, which is included in Windows 10 and 11. The interface is HTML/CSS in
[`ui/`](ui/), shown with Microsoft Edge WebView2; the WebView2 SDK files are
embedded in the exe.

    dotnet build -c Release -o dist

Builds are deterministic: the same source always gives the same
`PropnightRevival.exe`, so you can compare the SHA-256 of your build with the
published one.

`release/release.py` builds, signs and uploads an update. It needs the private
signing key and the storage keys, which are not in this repository.
`release/signing-public-key.txt` is the key the launcher checks updates against.

## License

MIT, see [LICENSE](LICENSE).
