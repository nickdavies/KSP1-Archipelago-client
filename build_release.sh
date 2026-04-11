#!/bin/bash

set -e

INPUT="$1/KSPArchipelago/bin/Release/net40"
KSP_DIR="$2"
MOD_SUBDIR="${3:-GameData/KSPArchipelago}"
OUTPUT="$KSP_DIR/$MOD_SUBDIR"
LAUNCHER_MANAGED="$KSP_DIR/KSPLauncher_Data/Managed"

mkdir -p "$OUTPUT"

echo "Copying from $INPUT to $OUTPUT"
cp "$INPUT/KSPArchipelago.dll" "$OUTPUT"
cp "$INPUT/Archipelago.MultiClient.Net.dll" "$OUTPUT"
cp "$INPUT/Newtonsoft.Json.dll" "$OUTPUT"
cp "$INPUT/websocket-sharp.dll" "$OUTPUT"

# Mono runtime assemblies not shipped with KSP_Data but needed by Newtonsoft.Json.
echo "Copying Mono assemblies from $LAUNCHER_MANAGED"
cp "$LAUNCHER_MANAGED/System.Numerics.dll" "$OUTPUT"
cp "$LAUNCHER_MANAGED/System.Runtime.Serialization.dll" "$OUTPUT"
