# Third-Party Notices

Klydis distributes or references the following third-party components.

## Smeagle 4B

- **Upstream:** [Hob-forge/smeagle-4b](https://huggingface.co/Hob-forge/smeagle-4b)
- **Fine-tuned by:** [Hob Forge](https://huggingface.co/Hob-forge)
- **Base model:** [Qwen/Qwen3.5-4B-Base](https://huggingface.co/Qwen/Qwen3.5-4B-Base)
- **License:** [Hob Forge Community License v1.0](https://huggingface.co/Hob-forge/smeagle-4b/blob/main/LICENSE.md)
- **Attribution:** Hob Forge — smeagle

Smeagle is redistributed with Klydis under the Hob Forge Community License v1.0. The license
requires visible attribution ("Hob Forge — smeagle") whenever the model is redistributed;
this notice satisfies that requirement. The base model `Qwen/Qwen3.5-4B-Base` remains under
the Apache-2.0 license, whose terms are unaffected.

The license also requires public disclosure when the model is served at frontier-AI-lab
scale or to 10M+ monthly users, and prohibits harmful use, relabeling an abliterated
derivative as Hob Forge, and passing model output off as human-authored to deceive. See the
linked license text for the full terms.

The Smeagle model file is stored in this repository as ≤100 MB part files under
`assets/models/Hob-forge_smeagle-4b/` (a single file would exceed GitHub's per-file push
limit). Run `powershell -ExecutionPolicy Bypass -File .\restore-smeagle.ps1` to reassemble
the `.gguf`; the script verifies size and SHA-256 against `manifest.json`.