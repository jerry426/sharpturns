# SharpTurns Design Notes

Why SharpTurns works the way it does. The code is the source of truth for
behavior; this file records the decisions behind it, especially the ones a
contributor might otherwise undo. `AGENTS.md` holds the working rules. Update
this file when a decision changes.

## Scope

SharpTurns gives the user's installed Claude Code CLI a rendered-Markdown
conversation UI with projects, conversations, and context management. Every
model call goes through the user's installed, unmodified `claude` binary: the
app has no API client, model provider, or tool runtime, and never handles
credentials.

Out of scope unless the maintainer agrees otherwise: other model providers, an
in-app tool runtime, other databases, document management, multi-agent
features, browser automation, and IDE integrations. The optional Instance
Manager (see Multiple instances) manages SharpTurns windows only.

There are no prebuilt binaries. Users build from source, typically with their
CLI's help (see the README).

## Architecture

Flow: Views → ViewModels → application services → CLI client and persistence.

```
src/
  SharpTurns.App                  Avalonia app: views, view models, services, composition root
  SharpTurns.ClaudeCli            CLI process client and stream-json protocol; no UI or persistence deps
  SharpTurns.Core                 conversation records, replay seeding and fingerprints, context and compression logic
    Persistence/                  SQLite store, numbered migrations, and conversation locks
    InstanceManagement/           local IPC and window-linking geometry shared with the Instance Manager
  SharpTurns.InstanceManager.App  optional Instance Manager, run on its own
  SharpTurns.Markdown.Rendering   Markdown rendering controls (LiveMarkdown.Avalonia, Markdig)
  SharpTurns.MarkdownViewer.App   standalone viewer, launched as a separate process
tests/
  SharpTurns.Tests                xunit
```

- The SQLite store lives in Core rather than its own project. It has one
  implementation, so it has no interface.
- `ClaudeTurnRunner` (App `Services/`) runs a turn: it saves the prompt and
  images, resumes or reseeds the CLI session, streams the response, answers
  permission and question requests through dialogs, and saves the response
  parts with the turn's usage.
- CLI stream interpretation that isn't UI-specific stays in
  `SharpTurns.ClaudeCli` (`ClaudeCliActivity`, `ClaudeCliUsageTracker`,
  `ClaudeCliInputQueue`, `ClaudeCliQuestions`).
- Tests are a focused xunit suite: protocol parsing and argument building,
  replay seeding and fingerprints, SQLite round trips on temporary files, and
  view-model logic. There is no UI automation.

## CLI integration

### Launch profile

Each turn is one `claude -p` process with stream-json input and output
(`ClaudeCliClient.CreateStartInfo`).

- **No `--safe-mode` or `--restricted` on turns.** Either flag stops
  `CLAUDE.md` discovery (verified with CLI 2.1.286), and SharpTurns relies on
  the CLI's own discovery instead of injecting instruction files. The client
  also removes inherited `CLAUDE_CODE_SAFE_MODE` and
  `CLAUDE_CODE_DISABLE_CLAUDE_MDS`, which leak in when the app is started from
  a CLI session and also stop discovery.
- **The user's settings files load,** as a consequence. `--settings` overrides
  them: hooks off, auto-compact off (SharpTurns manages the context), no
  fallback model, and no claude.ai connectors.
- **Tools.** The coding tools (Read, Glob, Grep, Edit, Write, NotebookEdit,
  Bash, WebFetch, WebSearch, and PowerShell on Windows) are preapproved with
  `--allowed-tools`. The CLI still asks the host for its own safety checks,
  which show an Allow/Deny dialog. Subagents are denied, and so are `pkill`
  and `killall` (bare and `/usr/bin/`): pattern-based killers can stop
  unrelated apps, and a user's own settings may not deny them. MCP tools are
  denied unless the conversation selected servers (see MCP servers below).
- **`--strict-mcp-config` and `--disable-slash-commands`** keep the user's MCP
  servers, plugins' skills, and slash commands out of turns.
