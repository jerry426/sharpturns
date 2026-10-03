# SharpTurns

A lightweight desktop app (.NET 10, Avalonia) that gives your installed Claude
Code CLI a rendered-Markdown conversation UI, with projects, conversations, and
context management.

The name has two halves. **Sharp** is for C#, and for how sharp a rendered
Markdown conversation looks next to the CLI's terminal output. **Turns** is for
conversation turns, the unit the whole app is built around.

SharpTurns is an independent project. It is not affiliated with or endorsed by
Anthropic.

## Why context management matters

In a plain CLI session, everything a turn does stays in the context for the
rest of the conversation: every file it read, every build log, every search
result. Long sessions fill the context window with output the model no longer
needs, and every request carries all of it. When the window is full,
auto-compact summarizes the whole conversation at once, on its own schedule.

SharpTurns turns auto-compact off and lets you decide what the model sees, one
turn at a time:

- **Compress** a finished turn. Only the work in between is summarized: the
  tool calls, their output, and the intermediate steps become a generated work
  summary. Your messages and answers and the model's final response are never
  summarized; they are replayed word for word, so the two most important parts
  of every turn stay fully intact. Each compressed turn shows how much smaller
  its replay became, and a compression is kept only if it makes the replay
  smaller.
- **Auto-Summarize**, on for new conversations, compresses each turn as soon as
  it finishes, so a conversation stays lean without any effort from you.
- **Hide** a dead end or a turn that no longer matters, and it leaves the
  replay entirely.
- **Choose which images** keep being replayed, instead of resending them all.
- **Branch** a useful run of turns into a fresh conversation.

**Less noise, more signal.** Once a turn is done, most of its raw tool output
is noise: whole files read for one function, pages of build and test logs,
searches that found nothing, attempts that were abandoned. Left in the
context, all of that bloat stresses the model's attention: every token
competes with the ones that matter, and the decision or fact the model needs
becomes a needle in an ever-larger haystack. Auto-Summarize removes the noise
without touching the signal: each finished turn is replayed as your own
messages and answers, word for word, a summary of what was done and decided,
and the final response, word for word. Only the work effort is condensed;
what you asked and what the model concluded reach every later turn exactly as
they were written. Every later turn starts from that distilled history instead
of the raw transcript, so the context holds a much higher share of useful
information, and the gain grows with every turn.

Nothing is thrown away. The full turn stays in the database, View Full Turn
Content shows it, and Expand or Show puts it back in the replay. Each change
starts the next turn in a fresh CLI session, seeded from the trimmed history.

**A fresh session doesn't mean a cold cache.** A lot of work went into keeping
the reseeded history eligible for the API's input token cache:

- The history is replayed the same way every time, so earlier turns stay
  unchanged from one request to the next, and a single cache marker with a
  1-hour lifetime sits at the end of the history.
- The CLI's Git status snapshot, which changes with the repository and would
  invalidate the cached history after it, is turned off.
- The CLI's experimental betas are turned off, because they use all four of
  the API's cache markers and leave none for the history.

So when you compress the latest turn, the next turn can read every earlier
turn from the cache. Only the changed turn and the new request are sent
uncached. Hiding or compressing an older turn changes the history from that
turn on, so that part is cached again. Each turn card's **First-Request Cache
Hits** shows how much of the turn's opening request came from the cache.

The result is conversations that can run far longer, a model that stays
focused on what matters, and fewer tokens sent with every request.

## Features

- **Context management**, described above: compress turns into work summaries
  (by hand or automatically), hide them from the replay, choose which images
  are replayed, and clean up or delete turns in bulk.
- **Projects and conversations**, stored in a local SQLite database.
- **Streaming turns** rendered as Markdown, with a collapsible card for each
  tool call, dialogs for the CLI's questions and permission checks, messages
  queued during a turn, and image attachments.
- **Usage**: per-turn token and cache metrics, and your subscription's
  remaining 5-hour and weekly limits as the CLI reports them.
- **Branching**: copy a run of turns into a new conversation.
- **Extras**: notes, search, user message history, Markdown and DOCX export,
  and a separate Markdown Viewer.
