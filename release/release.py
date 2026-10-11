"""Publish an update for the launcher.

    python revival/release/release.py            build the manifest, sign it, upload what changed
    python revival/release/release.py --dry-run  build and sign only (revival/release/out/)

What players get is the list below, taken from this PC's patched game install
(the reference install). Files go to the Cloudflare R2 bucket under their SHA-256
(content-addressed: an unchanged file is never uploaded again; downloads from R2
cost nothing). Only the small signed manifest is on the VPS. R2 credentials and
the bucket's public URL: revival/release/.r2.json (private). The manifest is
signed with the private key revival/release/.signing-key.pem (never uploaded,
never published); the launcher only accepts a manifest signed with the matching
public key built into it.
"""
import argparse, base64, hashlib, importlib.util, json, os, sys, time
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

# Paths relative to the game root (steamapps/common/Propnight).
FILES = [
    "Propnight/Binaries/Win64/dwmapi.dll",
    "Propnight/Binaries/Win64/UE4SS.dll",
    "Propnight/Binaries/Win64/UE4SS-settings.ini",
    "Propnight/Binaries/Win64/Mods/PropnightLoginBypasser/Scripts/main.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/Scripts/main.lua",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/online.txt",
    "Propnight/Binaries/Win64/Mods/PropnightLocalMap/avatar.png",   # squad / friends avatar picture
    "Propnight/Binaries/Win64/RevivalRelay/RevivalRelay.exe",
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalOnline_P.pak",
    # Steam emulator (Goldberg, LGPL): the game runs without Steam's client
    "Engine/Binaries/ThirdParty/Steamworks/Steamv147/Win64/steam_api64.dll",
]
# Downloaded once (3 GB since 2026-10-10, without the 2022 Camp and Small Abbey): frozen, fixes go to numbered patch paks.
LARGE_FILES = [
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMaps_P.pak",
]
# Optional content (launcher DLC tab): files installed only when the player turns it on.
# The menu pak goes with the maps: it lists them in the map selector.
DLC_FILES = {
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMaps_P.pak": "classic-maps",
    # fixes over the frozen maps pak (higher priority): the game's own copy of the files the
    # maps pak also holds, so its older copies no longer replace the last build's
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMapsPatch_1_P.pak": "classic-maps",
    # the selector list with the classic maps (+200: wins over the base list below)
    "Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMenuClassic_1_P.pak": "classic-maps",
}
# every player: the game's own map selector list, sorted A to Z (build_modpak.py menu: RevivalMenuBase.pak)
FILES.append("Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMenu_P.pak")
FILES.append("Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMenuClassic_1_P.pak")
FILES.append("Propnight/Content/Paks/Propnight-WindowsNoEditor_RevivalMapsPatch_1_P.pak")
# Previews: cut from the October 2022 map selector atlas (revival/release/dlc, not published in the repo).
DLC = [{
    "id": "classic-maps",
    "title": "Classic maps",
    "description": "Brings back maps that are no longer in the last version of Propnight, restored from older "
                   "versions of the game. Hosts pick them in Custom Game; quick play only sends you to maps you have.",
    "maps": [
        {"name": "Farm (day)", "from": "February 2022", "image": "farm-day.jpg"},
        {"name": "School", "from": "February 2022", "image": "school.jpg"},
        {"name": "Mansion", "from": "October 2022", "image": "mansion.jpg"},
    ],
}]
DLC_IMAGES = HERE / "dlc"
LAUNCHER = REVIVAL / "launcher/dist/PropnightRevival.exe"   # dotnet build -c Release -o dist
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


def build():
    cache_path = HERE / "out/.hash-cache.json"
    cache_path.parent.mkdir(parents=True, exist_ok=True)
    cache = json.loads(cache_path.read_text()) if cache_path.exists() else {}
    entries = []
    for rel, large in [(f, False) for f in FILES] + [(f, True) for f in LARGE_FILES]:
        path = GAME / rel
        if not path.is_file():
            raise SystemExit(f"missing in the reference install: {rel}")
        entry = {"path": rel, "size": path.stat().st_size, "sha256": sha256_of(path, cache), "large": large}
        if rel in DLC_FILES:
            entry["dlc"] = DLC_FILES[rel]
        entries.append(entry)
    dlc = []
    for pack in DLC:
        pack = json.loads(json.dumps(pack))
        pack["size"] = sum(e["size"] for e in entries if e.get("dlc") == pack["id"])
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
        "modsEnabled": ["PropnightLoginBypasser", "PropnightLocalMap"],
        "dlc": dlc,
    }
    if LAUNCHER.is_file():
        manifest["launcher"] = {"version": LAUNCHER_VERSION_FILE.read_text().strip(), "size": LAUNCHER.stat().st_size,
                                "sha256": sha256_of(LAUNCHER, cache)}
    cache_path.write_text(json.dumps(cache))
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


def upload(manifest, data):
    import boto3
    from boto3.s3.transfer import TransferConfig
    cfg = r2_config()
    s3 = boto3.client("s3", endpoint_url=cfg["endpoint"], aws_access_key_id=cfg["accessKeyId"],
                      aws_secret_access_key=cfg["secretAccessKey"], region_name="auto")
    transfer = TransferConfig(multipart_threshold=64 << 20, multipart_chunksize=64 << 20, max_concurrency=4)
    sources = [(GAME / e["path"], e["sha256"], e["size"]) for e in manifest["files"]]
    for pack, src in zip(manifest["dlc"], DLC):
        for m, m_src in zip(pack["maps"], src["maps"]):
            sources.append((DLC_IMAGES / m_src["image"], m["image"]["sha256"], m["image"]["size"]))
    if "launcher" in manifest:
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
        remote.run(client, f"mkdir -p {REMOTE_DIR} && chown -R {remote.target()[1]} {REMOTE_DIR}", password)
        sftp = client.open_sftp()
        for name, content in (("manifest.sig", (HERE / "out/manifest.sig").read_bytes()), ("manifest.json", data)):
            with sftp.open(f"{REMOTE_DIR}/{name}.new", "wb") as f:
                f.write(content)
        for name in ("manifest.sig", "manifest.json"):
            sftp.posix_rename(f"{REMOTE_DIR}/{name}.new", f"{REMOTE_DIR}/{name}")
        sftp.close()
    finally:
        client.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    manifest, data = build()
    total = sum(e["size"] for e in manifest["files"])
    print(f"version {manifest['version']}: {len(manifest['files'])} files, {total / 1e9:.2f} GB")
    if not args.dry_run:
        upload(manifest, data)
        print(f"published: {BASE_URL}manifest.json")


if __name__ == "__main__":
    main()