- **MCP servers.** The conversation's selected, enabled servers are written
  to a temporary `--mcp-config` file only the user can read, so their
  environment values stay out of the process list. The file is deleted when
  the turn ends. Each server's tools are preapproved with an
  `mcp__<server>` rule, using the CLI's tool-name form (characters other than
  letters, digits, `_`, and `-` become `_`), and the `mcp__*` denial is
  dropped. The CLI's stdio configuration has no working directory (CLI
  2.1.288 ignores `cwd`), so a server with one is started through
  `/usr/bin/env -C`; Windows has no equivalent, and such a server fails the
  turn there. Without one, a server starts in the turn's workspace. Their
  tools load eagerly while the experimental betas are off, so `--tools` needn't
  name them.
- **`CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS=1`.** The CLI's experimental
  layout uses all four of the API's cache markers, leaving none for the
  marker SharpTurns puts on reseeded history. Without that marker, every
  reseeded turn pays full price for its history. The cost is that the CLI
  receives no readable thinking text, so SharpTurns shows only a
  "Thinking…" status.
- **`CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS=1`** drops the CLI's Git status
  snapshot, which changes with the repository and would invalidate the
  cached history after it. The default system prompt carries Git safety
  instructions instead.
- **`CLAUDE_CODE_DISABLE_AUTO_MEMORY=1`.** Auto memory would add hidden
  context outside the app's context management.
- **One-shot calls** (work summaries) keep `--restricted` and `--safe-mode`,
  with an empty `--tools`, a replacement `--system-prompt`, and
  `--no-session-persistence`. A disposable request needs no project
  instructions or customizations.

### Sessions and replay

- The default system prompt (`ClaudeCliCodingPolicy.DefaultSystemPrompt`) is
  passed with `--append-system-prompt`, unless the user saves their own in
  Config → Preferences.
- The CLI reuses a session's saved system prompt on resume. The session policy
  version therefore hashes the prompt and the conversation's output style, so
  changing either starts a fresh session. It also hashes the names of the MCP
  servers the turn starts, since they change the session's tools; a session
  without any keeps its version.
- A session is tied to its working directory, so changing the conversation's
  workspace, or moving a conversation without one to another project, starts a
  fresh session.
- A session is resumed only after a turn completes cleanly and the saved
  history still matches what the CLI saw. A stopped, failed, or interrupted
  turn, or any change to the saved history (deleting, hiding, compressing, or
  expanding a turn, or changing a replayed image), makes the next turn reseed:
  it starts a fresh session seeded from the saved conversation.
- Reseeding replays each turn's dialogue: the prompt, assistant text, messages
  sent during the turn, question and answer pairs, and images. Tool activity
  and thinking are not replayed. Each turn's block carries both `turn_id` (a
  database ID) and `turn_number` (its position), and the system prompt
  explains the difference so the model refers to turns by Turn #.
- Reseeded history carries one cache marker (`ephemeral`, 1-hour TTL) on its
  last block, never on the current request or its images. Earlier blocks stay
  unchanged as the marker moves, so a reseed after compressing the latest turn
  reads the earlier turns from the cache. This is why the experimental betas
  and the Git snapshot are turned off (see Launch profile).
- Messages sent during a turn go through `ClaudeCliInputQueue` (up to 5
  waiting). They are saved where the CLI acknowledged them; any it never
  accepted return to the composer when the turn ends. Queued messages are
  text only.

### Version

The minimum CLI version is `ClaudeCliVersion.Minimum` (2.1.286), the oldest
release the launch profile was verified with. Raise it when a change depends
on a newer release. The app checks `claude --version` at startup and when the
claude path changes, and reports a missing or older CLI without blocking
turns. CLI flags, environment switches, and stream-json events change between
releases, and some are undocumented, so verify them against the installed
version.

## Context management

- **Hide** leaves a turn out of the replay. The Show picker (Visible by
  default) also takes it off the timeline.
- **Compress** replays a turn as its user inputs plus a summary: a generated
  `## Work Summary`, then `## Final Assistant Response — Verbatim` (or
  `Partial` for a stopped or failed turn) with the last response unchanged.
  The summary comes from a tool-less one-shot call with the conversation's
  summarizer model and effort (Edit Conversation), or else the default ones
  (Config → Preferences). A turn with no tool calls or
  intermediate text gets a fixed summary without a call. On screen, the Work Summary is a collapsed
  card and the Final heading is hidden; the stored and replayed summary keeps
  both.
- **A compression is kept only if it makes the replay smaller** than the
  turn's saved content (dialogue, tool activity, and images). That reduction
  is what the card reports.