- **Dictation** with your own Deepgram API key (optional).

## How it uses the CLI

SharpTurns has no model provider, API client, or tool runtime of its own. Each
turn runs your installed, unmodified `claude` binary as `claude -p` with
stream-json input and output, and the CLI runs its own tools under its own
sign-in. SharpTurns never reads or stores your Claude credentials. Turns count
against your Claude plan or API usage as usual.

Before you use it, know that:

- **Coding tools run without asking.** Read, Glob, Grep, Edit, Write,
  NotebookEdit, Bash, WebFetch, and WebSearch (plus PowerShell on Windows) are
  preapproved. Turns start in the project's folder, but the tools aren't
  confined to it. The CLI's own safety checks still open an Allow/Deny dialog. Subagents, MCP tools, and the `pkill` and
  `killall` commands are denied.
- **Your CLI setup still applies.** The CLI discovers the project's `CLAUDE.md`
  (or `AGENTS.md`) and loads your settings files. SharpTurns overrides them to
  turn off hooks, auto-compact, and the fallback model.
- **SharpTurns manages the context.** When a CLI session can't be resumed, or
  after you hide or compress turns, the next turn starts a fresh session seeded
  from the saved conversation. Work summaries come from separate tool-less
  `claude -p` calls.

[docs/DESIGN.md](docs/DESIGN.md) explains the reasons behind these choices.

## Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
  (10.0.300 or a later feature band).
- Claude Code CLI **2.1.286 or later**, installed and signed in. Run `claude`
  once in a terminal to sign in. SharpTurns checks the version at startup and
  shows an error if it's missing or older.
- Optional, for dictation: a [Deepgram](https://deepgram.com/) API key and a
  command-line recorder: `ffmpeg` on macOS and Windows, or `pw-record` or
  `arecord` on Linux.

SharpTurns is developed and tested mainly on macOS. It also targets Windows and
Linux. System sounds are available on macOS and Windows.

## Build and run

There are no prebuilt binaries. Clone the repository, then from its root:

```sh
./run.sh                # macOS and Linux
./run.sh -c Release     # an optimized build
dotnet run --project src/SharpTurns.App   # any platform, including Windows
```

To run the tests:

```sh
dotnet test
```

### Let your CLI set it up

The quickest way to get running is to have the Claude Code CLI you already use
do the setup. Clone the repository, start `claude` in its folder with a capable
model such as Opus 5.5 (`claude --model claude-opus-5-5`), and ask something
like:

> Check that this machine has what SharpTurns needs, install anything missing,
> then build it, run the tests, and start the app.

The CLI reads the repository's `AGENTS.md` (through `CLAUDE.md`), so it knows
how the project is laid out and how to build and test it. It can also install
a recorder for dictation and work through platform-specific problems.

By default SharpTurns runs `claude` from your `PATH`. To use another install,
enter the full path to the executable in **Config → Preferences**. On Windows,
it must be the native `claude.exe`, not a `.cmd` or shell wrapper.

## Your data

- Everything is stored in one SQLite database, `sharpturns.db`, in a
  `SharpTurns` folder under your local app-data directory:
  `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on Windows, and
  `~/.local/share` on Linux. This includes your conversations, the images you
  attach, and your settings.
- **The Deepgram API key is stored in plain text** in that database. Anyone who
  can read the file can read the key.
- Turns go through the CLI. SharpTurns itself only connects to the network to
  send a dictation recording to Deepgram, to load images that rendered Markdown
  links to, and to fetch a remote file you open in the Markdown Viewer.

## Dictation

Click **Voice** in the composer (or press Ctrl+1 / ⌘+1 in the message box) to
start recording, then **Stop** to transcribe. The transcript goes in at the
cursor, and **Undo** removes it. Save your Deepgram key in
**Config → API Keys** first. On macOS, the first recording may ask for
microphone permission for the app you launched SharpTurns from.

Two environment variables override the recorder:

- `SHARPTURNS_AUDIO_RECORDER_PATH`: the recorder executable.
- `SHARPTURNS_AUDIO_INPUT_DEVICE`: the input device to record from.

## License

MIT; see [LICENSE](LICENSE). Third-party components and their licenses are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
