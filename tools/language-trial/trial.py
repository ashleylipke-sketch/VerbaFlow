"""
Scores a speech-to-text model on public recordings of South African languages that come with correct transcripts
typed by native speakers. Nobody needs to read the languages: the score is the share of words that come out wrong
(word error rate, WER) and of letters (character error rate, CER).

Usage: python trial.py <lang> <out.json>    lang = xho sot tsn nso tso ssw ven nbl
ENGINE=meta (default: Meta Omnilingual on this computer) or ENGINE=google (Google Speech-to-Text v2, EU, with a
service account key in GOOGLE_SPEECH_KEY).
"""
import csv, io, json, os, random, re, sys, tarfile, time, unicodedata, urllib.request, xml.etree.ElementTree as ET, zipfile
from pathlib import Path

CLIPS = int(os.environ.get("CLIPS", "30"))
WORK = Path(os.environ.get("WORK", "/tmp/trial"))
MODELS = os.environ.get("MODELS", "omniASR_CTC_1B,omniASR_LLM_1B").split(",")
ENGINE = os.environ.get("ENGINE", "meta")
# Google's code and model per language (EU list checked 2026-11-04); isiNdebele is not offered.
GOOGLE = {"xho": ("xh-ZA", "chirp_3"), "nso": ("nso-ZA", "chirp_3"), "sot": ("st-ZA", "long"),
          "tsn": ("tn-Latn-ZA", "long"), "tso": ("ts-ZA", "long"), "ssw": ("ss-Latn-ZA", "long"), "ven": ("ve-ZA", "long")}

OPENSLR = "https://openslr.elda.org/resources/32/{}.tar.gz"
FLEURS = "https://huggingface.co/datasets/google/fleurs/resolve/main/data/{}/{}"
SADILAR = "https://repo.sadilar.org/server/api/core/bitstreams/{}/content"
NCHLT = {"tso": "11b4e300-1bd3-449d-a03a-dfde4fb0fa12", "ven": "de9ab4c5-864b-4786-99aa-1d65840f24d7",
         "nbl": "340d8c5b-a353-49f8-951c-e49731a8446c", "ssw": "9e966e2f-42f1-41ec-8ceb-025d0d02ff45"}