- **Expand** discards the summary.
- **Auto-Summarize** compresses each turn right after it completes. Stopped
  and failed turns are left alone. It is on for new conversations; the
  column's database default stays off, so conversations created before that
  change keep their setting.
- **Everything waits for a running turn.** Every change reseeds the next turn,
  so hiding, compressing, deleting, and branching wait while a turn or a
  compression runs. One compression runs at a time, and Send waits for it.
- **Bulk actions** in the Context Management tab only touch the turns the
  Show picker lists; an unlisted turn is unchecked, so a bulk action never
  changes a turn the user can't see.

## Turns and rendering

- Text streams as plain text and renders as Markdown when its item ends (a
  tool card, question, or message follows, or the turn finishes). Completed
  fenced code blocks render early.
- Markdig's advanced extensions are mostly turned off, along with raw HTML.
  Generic attributes, math, emphasis extras other than strikethrough, and
  several others dropped or rewrote ordinary text such as `{}`, `List<T>`,
  `$x$`, and `2^10`. A module initializer applies this before LiveMarkdown
  builds its shared pipeline.
- Each tool call gets its own card, in order. A card shows the cache hit rate
  of the request that made the call. That request's tokens are saved in the
  tool part's JSON, so older calls simply show no rate.
- Search counts what is on screen: the lines a tool card shows, and a Work
  Summary only while its card is open.

## Images

- PNG, JPEG, GIF, WebP, or BMP, up to 10 per turn. Images over 1 MB or 2000 px
  are downscaled to a 2000 px long edge and re-encoded as WebP. Reseeding
  resends earlier turns' images, and the API limits each image to 2000 px
  once a request holds more than 20 images.
- Images are saved as base64 in the database.
- Each image has a replay choice (`IncludeInFutureReplay` in its part's
  JSON). A turn replayed in full sends all its images, a compressed turn sends
  only the chosen ones (the rest as text descriptors), and a hidden turn sends
  none. Flags like this live in a part's JSON, so they need no migration.

## Branching

A branch is a new conversation holding a contiguous run of the source's turns,
renumbered from 1, with their parts, usage, and hidden and compressed state. It
keeps the source's project, title, model, effort, output style,
Auto-Summarize, workspace, and MCP servers, but not its notes, protection, or
CLI session, so its first turn reseeds
from the copied history. The source is unchanged. A range must be contiguous,
and turns hidden from the timeline are copied when they fall inside it. Where a branch came
from isn't stored, since nothing shows it.

## Dictation

- Recording uses a command-line recorder the user installs (`ffmpeg` on macOS
  and Windows, `pw-record` or `arecord` on Linux), so the app bundles no audio
  code. Deepgram (`nova-3`) transcribes the WAV with the user's own key.
  Transcripts below 45% confidence are dropped.
- Every composer shares one dictation service, so only one recording runs at a
  time.
- While dictation records or transcribes, Send and anything that would show
  another conversation are locked: switching conversations or projects, New,
  Edit (which can move the conversation to another project), and Delete,
  branching (a branch opens in place of the current conversation), and the
  Projects and Config tabs. The transcript belongs to
  the conversation where recording started, and the lock keeps the user
  looking at it. Clicking a locked control explains why.
- Deleting a conversation or closing the app stops its recorder, so no
  recorder process outlives it.

## Multiple instances

- **One conversation, one instance.** Two instances writing to one
  conversation would collide on turn numbers, the CLI session, and the replay.
  So each instance holds a lock file for the conversation it shows, and for any
  conversation still running a turn, an Auto-Summarize, or a compression
  (`ConversationLocks`, a `locks` folder beside the database). The file is
  open with `FileShare.None`, so the OS releases it if the instance exits or
  crashes.
- **Switching away from an idle conversation releases it,** so another
  instance can open it. One that selects a held conversation shows why and
  checks every second until it's free.
- **Taking a conversation back reloads it only if another instance held it
  meanwhile.** Each holder writes a new token into the lock file, so finding
  its own token means nothing changed, and the turn cards keep their state.
  The composer's draft and images survive a reload.
- **Turns left `running` are failed at startup only if their conversation's
  lock is free,** since a live instance may still be running them. The lock is
  held while they're closed, so no instance starts a turn there meanwhile.
- Deleting a conversation, or a project with one, that another instance holds
  is refused.
- Other state isn't shared live: each instance reads the conversation list
  when a project is selected, and preferences at startup.

### Instance Manager

