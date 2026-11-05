"""Publish an update for the launcher.

    python revival/release/release.py            build the manifest, sign it, upload what changed
    python revival/release/release.py --dry-run  build and sign only (revival/release/out/)
    python revival/release/release.py --beta     publish to the test channel only (prints its code)
    python revival/release/release.py --diag     publish to the diagnostic channel only (code diag-...)

Test channel: the same files, under a manifest in a secret folder of the VPS
(updates/beta-<code>/, no directory listing). Its code is in
revival/release/.beta-code (private, created on the first --beta); a player
types it in the launcher's Settings > Test channel. A normal release.py then
publishes the tested install to everyone.

What players get is the list below, taken from this PC's patched game install
(the reference install). Files go to the Cloudflare R2 bucket under their SHA-256
(content-addressed: an unchanged file is never uploaded again; downloads from R2
cost nothing). Only the small signed manifest is on the VPS. R2 credentials and
the bucket's public URL: revival/release/.r2.json (private). The manifest is
signed with the private key revival/release/.signing-key.pem (never uploaded,
never published); the launcher only accepts a manifest signed with the matching
public key built into it.
"""
import argparse, base64, hashlib, importlib.util, json, os, re, sys, time
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REVIVAL = HERE.parent
sys.path.insert(0, str(REVIVAL / "tools"))
from game_root import COMMON  # noqa: E402  (reference install: steamapps/common)
GAME = COMMON / "Propnight"                  # game root as seen by the launcher
BASE_URL = "https://api.propnightrevival.online/updates/"   # manifest (VPS)
R2_CONFIG = HERE / ".r2.json"
REMOTE_DIR = "/opt/propnight-revival/updates"
BETA_CODE = HERE / ".beta-code"
# diagnostic channel (launcher >= 1.17.3): code "diag-...", its players count as public, the mod measures its ticks
DIAG_CODE = HERE / ".diag-code"


def beta_code(path=BETA_CODE, prefix=""):
    if not path.exists():
        import secrets
        path.write_text(prefix + secrets.token_urlsafe(12).replace("_", "x").replace("-", "y") + "\n", encoding="utf-8")
    return path.read_text(encoding="utf-8").strip()

