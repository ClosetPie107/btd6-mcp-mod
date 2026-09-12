# BTD6 MCP Mod

Let an AI agent play Bloons TD 6 through game-state reads and native game actions, rather than mouse clicks and screen coordinates.

The project has two parts:

- **AgentBridge** — a C# mod loaded by MelonLoader. Reads the running game and executes commands on the game thread.
- **btd6-mcp** — a local TypeScript [Model Context Protocol](https://modelcontextprotocol.io/) server. Turns those reads and commands into tools for Claude Code, Codex CLI, and other MCP-compatible agent harnesses.

```text
Agent harness  ← stdio MCP →  btd6-mcp  ← local JSON mailbox →  AgentBridge  ↔  BTD6
```

The model chooses the strategy; the bridge handles game access. No model or provider API is embedded in the mod. The MCP server runs outside the game and communicates through local files, not an exposed network port.

## Why BTD6?

BTD6 is an interesting test of long-term planning and adaptation. A CHIMPS run ends at round 100: the model has to survive the next wave while saving for upgrades that may only pay off much later. Cash spent on an early fix is cash unavailable for the late-game defense, and CHIMPS does not let you sell towers to undo a purchase.

It also tests whether a model can revise a plan when the defense struggles. Placement, targeting, damage types, and ability timing all matter; buying more damage is not always the answer. Checkpoint-assisted runs let models diagnose a failed round and try a different approach, while unassisted runs test whether they can anticipate those problems before committing.

## What agents can do

- **Read and run matches.** Inspect cash, lives, rounds, towers, heroes, abilities, and UI blockers. Start matches, play single rounds or batches with stopping points, change speed, and return to the menu. Failed rounds include threat and leak diagnostics where available.
- **Reason about the map.** Read track geometry, branching routes, terrain bounds, and tactical PNG maps. Find native-validated placement spots, compare track or support coverage, and inspect selected towers' range and line-of-sight overlays. Positions use game coordinates, not screen pixels.
- **Build and control a defense.** Place, upgrade, and sell towers; change targeting priorities; set aim points, flight modes, and patrol points; merge Beast Handler beasts; and remove supported paid obstacles. Purchases still obey the game's cash, placement, upgrade, and mode restrictions.
- **Time abilities and purchases.** Activate abilities immediately or schedule them against the round's simulation clock, including targeted abilities. Schedule ordered tower upgrades for a particular time or when enough cash is available. Pending schedules can be inspected and cancelled; they do not reserve cash.
- **Plan income and upgrades.** Inspect live costs, tower attacks, buffs, debuffs, popping capabilities, bank balances, and income sources. Project future cash with explicit assumptions, collect banks and ground drops, and estimate Paragon degree, Temple sacrifices, and Monkeyopolis value. These are planning aids, not guaranteed DPS or earnings forecasts.
- **Micro Geraldo and Corvus.** Inspect Geraldo's shop prices, stock, unlocks, and valid targets; buy and apply items now or on a schedule. Read Corvus's mana, spell costs, cooldowns, and active spells; cast spells or explicitly enable/disable continuous spells, immediately or on a schedule.
- **Retry from checkpoints.** Save and restore between-round positions to test another defense without replaying the opening. Assisted matches capture automatic checkpoints; named saves support deliberate branches. Explicit export/import carries supported checkpoints across game restarts. Ordinary checkpoints are memory-only, and restoring clears pending schedules.
- **Test in Sandbox.** Start Sandbox matches, spawn a chosen round's natural waves, and clear bloons. Separate research controls can pause the simulation, set the round number, or advance round-end events for mechanics experiments.
- **Inspect bosses.** Read the installed boss roster and live boss health, skull progress, defenses, and recognized phases. Unknown mechanics remain marked as unknown.

The adapter writes local action journals so purchases, round outcomes, and checkpoint restores can be reviewed after a run. See the [tool reference](btd6-mcp/README.md) for exact options and limitations.

## Notable model achievements

Selected runs reported by the project maintainer:

| Model | Harness | Result |
| --- | --- | --- |
| GPT 6 Astra | Codex | Beat **Dark Castle CHIMPS**. Reached round 100 on **Party Parade CHIMPS**, but did not beat round 100. |
| GPT 5.6 Luna | Codex | Beat **Spa Pits CHIMPS** and **Streambed CHIMPS**. |
| Gemini 3.8 Flash | Antigravity CLI | Beat **Off The Coast CHIMPS** and **Cornfield CHIMPS**. |

These are individual runs, not a controlled model ranking. Checkpoint/retry use and human intervention are not specified here; the results should not be read as verified unassisted or black-border clears.

## Screenshots

Screenshots of models playing will go here.

<!-- Add images below, with captions naming the model, harness, map, mode, and round.
     Note checkpoint use or other research assistance when relevant.
     Example once the image exists:
     ![Model playing a CHIMPS match](screenshots/model-map-chimps.png)
-->

## Before installing

**Use a separate modded profile/account and keep it out of normal online or competitive play.** Mods and research controls can affect saves, progression, and account standing. Back up your saves. A separate Proton prefix does not separate an account linked to the same Ninja Kiwi login. This is an unofficial project, not affiliated with Ninja Kiwi.

The verified setup is **BTD6 56.3 + MelonLoader 0.7.3 + BTD Mod Helper 3.6.8 on Linux/Proton**. Native Windows and other version combinations have not been verified for this project. Game updates can break the bridge.

You will need:

- A legitimate Steam copy of BTD6.
- MelonLoader and BTD Mod Helper, installed below.
- The **.NET SDK selected by [`global.json`](global.json)**: currently 10.0.400 with patch roll-forward. The mod itself targets .NET 6.
- **Node.js 20+**, npm, and Git.
- An MCP-compatible agent harness, with its model access configured separately.

The steps below build from source. This repository does not bundle the game, loader, Mod Helper, or a model. Shell examples use Bash syntax; replace the absolute paths with yours. In PowerShell, use the commands on one line instead of Bash's `\` continuations.

## Installation

### 1. Install MelonLoader

1. In Steam, right-click BTD6 → **Manage → Browse local files**. Close the game before changing files.
2. Follow the [official MelonLoader installer guide](https://github.com/LavaGang/MelonLoader#how-to-use-the-installer), selecting `BloonsTD6.exe`. Version **0.7.3** is the version tested here.
3. Install its runtime prerequisites, including the **.NET 6 Desktop Runtime** for the IL2CPP game. This is separate from the SDK used to compile AgentBridge.
4. Launch BTD6 once and allow MelonLoader to generate its assemblies and `Mods` folder, then close it.

**Linux/Proton:** follow MelonLoader's [Linux instructions](https://melonwiki.xyz/#/README?id=linux-instructions). Install the Windows runtime into the Proton prefix that actually runs BTD6, not just onto the Linux host. The research setup uses the `version` DLL override in Steam launch options:

```text
WINEDLLOVERRIDES="version=n,b" %command%
```

If you use an isolated prefix, keep its `STEAM_COMPAT_DATA_PATH` in those launch options as well. The game and loader need to use that same prefix on every launch.

### 2. Install BTD Mod Helper

Download `Btd6ModHelper.dll` from the [BTD Mod Helper releases](https://github.com/gurrenm3/BTD-Mod-Helper/releases) and put it in the game's `Mods` folder. Version **3.6.8** is the version tested here.

Launch BTD6 and confirm the **Mods** button appears on the main menu, then close the game. The upstream [installation guide](https://github.com/gurrenm3/BTD-Mod-Helper/wiki/Install-Guide) covers troubleshooting.

### 3. Build and install AgentBridge

Clone this repository and enter it:

```bash
git clone https://github.com/ClosetPie107/btd6-mcp-mod.git
cd btd6-mcp-mod
```

While the repository is private, cloning requires access and GitHub authentication. SSH users can use `git@github.com:ClosetPie107/btd6-mcp-mod.git` instead.

Also obtain a [BTD Mod Helper source checkout](https://github.com/gurrenm3/BTD-Mod-Helper/tree/3.6.8) matching the installed version. The build needs its `BloonsTD6 Mod Helper/btd6.targets` file; the installed DLL alone is not enough.

From this repository's root:

```bash
dotnet build AgentBridge/AgentBridge.csproj -c Release \
  -p:BloonsTD6="/absolute/path/to/BloonsTD6" \
  -p:ModHelperTargets="/absolute/path/to/BTD-Mod-Helper/BloonsTD6 Mod Helper/btd6.targets"
```

The game path must contain the generated `MelonLoader/Il2CppAssemblies` directory and the installed Mod Helper DLL. On Linux, pass Linux filesystem paths.

With the game **stopped**, copy `AgentBridge/bin/Release/AgentBridge.dll` into its `Mods` folder alongside `Btd6ModHelper.dll`. Building does not install the mod automatically.

Launch BTD6 and check `MelonLoader/Logs` for AgentBridge loading successfully. Reach the main menu and acknowledge any initial mod warnings. A new DLL always requires a game restart.

### 4. Build the MCP server

From the repository root:

```bash
npm --prefix btd6-mcp ci
npm --prefix btd6-mcp run build
```

The entry point is now `btd6-mcp/dist/index.js`.

Find the loaded bridge's mailbox, normally:

```text
<BTD6 installation>/UserData/AgentBridge/ipc
```

If your loader stores `UserData` elsewhere, use that actual location. Set **`BTD6_AGENT_BRIDGE_IPC_ROOT` explicitly** in your harness configuration; the adapter's development default is not a portable installation path. For Linux/Proton, this must be a path the native Node process can access, not a Wine drive-letter path.

## Connect an agent harness

The harness launches the MCP server itself. Do not start a separate background server. BTD6 must be running with AgentBridge loaded for game reads and actions to work.

### Claude Code

Register a local stdio server, replacing both paths:

```bash
claude mcp add btd6 --transport stdio --scope user \
  --env BTD6_AGENT_BRIDGE_IPC_ROOT="/absolute/path/to/BloonsTD6/UserData/AgentBridge/ipc" \
  -- node "/absolute/path/to/btd6-mcp-mod/btd6-mcp/dist/index.js"
```

`--scope user` makes it available across your projects. Use `--scope local` instead to limit it to the current project without committing machine-specific paths.

Run `claude mcp get btd6`, then open Claude Code and use `/mcp` to inspect the connection and permissions. See [Claude Code's MCP documentation](https://code.claude.com/docs/en/mcp).

### Codex CLI

Register the same server:

```bash
codex mcp add btd6 \
  --env BTD6_AGENT_BRIDGE_IPC_ROOT="/absolute/path/to/BloonsTD6/UserData/AgentBridge/ipc" \
  -- node "/absolute/path/to/btd6-mcp-mod/btd6-mcp/dist/index.js"
```

Round batches can take longer than Codex's default tool timeout. In `~/.codex/config.toml`, add `tool_timeout_sec` to the existing server table. The complete entry should look like this; do not create a duplicate table:

```toml
[mcp_servers.btd6]
command = "node"
args = ["/absolute/path/to/btd6-mcp-mod/btd6-mcp/dist/index.js"]
tool_timeout_sec = 3600

[mcp_servers.btd6.env]
BTD6_AGENT_BRIDGE_IPC_ROOT = "/absolute/path/to/BloonsTD6/UserData/AgentBridge/ipc"
```

Run `codex mcp list`, then use `/mcp` inside Codex to inspect the connection. See [Codex's MCP documentation](https://developers.openai.com/codex/mcp). For either harness, keep batch sizes and round wait limits within the host's tool timeout.

### Other MCP hosts

Configure a **local stdio** server with:

- **Command:** `node` (or its absolute executable path if it is not on the host's `PATH`).
- **Arguments:** the absolute path to `btd6-mcp/dist/index.js`.
- **Environment:** `BTD6_AGENT_BRIDGE_IPC_ROOT` pointing to the bridge mailbox.

No HTTP URL, port, or bridge API key is needed. The harness still needs its own model credentials. Approve tool access according to what you want the agent to control; this server includes actions that spend cash, replace matches, and restore earlier state.

## First run

Start with a read-only connection check:

> Use `btd6_status` to check the connection. Don't start or replace a match yet.

Before asking the agent to play, have it read the bundled [playing guide](btd6-mcp/PLAYING.md), also exposed as the MCP resource `btd6://guides/playing`. For example:

> Read the BTD6 playing guide, then start Logs on Hard Standard with Sauda. Use checkpoints to retry failed rounds, but don't use round-setting or round-advancement research controls. Play to victory and report any restores or other assistance used.

For an unassisted attempt, explicitly request `checkpointPolicy: "none"` and prohibit restores and research mutations. **Assisted checkpointing is the default**, so a CHIMPS win alone does not establish an unassisted clear. Automatic ground-drop collection also defaults to enabled; disable it if your rules require manual collection.

Profile provisioning is optional and changes unlocks; it is not needed to test the connection. After updating C# code, rebuild the DLL and restart the game. After updating TypeScript, rebuild the adapter and reconnect it in the harness.

## Further reading

- [MCP tool reference and troubleshooting](btd6-mcp/README.md)
- [Gameplay guide and run-reporting rules](btd6-mcp/PLAYING.md)
- [Bridge protocol](protocol/v1.md)