`SharpTurns.InstanceManager.App` is a separate, optional app that lists the
running instances and docks their windows beside its own.

- **Reporting.** Each instance sends its project, open conversation, model,
  and whether a turn or summary is running over a per-user local socket
  (a named pipe on Windows), on every change and as a 5-second heartbeat. The
  manager drops an instance it hasn't heard from in 20 seconds. Without a
  manager the reports simply fail and retry.
- **Linking.** The manager links every instance it discovers through a
  per-process window-control endpoint. Each instance keeps ownership of its
  own window and applies the bounds the manager sends; the manager follows
  the window the user moves or resizes. The linked window's left edge sits on
  the manager's right edge, and both share one top and height. Detaching, or
  losing the connection, restores the window's own bounds.
- **Launch and Reload** run `dotnet run --project src/SharpTurns.App` from the
  repository above the manager's build folder. On Windows they pass
  `--no-build`, since running instances lock the build output. Reload ends the
  instance first, so its conversation lock is free for the replacement, which
  opens that project and conversation from `SHARPTURNS_APP_STARTUP_SESSION`.
- **End Process and Reload** wait for running turns and summaries, then end
  the instance's whole process tree.

## Persistence

- One SQLite file, `sharpturns.db`, in WAL mode, under the per-user local
  app-data folder. Explicit SQL and row mapping, no ORM.
- Numbered migrations in `Persistence/Migrations.cs`, tracked by
  `PRAGMA user_version`. A schema newer than the build supports is refused.
  Merged migrations are never edited.
- Several instances can share the database (see Multiple instances). Each
  migration re-reads the schema version inside an immediate transaction, so
  two instances starting at once don't apply the same one twice.
- Response parts are written when a turn ends; the prompt and images when it
  starts.
- Core references `SQLitePCLRaw.bundle_e_sqlite3` directly to override the
  version `Microsoft.Data.Sqlite` brings in, which has a high-severity
  advisory (GHSA-2m69-gcr7-jv3q).
- The Deepgram key is stored in plain text, and the README says so.

## Settings

- **Models** are raw model IDs in a user-managed list that fills every model
  picker. There is no "default" entry; every conversation names an ID.
  Renaming or deleting an ID moves the conversations and settings that use it.
- **Edit Conversation** (the sidebar card's edit button) sets the title, moves
  the conversation to another project, sets a workspace in place of the
  project's working directory, protects it from deletion, and selects its MCP
  servers and context files. A protected conversation can't be deleted, and
  neither can a project that holds one.
- **Context files** are text files in the workspace, stored by relative path
  with a role, an optional purpose, and Required and model-may-maintain flags.
  Each turn reads the enabled ones (up to 256 KiB each, 512 KiB in all) before
  choosing a session. Their content and settings are part of the session
  fingerprint, so a change starts a fresh session, which receives each file
  after the seeded history and its cache marker; a resumed session already has
  them. A required file that can't be read fails the turn before the CLI
  starts; an optional one is left out and named in the status line.
- **MCP servers** are defined in Config → MCP Servers and selected per
  conversation. A disabled server stays selected but isn't started. Deleting a
  server removes it from every conversation. There's no Test Connection, since
  the CLI, not SharpTurns, starts the servers.
- **The main window** opens at the Preferences startup size, centered on the
  monitor it was last moved to (`startup_window_screen`: the monitor's name
  and bounds). The monitor is saved when the window reaches it, not on close,
  because not every way out of the app closes the window first. It falls
  back to the main display when that monitor is gone. With several
  instances, the last one moved to another monitor decides.
- **Display settings** (font size, background, text brightness, Markdown and
  monospace toggles) are shared by every conversation and reset on restart.
- **Notes** are never sent to Claude.
- **Sounds** play on macOS and Windows; Linux has no sound player.

## Open questions

- User-installed plugins load with turn launches. Their hooks, skills, and MCP
  servers stay off through `--settings`, `--disable-slash-commands`, and
  `--strict-mcp-config`, and subagents are denied, but this hasn't been tested
  with an installed plugin.
- Because response parts are saved when a turn ends, a crash mid-turn keeps
  only the prompt and images. Incremental saving may be worth adding.
- Reseeding resends every retained image, so many images could approach the
  API's 32 MB request limit. Compressing or hiding a turn drops its images
  from the replay, except those chosen to stay.
