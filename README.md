# Propnight Revival launcher

Windows launcher for the Propnight Revival community servers. It keeps the
revival patch up to date, signs players in and keeps their profile on the server.

Part of [Propnight Revival](https://propnightrevival.online): a fan project, not
affiliated with the game's developers or publisher.

It does **not** contain or download the game: install Propnight from your own
Steam library first. The launcher only adds the revival files on top of it.

## What it does when you press PLAY

1. Downloads `https://api.propnightrevival.online/updates/manifest.json` and
   checks its signature (ECDSA P-256) against the public key in
   [`src/Common.cs`](src/Common.cs). An unsigned or modified manifest is refused
   and nothing is changed.
2. Compares the SHA-256 of each listed file with your install and downloads only
   what differs. Every download is checked against the manifest before it
   replaces anything. Game files are backed up once in
   `steamapps\common\Propnight\revival-backups` before they are first replaced.
3. Patches `Propnight.exe` (byte patches listed in the manifest, applied only to
   the latest Steam build, checked before and after).
4. Gives your account name and ID to the Steam emulator
   (`steam_settings\force_account_name.txt`, `force_steamid.txt`).
5. Syncs `%LOCALAPPDATA%\Propnight\Saved\SaveGames\SaveUserData.sav` with the
   server (sent before the game starts and after every game, written back when
   the server keeps its own copy). Older local saves are kept in
   `%APPDATA%\PropnightRevival\save-backups`.
6. Starts the game and stays open until it closes.

## Friends

The FRIENDS card adds players by username (requests, accept, decline, remove)
and shows where they are: in the launcher, playing, hosting a game or in one.
**Join** connects to a friend's game through the revival relay; **Invite** sends
your game to a friend, who gets a notification with a Join button. Joining
writes `joinrelay <port>` to the mod's command file (the relay address comes
from the mod's `online.txt`), starting the game first when needed.

The interface (`ui/index.html`) is HTML/CSS shown with Microsoft Edge WebView2,
which ships with Windows 10/11; the WebView2 SDK files are embedded in the exe.
Updates are announced, never forced silently: a toast and a banner say when the
launcher or the game needs an update, and nothing is downloaded before you press
the button.

It never asks for administrator rights. Your login token is stored encrypted
for your Windows user (DPAPI) in `%APPDATA%\PropnightRevival\settings.json`.

## Build

Requires the .NET SDK (any recent version); the launcher targets
.NET Framework 4.8, which is part of Windows 10 and 11.

    dotnet build -c Release -o dist

Development aid: `PROPNIGHT_LAUNCHER_SCREENSHOT=<file.png>` saves a capture of the
interface after a few seconds (`PROPNIGHT_LAUNCHER_SCRIPT` runs JavaScript first,
e.g. `render({...})` or `toast(...)`).

`release/release.py` builds, signs and uploads an update (it needs the private
signing key and the storage keys, which are not in this repository);
`release/signing-public-key.txt` is the key the launcher checks against.

Builds are deterministic: the same source gives the same `PropnightRevival.exe`,
so you can compare your build's SHA-256 with the published one.

## License

MIT, see [LICENSE](LICENSE).