# Paths relative to the game root (steamapps/common/Propnight).
FILES = [
    "Propnight/Binaries/Win64/dwmapi.dll",
    "Propnight/Binaries/Win64/UE4SS.dll",
    "Propnight/Binaries/Win64/UE4SS-settings.ini",
    "Propnight/Binaries/Win64/Mods/PropnightLoginBypasser/Scripts/main.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/main.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/social.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/hosting.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/lobby.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/endscreen.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/fixes.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/maps.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/lootbox.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/commands.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/diagnostics.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/anticheat.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/hostleft.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/mapselect.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/overlay.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/channel.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/replay.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/resume.lua",
    # anti-cheat: the programs that open the game to write into it (tools/guard, loaded by anticheat.lua)
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/RevivalGuard.dll",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/online.txt",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/avatar.png",   # squad / friends avatar picture
    "Propnight/Binaries/Win64/RevivalRelay/RevivalRelay.exe",
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalOnline_P.pak",
    # gameplay Blueprint fixes (same-length patches, tools/kismet/build_fixes_pak.py)
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalFixes_P.pak",
    # Steam emulator (Goldberg, LGPL): the game runs without Steam's client
    "Engine/Binaries/ThirdParty/Steamworks/Steamv147/Win64/steam_api64.dll",
]
# Test channels only (--beta / --diag): the in-game admin menu (F10: money, items, machines, teleport). Never in the
# public manifest: build() refuses it there. The mod also stays off unless the launcher wrote the channel's flag file,
# and the launcher (1.18.2+) deletes it on a PC back on the public release.
TEST_ONLY_FILES = [
    "Propnight/Binaries/Win64/Mods/PropnightAdmin/Scripts/main.lua",
]
TEST_ONLY_MODS = ["PropnightAdmin"]
# (no large file since the classic maps DLC split, 2026-10-30)
LARGE_FILES = []
# Optional content (launcher DLC tab): files installed only when the player turns their pack on. Since 2026-10-30 the
# classic maps are three packs (tools/kismet/split_dlc.py): each year's maps, their fixes and their own art in one pak,
# and a common pak (what several years share, the props' breakage table, the 1.0.6 machine model) with the selector
# list, both installed with any of the three. A file's value: its pack, or the list of packs any of which installs it
# (manifest "dlc": the first one, for launchers before 1.50; "dlcAny": the list).
CLASSIC = ["classic-2021", "classic-2022", "classic-2023"]
DLC_FILES = {
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassicCommon_10_P.pak": CLASSIC,
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMenuClassic_1_P.pak": CLASSIC,
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassic2021_11_P.pak": "classic-2021",
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassic2022_12_P.pak": "classic-2022",
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassic2023_13_P.pak": "classic-2023",
    # small fixes over a year's pak (its levels changed since, without sending the big pak again; 2026-10-30: the 1.0.6
    # machine model kept loaded by the 2021 / May 2022 maps)
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassic2021Fix_14_P.pak": "classic-2021",
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassic2022Fix_15_P.pak": "classic-2022",
    # over the common pak: the 56 files with Cyrillic letters in their names the split dropped (broken crown, Corgis,
    # cabinet table; the Castle chandelier, the kitchen furniture, the crown's textures) and Abbey (2021)'s toy car breakage
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalClassicCommonFix_16_P.pak": CLASSIC,
    # the Reborn Beta maps (2026-11-03, a community member's: tools/kismet/reborn_beta.py), with their own map list
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalRebornBeta_17_P.pak": "reborn-beta",
    # over it: the farm's small lights without dynamic shadows (42 -> 237 fps; tools/kismet/reborn_beta.py fixpak)
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalRebornBetaFix_18_P.pak": "reborn-beta",
    # the classic packs' packages its levels and materials use (344, ~400 MB): the DLC works without any classic pack
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalRebornBetaClassic_19_P.pak": "reborn-beta",
}
# Files of older releases a launcher (1.50+) deletes once the files of the packs it installs are in (the one classic
# maps DLC's paks: the same paths as the new ones', never loaded together); first when the disk lacks room for both
REMOVED_FILES = [f"Propnight/Content/Paks/Propnight-WindowsNoEditor_{n}.pak" for n in (
    "RevivalMaps_P", "RevivalMapsPatch_1_P", "RevivalMapsPatch_2_P", "RevivalMapsFix_3_P", "RevivalMapsSchool_4_P",
    "RevivalMapsHouse_5_P", "RevivalMapsFix_6_P", "RevivalMapsFix_7_P")]
