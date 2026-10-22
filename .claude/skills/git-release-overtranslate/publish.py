"""把 build.py 產出的完整版換進 GitHub Release 的內文。

    python .claude/skills/git-release-overtranslate/publish.py \
        --version 2.2.1-beta.2 --notes <build.py 的輸出檔> [--check] [--replace]

CI 建立 release 時內文只有佔位與出處：

    ## TODO
    - TODO

    ---

    - commit: <sha>
    - 觸發：push

這裡把最後一條 `---` 以上整段換成完整版（它本身就以 `---` 結尾），以下的出處原樣保留。

只送 body 一個欄位：一次 PATCH，請求裡沒有 name、prerelease、draft、tag 或 assets。
送出前後各讀一次 release，除了 body 以外有任何一項不同就回報失敗。

內文已經不是佔位（之前發過一次）時預設拒絕，要改寫已發布的內文才加 --replace。
"""
import argparse
import json
import pathlib
import subprocess
import sys
import tempfile

SEPARATOR = "---"


def gh(*args, input_text=None):
    # 固定 UTF-8：Windows 的 Python 預設會挑到 cp950，中文內文直接炸。
    result = subprocess.run(
        ["gh", *args], input=input_text, capture_output=True, text=True, encoding="utf-8"
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or f"gh {' '.join(args)} 失敗")
    return result.stdout


def snapshot(release):
    """body 以外、這次不准變的東西。"""
    return {
        "id": release["id"],
        "tag_name": release["tag_name"],
        "target_commitish": release["target_commitish"],
        "name": release["name"],
        "draft": release["draft"],
        "prerelease": release["prerelease"],
        "assets": sorted(
            (a["id"], a["name"], a["size"], a["updated_at"], a["label"] or "")
            for a in release["assets"]
        ),
    }


def split_footer(body):
    """(最後一條 --- 以上, 以下的出處)。找不到分隔線就是 None。"""
    lines = body.replace("\r\n", "\n").split("\n")
    for i in range(len(lines) - 1, -1, -1):
        if lines[i].strip() == SEPARATOR:
            return "\n".join(lines[:i]), "\n".join(lines[i + 1:]).strip()
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True, help="目標 tag，例如 2.2.1-beta.2")
    ap.add_argument("--notes", required=True, type=pathlib.Path, help="build.py 的輸出檔")
    ap.add_argument("--replace", action="store_true", help="內文已經不是佔位時仍然覆蓋")
    ap.add_argument("--check", action="store_true", help="只做檢查、印出會保留的出處，不送出")
    args = ap.parse_args()

    notes = args.notes.read_text(encoding="utf-8").replace("\r\n", "\n").strip()
    if "TODO" in notes:
        sys.exit(f"完整版還留著 TODO：{args.notes}")
    if f"# OverTranslate {args.version}" not in notes:
        sys.exit(f"完整版的標題不是 {args.version}，是不是拿錯檔了：{args.notes}")
    if notes.split("\n")[-1].strip() != SEPARATOR:
        sys.exit(f"完整版沒有以 --- 結尾，不是 build.py 的輸出：{args.notes}")

    repo = gh("repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner").strip()

    # tag 與 release 是兩件事：tag 推上去之後 CI 還要打包十幾分鐘才建 release。
    remote_tag = subprocess.run(
        ["git", "ls-remote", "--tags", "origin", f"refs/tags/{args.version}"],
        capture_output=True, text=True, encoding="utf-8",
    ).stdout.strip()
    if not remote_tag:
        sys.exit(f"遠端沒有 tag {args.version}（tag 名沒有 v 前綴）")
    try:
        before = json.loads(gh("api", f"repos/{repo}/releases/tags/{args.version}"))
    except RuntimeError as e:
        if "404" in str(e) or "Not Found" in str(e):
            sys.exit(f"tag {args.version} 在，但 release 還沒建立——CI 可能還沒跑完，等它結束再來")
        raise

    parts = split_footer(before["body"] or "")
    if parts is None or "- commit:" not in parts[1]:
        sys.exit("現在的內文找不到 CI 留下的「---」與出處，不知道該換哪一段，停下來")
    head, footer = parts
    if head.strip() != "## TODO\n- TODO" and not args.replace:
        sys.exit("內文已經不是佔位（之前發過）。確定要改寫已發布的內文，加 --replace 再跑一次")

    new_body = f"{notes}\n\n{footer}\n"
    if args.check:
        print(f"檢查通過，未送出。{len(new_body)} 字元；保留的出處：\n{footer}")
        return

    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".json", delete=False) as f:
        json.dump({"body": new_body}, f, ensure_ascii=False)
        payload = f.name
    try:
        gh("api", "-X", "PATCH", f"repos/{repo}/releases/{before['id']}", "--input", payload)
    finally:
        pathlib.Path(payload).unlink(missing_ok=True)

    after = json.loads(gh("api", f"repos/{repo}/releases/tags/{args.version}"))
    changed = [k for k, v in snapshot(before).items() if snapshot(after)[k] != v]
    if changed:
        sys.exit(f"內文以外的東西變了，請手動檢查：{changed}")
    if (after["body"] or "").replace("\r\n", "\n") != new_body:
        sys.exit("送出後讀回的內文和送出的不一樣，請手動檢查")

    print(after["html_url"])
    print(f"已更新內文；標題、pre-release、tag 與 {len(after['assets'])} 個 assets 皆未變動")


if __name__ == "__main__":
    main()
