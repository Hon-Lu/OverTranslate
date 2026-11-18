"""Builds src/OverTranslate/Resources/WordLists/english-short-words.txt from SCOWL.

The realtime overlay leaves a group of capitals undrawn when none of its words is an English word
(RealtimeHiddenText). This writes the words it asks about: every word of two to five letters in SCOWL's
"words" lists up to size 60, in any of the English, American, British and Canadian spellings, lower
case. SCOWL keeps abbreviations, proper names and upper-case entries in separate files (abbreviations,
proper-names, upper), so none of them come in. Not past size 60: from 70 on SCOWL has cha, and from 80
dex, wis, ko, san, esc, vig — rare words that are also a game's abbreviations, which would then be drawn.

Source: SCOWL 2020.12.07, http://wordlist.aspell.net/ ,
https://sourceforge.net/projects/wordlist/files/SCOWL/2020.12.07/scowl-2020.12.07.tar.gz
License: see the SCOWL section of THIRD-PARTY-NOTICES.txt.

Usage: python tools/build-short-words.py <scowl-2020.12.07/final>
"""
import os
import re
import sys

SIZES = (10, 20, 35, 40, 50, 55, 60)
REGIONS = ("english", "american", "british", "canadian")
SHORT = re.compile(r"^[a-z]{2,5}$")
# c's, l's, t's: SCOWL spells the plural of a letter without the apostrophe. As, is and us are words.
LETTER_PLURAL = re.compile(r"^[a-z]s$")
# Words a screen uses as words that SCOWL files as abbreviations (max, min, dev, app, intro, bro), keeps
# only in capitals (OK), or leaves out (nerf, noob, mana, yay).
SUPPLEMENT = ("ok", "max", "min", "dev", "app", "intro", "bro", "nerf", "noob", "mana", "yay")

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "OverTranslate",
                   "Resources", "WordLists", "english-short-words.txt")


def main(final):
    words = set()
    for size in SIZES:
        for region in REGIONS:
            path = os.path.join(final, f"{region}-words.{size}")
            if not os.path.exists(path):
                continue
            with open(path, encoding="latin-1") as f:
                words |= {w for w in f.read().split() if SHORT.match(w)}
    words = {w for w in words if not LETTER_PLURAL.match(w) or w in ("as", "is", "us")}
    words |= set(SUPPLEMENT)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# English words of two to five letters, lower case: SCOWL 2020.12.07, the english, american, british\n")
        f.write("# and canadian words lists up to size 60, and a few words SCOWL files as abbreviations.\n")
        f.write("# Made by tools/build-short-words.py.\n")
        f.write("# SCOWL: Copyright 2000-2018 by Kevin Atkinson and others; see THIRD-PARTY-NOTICES.txt.\n")
        for w in sorted(words):
            f.write(w + "\n")
    print(len(words), "words ->", os.path.normpath(OUT))


if __name__ == "__main__":
    main(sys.argv[1])
