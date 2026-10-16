# Shiyu 拾语

[![CI](https://github.com/youguoda/shiyu/actions/workflows/ci.yml/badge.svg)](https://github.com/youguoda/shiyu/actions/workflows/ci.yml)

[简体中文](README.md) | **English**

A clipboard history and translation tool that lives in the Windows 11 tray.

> A fish trap is for catching fish; once you have the fish, you can forget the trap. Words are for catching meaning; once you have the meaning, you can forget the words.
>
> — Zhuangzi, "External Things"

You copy dozens of times a day: a sentence, a link, an error message, a screenshot, half a paragraph of English you didn't quite follow. Yet the clipboard remembers only the last one; each copy pushes the one before it into the river. Heraclitus said you cannot step into the same river twice. The clipboard's river has no bank to step back onto.

The name **Shiyu (拾语)** is the two things it does:

- **拾 (*shí*), to pick up**: it picks up every piece of text you copy, keeps them in order, and puts them within reach of your cursor when you need them;
- **语 (*yǔ*), language**: it turns the foreign text in front of you into words you can read, and turns what you want to say into words others can read.

Old China praised a well-ordered land with the saying *dào bù shí yí*: "no one picks up what was dropped on the road." On the road of your clipboard, Shiyu does the opposite. It picks up everything, but only what you dropped, and it keeps it only on your own computer.

It holds to one plain rule: **record greedily, interrupt sparingly.** Record every copy; interrupt only when it can't be helped. Laozi wrote: "Cut doors and windows to make a room; it is the empty space inside that makes the room of use." Most of the time Shiyu is out of sight: the bar closes once you've pasted, the translation panel never takes your cursor, and the 译 badge stays off unless you turn it on. Its usefulness lives in that emptiness.

**[Download the latest release](https://github.com/youguoda/shiyu/releases/latest)** · [Quick start](#quick-start) · [Layer by layer](#layer-by-layer) · [Ways of thinking](#ways-of-thinking) · [Combos](#combos) · [Manual](#manual) · [Coda](#coda)

> **Note:** Shiyu's interface is in Chinese only. It is designed around Chinese as your own language: by default it translates foreign text into Chinese, and its reverse input box turns your Chinese into English. Below, settings and buttons are named in English, followed by their Chinese labels in brackets so you can find them.

## Quick start

1. Download `shiyu-win-x64-setup.exe` from [Releases](https://github.com/youguoda/shiyu/releases/latest) and install it. Shiyu's icon appears in the tray (Windows 11 may first tuck it under the "^" at the right of the taskbar). The first launch offers a short guided setup in which every step can be skipped.
2. Copy a few things, then press **`Ctrl+Shift+V`** in any text box: the bar appears beside your cursor. `↑` `↓` to choose, `Enter` to paste.
3. Select some foreign text and press **`Ctrl+Shift+Z`**: the translation streams in beside it. The default free engine needs no account and no key.
4. In a chat box, press **`Alt+Q`**: type Chinese, get English, and press `Enter` to paste it back.

## Layer by layer

From the layer that needs no setup at all, down to the layer that needs a large language model.

### Layer 1 · Remember: no copy left behind

> Doing nothing, yet nothing is left undone. — Laozi

You don't need to do anything. Shiyu watches the clipboard from the tray and records every copy:

- text, links, file paths, formatted HTML / RTF text, images, files and folders;
- the source app and the time of each copy; image thumbnails are kept forever, full-size images for 30 days by default;
- offline, or with no translation set up, recording goes on: this layer depends on no service.

What it does not record matters just as much:

- 11 common password managers, including KeePass, Bitwarden and 1Password, are excluded by default;
- when the copying app marks its content "do not record", Shiyu obeys;
- you can add your own exclusion rules, by source app, or by content with a regular expression that starts with `re:`.

Light while resident: turn on Lightweight mode (「轻量模式」) and Shiyu releases UI resources and trims its memory while the bar is hidden; recording continues.

### Layer 2 · Retrieve: the quick bar and the full library

Recording is for getting things back. Shiyu has two ways in, one quick and one complete, each with its own job.

**The bar** (`Ctrl+Shift+V`) handles the dozens of quick grabs a day:

- it appears beside your text cursor; just type to search, `↑` `↓` to choose, `Enter` to paste into the window you came from;
- `1`–`9` and `0` paste items 1–10 directly, with pinned items first;
- hold `Space` to preview the full content; hold `Ctrl` and every action shows its key;
- hover over a card and an action tray appears: copy, paste, paste as plain text, open, locate, pin, favorite, note, group, delete. You choose which ones appear, and in what order, in Settings;
- `Esc` backs out one layer at a time; the bar closes after you paste, or stays put if you click the pin in its top-right corner (pinned bar, 「常驻钉住」);
- if your hands are used to `Win+V`, you can let `Win+V` open it too (off by default; Windows gets the key back when Shiyu exits).

**The library** (from the tray menu, the brand button on the bar, or a hotkey you assign) is the complete collection, for browsing at leisure: full search, filters by type, date and subtype, favorites, pins, tags, groups, notes, use counts, and bulk actions. Favorite and pinned items are **protected from deletion**: automatic cleanup skips them, and bulk deletes leave them alone.

### Layer 3 · Understand: foreign text into your own words

> The limits of my language mean the limits of my world. — Ludwig Wittgenstein, *Tractatus Logico-Philosophicus*

Three roads lead to the translation panel, each for its own moment:

| Way in | When to use it |
| --- | --- |
| Translate selection `Ctrl+Shift+Z` | You hit something you can't read: select it and translate |
| Translate clipboard `Ctrl+Shift+X` | You already copied it; or the badge didn't show when it should have, and you insist |
| The 译 badge (「翻译徽标」, off by default) | Once it's on, a small 译 appears by your cursor when you copy foreign text, and translates only when clicked; it can also appear after you drag-select text |

The translation panel:

- the translation appears as it streams in; the panel **never takes focus**, so whatever you selected in the other app stays selected;
- a single English word comes with a **dictionary card**: phonetic transcription, part of speech, definitions and examples, and you can hear the word read aloud;
- switch to a sentence-by-sentence view (「逐句」), have the translation read aloud, or flip the direction with one click;
- if you select Chinese while your target language is also Chinese, Shiyu recognizes the "echo" and translates it into English instead;
- keep a useful translation with Save to history (「存入历史」): it goes into your clipboard history, linked to the original;
- every translation goes into the translation log (「翻译记录」), which you can search on the library's 翻译记录 page (cleared automatically after 30 days by default).

### Layer 4 · Speak: send your words out

> In language it is simply required that it convey the meaning. — Confucius, *Analects* (tr. James Legge)

Reading turns inward; writing turns outward. The **reverse input box** (`Alt+Q`) opens beside the text box you're typing in:

- type Chinese; when your hands pause, the English appears; press `Enter` or click the `↵` at the bottom to paste it back where you were;
- `Tab` flips the direction, `Shift+Enter` starts a new line; while your input method is still choosing characters, `Enter` belongs to the input method;
- after pasting, your original clipboard is put back (unless Auto-copy translation, 「自动复制译文」, is on, which leaves the translation there): the box is only a courier.

**Prompt templates** decide how the text is handled, in both the panel and the reverse input box (they take effect only with an LLM on your own key):

- **Standard / Casual / Formal** (「标准 / 口语 / 正式」): translation, in a different register;
- **Prompt polish** (「提示词优化」): turns a rough requirement into a prompt for an AI coding assistant;
- **Your own templates**: write your own prompt to polish the text, turn it into an email, render it in classical Chinese, or anything else you like.

Switch templates with a click in the box, or cycle through them with `Ctrl+E`. Rewrite templates start only when you press `Enter`, stop when they're done so you can review the result, and paste on the next `Enter`.

### Layer 5 · Think: let a model organize for you (own key required)

> Learning without thought is labour lost; thought without learning is perilous. — Confucius, *Analects* (tr. James Legge)

Connect your own large language model, and the items you select in the library can be:

- **summarized** or **rewritten**;
- **merged into a note**: select several, and you have a collection basket;
- **tagged by suggestion**: click a suggested tag to apply it;
- **translated in bulk**.

Chinese words get dictionary cards too, written by the model.

This layer has one iron rule: **no background, automatic or item-by-item model processing.** If you don't click, nothing is sent.

### Layer 6 · Guard: boundaries are trust

> There is nothing more visible than what is secret, and nothing more manifest than what is minute. Therefore the superior man is watchful over himself, when he is alone. — *The Doctrine of the Mean* (tr. James Legge)

You learn a tool's character when no one is watching.

- **Your clipboard history stays on your machine** (`%LOCALAPPDATA%\Shiyu`): never uploaded, never synced. If you move the data folder into a synced folder such as OneDrive, Shiyu warns you plainly: the history database itself is not encrypted, and its safety rests on Windows account isolation (the trade-off is explained in [ADR-0007](docs/adr/0007-history-entry-and-data-safety.md), in Chinese).
- **Only the text you choose to translate leaves your computer**, sent to the translation method you chose. The free engine sends it to Microsoft Bing Translator's web endpoint, falling back to Tencent TranSmart when Bing is unavailable (no account needed; these are web endpoints rather than official APIs, and may change or be rate-limited at any time). With your own key, it goes straight to the provider you configured.
- **No telemetry**, and no usage statistics of any kind. Besides translation, only two things go online: Check for updates (「检查更新」) calls the GitHub API (`api.github.com`; automatic checks can be turned off), and dictionary cards for English words call `dictionaryapi.dev`.
- **Each credential stays with its owner**: own-key credentials are stored per provider and sent only to the provider they belong to. They are encrypted by Windows in the settings file, and left out of backups by default.
- **Backups** can be encrypted with a passphrase; on import, you choose to merge or overwrite.
- **Updates are signed**: each package is checked against a built-in public key before it installs; if it doesn't match, it isn't installed.

## Ways of thinking

Laozi wrote: "Being and non-being give rise to each other; the difficult and the easy complete each other." Shiyu holds several pairs of seemingly opposite ideas. Each pair takes care of one thing; together they make the tool.

| A pair | How it shows in Shiyu | What it takes care of |
| --- | --- | --- |
| **Greedy ↔ Restrained** | Record every copy; interrupt as little as possible: the badge is off by default, the panel never takes focus, the bar closes once you paste | You never have to decide whether something is worth keeping, and it never breaks your flow |
| **Passive ↔ Active** | What you can't read (foreign → Chinese) goes through selection and the badge; what you want to write (Chinese → foreign) goes through the reverse input box | Reading and writing each get their shortest path |
| **Quick ↔ Complete** | The bar: a column of cards, a few keystrokes away; the library: filters, tags, bulk actions | The quick one stays light; the complete one is never rushed |
| **Deterministic ↔ Generative** | Whether a copy deserves the badge is judged by rules, not a model; the free engine needs no setup; a model appears only when you connect one and click | Simple things get certain methods; only complex things call in a model |
| **Present ↔ Absent** | The bar needs the keyboard, so it takes focus and hands it back the moment you paste; the panel is only for reading, so it never takes focus | It shows up with exactly what's needed and leaves no trace |
| **Remembering ↔ Forgetting** | Thumbnails forever, full-size images until they expire; the translation log clears on a schedule; some things are never recorded at all | Remember what's worth remembering; forget what should be forgotten |

## Combos

A single feature is a move; chain a few together and you have a routine.

### Read

- **Word lookup in three moves**: select an unfamiliar word → `Ctrl+Shift+Z` → read the dictionary card and hear the word → switch the template at the top to Casual (「口语」) to see how it's said in everyday speech (own key).
- **Working through a long text**: select a passage → `Ctrl+Shift+Z` → switch to the sentence-by-sentence view → keep the useful parts with Save to history; later, a search in the bar finds them, and so does the translation log.
- **Chinese straight to English**: select some Chinese → `Ctrl+Shift+Z`. When your target language is Chinese, Shiyu recognizes the echo and translates into English instead.

### Write

- **Replying to a colleague abroad**: in the chat box, `Alt+Q` → type Chinese → switch the template to Casual → `Enter` to paste (own key). Turn on Auto-copy translation, and the translation also lands in your history, ready to reuse from the bar.
- **Explaining a requirement to an AI**: `Alt+Q` → switch the template to Prompt polish → jot the requirement down in Chinese → `Enter` to polish it → review → `Enter` again (or click `↵`) to paste it into your AI coding assistant (own key).

### Organize

- **Collection basket**: copy freely during the day; in the evening, select the related items in the library → Merge into note (「合并成笔记」) → copy the result and put it to use; then Suggest tags (「建议标签」) and file them with a click (own key).
- **Finding last week's translation**: library → the 翻译记录 page → search the original or the translation, and click an entry to expand it in place.

### Everyday

- **Scrapbook**: while writing something long, pin the bar with the pin in its top-right corner, and pin your most-used items to the top of the list; when you need one, press `Ctrl+Shift+V` and then `1`–`9`. The bar stays where it is after pasting.
- **Replacing Win+V**: Settings → Hotkeys (「快捷键」) → turn on Also open with Win+V (「也用 Win+V 呼出」). Your old habit keeps working, and Windows gets `Win+V` back when Shiyu exits.
- **Moving to a new PC**: Settings → General (「常规」) → Backup (「备份」), and export with a passphrase; import on the new machine, choosing merge or overwrite.

## Manual

### Install

Download the installer `shiyu-win-x64-setup.exe` from [Releases](https://github.com/youguoda/shiyu/releases) and run it:

- It installs for the current user only and needs no administrator rights. The default location is `%LOCALAPPDATA%\Programs\Shiyu`, and you can choose another during setup (a new or empty folder).
- You can choose to create a desktop shortcut; the Start menu always gets a 拾语 entry.
- If Shiyu is running, setup and uninstall ask you to exit it from the tray first.
- Uninstall from Windows Settings → Apps. Uninstalling asks whether to also delete your history, translation log and settings. They are kept by default, so a reinstall picks up where you left off.
- The installer is not code-signed, so Windows may say "Windows protected your PC": click "More info" → "Run anyway". To verify the download, `.sha256` and `.sig` signature files sit alongside it.

There is also a portable `shiyu-win-x64.zip`: after verifying it, extract it to a fixed location (for example `%LOCALAPPDATA%\Programs\Shiyu`) and run `Shiyu.App.exe` inside. If you've been using the zip, run the installer and install into the same folder; your history and settings are untouched.

- Both include the .NET runtime; there's nothing else to install.
- Shiyu starts with Windows by default, and the autostart entry points at wherever you actually run it: if you move it, run it once from the new location and autostart follows.
- The tray icon: a left click opens Settings; a right click opens the menu (library, check for updates, exit and more). Starting Shiyu again while it's already running also opens Settings.
- Settings → About (「关于」) → Check for updates (「检查更新」) looks for a new version; both the installed and the portable versions update in place.

### Setting up translation

There are two translation methods (right-click the tray icon → Settings → **Translate** (「翻译」) → Translation method (「翻译方式」)):

- **Free engine** (「免费引擎」): no account and no key; it works right after install. The text is sent to Microsoft Bing Translator's web endpoint, falling back automatically to Tencent TranSmart when Bing is unavailable. These are web endpoints rather than official APIs, so they may change or be rate-limited at any time; each translation is limited to 5,000 characters. The first-run setup preselects it, and until translation is configured, the panel offers a one-click Use the free engine (「用免费引擎」).
- **Own key** (「自备密钥」): use your own **OpenAI-compatible** model service. Fill in the **base URL** (or pick a provider from the presets, some of them free; presets come with a link to get a key and a Test connection button), the **model** name and the **API key**. Prompt templates, AI actions (summarize, merge into note, rewrite, suggest tags), bulk translation and dictionary cards for Chinese words all need your own key.

Without any translation service, every clipboard-history feature still works.

### Hotkeys

Global hotkeys (all of them can be changed in Settings → Hotkeys, 「快捷键」):

| Hotkey | What it does |
| --- | --- |
| `Ctrl+Shift+V` | Quick paste: open the bar beside your text cursor; it closes after pasting, or stays when pinned |
| `Ctrl+Shift+Z` | Translate selection: translate the text currently selected |
| `Ctrl+Shift+X` | Translate clipboard: the escape hatch when the filter judges wrong |
| `Alt+Q` | Reverse input box: type Chinese beside the current text box and get English |
| (none by default) | Open the library |
| `Win+V` (optional) | Also open the bar with Win+V; off by default, and Windows gets it back when you turn it off or Shiyu exits |

While Shiyu runs, these combinations belong to it: `Ctrl+Shift+Z` ("redo" in many apps) and `Ctrl+Shift+V` ("paste as plain text" in browsers, Office and others) are overridden, and `Alt+Q` overrides Microsoft 365's "jump to search". If one clashes with something you use, change it.

In the bar (**hold `Ctrl` and each available action shows its key in place**):

| Key | What it does |
| --- | --- |
| `↑` / `↓` | Choose an item |
| `Enter` | Paste the chosen item into the window you came from |
| `1`–`9`, `0` | Paste item 1–10 directly (pinned items come first) |
| Hold `Space` | Preview the chosen item in full; release to close |
| `C` / `O` / `P` / `S` | Copy / open / pin / favorite |
| `N` / `G` / `D` / `Z` | Note / group / delete / undo delete (within 5 seconds) |
| `←` / `→` | Switch content type (all / text / images / files) |
| `Tab` | Switch the tag filter |
| `Ctrl+F` | Focus the search box (or just start typing after opening the bar) |
| `Esc` | Back out one layer: close menus and previews first, then clear the search and each filter in turn, and only then hide the bar |

In the reverse input box:

| Key | What it does |
| --- | --- |
| `Enter` / click `↵` | Take the next step: produce the result, or paste it back |
| `Shift+Enter` | New line |
| `Tab` | Flip the direction (for rewrite templates: switch the output language) |
| `Ctrl+E` | Switch the prompt template |
| `Esc` | Close without pasting |

In the translation panel: `Esc` closes it; the top lets you flip the direction and switch templates; the bottom lets you copy the translation, save it to history, read it aloud, or switch to the sentence-by-sentence view.

### System requirements

- Windows 11 (not verified on Windows 10, and not promised to work there).
- The installer and the zip both include the .NET runtime; building from source needs the .NET 9 SDK (WPF projects build only on Windows).

### Development

```
src/Shiyu.Core          # pure logic (including SQLite storage); the target of all unit tests
src/Shiyu.Windows       # platform layer: Win32 / WPF interop (clipboard, hotkeys, tray, hooks, speech)
src/Shiyu.App           # WPF UI (bar, library, settings, onboarding, updates, backups)
tests/Shiyu.Core.Tests  # xUnit unit tests
server/                 # public translation relay: Cloudflare Worker (TypeScript + vitest; no longer offered in the UI, code frozen)
docs/                   # ADRs, reviews, manual acceptance checklists
.scratch/               # tickets (issue tracker): milestones shiyu-v1 / shiyu-v2
spikes/                 # technical spikes
```

Build and test (the same commands as [CI](.github/workflows/ci.yml)):

```powershell
dotnet restore Shiyu.sln
dotnet build Shiyu.sln -c Release --no-restore
dotnet test tests/Shiyu.Core.Tests/Shiyu.Core.Tests.csproj -c Release --no-build

cd server
npm ci
npm run typecheck
npm test
```

The build output lands in `src/Shiyu.App/bin/Release/net9.0-windows/`. **Debug builds** don't register autostart, which suits development.

Where to read more (these documents are in Chinese):

- [`CONTEXT.md`](CONTEXT.md) — the domain language and settled principles (the authority on terms).
- [`docs/adr/`](docs/adr/) — architecture decision records, including the alternatives that were rejected.
- [`docs/review/`](docs/review/) — the CTO review, the optimization plan and the UI design review.
- [`.scratch/`](.scratch/) — every ticket and acceptance criterion for both milestones.
- [`server/README.md`](server/README.md) — the relay Worker's contract, quotas and deployment.

For the intended direction of the UI, see the target mockups in [`docs/review/mockups/`](docs/review/mockups/) (bar / translation panel / settings / library). **They are design targets filled with placeholder data, not the current interface.**

### License

[MIT](LICENSE) © 2026 youguoda

## Coda

**Picking up and letting go**

In Plato's *Phaedrus*, Socrates tells a story. Theuth, the inventor of writing, presents his invention to Thamus, king of Egypt, promising that it will make people wiser and improve their memory. The king answers that it will do the opposite: trusting in marks outside themselves, people will stop exercising the memory within. What Theuth has found is a remedy not for memory, but for reminding.

Shiyu is exactly such a remedy for reminding. It remembers everything you copied, so you can safely forget; it translates for you, so you can read a foreign paragraph without opening a dictionary. Is that freedom, or dependence?

Borges wrote of Funes, a man who could forget nothing. He remembered every leaf and the shape of every cloud, and yet he could no longer think, because "to think is to forget differences; it is to generalize, to abstract." A perfect memory is not wisdom but a burden. So Shiyu is learning to forget as well: full-size images expire, the translation log clears on a schedule, and passwords are never recorded in the first place. Picking up and letting go are not opposites: let go of what should go, and what you picked up becomes easier to find and to use.

Heidegger observed that a good hammer, while in use, withdraws from view: you see the nail, not the hammer. Only when the hammer breaks does it stand in front of you again. Shiyu wants to be that kind of hammer: the bar closes once you've pasted, the panel never takes your cursor, the badge stays quiet by default. At its best, you almost forget it's there.

But could a tool that makes you forget it also make you forget other things: the word you should have remembered yourself, the English sentence you should have weighed yourself? That is why the panel keeps the dictionary card, the pronunciation and the sentence-by-sentence view. It does not only translate for you; it is also willing to learn alongside you. How far you go is up to you.

Right after "once you have the meaning, you can forget the words", Zhuangzi sighs:

> Where can I find someone who has forgotten words, so that I can have a word with them?

The line contradicts itself: to talk with someone who has forgotten words, you still need words. Perhaps tools and people are like that. We use a tool to remember so that we need not remember, and to translate so that one day we need not translate. Shiyu picks up the words; what it leaves you is the meaning beyond them.
