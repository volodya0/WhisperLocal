#!/usr/bin/env bash
# Downloads a Whisper model (ggml format) from HuggingFace into ~/Library/Application Support/WhisperLocal/models
# (where WhisperLocal for macOS looks for it) and verifies it.
#
#   scripts/get-model.sh                              # ggml-large-v3.bin
#   scripts/get-model.sh ggml-large-v3-turbo.bin      # other models for comparison
#   MODELS_DIR=/some/folder scripts/get-model.sh
set -euo pipefail

NAME="${1:-ggml-large-v3.bin}"
MODELS_DIR="${MODELS_DIR:-$HOME/Library/Application Support/WhisperLocal/models}"
URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$NAME"
TARGET="$MODELS_DIR/$NAME"

# Known-good hashes; models without an entry are downloaded without verification
known_hash() {
    case "$1" in
        ggml-large-v3.bin) echo "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2" ;;
        *) echo "" ;;
    esac
}

mkdir -p "$MODELS_DIR"

if [[ -f "$TARGET" ]]; then
    echo "Already exists: $TARGET"
else
    echo "Downloading $URL"
    echo "         to $TARGET"
    # -C - resumes an interrupted download
    curl -L --fail -C - -o "$TARGET.partial" "$URL"
    mv "$TARGET.partial" "$TARGET"
fi

EXPECTED="$(known_hash "$NAME")"
if [[ -n "$EXPECTED" ]]; then
    echo "Verifying SHA256..."
    ACTUAL="$(shasum -a 256 "$TARGET" | cut -d' ' -f1)"
    if [[ "$ACTUAL" != "$EXPECTED" ]]; then
        echo "SHA256 mismatch for $NAME: got $ACTUAL, expected $EXPECTED. Delete the file and retry." >&2
        exit 1
    fi
    echo "OK: $TARGET"
else
    echo "Done (no known hash for $NAME, not verified): $TARGET"
fi
