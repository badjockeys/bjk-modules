#!/usr/bin/env sh
# Downloads the face and number plate detection models used by the media worker's automatic blurring (OpenCV Zoo,
# pinned to one commit) and the CLIP model for its nudity and graphic content check (Hugging Face, pinned to one
# revision), and checks each file's SHA-256. Fails (non-zero exit) if anything doesn't match.
#
#   scripts/download-redaction-models.sh /opt/bjk/models            # models only (worker image build)
#   scripts/download-redaction-models.sh ./models --test-images      # plus sample pictures for the model tests
#
# Then set Media:Redaction:FaceModelPath and Media:Redaction:PlateModelPath to the two detection models, and
# Media:SensitiveDetection:ModelPath to clip-vit-b32-vision-quantized.onnx (89 MB).
# Licences: YuNet face detection - MIT; LPD-YuNet plate detection - Apache 2.0; CLIP - MIT (see README "Automatic blurring").
set -eu

dir="${1:?usage: download-redaction-models.sh <directory> [--test-images]}"
commit="47534e27c9851bb1128ccc0102f1145e27f23f98"
base="https://media.githubusercontent.com/media/opencv/opencv_zoo/${commit}/models"
mkdir -p "$dir"

# sha256sum on Linux; shasum on macOS, which may not have sha256sum.
if command -v sha256sum >/dev/null 2>&1; then sha256_check() { sha256sum -c -; }
else sha256_check() { shasum -a 256 -c -; }; fi

fetch() { # url file sha256
  curl -fsSL --retry 3 -o "$dir/$2" "$1"
  echo "$3  $dir/$2" | sha256_check
}

fetch "$base/face_detection_yunet/face_detection_yunet_2026may.onnx" \
  face_detection_yunet_2026may.onnx ebafce4e3c118d6554634be5c27ab333b4c047a9a8c3faf1d7cf93101c22f0f0
fetch "$base/license_plate_detection_yunet/license_plate_detection_lpd_yunet_2023mar.onnx" \
  license_plate_detection_lpd_yunet_2023mar.onnx 6d4978a7b6d25514d5e24811b82bfb511d166bdd8ca3b03aa63c1623d4d039c7

# The nudity and graphic content check: OpenAI CLIP ViT-B/32 (MIT), Xenova's quantised ONNX export of the vision
# model, pinned to one revision. Then set Media:SensitiveDetection:ModelPath to it.
fetch "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/d15189d7028b43f1d3e65039190477f6af591c2a/onnx/vision_model_quantized.onnx" \
  clip-vit-b32-vision-quantized.onnx 583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299

if [ "${2:-}" = "--test-images" ]; then
  fetch "$base/face_detection_yunet/example_outputs/largest_selfie.jpg" \
    test-faces.jpg ab8413ad9bb4f53068f4fb63c6747e5989991dd02241c923d5595b614ecf2bf6
  fetch "$base/license_plate_detection_yunet/example_outputs/result-4.jpg" \
    test-plate.jpg 6257486bd6e08c6c1fd80874ad7cc8be2d1ed06e288d16670a04b9b8acb18530
fi
