# Voice Dictation guide

## First minute

1. Start `VoiceDictation.exe`. The tray (bottom-right, maybe under the **^** arrow) shows a green microphone icon.
   On first run the pill at the bottom of the screen shows the model download.
2. Click into any text box, for example a chat message box.
3. **Tap Ctrl+Win** (press and let go), say "Hello comma this is my first dictation full stop", then **tap Ctrl+Win
   again**. You hear a short tone when recording starts and stops. While it listens, the pill shows a moving voice-level
   meter, your words as you speak and "Ctrl+Win to finish · Esc to cancel"; after the second tap it shows
   "Transcribing…" and the text appears where your cursor is.

The pill hides itself when idle. Tray › **Always show status pill** (or left-click the tray icon) keeps it on screen.

## Speaking tips

- Speak in phrases, the way you'd talk to a person. Short pauses are fine.
- Punctuation: "comma", "full stop" or "period", "question mark", "exclamation mark", "colon", "new line",
  "new paragraph".
- Changed your mind mid-sentence? Say "scratch that" and the phrase before it is dropped.
- You never have to hold the keys: dictate as long as you like and tap Ctrl+Win when you're done. **Ctrl+Win+Space**
  works the same way. **Esc** cancels. A recording stops and is typed by itself after 10 minutes
  (`MaxDictationSeconds` in the settings file; 0 turns the limit off).
- Pressing another key together with the Ctrl+Win that starts a recording (for example Ctrl+Win+D) is treated as a
  Windows shortcut: that recording is discarded. Using a Ctrl+Win shortcut while recording keeps the recording going.

## Hindi + English

Press **Alt+Shift+H** (or tray › Dictation language). The first time, an 80 MB model downloads in the background; the pill
shows **HI + EN** while it's on. Hindi words are typed in Roman letters ("kal meeting hai"), English words stay English.
Press Alt+Shift+H again for English, which is more accurate for English-only speech.

If Hindi words come out wrong too often, choose tray › Dictation language › **Hindi + English — accurate**. It uses a much
larger model (830 MB download, once): fewer mistakes, but a few seconds per phrase after you tap Ctrl+Win to finish (about
2.5 s on a Snapdragon X Elite, more on older PCs). Words still appear live while you speak. The pill shows
**HI + EN · accurate**, and Alt+Shift+H then switches between English and this mode.

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
