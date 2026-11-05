# Shared note material fixtures

These are synthetic outputs of the production client builders, not a separate exchange format. Keep their original bytes: ink paths and recording manifests depend on the exact JSON representation, including values such as `320.0`.

Both fixture directories use `* -text` to prevent Git line-ending conversion. Do not format or hand-edit their JSON, Markdown, PNG, or WAV files.

| Fixture | Producer | Version / base | Input SHA-256 | Manifest SHA-256 |
| --- | --- | --- | --- | --- |
| `mixed-material-v1` | Android `MixedMaterialFixture`, `NoteMaterialStore.prepare` / `uploadSnapshot` | `22222222-2222-4222-8222-222222222222` / none | `67d62efc9eda1ce06283c8417f424a5d7a4e70a43370bdd574793d1be246930b` | `31389e894159dc249501cbb7aa65605e6efda58f05482406b7e593cda7adb5d8` |
| `web-continuation-v1` | Web `native-notes-canonical-bundle.test.tsx`, `buildMaterialBundle` | `99999999-9999-4999-8999-999999999999` / Android version above | `19916b65f47d4c9dea18a989e0a55fc5de28f926584e261a37a60796b47377c3` | `5cbf921733156fe44daba52ce291f09fcc85671e64ce6a032765810f51cf492a` |

The Android fixture contains ordered Markdown/photo/ink blocks and one recording with two PCM WAV chunks. Its seven material files are accompanied by `input.json` and `manifest.json`.

The Web continuation changes the title and one Markdown block. It retains the original photo, ink preview, recording manifest, and WAV bytes. `request.json` records the source version and clone arguments produced by the Web builder.

`AndroidMixedMaterialInteropTests` loads these exact files to verify resumable upload, current/version reads, byte hashes, concurrent branches, explicit promotion, and Web continuation through the Store and TestServer. The Web golden consumer loads the same Android fixture directly. The Android companion tests consume the Web continuation through `CloudNoteRepository`.

To update the fixtures, run the corresponding production-builder test, copy its complete exported output, and review the input/manifest/file digests together. A missing fixture is a test failure.