# every player: the game's own map selector list, sorted A to Z (build_modpak.py menu: RevivalMenuBase.pak)
FILES.append("Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMenu_P.pak")
FILES.extend(DLC_FILES)
# Previews: cut from the October 2022 and May 2022 map selector atlases (revival/release/dlc, not published in the repo).
# The pack a player had before the split ("classic-maps") turns the three on (launcher 1.50: Updater.DlcDefaults).
DLC = [{
    "id": "classic-2021",
    "title": "2021 Classic",
    "description": "The maps as Propnight came out in December 2021: the first Abbey, Camp and House, with their own "
                   "machines. Hosts pick them in Custom Game; quick play only sends you to maps you have.",
    "maps": [
        {"name": "Abbey (2021)", "from": "December 2021", "image": "abbey-2021.jpg"},
        {"name": "Camp (2021)", "from": "December 2021", "image": "camp-2021.jpg"},
        {"name": "House (2021)", "from": "December 2021", "image": "house-2021.jpg"},
    ],
}, {
    "id": "classic-2022",
    "title": "2022 Classic",
    "description": "The 2022 maps, as they were last before the Reborn update. Hosts pick them in Custom Game; quick play "
                   "only sends you to maps you have.",
    "maps": [
        {"name": "Farm (day)", "from": "May 2022", "image": "farm-day.jpg"},
        {"name": "Castle", "from": "May 2022", "image": "castle.jpg"},
        {"name": "Village", "from": "May 2022", "image": "village.jpg"},
        {"name": "School (2022)", "from": "May 2022", "image": "school-2022.jpg"},
        {"name": "House (2022)", "from": "October 2022", "image": "house-2022.jpg"},
    ],
}, {
    "id": "classic-2023",
    "title": "2023 Classic",
    "description": "The Reborn School and House as they were last, in February 2023. Hosts pick them in Custom Game; "
                   "quick play only sends you to maps you have.",
    "maps": [
        {"name": "School (Reborn)", "from": "February 2023", "image": "school.jpg"},
        {"name": "House (Reborn)", "from": "February 2023", "image": "mansion.jpg"},
    ],
}, {
    "id": "reborn-beta",
    "title": "Reborn Beta",
    "description": "Camp and the small farm as they were in the October 2022 Reborn beta, brought back by a member of the "
                   "community. Hosts pick them in Custom Game.",
    "maps": [
        {"name": "Camp (Reborn Beta)", "from": "October 2022", "image": "camp-reborn-beta.jpg"},
        {"name": "Farm (Reborn Beta)", "from": "October 2022", "image": "farm-reborn-beta.jpg"},
    ],
}]
DLC_IMAGES = HERE / "dlc"
# Public release (launcher 1.41.0+): the only DLLs next to Propnight.exe and paks under Content/Paks that load, besides
# the files above. Any other DLL there (version.dll, winmm.dll…: loaded by Windows with the game, another UE4SS or a
# trainer) or pak (pak signing is off in the exe patch) is turned off; a game DLL with another hash stops PLAY.
# The reference install's hashes are read at build, plus those of the March 2023 build (10856378, v5.4.4) that differ.
GAME_DLLS = ["D3DCompiler_43.dll", "libcrypto-1_1-x64.dll", "libcurl.dll", "libssl-1_1-x64.dll", "msacm32.dll",
             "turbojpeg.dll", "xinput1_3.dll"]
GAME_DLLS_OLDER_BUILD = {
    "libcrypto-1_1-x64.dll": "7d0bca5463c7a3510e0ac039200ed46b40667008c458d68969a46acac658bfc3",
    "libcurl.dll": "4eb547db7375076f90b9b406e5b82f59d06e629513954c75cfdf43292f248098",
    "libssl-1_1-x64.dll": "83890e946259d44a7fc6d0b8e5a997d59e662251fa330965dc0849985d6f30a7",
    "turbojpeg.dll": "de61126e8459634801d09a6953a06b6c5c9e5a2c66a49f34dc91438508ea0e05",
}
GAME_PAKS = ["Propnight-WindowsNoEditor.pak"]
LAUNCHER = REVIVAL / "launcher/dist/PropnightRevival.exe"   # dotnet build -c Release -o dist

# The launcher is published as built (2026-11-01): the ConfuserEx obfuscation of 1.52.0 / 1.53.0 is withdrawn, Microsoft's
# machine-learning guess (Trojan:Win32/Wacatac.C!ml) flagged it again and again; the cheat checks that matter are the
# server's, the host's and RevivalGuard.dll (loaded by the mod), not the launcher's. tools/confuserex is kept unused.
LAUNCHER_VERSION_FILE = REVIVAL / "launcher/version.txt"


def sha256_of(path, cache):
    st = path.stat()
    key = str(path)
    hit = cache.get(key)
    if hit and hit["size"] == st.st_size and hit["mtime"] == st.st_mtime:
        return hit["sha256"]
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8 << 20), b""):
            h.update(chunk)
    cache[key] = {"size": st.st_size, "mtime": st.st_mtime, "sha256": h.hexdigest()}
    return cache[key]["sha256"]


