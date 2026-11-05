# Voice Dictation guide

## First minute

1. Start `VoiceDictation.exe`. The tray (bottom-right, maybe under the **^** arrow) shows a green microphone icon.
   On first run the pill at the bottom of the screen shows the model download.
2. Click into any text box, for example a chat message box.
3. **Hold Ctrl+Win**, say "Hello comma this is my first dictation full stop", and **release**.
   You hear a short tone when recording starts and stops, the pill shows your words as you speak, and the text appears
   where your cursor is.

The pill hides itself when idle. Tray › **Always show status pill** (or left-click the tray icon) keeps it on screen.

## Speaking tips

- Speak in phrases, the way you'd talk to a person. Short pauses are fine.
- Punctuation: "comma", "full stop" or "period", "question mark", "exclamation mark", "colon", "new line",
  "new paragraph".
- Changed your mind mid-sentence? Say "scratch that" and the phrase before it is dropped.
- For long dictation use **Ctrl+Win+Space** (hands-free) so you don't have to hold the keys. Press it again to finish.
- Pressing another key while holding Ctrl+Win (for example Ctrl+Win+D) is treated as a Windows shortcut: the recording
  is discarded.

## Hindi + English

Press **Alt+Shift+H** (or tray › Dictation language). The first time, an 80 MB model downloads in the background; the pill
shows **HI + EN** while it's on. Hindi words are typed in Roman letters ("kal meeting hai"), English words stay English.
Press Alt+Shift+H again for English, which is more accurate for English-only speech.

## Personal dictionary and snippets

Tray › **Open settings file**, edit, save, and restart Voice Dictation.

```json
"replacements": { "git hub": "GitHub", "jira": "Jira" },
"snippets": { "my signature": "Thanks,\nShivam", "office address": "1 Main Street, Bengaluru" }
```

Say "my signature" (or "insert my signature") on its own to type the snippet.

## New lines in chat apps

"New line" presses **Shift+Enter**, which chat apps treat as a line break instead of sending the message. Notepad,
Word, VS Code and Visual Studio get **Enter**. Add other apps by their process name (Task Manager › Details, without
`.exe`):

```json
"newLineKeys": { "Notepad": "Enter", "obsidian": "Enter" }
```

## Troubleshooting

| Problem | Try this |
|---|---|
| Nothing happens on Ctrl+Win | If the tray icon has a grey dot, the keys are paused: Tray › Resume dictation keys. |
| "Couldn't open the microphone" | Settings › Privacy & security › Microphone: turn on *Microphone access* and *Let desktop apps access your microphone*. Check the right device is the Windows default, or set `microphoneDevice`. |
| "I didn't hear any speech" | Speak a little louder or closer; check the microphone level in Settings › System › Sound. |
| Text is typed twice | Voice Assistant is also running. Exit one of them. |
| Nothing is typed into one app | If the app runs as administrator, Windows blocks typing into it. Run the app normally, or run Voice Dictation as administrator too. |
| Some characters go missing | Set `"insertMethod": "Paste"` (the default). |
| Slow on an older PC | Set `"modelFile": "ggml-base.en-q8_0.bin"` and `"livePreview": false`. |
| The Start menu opens after dictating | Release Ctrl and Win together; if it keeps happening, please open an issue with your keyboard layout. |

Logs are in tray › **Open logs folder** (`%LOCALAPPDATA%\VoiceDictation\logs`). They contain timings and error names,
never what you said, so they're safe to share in an issue.

## Testing changes

- Unit tests: `dotnet test tests\VoiceDictation.Core.Tests`
- Real-app test (uses a Chrome test page, scripted transcripts, no microphone). Run it while you're away from the
  keyboard; it stops at the first failed check so nothing is typed elsewhere:

  ```powershell
  .\tests\DictationLab\run.ps1 -Exe .\src\VoiceDictation.App\bin\Release\net10.0-windows\win-x64\VoiceDictation.exe
  ```
