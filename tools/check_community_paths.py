# The launcher only forwards the community calls its whitelist allows (ShellForm.CommunityPath; anything else answers
# "bad request" without reaching the server). This checks the whitelist against the paths the interface builds, so a new
# filter or parameter cannot be added in index.html and forgotten here (1.58.2: the leaderboard filters were refused).
#   python launcher/tools/check_community_paths.py
import itertools, re, sys
from pathlib import Path
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[1]
src = (ROOT / "src" / "ShellForm.cs").read_text(encoding="utf-8")
m = re.search(r'CommunityPath\s*=\s*new System\.Text\.RegularExpressions\.Regex\(@"(.*?)"\);', src, re.S)
if not m:
    sys.exit("CommunityPath not found in ShellForm.cs")
path_re = re.compile(m.group(1).replace('""', '"'))

ok = [
    "/revival/suggestions", "/revival/suggestion/vote", "/revival/polls", "/revival/notifications/read", "/revival/matches",
    "/revival/match?ref=m%3Aabc-12", "/revival/social/profile", "/revival/lfg", "/revival/lfg/post", "/revival/lfg/close", "/revival/lfg/ask",
    "/revival/lfg/cancel", "/revival/lfg/answer", "/revival/lobby/create", "/revival/lobby/pick", "/revival/lobby/vote", "/revival/lobby/leave", "/revival/social/profile?u=Player_1", "/revival/social/avatars?u=Player_1", "/revival/messages?with=Player_1", "/revival/crews", "/revival/crews/mine", "/revival/crews/board", "/revival/crews/create", "/revival/crews/invite-answer", "/revival/crew?id=12", "/revival/messages/send", "/revival/messages/read", "/revival/messages/mute", "/revival/messages/report", "/revival/messages/typing", "/revival/social/avatars?u=A%2Cb-2%2CC_3", "/revival/social/profile/edit",
    "/revival/player/report", "/revival/friends/request-squad", "/revival/friends/request-name",
    "/revival/social/image/delete", "/revival/social/image/move",
]
# the leaderboard as loadBoard() asks for it: URLSearchParams in this order, each filter optional
for board in ("rank", "level", "wins", "hours", "escapes", "hosted"):
    for role, country, friends in itertools.product((None, "killer", "survivor"), (None, "fr", "gb-sct"), (False, True)):
        q = {"board": board}
        if role:
            q["role"] = role
        if country:
            q["country"] = country
        if friends:
            q["friends"] = "1"
        ok.append("/revival/social/leaderboard?" + urlencode(q))
ok.append("/revival/social/leaderboard")
bad = [
    "/revival/social/leaderboard?board=rank&role=killer&x=1", "/revival/social/leaderboard?country=fr", "/revival/social/leaderboard?board=rank&friends=2",
    "/revival/social/profile?u=a/b", "/revival/social/avatars?u=", "/revival/messages", "/revival/messages?with=a/b", "/revival/crew", "/revival/crew?id=x", "/revival/crews/delete", "/revival/crews?q=a", "/revival/messages/delete", "/revival/messages/typing?to=a", "/revival/social/avatars?u=a%2C", "/revival/social/avatars?u=a,b", "/revival/admin", "/revival/lfg/delete", "/revival/lfg?x=1", "/revival/lobby/hosting2", "/revival/lobby", "/revival/social/leaderboard?board=rank&country=../x",
    "/revival/player/report?x=1", "/revival/friends/request", "/revival/friends/remove", "/revival/friends/request-names", "/revival/friends/request-code",
    "/revival/social/image", "/revival/social/image/approve", "/revival/social/image/delete?id=1", "/revival/social/image/moved",
]
fail = [p for p in ok if not path_re.match(p)] + [f"allowed: {p}" for p in bad if path_re.match(p)]
for f in fail:
    print("FAIL", f)
print(f"{len(ok) + len(bad) - len(fail)}/{len(ok) + len(bad)} ok")
sys.exit(1 if fail else 0)
