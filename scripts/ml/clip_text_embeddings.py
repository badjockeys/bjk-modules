"""Turns written prompts into CLIP text embeddings for zero-shot suggestions.

Usage: python scripts/ml/clip_text_embeddings.py <source.json> <output.json>

Uses the same CLIP model as the media worker (openai/clip-vit-base-patch32, Xenova's ONNX export, pinned revision),
so the text embeddings match the worker's image embeddings. Needs: pip install onnxruntime tokenizers numpy.
Downloads the text model (about 65 MB) and tokenizer from Hugging Face the first time, into ~/.cache/bjk-clip.
"""

import json
import sys
import urllib.request
from pathlib import Path

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

REVISION = "d15189d7028b43f1d3e65039190477f6af591c2a"
BASE = f"https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/{REVISION}"
CACHE = Path.home() / ".cache" / "bjk-clip"
CONTEXT = 77
EOT = 49407


def fetch(name: str) -> Path:
    path = CACHE / name.replace("/", "_")
    if not path.exists():
        CACHE.mkdir(parents=True, exist_ok=True)
        urllib.request.urlretrieve(f"{BASE}/{name}", path)
    return path


MODEL = "onnx/text_model_quantized.onnx"
PAD = EOT


def embed(texts: list[str], model: str | None = None, pad: int | None = None, lower: bool = True) -> np.ndarray:
    tokenizer = Tokenizer.from_file(str(fetch("tokenizer.json")))
    session = ort.InferenceSession(str(fetch(model or MODEL)))
    ids = np.full((len(texts), CONTEXT), PAD if pad is None else pad, dtype=np.int64)
    mask = np.zeros((len(texts), CONTEXT), dtype=np.int64)
    for i, text in enumerate(texts):
        tokens = tokenizer.encode(text.lower() if lower else text).ids[:CONTEXT]
        ids[i, : len(tokens)] = tokens
        mask[i, : len(tokens)] = 1
    names = [i.name for i in session.get_inputs()]
    feeds = {"input_ids": ids}
    if "attention_mask" in names:
        feeds["attention_mask"] = mask
    outputs = session.run(None, feeds)
    out_names = [o.name for o in session.get_outputs()]
    vectors = outputs[out_names.index("text_embeds")] if "text_embeds" in out_names else outputs[0]
    return vectors / np.linalg.norm(vectors, axis=1, keepdims=True)


def calibrate() -> None:
    """Picks the model and padding that reproduce the worker's existing embeddings (clip-prompts.json)."""
    global MODEL, PAD
    check = Path("src/BJK.Modules.Media/Infrastructure/Processing/Sensitive/clip-prompts.json")
    if not check.exists():
        return
    known = [p for group in json.loads(check.read_text())["groups"].values() for p in group][:4]
    best = None
    for model in ["onnx/text_model_quantized.onnx", "onnx/text_model.onnx"]:
        for pad in [EOT, 0]:
            for lower in [True, False]:
                mine = embed([k["prompt"] for k in known], model, pad, lower)
                score = float(np.mean([np.dot(m, np.array(k["embedding"]) / np.linalg.norm(k["embedding"])) for m, k in zip(mine, known)]))
                print(f"  {model} pad={pad} lower={lower}: mean cosine {score:.4f}")
                if best is None or score > best[0]:
                    best = (score, model, pad)
    _, MODEL, PAD = best
    print(f"Using {MODEL} with pad {PAD} (mean cosine {best[0]:.4f})")


def main() -> None:
    calibrate()
    source, output = Path(sys.argv[1]), Path(sys.argv[2])
    spec = json.loads(source.read_text())
    prompts = [("baseline", p) for p in spec["baseline"]] + [(t, p) for t, ps in spec["types"].items() for p in ps]
    vectors = embed([p for _, p in prompts])
    result = {
        "model": f"openai/clip-vit-base-patch32 (ONNX: Xenova/clip-vit-base-patch32@{REVISION}, onnx/text_model_quantized.onnx)",
        "logitScale": 100.0,
        "prompts": [
            {"type": t, "prompt": p, "embedding": [round(float(x), 6) for x in v]} for (t, p), v in zip(prompts, vectors)
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result) + "\n")
    print(f"Wrote {len(prompts)} prompt embeddings to {output}")

    # Check against an embedding the worker already uses, made the same way.
    check = Path("src/BJK.Modules.Media/Infrastructure/Processing/Sensitive/clip-prompts.json")
    if check.exists():
        known = json.loads(check.read_text())["groups"]["safe"][0]
        mine = embed([known["prompt"]])[0]
        cosine = float(np.dot(mine, np.array(known["embedding"]) / np.linalg.norm(known["embedding"])))
        print(f"Check against clip-prompts.json ('{known['prompt']}'): cosine {cosine:.4f}")


if __name__ == "__main__":
    main()