def exe_patch():
    spec = importlib.util.spec_from_file_location("bcp", REVIVAL / "distribution/Build-ClientPatch.py")
    bcp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bcp)
    exe = (GAME / "Propnight/Binaries/Win64/Propnight.exe").read_bytes()
    patches = []
    for name, rva, original, replacement in bcp.STEAM_NODE_PATCHES:
        patches.append({"name": name, "offset": bcp.file_offset_for_rva(exe, rva), "original": original, "replacement": replacement})
    patched = bytearray(exe)
    for p in patches:
        cur = bytes(patched[p["offset"]:p["offset"] + len(p["original"]) // 2]).hex()
        if cur not in (p["original"], p["replacement"]):
            raise SystemExit(f"unexpected bytes for {p['name']} in the reference exe")
        patched[p["offset"]:p["offset"] + len(p["replacement"]) // 2] = bytes.fromhex(p["replacement"])
    return {
        "path": "Propnight/Binaries/Win64/Propnight.exe",
        "originalSha256": bcp.GAME_EXE_ORIGINAL_SHA256,
        "acceptedSha256": [bcp.GAME_EXE_ORIGINAL_SHA256, bcp.GAME_EXE_ACHIEVEMENT_ONLY_SHA256, *bcp.GAME_EXE_PREVIOUS_PATCHED_SHA256],
        "patchedSha256": hashlib.sha256(patched).hexdigest(),
        "patches": patches,
    }


def sign(data):
    """ECDSA P-256 / SHA-256, raw r||s (64 bytes): the format .NET Framework's ECDsaCng verifies."""
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec, utils
    key = serialization.load_pem_private_key((HERE / ".signing-key.pem").read_bytes(), None)
    r, s = utils.decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


PATCH_NOTES = HERE / "patch-notes.json"


def patch_notes(keep=8):
    """Player-facing notes shown on the launcher home page, newest first (release/patch-notes.json).
    Each entry: version, title, date, launcher (optional), image (optional: a banner shown above the notes, a file of
    site/img/notes/ given as its address on the site, published by deploy_site.py), items [{kind: fix|new|change|next,
    text}]. Only what is done,
    plus the plans announced with it (next: "What's next", shown last; launcher >= 1.18.7, older ones list it with the
    changes); the state of the bugs being tested lives in the admin's Bugs page, not here."""
    # the notes are written in the admin (admin › Patch notes) since 2026-10-26: the ones published to everyone, from the
    # server (launchers before 1.36 read them from the manifest); the file only when the server cannot be reached
    notes = None
    try:
        import urllib.request
        with urllib.request.urlopen("https://api.propnightrevival.online/revival/patchnotes/public", timeout=20) as r:
            notes = json.load(r).get("notes") or None
        if notes:
            print(f"patch notes: {len(notes)} from the server (admin › Patch notes)")
    except Exception as e:
        print(f"patch notes: server not reached ({e}), release/patch-notes.json used")
    if notes is None:
        if not PATCH_NOTES.is_file():
            return []
        notes = json.loads(PATCH_NOTES.read_text(encoding="utf-8"))
    for n in notes:
        # the images between the lines (admin › Patch notes) are for launcher 1.36.1+, which reads the notes from the server:
        # the manifest's notes (older launchers) keep the lines only
        n["items"] = [it for it in n.get("items") or [] if it.get("text")]
        if not n.get("version") or not n.get("items"):
            raise SystemExit(f"patch-notes.json: entry without version or items: {n}")
        if n.get("image"):
            # a banner sent in the admin (kept by the server), or a picture of the site's img/notes
            from_admin = re.fullmatch(r"https://api\.propnightrevival\.online/revival/suggestion-img/[0-9a-f]{24}\.(?:png|jpg|webp|gif)", n["image"])
            m = re.fullmatch(r"https://propnightrevival\.online/img/notes/([\w.-]+\.(?:jpg|png|webp))", n["image"])
            if not from_admin and (not m or not (REVIVAL / "site/img/notes" / m.group(1)).is_file()):
                raise SystemExit(f"patch-notes.json: image of {n['version']} must be https://propnightrevival.online/img/notes/<file> "
                                 f"with the file in site/img/notes (deploy_site.py publishes it): {n['image']}")
        for it in n["items"]:
            if it.get("kind") not in ("fix", "new", "change", "next", "info") or not it.get("text"):
                raise SystemExit(f"patch-notes.json: bad item in {n['version']}: {it}")
    return notes[:keep]


# --hold PATH...: files of the reference install that are not ready (2026-10-28: House 2022 in the maps paks) keep the
# public release's version: their entry is copied from the public manifest, nothing of them is uploaded
HELD = set()


def public_manifest():
    import urllib.request
    req = urllib.request.Request(BASE_URL + "manifest.json", headers={"User-Agent": "PropnightRevival-release"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read())


def public_entries():
    return {e["path"]: e for e in public_manifest()["files"]}


def build(test=False):
    """test: a test channel's manifest (--beta / --diag), with the admin menu; the public one never has it."""
    # the Reborn Beta maps' pak has its own map list (the game reads the last pak's): it must still be the classic one +
    # its rows, or the classic maps' list changes would be hidden from its players
    sys.path.insert(0, str(REVIVAL / "tools/kismet"))
    import reborn_beta
    if not reborn_beta.check():
        raise SystemExit("the Reborn Beta maps' map list is out of step with the classic one (tools/kismet/reborn_beta.py)")
    cache_path = HERE / "out/.hash-cache.json"
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    cache = json.loads(cache_path.read_text()) if cache_path.exists() else {}
    entries = []
    files = FILES + (TEST_ONLY_FILES if test else [])
    for rel, large in [(f, False) for f in files] + [(f, True) for f in LARGE_FILES]:
        path = GAME / rel
        if not path.is_file():
            raise SystemExit(f"missing in the reference install: {rel}")
        entry = {"path": rel, "size": path.stat().st_size, "sha256": sha256_of(path, cache), "large": large}
        if rel in DLC_FILES:
            packs = DLC_FILES[rel]
            if isinstance(packs, list):
                entry["dlc"], entry["dlcAny"] = packs[0], packs
            else:
                entry["dlc"] = packs
        entries.append(entry)
    if HELD - {"launcher"}:
        public = public_entries()
        missing = HELD - {e["path"] for e in entries} - {"launcher"}
        if missing:
            raise SystemExit(f"--hold: not a published file: {', '.join(sorted(missing))}")
        # a held file the public release does not have yet (2026-11-05: replay.lua, new on the test channels) stays out
        # of a public manifest; on a test channel it must be public (its entry is the public one)
        new = [e["path"] for e in entries if e["path"] in HELD and e["path"] not in public]
        if new and test:
            raise SystemExit(f"--hold: not in the public release: {', '.join(new)}")
        for path in new:
            print(f"--hold: {path} is not public yet: left out")
        entries = [e for e in entries if e["path"] not in new]
        for entry in entries:
            if entry["path"] in HELD:
                entry.update(size=public[entry["path"]]["size"], sha256=public[entry["path"]]["sha256"])
    dlc = []
    for pack in DLC:
        pack = json.loads(json.dumps(pack))
        pack["size"] = sum(e["size"] for e in entries if pack["id"] in (e.get("dlcAny") or [e.get("dlc")]))
        for m in pack["maps"]:
            img = DLC_IMAGES / m["image"]
            m["image"] = {"sha256": sha256_of(img, cache), "size": img.stat().st_size}
        dlc.append(pack)
    manifest = {
        "version": datetime.now(timezone.utc).strftime("%Y.%m.%d-%H%M"),
        "created": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "filesUrl": r2_config()["publicUrl"].rstrip("/") + "/files/",
        "files": entries,
        "exePatch": exe_patch(),
        "modsEnabled": ["PropnightLoginBypasser", "PropnightLocalMap"] + (TEST_ONLY_MODS if test else []),
        # public release: the only Lua mods a launcher (1.40.4+) lets UE4SS load: ours, whose files the launcher checks
        # (bugs 137 / 149: an old community pack loaded next to ours). Not UE4SS's stock ConsoleEnablerMod /
        # ConsoleCommandsMod / Keybinds: unused by our mod, their scripts not checked (a script put there would load),
        # and the console commands change game values. The test channels keep any mod.
        "modsAllowed": ["PropnightLoginBypasser", "PropnightLocalMap"],
        "gameDlls": {name: sorted({sha256_of(GAME / "Propnight/Binaries/Win64" / name, cache)} |
                                  ({GAME_DLLS_OLDER_BUILD[name]} if name in GAME_DLLS_OLDER_BUILD else set()))
                     for name in GAME_DLLS},
        "paksAllowed": GAME_PAKS,
        "dlc": dlc,
        "removed": REMOVED_FILES,
        "notes": patch_notes(),
    }
    if LAUNCHER.is_file():
        # version.txt is what the launchers compare: it must be the csproj's (2026-10-27: 1.40.3 built, 1.40.2 announced,
        # nobody was offered the update)
        csproj = re.search(r"<Version>([^<]+)</Version>", (REVIVAL / "launcher/PropnightRevival.csproj").read_text(encoding="utf-8"))
        if csproj and csproj.group(1).strip() != LAUNCHER_VERSION_FILE.read_text().strip():
            raise SystemExit(f"launcher/version.txt ({LAUNCHER_VERSION_FILE.read_text().strip()}) is not the csproj's version "
                             f"({csproj.group(1).strip()}): update it (and rebuild dist) first")
        manifest["launcher"] = {"version": LAUNCHER_VERSION_FILE.read_text().strip(), "size": LAUNCHER.stat().st_size,
                                "sha256": sha256_of(LAUNCHER, cache)}
    if "launcher" in HELD:   # --hold launcher: the public release's launcher stays (its file is on R2 already)
        manifest["launcher"] = public_manifest()["launcher"]
    cache_path.write_text(json.dumps(cache))
    if not test:
        leaked = [e["path"] for e in entries if any(m in e["path"] for m in TEST_ONLY_MODS)]
        if leaked or set(TEST_ONLY_MODS) & set(manifest["modsEnabled"]):
            raise SystemExit(f"the admin menu must never reach the public release: {leaked or TEST_ONLY_MODS}")
    data = json.dumps(manifest, indent=2).encode()
    (HERE / "out/manifest.json").write_bytes(data)
    (HERE / "out/manifest.sig").write_bytes(base64.b64encode(sign(data)))
    return manifest, data


def r2_config():
    cfg = json.loads(R2_CONFIG.read_text())
    missing = [k for k in ("endpoint", "bucket", "accessKeyId", "secretAccessKey", "publicUrl") if not cfg.get(k)]
    if missing:
        raise SystemExit(f"fill {', '.join(missing)} in {R2_CONFIG}")
    return cfg


def upload(manifest, data, folder=""):
    import boto3
    from boto3.s3.transfer import TransferConfig
    cfg = r2_config()
    s3 = boto3.client("s3", endpoint_url=cfg["endpoint"], aws_access_key_id=cfg["accessKeyId"],
                      aws_secret_access_key=cfg["secretAccessKey"], region_name="auto")
    transfer = TransferConfig(multipart_threshold=64 << 20, multipart_chunksize=64 << 20, max_concurrency=4)
    sources = [(GAME / e["path"], e["sha256"], e["size"]) for e in manifest["files"] if e["path"] not in HELD]
    for pack, src in zip(manifest["dlc"], DLC):
        for m, m_src in zip(pack["maps"], src["maps"]):
            sources.append((DLC_IMAGES / m_src["image"], m["image"]["sha256"], m["image"]["size"]))
    if "launcher" in manifest and "launcher" not in HELD:
        sources.append((LAUNCHER, manifest["launcher"]["sha256"], manifest["launcher"]["size"]))
    for path, sha, size in sources:
        key = f"files/{sha}"
        try:
            if s3.head_object(Bucket=cfg["bucket"], Key=key)["ContentLength"] == size:
                continue
        except s3.exceptions.ClientError:
            pass
        print(f"upload {path.name} ({size / 1e6:.1f} MB)")
        start, last = time.time(), [0.0]

        def progress(n, done=[0]):
            done[0] += n
            if size > 50e6 and time.time() - last[0] > 15:
                last[0] = time.time()
                print(f"  {done[0] / 1e6:.0f} / {size / 1e6:.0f} MB ({done[0] / 1e6 / max(time.time() - start, 1):.1f} MB/s)")
        s3.upload_file(str(path), cfg["bucket"], key, Config=transfer, Callback=progress,
                       ExtraArgs={"ContentType": "application/octet-stream", "CacheControl": "public, max-age=31536000, immutable"})
    # the manifest (small, signed) stays on the VPS and goes last, swapped atomically
    sys.path.insert(0, str(REVIVAL / "server/deploy"))
    import remote
    client, password = remote.connect()
    try:
        target = f"{REMOTE_DIR}/{folder}".rstrip("/")
        remote.run(client, f"mkdir -p {target} && chown -R {remote.target()[1]} {REMOTE_DIR}", password)
        sftp = client.open_sftp()
        for name, content in (("manifest.sig", (HERE / "out/manifest.sig").read_bytes()), ("manifest.json", data)):
            with sftp.open(f"{target}/{name}.new", "wb") as f:
                f.write(content)
        for name in ("manifest.sig", "manifest.json"):
            sftp.posix_rename(f"{target}/{name}.new", f"{target}/{name}")
        sftp.close()
    finally:
        client.close()


def prune():
    """R2 holds every file ever published (files/<sha256>): those that no channel's live manifest (public, test,
    diagnostic) names any more are deleted (2026-10-28: the bucket went past the 10 GB of R2's free tier). media/ is
    left alone."""
    import boto3, urllib.request
    cfg = r2_config()
    s3 = boto3.client("s3", endpoint_url=cfg["endpoint"], aws_access_key_id=cfg["accessKeyId"],
                      aws_secret_access_key=cfg["secretAccessKey"], region_name="auto")
    used = set()
    for folder in ("", f"beta-{beta_code()}/", f"beta-{beta_code(DIAG_CODE, 'diag-')}/"):
        req = urllib.request.Request(BASE_URL + folder + "manifest.json", headers={"User-Agent": "PropnightRevival-release"})
        with urllib.request.urlopen(req, timeout=20) as r:
            m = json.loads(r.read())
        used |= {"files/" + e["sha256"] for e in m["files"]}
        if "launcher" in m:
            used.add("files/" + m["launcher"]["sha256"])
        used |= {"files/" + mp["image"]["sha256"] for pack in m.get("dlc", []) for mp in pack["maps"]}
    objects, token = [], None
    while True:
        r = s3.list_objects_v2(Bucket=cfg["bucket"], **({"ContinuationToken": token} if token else {}))
        objects += r.get("Contents", [])
        if not r.get("IsTruncated"):
            break
        token = r["NextContinuationToken"]
    gone = [o for o in objects if o["Key"].startswith("files/") and o["Key"] not in used]
    for i in range(0, len(gone), 1000):
        s3.delete_objects(Bucket=cfg["bucket"], Delete={"Objects": [{"Key": o["Key"]} for o in gone[i:i + 1000]], "Quiet": True})
    left = sum(o["Size"] for o in objects) - sum(o["Size"] for o in gone)
    print(f"R2: {len(gone)} unused file(s) deleted ({sum(o['Size'] for o in gone) / 1e9:.2f} GB), {left / 1e9:.2f} GB left")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--beta", action="store_true", help="publish to the test channel only")
    ap.add_argument("--diag", action="store_true", help="publish to the diagnostic channel only (code diag-...)")
    ap.add_argument("--hold", nargs="+", default=[], metavar="PATH", help="keep these files (or \"launcher\") at the public release's version")
    args = ap.parse_args()
    HELD.update(args.hold)
    manifest, data = build(test=args.beta or args.diag)
    total = sum(e["size"] for e in manifest["files"])
    print(f"version {manifest['version']}: {len(manifest['files'])} files, {total / 1e9:.2f} GB")
    if not args.dry_run:
        if args.diag:
            code = beta_code(DIAG_CODE, "diag-")
            upload(manifest, data, f"beta-{code}")
            print(f"published to the diagnostic channel (launcher Settings > Test channel, code: {code})")
        elif args.beta:
            code = beta_code()
            upload(manifest, data, f"beta-{code}")
            print(f"published to the test channel (launcher Settings > Test channel, code: {code})")
        else:
            upload(manifest, data)
            print(f"published: {BASE_URL}manifest.json")
        prune()


if __name__ == "__main__":
    main()
