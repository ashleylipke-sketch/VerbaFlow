"""Joins the per-language results into one table (Markdown) for the owner."""
import json, sys
from pathlib import Path

rows = ["| Language | Recordings | Clips | Model | Words wrong (WER) | Letters wrong (CER) |", "|---|---|---|---|---|---|"]
examples = []
for f in sorted(Path(sys.argv[1]).rglob("*.json")):
    r = json.loads(f.read_text(encoding="utf-8"))
    for s in r["sets"]:
        if "error" in s:
            rows.append(f"| {r['name']} | {s['source']} | - | - | could not download: {s['error'][:80]} | |")
            continue
        for m, v in s["models"].items():
            if "error" in v:
                rows.append(f"| {r['name']} | {s['source']} | {s['clips']} | {m} | failed: {v['error'][:80]} | |")
            else:
                rows.append(f"| {r['name']} | {s['source']} | {s['clips']} | {m} | {v['wer']*100:.0f}% | {v['cer']*100:.0f}% |")
                for e in v["examples"][:2]:
                    examples.append(f"- {r['name']} ({m}): correct `{e['correct']}` / model `{e['model']}`")
print("\n".join(rows))
print("\nExamples:\n" + "\n".join(examples))
