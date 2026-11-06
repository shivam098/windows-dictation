# Third-party notices

Voice Dictation downloads and runs these models and libraries. Each keeps its own license.

| Component | Used for | License |
|---|---|---|
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) via [Whisper.net](https://github.com/sandrohanea/whisper.net) | Speech recognition engine | MIT |
| [OpenAI Whisper](https://github.com/openai/whisper) `small.en` (ggml from [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp)) | English dictation | MIT |
| [Silero VAD](https://github.com/snakers4/silero-vad) (ggml from [ggml-org/whisper-vad](https://huggingface.co/ggml-org/whisper-vad)) | Detecting speech vs. noise | MIT |
| [Oriserve/Whisper-Hindi2Hinglish-Swift](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Swift), fine-tuned from [openai/whisper-base](https://huggingface.co/openai/whisper-base) | Hindi + English dictation | Apache-2.0 (fine-tune); MIT (OpenAI Whisper base model, mel filters, vocabulary) |
| [Oriserve/Whisper-Hindi2Hinglish-Apex](https://huggingface.co/Oriserve/Whisper-Hindi2Hinglish-Apex), fine-tuned from [openai/whisper-large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) | Hindi + English dictation, accurate option | Apache-2.0 (fine-tune); MIT (OpenAI Whisper base model, mel filters, vocabulary) |
| [NAudio](https://github.com/naudio/NAudio) | Microphone capture | MIT |

## Oriserve/Whisper-Hindi2Hinglish-Swift

Model by [Oriserve](https://oriserve.com). Licensed under the Apache License, Version 2.0
(<https://www.apache.org/licenses/LICENSE-2.0>); a copy of the license is attached to the
[`models-hinglish-swift-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-swift-1) release.

It is fine-tuned from OpenAI's `whisper-base`, and the converted file also contains OpenAI Whisper's mel filter bank and
tokenizer vocabulary. Those are Copyright (c) 2022 OpenAI under the MIT License; OpenAI's license text is attached to the
same release.

**Changes made:** the original Hugging Face weights were converted to the ggml format and quantized to 8-bit (q8_0) so
whisper.cpp can run them; the model itself was not retrained. The steps are in
[tools/ModelConvert/README.md](https://github.com/shivam098/windows-voice-assistant/blob/main/tools/ModelConvert/README.md) in the Voice Assistant repository. The model is distributed "AS IS", without warranties
or conditions of any kind.

The accurate option uses Oriserve/Whisper-Hindi2Hinglish-Apex (Apache-2.0, fine-tuned from OpenAI's whisper-large-v3-turbo),
converted the same way; licenses are attached to the [`models-hinglish-apex-1`](https://github.com/shivam098/windows-voice-assistant/releases/tag/models-hinglish-apex-1) release.
