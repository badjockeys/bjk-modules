import hashlib, json
import numpy as np
from huggingface_hub import hf_hub_download, HfApi
import onnxruntime as ort
import torch
from transformers import CLIPModel, CLIPTokenizer

out = {}

# 1. The CLIP vision model in ONNX (Xenova's export of openai/clip-vit-base-patch32), pinned to a revision.
api = HfApi()
info = api.model_info("Xenova/clip-vit-base-patch32")
out["xenova_revision"] = info.sha
out["xenova_files"] = [s.rfilename for s in info.siblings if s.rfilename.startswith("onnx/")]
results = {}
for name in ["onnx/vision_model.onnx", "onnx/vision_model_quantized.onnx"]:
    try:
        path = hf_hub_download("Xenova/clip-vit-base-patch32", name, revision=info.sha)
        data = open(path, "rb").read()
        sess = ort.InferenceSession(path)
        results[name] = {
            "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
            "inputs": [(i.name, i.shape, i.type) for i in sess.get_inputs()],
            "outputs": [(o.name, o.shape, o.type) for o in sess.get_outputs()],
            "url": f"https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/{info.sha}/{name}",
        }
    except Exception as e:
        results[name] = {"error": str(e)}
out["vision_models"] = results

# 2. Text embeddings for the prompts, from the same weights.
prompts = {
    "safe": [
        "a photo of a road taken from inside a car",
        "a dashcam video frame of traffic",
        "a car parked on a street",
        "a street with houses and parked cars",
        "a motorway with lorries",
        "a cyclist riding on a road",
        "people walking on a pavement",
        "a road at night with headlights",
        "a car crash with damaged vehicles",
        "a person's face",
        "a car park",
        "a roundabout",
    ],
    "graphic": [
        "a graphic photo of a bloody injury",
        "a dead body",
        "an injured person lying in the road covered in blood",
        "gore",
        "severe wounds and blood",
    ],
    "nudity": [
        "a photo of a naked person",
        "pornography",
        "explicit sexual content",
        "nude breasts",
        "exposed genitals",
    ],
}
model = CLIPModel.from_pretrained("openai/clip-vit-base-patch32")
tok = CLIPTokenizer.from_pretrained("openai/clip-vit-base-patch32")
emb = {}
with torch.no_grad():
    for group, texts in prompts.items():
        t = tok(texts, padding=True, return_tensors="pt")
        f = model.get_text_features(**t)
        f = f / f.norm(dim=-1, keepdim=True)
        emb[group] = [[round(float(x), 6) for x in row] for row in f]
out["logit_scale"] = float(model.logit_scale.exp())
out["prompts"] = prompts
out["embeddings"] = emb

# 3. Does the ONNX vision model give CLIP's projected, comparable embeddings? Compare with PyTorch on one image.
from PIL import Image
rng = np.random.default_rng(1)
img = Image.fromarray((rng.random((300, 400, 3)) * 255).astype("uint8"))
from transformers import CLIPImageProcessor
proc = CLIPImageProcessor.from_pretrained("openai/clip-vit-base-patch32")
pix = proc(images=img, return_tensors="np")["pixel_values"]
with torch.no_grad():
    ref = model.get_image_features(pixel_values=torch.tensor(pix)).numpy()[0]
ref = ref / np.linalg.norm(ref)
for name, r in results.items():
    if "error" in r:
        continue
    sess = ort.InferenceSession(hf_hub_download("Xenova/clip-vit-base-patch32", name, revision=info.sha))
    o = sess.run(None, {sess.get_inputs()[0].name: pix.astype("float32")})
    for idx, arr in enumerate(o):
        v = arr.reshape(-1)
        if v.shape[0] == 512:
            v = v / np.linalg.norm(v)
            r[f"cosine_vs_torch_output{idx}"] = float(np.dot(v, ref))

# 4. PDQ reference hashes for synthetic images the C# tests can rebuild exactly.
import pdqhash
def gradient(w, h):
    a = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            a[y, x] = ((x * 255) // max(1, w - 1), (y * 255) // max(1, h - 1), ((x + y) * 255) // max(1, w + h - 2))
    return a
def checker(w, h, s):
    a = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            v = 255 if ((x // s) + (y // s)) % 2 == 0 else 0
            a[y, x] = (v, v, v)
    return a
def lcg(w, h, seed):
    # Deterministic noise any language can rebuild: x = (1103515245 * x + 12345) mod 2^31, byte = x >> 16 & 255.
    a = np.zeros((h, w, 3), dtype=np.uint8)
    x = seed
    for y in range(h):
        for xx in range(w):
            for c in range(3):
                x = (1103515245 * x + 12345) % 2147483648
                a[y, xx, c] = (x >> 16) & 255
    return a
cases = {
    "gradient_64x48": gradient(64, 48),
    "gradient_320x200": gradient(320, 200),
    "checker_128x128_s16": checker(128, 128, 16),
    "noise_97x61_seed7": lcg(97, 61, 7),
    "noise_250x180_seed42": lcg(250, 180, 42),
}
pdq = {}
for name, arr in cases.items():
    h, q = pdqhash.compute(arr)
    bits = "".join(str(int(b)) for b in h)
    pdq[name] = {"bits": bits, "quality": int(q), "hex": hex(int(bits, 2))[2:].zfill(64)}
out["pdq"] = pdq
json.dump(out, open("osa-reference.json", "w"), indent=1)
print(json.dumps({k: v for k, v in out.items() if k not in ("embeddings",)}, indent=1)[:6000])