SOURCES = {
    "xho": [("openslr", "xh_za"), ("fleurs", "xh_za")],
    "sot": [("openslr", "st_za")],
    "tsn": [("openslr", "tn_za")],
    "nso": [("fleurs", "nso_za")],
    "tso": [("nchlt", "tso")], "ssw": [("nchlt", "ssw")], "ven": [("nchlt", "ven")], "nbl": [("nchlt", "nbl")],
}
NAMES = {"xho": "isiXhosa", "sot": "Sesotho", "tsn": "Setswana", "nso": "Sepedi (Northern Sotho)", "tso": "Xitsonga",
         "ssw": "siSwati", "ven": "Tshivenda", "nbl": "isiNdebele"}


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def fetch(url, dest):
    if dest.exists():
        return dest
    log("downloading", url)
    req = urllib.request.Request(url, headers={"User-Agent": "verbaflow-language-trial"})
    with urllib.request.urlopen(req, timeout=600) as r, open(str(dest) + ".part", "wb") as f:
        while chunk := r.read(1 << 20):
            f.write(chunk)
    os.replace(str(dest) + ".part", dest)
    log("downloaded", dest.name, dest.stat().st_size // (1 << 20), "MB")
    return dest


def spread(items, n):
    """n items spread evenly over the list, so many speakers are included."""
    if len(items) <= n:
        return items
    step = len(items) / n
    return [items[int(i * step)] for i in range(n)]


def openslr(code):
    tgz = fetch(OPENSLR.format(code), WORK / f"{code}.tar.gz")
    out = WORK / code
    if not out.exists():
        with tarfile.open(tgz) as t:
            t.extractall(out, filter="data")
    texts = {}
    for idx in out.rglob("line_index.tsv"):
        for row in idx.read_text(encoding="utf-8").splitlines():
            if "\t" in row:
                fid, text = row.split("\t", 1)
                texts[fid.strip()] = text.strip()
    wavs = {p.stem: p for p in out.rglob("*.wav")}
    pairs = sorted((wavs[k], v) for k, v in texts.items() if k in wavs)
    return spread(pairs, CLIPS), "OpenSLR 32 (read sentences, studio quality, CC BY-SA 4.0)"


def fleurs(code):
    tsv = fetch(FLEURS.format(code, "test.tsv"), WORK / f"{code}-test.tsv")
    tgz = fetch(FLEURS.format(code, "audio/test.tar.gz"), WORK / f"{code}-test.tar.gz")
    out = WORK / f"fleurs-{code}"
    if not out.exists():
        with tarfile.open(tgz) as t:
            t.extractall(out, filter="data")
    wavs = {p.name: p for p in out.rglob("*.wav")}
    pairs = []
    for row in csv.reader(tsv.read_text(encoding="utf-8").splitlines(), delimiter="\t", quoting=csv.QUOTE_NONE):
        if len(row) > 2 and row[1] in wavs:
            pairs.append((wavs[row[1]], row[2]))
    seen, unique = set(), []
    for w, t in sorted(pairs):
        if t not in seen:
            seen.add(t)
            unique.append((w, t))
    return spread(unique, CLIPS), "FLEURS test set (read Wikipedia sentences, phone and laptop microphones, CC BY 4.0)"


def nchlt(code):
    """The NCHLT corpus is one 4 GB zip; only the test speakers' files are read, using range requests when allowed."""
    url = SADILAR.format(NCHLT[code])
    out = WORK / f"nchlt-{code}"
    out.mkdir(parents=True, exist_ok=True)
    try:
        from remotezip import RemoteZip
        z = RemoteZip(url, headers={"User-Agent": "verbaflow-language-trial"})
        log("reading the zip's file list over range requests")
    except Exception as e:
        log("range requests not possible:", repr(e)[:200], "- downloading the whole file")
        z = zipfile.ZipFile(fetch(url, WORK / f"nchlt-{code}.zip"))
    names = z.namelist()
    tst = [n for n in names if n.endswith(".tst.xml")] or [n for n in names if n.endswith(".xml")]
    log("transcript files", tst[:5])
    root = ET.fromstring(z.read(tst[0]))
    recs = []
    for r in root.iter("recording"):
        orth = r.find("orth")
        if orth is not None and orth.text and r.get("audio"):
            recs.append((r.get("audio"), orth.text.strip()))
    chosen = spread(sorted(recs), CLIPS)
    by_tail = {n.split("/", 1)[-1]: n for n in names}
    pairs = []
    for audio, text in chosen:
        name = audio if audio in names else by_tail.get(audio.split("/", 1)[-1]) or next((n for n in names if n.endswith(audio.split("/")[-1])), None)
        if not name:
            continue
        dest = out / Path(name).name
        if not dest.exists():
            dest.write_bytes(z.read(name))
        pairs.append((dest, text))
    return pairs, "NCHLT test set (read prompts on mobile phones, 8 test speakers, CC BY 3.0)"


def normalise(s):
    s = unicodedata.normalize("NFC", s).lower()
    s = re.sub(r"\[[^\]]*\]|<[^>]*>", " ", s)  # noise marks such as [n] or <unk>
    s = "".join(c if c.isalnum() or c in " '" else " " for c in s)
    return " ".join(s.replace("'", "").split())


class Google:
    """Google Speech-to-Text v2, one short (synchronous) request per clip, EU servers."""

    def __init__(self):
        from google.oauth2 import service_account
        import google.auth.transport.requests as gr
        info = json.loads(os.environ["GOOGLE_SPEECH_KEY"])
        self.project = info["project_id"]
        self.creds = service_account.Credentials.from_service_account_info(
            info, scopes=["https://www.googleapis.com/auth/cloud-platform"])
        self.session = gr.AuthorizedSession(self.creds)

    def transcribe(self, paths, code, model):
        import base64
        url = f"https://eu-speech.googleapis.com/v2/projects/{self.project}/locations/eu/recognizers/_:recognize"
        out = []
        for p in paths:
            body = {"config": {"autoDecodingConfig": {}, "languageCodes": [code], "model": model},
                    "content": base64.b64encode(Path(p).read_bytes()).decode()}
            for attempt in range(4):
                r = self.session.post(url, json=body, timeout=120)
                if r.status_code not in (429, 500, 503):
                    break
                time.sleep(2 ** (attempt + 1))
            if r.status_code != 200:
                raise RuntimeError(f"Google said {r.status_code}: {r.text[:300]}")
            res = r.json().get("results", [])
            out.append(" ".join(x["alternatives"][0].get("transcript", "") for x in res if x.get("alternatives")))
        return out


def main():
    lang, dest = sys.argv[1], sys.argv[2]
    WORK.mkdir(parents=True, exist_ok=True)
    import jiwer, soundfile as sf

    if ENGINE == "google":
        if lang not in GOOGLE:
            log(lang, "is not offered by Google")
            Path(dest).write_text(json.dumps({"lang": lang, "name": NAMES[lang], "sets": [
                {"source": "-", "error": "Google does not offer this language"}]}), encoding="utf-8")
            return
        google, supported = Google(), True
        models = [f"google-{GOOGLE[lang][1]}"]
    else:
        from omnilingual_asr.models.inference.pipeline import ASRInferencePipeline
        try:
            from omnilingual_asr.models.wav2vec2_llama.lang_ids import supported_langs
            supported = f"{lang}_Latn" in supported_langs
        except Exception:
            supported = None
        models = MODELS
    log(lang, "listed by the model:", supported)

    results = {"lang": lang, "name": NAMES[lang], "listedByModel": supported, "sets": []}
    for kind, code in SOURCES[lang]:
        try:
            pairs, about = {"openslr": openslr, "fleurs": fleurs, "nchlt": nchlt}[kind](code)
        except Exception as e:
            log("could not get", kind, code, repr(e)[:300])
            results["sets"].append({"source": kind, "error": repr(e)[:300]})
            continue
        # the model takes clips of up to 40 seconds
        pairs = [(w, t) for w, t in pairs if 0.5 < sf.info(str(w)).duration < 39]
        seconds = sum(sf.info(str(w)).duration for w, _ in pairs)
        entry = {"source": kind, "about": about, "clips": len(pairs), "seconds": round(seconds), "models": {}}
        refs = [normalise(t) for _, t in pairs]
        for card in models:
            log("model", card, "on", len(pairs), "clips from", kind)
            t0 = time.time()
            try:
                if ENGINE == "google":
                    hyps = google.transcribe([str(w) for w, _ in pairs], *GOOGLE[lang])
                else:
                    pipe = ASRInferencePipeline(model_card=card)
                    # a language the model does not list is transcribed without telling it the language
                    code = f"{lang}_Latn" if supported else None
                    hyps = pipe.transcribe([str(w) for w, _ in pairs], lang=[code] * len(pairs), batch_size=2)
                    del pipe
            except Exception as e:
                log("model failed", repr(e)[:300])
                entry["models"][card] = {"error": repr(e)[:300]}
                continue
            took = time.time() - t0
            hyps_n = [normalise(h) for h in hyps]
            wer = jiwer.wer(refs, hyps_n)
            cer = jiwer.cer(refs, hyps_n)
            entry["models"][card] = {
                "wer": round(wer, 3), "cer": round(cer, 3), "secondsToRun": round(took),
                "examples": [{"correct": pairs[i][1], "model": hyps[i]} for i in range(min(4, len(pairs)))],
            }
            log(card, kind, "WER", round(wer * 100, 1), "% CER", round(cer * 100, 1), "% in", round(took), "s")
        results["sets"].append(entry)
    Path(dest).write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
