# Production and release policy

From v4.5.0, main contains both the canonical production source and the website.
The production entry is StageManager.Card3DPrototype/StageManager.Card3DPrototype.csproj.
Despite its historical name, this is the shipped desktop app. StageManager.App,
StageManager.Core and StageManager.Legacy are retained experimental architecture
work, not release entry points. Do not publish them as an update to the production app.
The v4.4.16 tag remains unchanged; its release target_commitish field was not proof
of ancestry. v4.5.0 reconciles both histories while preserving the v4.4.16 interactions.

1. Build the production entry; run StageManager.Tests in Release.
2. Merge into main, then push a vX.Y.Z tag at that commit. Tags are never auto-created.
3. CI checks version/ancestry and builds the final self-contained, uncompressed EXE.
4. CI attests those bytes and creates a draft with checksum and build-info.json.
5. Download the draft, verify hash and attestation, back up settings and the previous
   application, and test on Windows for at least 24 hours. Record start/end, OS/DPI,
   app targets, Focus/fullscreen transitions, input/capture failures and memory/handles.
6. Run Publish a locally verified draft with the tag, validation record URL,
   tested EXE hash, actual UTC trial start and explicit manual-check confirmation.
   The workflow rejects a trial shorter than 24 hours or predating this binary's
   CI build, mismatched hashes/source ancestry or an invalid attestation.
   It publishes the same assets without rebuilding and explicitly deploys the website.

Public feedback: https://github.com/franklai-rise/StageManager/issues
Reports must remove document titles, personal paths, tokens and other private data.
No automatic telemetry is sent. Keep v4.4.16 and the pre-migration settings backup
for rollback; do not overwrite old tags or release assets.
