#!/usr/bin/env sh
# Downloads the face and number plate detection models used by the media worker's automatic blurring, from OpenCV
# Zoo pinned to one commit, and checks each file's SHA-256. Fails (non-zero exit) if anything doesn't match.
#
#   scripts/download-redaction-models.sh /opt/bjk/models            # models only (worker image build)
#   scripts/download-redaction-models.sh ./models --test-images      # plus sample pictures for the model tests
#
# Then set Media:Redaction:FaceModelPath and Media:Redaction:PlateModelPath to the two .onnx files.
# Licences: YuNet face detection - MIT; LPD-YuNet plate detection - Apache 2.0 (see README "Automatic blurring").
set -eu

dir="${1:?usage: download-redaction-models.sh <directory> [--test-images]}"
commit="47534e27c9851bb1128ccc0102f1145e27f23f98"
base="https://media.githubusercontent.com/media/opencv/opencv_zoo/${commit}/models"
mkdir -p "$dir"

fetch() { # url file sha256
  curl -fsSL --retry 3 -o "$dir/$2" "$1"
  echo "$3  $dir/$2" | sha256sum -c -
}

fetch "$base/face_detection_yunet/face_detection_yunet_2026may.onnx" \
  face_detection_yunet_2026may.onnx ebafce4e3c118d6554634be5c27ab333b4c047a9a8c3faf1d7cf93101c22f0f0
fetch "$base/license_plate_detection_yunet/license_plate_detection_lpd_yunet_2023mar.onnx" \
  license_plate_detection_lpd_yunet_2023mar.onnx 6d4978a7b6d25514d5e24811b82bfb511d166bdd8ca3b03aa63c1623d4d039c7

if [ "${2:-}" = "--test-images" ]; then
  fetch "$base/face_detection_yunet/example_outputs/largest_selfie.jpg" \
    test-faces.jpg ab8413ad9bb4f53068f4fb63c6747e5989991dd02241c923d5595b614ecf2bf6
  fetch "$base/license_plate_detection_yunet/example_outputs/result-4.jpg" \
    test-plate.jpg 6257486bd6e08c6c1fd80874ad7cc8be2d1ed06e288d16670a04b9b8acb18530
fi
