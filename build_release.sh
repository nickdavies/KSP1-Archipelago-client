#!/bin/bash

set -e

INPUT="$1/KSPArchipelago/bin/Release/net48"
OUTPUT="$2"

echo "Copying from $INPUT to $OUTPUT"
cp "$INPUT/KSPArchipelago.dll" "$OUTPUT"
cp "$INPUT/Archipelago.MultiClient.Net.dll" "$OUTPUT"
cp "$INPUT/Newtonsoft.Json.dll" "$OUTPUT"
