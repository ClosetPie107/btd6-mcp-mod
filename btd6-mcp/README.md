# btd6-mcp

Native Linux stdio MCP adapter for the BTD6 research `AgentBridge` protocol v1. It exposes BTD6 to any compatible MCP host without putting MCP, provider, or prompt logic in the BTD6 mod.

## Gameplay guide resource

The server exposes `PLAYING.md` as the discoverable MCP resource **`btd6://guides/playing`** (`text/markdown`, name `playing-guide`). Initialization instructions direct agents to read it before gameplay, once per session or when its guidance is lost from context.

Clients can discover it through `resources/list`, then request:

```json
{ "method": "resources/read", "params": { "uri": "btd6://guides/playing" } }
```

The resource serves the installed guide verbatim and requires neither a running game nor client-side filesystem access. It resolves relative to the adapter module, not the client's working directory; keep `PLAYING.md` alongside `dist/` when distributing the adapter. Missing files fail visibly rather than returning a substitute guide.

Hosts must support MCP resources and surface server instructions to the model; connecting alone does not guarantee that the host reads or injects the guide. Tool descriptions retain their own critical safety constraints. Reconnect an existing MCP session to discover the new resource and initialization instructions; no game restart is needed.

## Output profiles

Model-facing text is compact JSON. `outputProfile: "summary"` is the default for observation and intelligence tools. Round execution is structurally adaptive: clean rounds always return compact decision-ready state, while defeat and timeout retain diagnostic telemetry. Complete round state remains available through a follow-up full `btd6_observe`.

- Observations remain self-contained and expose both stable `mapId` and human-readable `mapName`. Tower summaries include semantic `nextUpgrades` and `crosspathSlotsRemaining`.
- Round summaries use top-level outcome/resource fields and report automatic checkpoint semantics as `nextPlayableRound` plus `lastCompletedRound`.
- `tower_catalog({ "towerType": "MonkeySub" })` returns that native tower's upgrade details. Without a filter, summary is a compact tower index; full includes all upgrade details. IDs match case-insensitively; unmatched filters return no towers, not the entire catalog.
- Tower-stat summaries retain actionable attacks, conditional damage, capabilities, income and coverage caveats. Request full for nested damage-source trees, projectile behavior lists and model/effective comparisons. Summary values are not a final DPS calculation.
- `round_info({ "round": 98, "outputProfile": "full" })` includes spawn groups for precise scheduling. Cash projection summaries retain totals, confidence, assumptions and unsupported reasons; full adds per-round breakdowns.
- Place/upgrade acknowledgements retain actionable tower state rather than lifetime statistics. Targeting setters return applied state, not the complete `micro` discovery payload; call `inspect_tower_micro` again when available modes or target indices are needed.

Errors retain submission certainty, retryability and details. Unknown economic/mechanical values remain unknown; output profiles do not round money or change game actions. The bridge protocol, direct `BridgeClient` consumers and action journal retain their existing rich results. These changes take effect only after rebuilding and starting a new MCP adapter process; an already-running process is unchanged.

## Tools

### Observation & Match Lifecycle (Phase 1)
- `btd6_status` — bridge/game version, connection state, active-match state, on-main-menu state, UI interruption state, and capabilities.
- `btd6_observe` — current BTD6 state (round, cash, lives, actionable towers, hero, abilities). Summary is the default; full includes lifetime statistics and retained reports. At the main menu it returns the structured `NO_ACTIVE_GAME` error plus the observation in the requested profile.
- `btd6_start_match` — start a match from an unobstructed main menu (`map`, `difficulty`, `mode`, `hero`, `autoHandleUi`, `checkpointPolicy`). Results expose separate `mapId` and `mapName`. `checkpointPolicy: "assisted"` is the default and retains a rolling latest next-round checkpoint plus permanent five-round anchors; use `"none"` for unassisted play.
- `btd6_select_hero` — select the active hero for matches (e.g. `ObynGreenfoot`, `Gwendolin`, `Sauda`, `Geraldo`, `Corvus`, etc.).
- `btd6_restart_match` — restart the active match from its mode's initial round, cash, and lives through the native simulation restart command, not retry the current round. Clears bridge checkpoints and scheduled abilities. Observe the resulting state before issuing new gameplay actions.
  Defeat-screen restart uses the same committed native restart cleanup. Opening its confirmation dialog and then cancelling preserves checkpoints and pending schedules.
- `btd6_quit_match` — quit the active match and return to the main menu.
- `btd6_ensure_main_menu` — return to the main menu; repeated calls on an already-ready main menu leave it open. Rejects with `UI_BLOCKED` rather than dismissing an unresolved dialog or menu. Leaving an active match initiates one native quit transition; poll status until `onMainMenu` and `ui.ready` are true before starting another match.

### Tower Intelligence
- `btd6_tower_catalog` — live tower index with identity, base cost/range and placement class. An exact native `towerType` filter includes that tower's upgrades; full includes upgrade IDs/costs for the whole catalog.
- `btd6_tower_stats` — retrieve behavior-derived stats for an exact tower configuration, including attacks, cooldowns, pierce, direct damage, debuffs, and source-attributed conditional damage modifiers.
- `btd6_inspect_tower` — inspect a placed tower's effective cooldowns/ranges/targeting, `poppingCapabilities`, per-weapon damage/pierce, active buffs, and income. Both tower tools retain conditional damage and coverage caveats in summary. With `outputProfile: "full"`, they expose bounded `damageSources` for direct/contact/exhaust/expire projectiles and attached damage-over-time, with each source's own modifiers and `damageCoverage`. Tag arrays, all/any/ignore conditions, and max-damage flags are retained. These are components, not additive totals or final target-specific DPS.
- Camo is split into `poppingCapabilities.camo` and per-weapon `camo`: nullable `canDetectCamo`, `canCollideCamo`, `needsDetection`, and `canDamageCamo`, with native sources, bounded evidence, and an unknown reason. Manual movement/aiming is not camo permission. Native 56.3 checks distinguish base Spike Factory, Mortar 0-0-1, Heli 0-2-0, Dartling 0-1-0, and Ace 0-2-0. Effective inspection refreshes native buffs; Radar gain/removal is reflected. `canDamageDdt` checks Camo, Lead, Black and MOAB eligibility on the same damage source; it is nullable and is not a DPS or survival guarantee.
- Paragon tower stats additionally expose `paragonBossDamage`: native degree configuration and, for a placed Paragon, `currentDegree` and `currentDegreeBonusPercent` from the native degree mutator. Native fraction `0.25` represents 25%; do not apply it again to already-mutated projectile modifiers. `actualTargetDamageBonusPercent` remains `null`: boss immunity, armour, tag matching, and damage-source timing are not evaluated as one final damage number.
- `btd6_project_paragon_degree` — project degree using native tower contributions, degree thresholds, cash parameters, and a placed Tier 5's native paragon price. Retains fractional pop/sacrifice power and rounds slider power upward before applying the shared cash-category cap, matching native 56.3 upgrades. Only the three cheapest Tier 5s are excluded from sacrifice cash. Reports capped sensitivity and minimum additional whole-dollar investment; an unreachable slider milestone has `cashSliderNeeded: null` and `cashSliderReachable: false`. Purchase readiness is not guaranteed.
- `btd6_inspect_temple_sacrifices` — inspect prospective Sun Avatar → Sun Temple or Sun Temple → True Sun God sacrifices, including co-op towers but excluding heroes, paragons, powers, and subtowers. Reports native-worth availability, $50k category thresholds, unresolved lowest-category ties, and VTSG positioning. Prior Temple sacrifices, Monkey Knowledge, and mode prerequisites are not verified; `canFormVtsg` remains `null`.
- `btd6_inspect_monkeyopolis_sacrifices` — inspect Monkey City (xx4) with same-owner, below-Tier-5 farms in range. Tier 5 farms remain in place. Uses native economic model fields with `max(1, floor(worth / incrementCost))` income increments, verified against native 56.3 upgrades. Reports standalone farm-income assumptions and nullable incremental payback. Bank balances, collection timing, income buffs, and mode restrictions are not included in that comparison.

These tools are read-only planning aids. Paragon `excludeTowerIds` models an alternative sacrifice set; it does not protect those towers during an actual upgrade. A placed eligible Tier 5 is required for the native price quote. Read `formulaConfidence`, `warnings`, availability flags, and economic `assumptions` before budgeting. Power can be fractional: do not independently truncate contributions before comparing degree thresholds. `null` is unknown or explicitly unreachable, not zero or success. Native formation and upgrade eligibility still require the game's normal checks.

### Boss Intelligence
- `btd6_boss_catalog({ bossType? })` — enumerate the installed native boss roster, English in-game descriptions with localization-key provenance, and baseline model variants. Each variant has its own mechanics; finite skull thresholds (`healthSkulls`) are separate from `repeatingHealthTriggers` such as Bloonarius's damage-driven spawning. Available at the main menu. Baselines come from the global game model, not a forecast of event/co-op scaling.
- `btd6_inspect_boss({ bloonId? })` — read active bosses and segments, including live HP, definition/effective model maximum HP, armour, category immunity, invulnerability, targetability, position, skull progress and recognized Lych/Phayze phases. `bloonId` selects the **stable runtime `id`**, not the model-name `bloonId` field. An absent requested instance is an error; an unfiltered query outside a match returns `activeGame: false` and an empty list.
- `btd6_observe` includes compact `bosses` summaries independently of ordinary-round activity. Bosses can persist between rounds; no active regular wave does not mean no boss.

Boss types and models are discovered rather than hardcoded. `coverage.unknownBehaviorTypes` retains unfamiliar non-cosmetic behavior, and unavailable defenses remain `null`, not “no immunity.” Segmented bosses may expose sentinel segment HP: **do not sum segment health into encounter HP**. Raw path progress can be negative while a segment is entering the map. Model flags can differ from live invulnerability during spawn animations and phases.

Native 56.3 verification covered all seven installed boss descriptions and live boss identification, Normal/Elite Bloonarius skulls, Dreadbloon's Primary → Military immunity transition, Lych ethereal state, Phayze shield/skull state, Vortex/Blastapopoulos skulls, and Diamondback's generic segment/unknown-mechanic reporting. Degree 1 and 24 Dart Paragons reported native boss-scaling fractions 0 and 0.25. Tests used explicit research match settings, health positioning and real tower hits; they were not ordinary boss completions. Specialized Diamondback damage routing, co-op scaling, and final target-specific damage remain outside the supported interpretation.

### Player Action Primitives (Phase 2)
- `btd6_can_place_tower` — check whether a tower can be placed at simulation coordinates `(x, y)` (checks collisions, terrain restrictions, and cash affordability with dynamic difficulty pricing).
- `btd6_place_tower` — place a tower or hero at simulation coordinates `(x, y)`.
- `btd6_upgrade_tower` — buy ordered tiers with `{ towerId, upgradeSequence: [0, 0, 2] }`, immediately by default. Optional `round`/`delaySeconds` or `delayFromNow` schedule native simulation timing; `whenAffordable` waits for cash. Each path entry buys one tier, not a final tier count.
- `btd6_cancel_scheduled_upgrade` — cancel one upgrade schedule or all pending upgrades. Already purchased tiers are not rolled back.
- `btd6_sell_tower` — sell through the native game action and report actual `cashReceived`. Observed tower `sellValue` is the native current quote, including sellback modifiers such as Favored Trades; do not multiply it by another assumed sell percentage. Re-observe after selling support because remaining quotes can change. Native no-selling rules and tower-specific selling blocks are enforced.
- `btd6_inspect_obstacles` / `btd6_remove_obstacle({ obstacleId })` — discover native paid removables, then purchase removal with normal cash and native availability checks. Discovery includes runtime IDs, positions, prices and activity; unknown data remains null. Removal reports charge and verification separately. If verification is pending/unavailable, inspect again rather than resubmitting. Re-read map geometry after removal. This is not a general map-gimmick API.
- `btd6_inspect_tower_micro({ towerId })` — discover current native priorities, flight/movement modes, independent arms and coordinate-target indices. Re-inspect after upgrades or mode changes; indices describe the current tower, not permanent UI slots.
- `btd6_set_target_priority({ towerId, priority, targetIndex? })` — select an advertised native ID. Ace examples: `Circle`, `FigureInfinite`, `FigureEight`, `Centered` (xx2); Wingmonkey is available only when native knowledge/mode rules expose it. Heli: `FollowTouch`, `LockInPlace`, `PatrolPoints`, `Pursuit` (2xx). Spike Factory 56.3 exposes `Track`, `CloseTrack`, `SmartTrack`, `TargetSelectedPoint`, and `AutoTrack`; Normal/Close/Smart labels resolve to the corresponding native mode. Do not assume a legacy Far mode exists. Robo/Tech Terror require an arm `targetIndex`; their native arms cannot share one priority.
- `btd6_set_tower_target_position({ towerId, x, y, targetIndex?, pointIndex?, points? })` — set an active Ace center, Heli lock/patrol point, Engineer Bloon Trap/XXL Trap point, or other advertised selected-point target. Coordinates are simulation coordinates. It does not switch flight modes. `targetIndex` chooses a coordinate target; `pointIndex` replaces one patrol waypoint without moving the others. Alternatively supply the complete native-length patrol `points` array, not both forms. Native trap attack-range checks and actual coordinate readback determine success.
- `btd6_inspect_beast_merges({ towerId })` / `btd6_merge_beast({ sourceTowerId, targetTowerId, path })` — inspect the donor's `validRecipientTowerIds`, then merge it into the keeper (`source` = donor, `target` = keeper; paths 0/1/2 = water/land/air). Reports native power and per-path contribution relationships. Uses the native primary/secondary pet merge input, does not delete handlers or grant cash, and verifies the actual donor-to-recipient link. Native eligibility, ownership, tier/capacity and contribution rules remain authoritative.
- `btd6_activate_ability` — activate an ability by exact `abilityId`, unambiguous name, or `abilityIndex`, with a typed `target` when required.
- `btd6_schedule_ability` — schedule the same typed activation against the native round clock; pins the selected ability's identity and retains the target through cooldown retries.
- `btd6_cancel_scheduled_ability` — cancel one schedule by `scheduleId`, or all pending schedules.
- `btd6_start_round` — start the next round. Defaults to `waitForCompletion: true`. Clean completion is always compact; defeat/timeout retain diagnostics. Request a separate full observation for complete state.
- `btd6_wait_for_round_end` — wait until the active round completes or ends with an explicit `statusReason`. Uses the same adaptive compact result.
- Round results include the latest automatic checkpoint with unambiguous `nextPlayableRound` and `lastCompletedRound` fields when assisted checkpointing is active.
- `btd6_collect_bank` — collect stored cash from a placed Monkey Bank (030+).
- `btd6_collect_drops` — manually sweep and collect all active ground pickups (bananas, supply crates) currently on the map.
- `btd6_set_auto_collect` — toggle automatic collection of ground pickups (`enabled: true/false`). Defaults to enabled.
- `btd6_respond_ui` — respond to the currently observed `blockerId` using an advertised native `action`: `confirm`, `cancel`, `next`, `select_tower`, `unlock`, `back`, `freeplay`, `home`, `restart`, `collect`, or `play`. `select_tower` also requires a tower ID from `choices` as `value`, different from `selectedChoice`. Stale IDs, unavailable actions, and repeated/in-flight actions are rejected.

Native 56.3 mechanics checks covered paid removals on Dark Castle CHIMPS, Encrypted and Peninsula (including exact charges, geometry changes and no duplicate charge), Normal Bloonarius on Logs and Elite Dreadbloon on Cubism, Favored Trades quote changes and actual sale proceeds, Ace patterns/center, Heli movement/lock/patrol endpoints, Permaspike targeting, independent Robo/Tech Terror arms, Bloon Trap/XXL targeting, all three Beast paths, independent secondary-beast merging and a merged tier-5 purchase. These were Sandbox/research-assisted mechanics checks, not ordinary map or boss completions. Wingmonkey was not advertised by the tested towers; co-op behavior was not exercised.

### Geraldo shop

- `btd6_inspect_geraldo` — native item IDs, target kinds, current prices, stock/replenishment, unlock levels and availability for the active player's Geraldo. Native descriptions are nullable when localization is unavailable.
- `btd6_can_use_geraldo_item({ itemId, target? })` — read-only native target/eligibility check and quote. Does not reserve stock or cash.
- `btd6_use_geraldo_item({ itemId, target?, round?, delaySeconds?, delayFromNow?, whenAffordable?, idempotencyKey? })` — buy and apply one item, immediately by default, including mid-round.
- `btd6_cancel_scheduled_geraldo_purchase({ scheduleId? })` — cancel one pending purchase; omit the selector or use `"all"` to cancel all pending purchases. Does not undo completed purchases.

| Native item IDs | Required target |
|---|---|
| `ShootyTurret`, `CreepyIdol`, `RareQuincyActionFigure`, `GenieBottle`, `ParagonPowerTotem` | `{ "kind": "point", "x": ..., "y": ... }`; native tower footprint/terrain checks |
| `JarOfPickles`, `SeeInvisibilityPotion`, `SharpeningStone`, `BottleHotSauce` | `{ "kind": "tower", "towerId": "..." }`; native eligible recipient |
| `WornHerosCape` | Eligible Dart Monkey tower ID |
| `Fertilizer` | Eligible Banana Farm tower ID, not an arbitrary tower |
| `StackOfOldNails`, `TubeOfAmazoGlue`, `BladeTrap` | Native-valid track point |
| `PetRabbit` | **No target.** Uses Geraldo's current position internally, including at scheduled execution |
| `RejuvPotion` | **No target** |

Rabbit retains the descriptive `geraldo_range` category, but advertises `targetKind: "none"`: agents never need to drag or choose its location. Prefer the current inspection's `targetKind` over assumptions about categories.

```json
{ "itemId": "PetRabbit" }
```

```json
{ "itemId": "Fertilizer", "target": { "kind": "tower", "towerId": "123" } }
```

```json
{ "itemId": "RejuvPotion", "round": 98, "delaySeconds": 12.5, "whenAffordable": true }
```

Timing follows upgrade scheduling: `round`/`delaySeconds` use native simulation time, zero waits for round start, and `delayFromNow` requires an active native clock and cannot be combined with the other timing fields. `whenAffordable` alone persists across rounds; timed affordability expires at target-round end. Purchases reserve neither stock nor cash, and do not wait for restocking or future unlocks. Price, stock and target eligibility are rechecked before spending. Tower targets pin an ID and resolve its current position; a missing or differently selected tower is rejected.

Scheduled execution stops while paused/UI-blocked. Each update processes due ordinary abilities, then upgrades, then at most one Geraldo purchase; due Geraldo purchases are considered in submission order. This ordering is not an action-dependency language: a Rejuvenation timestamp does not guarantee a preceding ability succeeded. Restore/restart/match replacement clears schedules and match-local idempotency. At most 32 purchases are pending, with 64 terminal outcomes and 512 protected idempotency keys per match.

Inspect `status`, `purchased`, cash/stock readback, created tower IDs and `failure`. `verification_pending` is an **uncertain native submission**, even when `purchased` is false; never repeat it as a new purchase. Reuse a supplied idempotency key only for the identical request. Full observations retain recent `scheduledGeraldoPurchases` results; summary retains pending, failed, expired and uncertain entries.

Historical native 56.3 verification in the private research harness covered all sixteen items, native tower buff readback, Cape transformation, Rejuvenation cooldown reset, automatic rabbit placement, invalid targets, limited-cash affordability, timed mid-round purchases, pause gating, cancellation, expiration and checkpoint schedule reset. The item fixture explicitly placed the existing `Geraldo 20` model in Sandbox; the cash/scheduling fixture used Standard mode. These were research-assisted mechanics checks, not an ordinary map completion.

### Corvus spellbook

- `btd6_inspect_corvus` — current mana/capacity, hero level, recovery/stun/drain state, and sixteen native spells with unlock levels, initial/ongoing mana costs, duration, cooldown, active state and actionable readiness.
- `btd6_cast_corvus_spell({ spellId, ...timing })` — cast a spell of kind `cast`. An already-active cast spell is rejected or waited on, not blindly activated again.
- `btd6_set_corvus_spell({ spellId, enabled, ...timing })` — set a `continuous` spell to an explicit desired state. Already-enabled/already-disabled requests complete without another native action or initial mana charge. Disabling does not require activation mana.
- `btd6_cancel_scheduled_corvus_action({ scheduleId? })` — cancel pending work; omit the selector or use `"all"` for all pending Corvus actions. This does **not** turn off an already active spell.

Continuous spells are `Aggression`, `Malevolence`, `Spear`, and `Storm`. The twelve cast spells are `AncestralMight`, `Echo`, `Ember`, `Frostbound`, `Haste`, `Nourishment`, `Overload`, `Recovery`, `Repel`, `SoulBarrier`, `Trample`, and `Vision`. Use exact IDs from inspection. Spellbook actions take **no point or tower target**. Ordinary hero abilities remain separate: use the existing advertised ability/targeting tools for those.

```json
{ "spellId": "Storm", "enabled": true }
```

```json
{ "spellId": "Vision", "round": 90, "delaySeconds": 2, "whenReady": true, "idempotencyKey": "r90-vision" }
```

Casts and desired-state changes are immediate by default. Both accept `round`/`delaySeconds`, or `delayFromNow` with an active native round clock. Zero seconds waits for round start. `whenReady` waits for transient blockers such as an active spell, cooldown, insufficient mana, recovery or stun; it does not wait for future unlocks or a replacement hero. Ready requests can complete immediately. Readiness-only waits persist across rounds; timed waits expire at target-round end. Automatic actions freeze with pause/UI blocking, reserve no mana, and never automatically maintain or re-enable a completed continuous-spell request.

Initial mana cost is not a promise of sustained uptime: continuous spells have ongoing native drain. `canCast` includes active/cooldown guards in addition to native eligibility. BTD6 56.3 can report its raw castability flag as true for an already-active Vision while ignoring another activation; the adapter-facing inspection reports that spell unavailable until it can actually be cast again.

Schedules pin the hero and spell IDs and refresh native spell models before execution. Each update processes existing abilities/upgrades/Geraldo work before at most one native Corvus action. There is no cross-action success dependency. Limits are 32 pending actions, 64 terminal outcomes, and 512 protected idempotency keys per match. Restore/restart/match replacement clears schedules and idempotency. Identical keyed retries retain their original timing, including relative requests and already-passed timestamps.

Results report `status`, `executed`, `changed`, mana before/after, native spell state, failure/wait reason and execution timing. A desired-state no-op has `executed:false` and `changed:false`. Native spell-start events are correlated to the exact manager/spell; active-state or cooldown transitions also establish execution. Mana changes alone do not. `verification_pending` and uncertain transport errors must never be replayed as fresh actions. Full observations retain recent `scheduledCorvusActions`; summaries omit completed/cancelled history.

Historical native 56.3 verification in the private research harness covered all sixteen spells, repeated continuous-state requests, level/kind rejection, mana and active-spell/cooldown waits, pause gating, timed execution, idempotency, cancellation, expiry, and restoring native mana, active spells and cooldowns. The scenario used explicit level-19/20 hero models in Sandbox/Deflation, with mana earned from real bloons rather than assigned by the bridge. This was research-assisted mechanics verification, not an ordinary map completion.

### Research Controls & Spatial Reasoning
- `btd6_get_map_layout` — topological track graph (`nodes`, `edges`, `routes`, `bossRoute`, `routingPolicy`, `edgeTraffic`), coordinate bounds, tactical color-coded PNG render, and optional native range/line-of-sight overlays for up to 20 selected tower IDs. `format: "geometry"` returns the same bounded triangle metadata without an image.
- `btd6_find_placement_spots` — find native-validated tower centers. Summary output omits track interval arrays; full retains them. Rankings include `trackCoverage` and `supportCoverage`; the latter requires `coverTowerIds` and ranks candidates by intended recipients geometrically inside the candidate model range. `btd6_inspect_support_coverage` audits a placed Village/Alchemist and emits a warning when it covers no other tower. Geometric coverage is not native buff-eligibility proof; verify recipients with `btd6_inspect_tower`.
  `trackCoverage` keeps its length-first score and angular tie-breaker, but searches bend offsets as well as a coarse grid, then performs three local refinement passes. Returned coverage candidates are spatially separated; fewer than `limit` may be returned even when `totalFound` is larger. Every ranking checks an explicitly requested `near` center. Discovery remains bounded to 4096 native checks and is not an exhaustive optimum or a damage/LOS simulation.
- `btd6_round_info` — round totals, threat intel, active paths and routing using cached topology. Full includes individual spawn groups and timing for ability schedules.
- `btd6_run_rounds` — execute at most 20 consecutive ordinary rounds in one call, with explicit `stopBeforeRounds` boundaries. It stops on all non-clear outcomes and retains one action-journal entry per underlying round.
  If a later iteration fails, the error retains `completedRounds`, `outcomes`, `attemptedCount`, and the exact nested `failure` with a one-based `failedIteration`. A batch with completed rounds reports `BATCH_PARTIALLY_COMPLETED` and is not retryable as a whole, even when the failed iteration was never submitted. Inspect current state and request only the remaining work; do not replay the original count. An uncertain failed iteration sets `nextPlayableRound` to `null`.
- `btd6_save_checkpoint` — save named or round-keyed checkpoints. Custom labels are case-insensitive and capped at 50; eviction selects the oldest save timestamp, including refreshes when a label is overwritten. There is no protected/pinned anchor tier. Eviction and deletion remove associated fidelity metadata immediately.
  Omit `label` for round-keyed storage. Supplied labels must contain 1–128 UTF-16 code units and no control characters; save, restore, delete, and checkpoint transfer use the same bounds.
- `btd6_export_checkpoints` — explicitly write current boundary state and/or local saved checkpoints to a versioned disk bundle. Returns `exportId`; does not enable automatic persistence or evict checkpoints.
  Native save types are validated against an exact BTD6 56.3 allowlist, including map-event trigger/action records and their dictionaries. Unknown types remain rejected with `UNSUPPORTED_NATIVE_TYPE`.
- `btd6_import_checkpoints` — validate a bundle by `exportId` and load its checkpoint archive without changing the simulation.
- `btd6_restore_checkpoint` — restore by imported `checkpointId`, local label, or round. It waits for the restored between-round state and returns compact tower/ability state; restore clears scheduled actions and round telemetry.
- `btd6_list_checkpoints` — list round, custom, and imported checkpoints. Defeat/timeout round results already include a bounded recovery inventory with exact restore selectors.
- `btd6_delete_checkpoint` — delete a local checkpoint or an imported `checkpointId` from memory; exported disk bundles remain.
- `btd6_set_round` — synchronize simulation and UI round number.
- `btd6_advance_round` — step simulation forward by 1 round, firing round-end simulation rewards/events.
- `btd6_set_game_speed` — toggle fast forward mode.
- `btd6_pause_match` / `btd6_resume_match` — pause and resume match simulation.
- `btd6_sandbox_spawn_round` — spawn natural bloon waves for any round in Sandbox mode.
- `btd6_sandbox_clear_bloons` — immediately clear all bloons in Sandbox mode.

### Scheduled upgrades, round feedback, and portable saves

Immediate purchases stay simple:

```json
{ "towerId": "123", "upgradeSequence": [0, 0] }
```

To buy as cash arrives, add `"whenAffordable": true`. To trigger during a round, add `"round": 98, "delaySeconds": 12.5`. These conditions combine: a timed affordability schedule starts trying at that timestamp and expires when the target round ends. Affordability-only schedules persist across rounds. Zero seconds means round start; `delayFromNow` requires an active native clock. Automatic purchases pause with the simulation/UI, and none reserve cash. Inspect `status`, `appliedPaths`, `appliedSteps`, `nextUpgradeCost`, and eventual observation outcomes. Use `idempotencyKey` only to identify identical submissions; never retry an uncertain mutation with a fresh key.

Full round reports retain native leak events and pre-cleanup defeat counts, plus sampled peak populations, near-exit pressure, lane/composition details and scheduled action outcomes. Sampling is requested every 0.1 simulation seconds. Summary retains concise pressure, uncertainty and relevant action/leak outcomes; defeat/timeout responses include terminal threat detail when captured. `coverage` and nullable fields expose missing information. Spawn-window estimates are not exact emission counts, and spawn completion is not a percentage of damage dealt. Late track progress may be intentional for exit defenses.

Ordinary bloon progress uses native `Bloon.PercThroughMap()`, normalized against the routed spawn/leak boundary rather than the entire path-model polyline. A native leak reaches progress 1; near-exit pressure starts above 0.85. `coverage.progressBasis`/`progressBoundary` identify the basis; unavailable progress is `null`, not zero. Placement-coverage interval fractions describe the source polyline and are not interchangeable with this simulation progress.

For an intentional restart, export while between rounds:

```json
{ "includeCurrent": true, "includeSaved": true }
```

Record the returned `exportId`. After restart, import `{ "exportId": "exp_..." }`, start the matching map/difficulty/mode, then restore `{ "checkpointId": "cp_..." }` from the returned inventory. Imports do not start or restore a match automatically. Native data is version/type/hash checked; active-wave exports and unsupported save types fail explicitly. Files live under `loader-payload/UserData/AgentBridge/exports/`; ordinary checkpoint captures remain memory-only.

The bridge advertises `scheduledUpgrades`, `geraldoShop`, `corvusSpells`, `roundInsights`, and `checkpointTransfer` capabilities. C# changes require a game restart. Rebuild/reconnect the MCP adapter as well: an already-running adapter does not reload changed JavaScript or tool schemas.

Historical native checkpoint verification used the private research harness to export state, restart the process, import it, and verify restored round, cash, lives, towers, tiers and positions. See [protocol v1](../protocol/v1.md) for limits, schemas and exact semantics.

Tactical images and compass labels follow the game's orientation: simulation +X is right and +Y is down. Placement and targeting coordinates are unchanged. When `rangeTowerIds` is supplied, native local range/LOS triangles are shaded light yellow (visible) and charcoal/amber hatch (blocked); only `status: "native"` entries receive the dashed maximum-range ring. Global-range and unsupported entries report reasons and are not rendered as circles. Per-attack `attackThroughWalls` flags are returned in the image summary and geometry result. This is native range/LOS metadata, not complete damage, projectile, attack-primary, or subtower coverage. `round_info` start/end/duration values are simulation seconds, converted from native 60-Hz schedule frames. `durationSeconds` is the last scheduled spawn-window end, not the time needed to clear the round. `isLead`/`hasLead` identify Lead bloons; `requiresLeadPopping` reports native Lead immunity, including DDTs.

The bridge generates local meshes with BTD6's `RangeMesh.GetMeshStatically`, copies their interior triangles at the placed tower's simulation position, and releases the owned mesh. Global meshes never enter this decoder. Global towers such as Sniper receive no circle; mixed towers such as Aircraft Carrier retain their local circle. Unknown local mesh encodings return `unsupported`, not guessed coverage. Prefer one selected tower for unambiguous shading: overlapping overlays are not a combined-coverage calculation. Image summaries contain triangle counts and attack metadata; use `format: "geometry"` for the triangles themselves.


### Ability targets

Both activation tools accept the same optional `target`:

```ts
{ kind: "point", x: 135, y: 10 }
{ kind: "tower", towerId: "319" }
{ kind: "tower_position", towerId: "319", x: 33, y: 33 }
{ kind: "points", points: [{ x: -110, y: -70 }, { x: 110, y: -70 }] }
```

Coordinates use the existing simulation frame (+X right, +Y down). Each target shape is strict: point coordinates must be paired, finite, and within the native float range; IDs must be nonblank and at most 128 characters; point arrays contain 1–32 points. Native rules can require a specific point count or minimum path length. This is ability input, not persistent tower attack targeting.

Read `observe.abilities[].targeting` before choosing an input. It reports `kind`, the native `inputClass`, `supported`, and nullable `pointCount` for multi-point handlers. Known handlers cover placement/repositioning, tower selection, tower-plus-destination redeployment, and Paragon Carpet Bomb's ordered points. Unrecognized handlers report `unsupported`; the bridge never substitutes the cursor, origin, or a previous target. Untargeted abilities omit `target`.

Supply at most **one** selector: `abilityId` or zero-based `abilityIndex`. Omitting both selects the first available ability. Exact simulation IDs take precedence over names; duplicate name matches return `AMBIGUOUS_ABILITY`. The old scheduled `index` field is rejected—use `abilityIndex` consistently for both tools.

The bridge validates native eligible-tower lists and placement restrictions before committing input. Missing, mismatched, unexpected, invalid, or unsupported targets return explicit pre-execution errors (`ABILITY_TARGET_REQUIRED`, `ABILITY_TARGET_KIND_MISMATCH`, `UNEXPECTED_ABILITY_TARGET`, `INVALID_ABILITY_TARGET`, `UNSUPPORTED_ABILITY_INPUT`). These are rejected actions, not uncertain activations. A native execution exception remains `submitted_outcome_unknown`; observe before deciding what to do next.

An activation result includes `activated`, resolved `abilityId`, `name`, and the requested `target` (null for untargeted abilities). `activated: true` means the native activation was submitted, not that a delayed projectile has already spawned or a helicopter has finished moving.

Schedules resolve and pin the ability ID when accepted; the returned `abilityIndex` is its position at scheduling time, not an execution-time selector. Selling or adding ability towers cannot redirect a schedule. The target is revalidated when activation becomes ready; only `ABILITY_NOT_READY` is retried when `autoRetry` is enabled. Check `scheduledAbilities[].triggered` **and** `error`: `triggered` and `triggeredAtUtc` also mark terminal failures. Terminal outcomes remain observable for 60 seconds from completion, including after long cooldown waits. The recorded `target` is retained through retries. Match replacement/restoration clears pending schedules.

Round timing retains an observed native start tick after the last spawn until the round ends; it does not depend on the lifetime of the game's spawn record. Round changes, explicit research round resets, and match replacement discard the anchor. Sandbox's direct wave-spawn shortcut may lack a native round clock; `ROUND_TIME_UNAVAILABLE` rejects scheduling rather than inventing timing.

After updating the adapter, restart existing MCP server connections to refresh tool schemas. The bridge DLL also requires a game restart.

Historical native checks for BTD6 56.3 used the private research harness and Spectacle for screenshots. The scenario used Sandbox for Overclock, Darkshift, Chinook, untargeted activation, and Mega Mine; Deflation with a research-set round 60 exercised native scheduling, index changes, cooldown retries, and terminal-result retention. This was not a map-completion benchmark.

Separate native readback verified Mega Mine projectiles at `(135, 10)` and `(-135, 85)`, and Carpet Bomb target/bomb positions along `(-110, -70)` to `(110, -70)`. Paragon setup/readback used a temporary, explicitly invoked Sandbox fixture; no fixture command ships in the bridge.

### Mode selection and pricing

Use `mode: "CHIMPS"` with difficulty `Hard`. The bridge translates the public name to BTD6's native CHIMPS identifier (`Clicks` in 56.3), rather than treating the display name as a game modifier. CHIMPS uses Hard prices and disables selling/extra income. `mode: "Impoppable"` is a separate Hard-category mode with higher prices and income allowed.

Mode/difficulty combinations come from the native mode selector. Exclusive modes select their native category even when the default `Easy` was supplied; the start result reports the resolved difficulty. Unknown modes and invalid categories are rejected. Starting a match no longer silently completes or unlocks modes; provisioning is a separate explicit research action.

`Sandbox` is resolved through the game's separate Sandbox identifier at the requested difficulty; it is not part of the regular-mode button list.

Both observed `round` and `endRound` are one-based. Placed-tower upgrade quotes include native $5 rounding and can be compared with the actual purchase's cash change. Catalog prices remain raw model values; use placement checks for final base purchase quotes, including inventory modifiers.

Unavailable upgrade paths report `null`, never a negative sentinel. Availability uses native target-tier checks, including hero and Monkey Knowledge exceptions; it does not impose a blanket one-tier-5 limit. Prices use the native target tier and current modifiers, including hero discounts. Placement rejects known `INSUFFICIENT_CASH`, `LOCATION_BLOCKED`, and `PRICE_UNAVAILABLE` conditions before submitting a purchase; quote-related errors include `error.details.cost`/`cash` when known and have `submissionState: "rejected"`.

Upgrade batches accept 1–15 purchases in `upgradeSequence` and execute them sequentially across frames. The result contains `completed`, `appliedPaths`, `tower`, `cash`, and `failure`. On success, tower/cash describe the final purchase. On failure, `appliedPaths` contains only confirmed purchases; `failure` identifies the zero-based failing index, path, error and submission state. Tower/cash are then `null`: observe before replanning, especially after an uncertain outcome. Batches stop at the first failure and never retry or roll back prior purchases.

Cash projections require an active match between rounds. They derive baseline pop cash recursively from the match's modified bloon models, including super-ceramic cash compensation, and use its native income thresholds and round-reward models. HalfCash and Deflation therefore use their actual modified payouts, without applying their multipliers twice. Fractional cash is preserved.

The result separates `baselineIncome` from `towerIncome`. Check `baselineIncome.confidence` before budgeting: `supported` means model-derived accounting conditional on popping every scheduled bloon, not a guarantee of winning. Tower income remains an estimate with per-tower inclusions/exclusions and collection assumptions; bank withdrawals, ability income, attack-dependent bonus cash, future spending, and future income-buff changes are excluded. Unknown economic behaviors, Double Cash, dynamic simulation cash modifiers, and generated waves produce explicit `unsupportedReasons` and `null` for unavailable cash—never a standard-mode fallback. A hypothetical `fromRound` requires an explicit `currentCash`; use `round_info` for threats.

Historical native mode regression in the private research harness checked standard difficulty prices, CHIMPS versus Impoppable purchases/selling, upgrade quotes, one-based final rounds, HalfCash/Deflation starting state, and actual Impoppable farm income. Research round advancement was used only to fund the farm test; this was not a completion benchmark.

### Failure and round-completion semantics

Published commands whose outcome cannot be established return `submissionState: "submitted_outcome_unknown"` with `retryable: false`. Response errors retain their request ID, including malformed JSON, mismatched IDs, and invalid payloads. Observe the game before repeating a mutation; a failed completion read does not mean that `start_round` was never executed.

`canStartRound: true` means the upcoming round is ready, not that it cleared. An idle standalone wait runs to its monitoring deadline and returns `statusReason: "timeout"` with `roundCompleted: null`. Defeat and match termination also do not populate a completed round. Invalid start/wait options are rejected before changing game speed or starting a round.

Automatic collection enumerates the direct `Pickup` factory in BTD6 56.3, snapshots owning projectiles, and disposes the enumerator before collecting. It no longer scans combat projectiles. This native enumeration path was verified with bananas, farm crates, ability supply crates, checkpoint restore, and match replacement.

### UI interruptions

Status, observation, and lightweight round progress include `ui: { autoHandlingEnabled, ready, blocker }`. A blocker exposes its `id`, `kind`, `screen`, `state` (`waiting`, `acting`, `blocked`, `failed`), bounded title/body text, failure reason, available `actions`, tower `choices`, and `selectedChoice`. `ready` is true only when no interruption is present. A rewards or unlock screen is not a main menu.

During an authorized agent-started match, the bridge acknowledges only allowlisted first-time gameplay events tagged at `InGame.ShowEventPopup`, level-up acknowledgements, and recognized informational reward panels. It uses normal OK/Next actions and preserves game completion callbacks. Tower unlock selection remains explicit. Main-menu rewards, generic dialogs, account/login prompts, and unrecognized UI are not automatically dismissed. Returning to the main menu ends this authorization; restarting the same match preserves it.

Automatic ownership is distinct from readiness: authorized, recognized candidates stay `waiting`/`acting` while startup readiness catches up. `autoHandlingEnabled` is match-wide authorization, not a claim that every popup is automatic. Generic mode-introduction dialogs remain manual, and stale blocker IDs remain rejected.

Actions wait for stable interactability, including the reward panel's own readiness flag. Click-to-continue Monkey Money and Monkey Knowledge screens use their native `OnClick` handlers without waiting for the parent transition that the click must complete. Each screen step receives at most one action. A step that does not complete within 10 seconds, readiness that does not arrive within 15 seconds, or a detected cycle becomes a reported failure—not a close/retry loop. A readiness-only timeout can recover if the screen later becomes interactable; an already-issued action is never automatically retried.

Round waits continue through known automatic transitions but return `statusReason: "ui_blocked"` with `completed: false`, `roundCompleted: null`, and the blocker when a decision or failure needs attention. Round start checks UI readiness before speed or start mutations. Use `btd6_status`, make an explicit `btd6_respond_ui` decision, then observe the new blocker/readiness before continuing. An accepted response means the action was issued, not that the transition has finished.

Use `next` for level-up rewards and hero-unlock splashes. Victory is a two-stage flow: `next` dismisses the statistics summary, then `freeplay` or `home` makes the gameplay decision. Defeat exposes `restart` and `home`; restarting may open a separate confirmation dialog. Collection events expose available `collect`, `play`, and `back` actions. These use native controls and require no screen coordinates, X11 automation, or forced menu-state edits. Freeplay, restart, hero-unlock continuation, and collection-event choices remain explicit.

After a successful explicit `cancel` or `back` transition, a returning parent menu is a fresh manual decision with a new blocker ID. This does not reset automatic cycle protection or permit an in-flight/failed action to repeat.

The old blanket `btd6_dismiss_popup` tool was removed. Rebuild/restart the adapter when updating the bridge; restart the MCP host's adapter session to refresh its tool list.

Native verification used a temporary companion mod to open real game UI: Camo tutorial completion and clock resume, level-up completion, reward-panel readiness and completion, manual main-menu rewards, unknown-dialog Cancel callbacks, stale-ID rejection, and automation opt-out. The companion was removed afterward. The daily chest itself was not reset, and the fully provisioned profile did not provide an outstanding tower-unlock selection to exercise.

Cash-projection verification compared native cash awards across scaling boundaries (50/51, 60/61, 85/86, 100/101, 120/121, and round 140), Standard, Half Cash, Deflation, CHIMPS, and Alternate Bloons Rounds. Generated round 141 and Apopalypse were explicitly unsupported. Native UI verification also exercised Monkey Money through stdio MCP, resumed gameplay afterward, and covered Monkey Knowledge, victory/freeplay, collection-event exit, defeat restart, and repeated restart cancellation followed by home.

OffTheCoast issue verification compared the native game and tactical PNG on OffTheCoast and Logs; checked null crosspath/max-tier quotes, normal tier-five limits, and Silas's permitted second Ice tier five with its discounted purchase price; and exercised a partial upgrade batch through real stdio MCP. Round-95 emissions were checked at normal and fast-forward speed. `roundElapsedSeconds` and scheduled abilities now use native simulation ticks and round-start records rather than accumulated Unity frame deltas. An unavailable active-round clock is reported as `null` and scheduling rejects `ROUND_TIME_UNAVAILABLE` rather than inventing elapsed time.

## Installation and setup

The previously verified environment used **BTD6 56.3, MelonLoader 0.7.3, and BTD Mod Helper 3.6.8 on Linux/Proton**. Other versions and native Windows installation have not been verified for this project. The upstream guides below cover platform-specific installation; this repository does not include a game installation, loader binaries, SDKs, or personal installation-switching scripts.

Use an isolated research account and avoid normal online play with research modifications. Separate local game data does not guarantee separate online account association. Profile provisioning is an optional, explicit in-game research action, not an installation requirement.

### 1. Install MelonLoader

Locate your BTD6 installation through Steam's **Manage → Browse local files**, then close the game. Follow the [official MelonLoader installation instructions](https://github.com/LavaGang/MelonLoader#how-to-use-the-installer), including their runtime prerequisites. For Linux/Proton, use the linked [Linux instructions](https://melonwiki.xyz/#/README?id=linux-instructions).

Launch the game once after installation so MelonLoader can generate its assemblies and the `Mods` directory, then close it before installing mods.

### 2. Install BTD Mod Helper

Follow the [BTD Mod Helper installation guide](https://github.com/gurrenm3/BTD-Mod-Helper/wiki/Install-Guide): download `Btd6ModHelper.dll` from its [releases](https://github.com/gurrenm3/BTD-Mod-Helper/releases) and place it in the game's `Mods` directory. Launch the game and confirm the **Mods** button appears on the main menu, then close the game.

### 3. Build and install AgentBridge

This source distribution uses a local build. Install the .NET SDK selected by `global.json` (10.0.400 with patch roll-forward). The SDK used to build the mod is separate from MelonLoader's in-game runtime prerequisites.

Also obtain a source checkout of [BTD Mod Helper](https://github.com/gurrenm3/BTD-Mod-Helper) corresponding to your installed version: the build imports `BloonsTD6 Mod Helper/btd6.targets` from that checkout. Installing the Mod Helper DLL alone does not provide this build file.

From this repository's root, provide both external paths explicitly. This example uses shell line continuations; on other shells, put the command on one line:

```bash
dotnet build AgentBridge/AgentBridge.csproj -c Release \
  -p:BloonsTD6="/absolute/path/to/BloonsTD6" \
  -p:ModHelperTargets="/absolute/path/to/BTD-Mod-Helper/BloonsTD6 Mod Helper/btd6.targets"
```

Building does not deploy. With the game stopped, copy `AgentBridge/bin/Release/AgentBridge.dll` to that game's `Mods` directory, alongside `Btd6ModHelper.dll`. Launch the game and check the MelonLoader log for AgentBridge loading successfully. Replacing a DLL requires another game restart; there is no live reload.

The mod author is `ClosetPie`. See the [project README](../README.md) for an overview, model results, and Claude Code/Codex setup. The original machine-specific live verification harness is not included; historical native verification described in this document used that private research environment.

### 4. Build and connect the MCP adapter

Install Node 20+ and npm. From this repository's root:

```bash
cd btd6-mcp
npm ci
npm run build
```

Configure your MCP host to run `node` with the absolute path to `btd6-mcp/dist/index.js`, and set its `BTD6_AGENT_BRIDGE_IPC_ROOT` environment variable to the loaded bridge's `UserData/AgentBridge/ipc` directory. This is normally beneath the game installation; with a custom loader layout, use its actual `UserData` location. On Linux/Proton, provide the path accessible to the native Node process, not a Windows drive-letter path.

See [Mailbox location](#mailbox-location), [Verify with MCP Inspector](#verify-with-mcp-inspector), and [Register with Claude Code](#register-with-claude-code) below for connection examples. The adapter uses stdio: **stdout is MCP protocol only**; diagnostics go to stderr. Let the MCP host own the adapter process rather than starting a separate background daemon.

With the game running, first call `btd6_status` to verify the bridge connection. If a match is already active, `btd6_observe` can confirm its state without changing it. No profile provisioning or match replacement is required to check the connection. If loading fails, inspect the logs in the game's `MelonLoader/Logs` directory before attempting gameplay actions.

## Action journals

Every adapter session automatically creates an append-only journal pair in `btd6-mcp/logs/`:

- `<timestamp>-<uuid>.log` — readable timestamped action requests and outcomes.
- `<timestamp>-<uuid>.jsonl` — structured schema-v1 events for later analysis.

The adapter prints the journal path to stderr at connection time. Logs are local and Git-ignored. Rebuild the adapter and reload its MCP registration to enable updated logging; no game restart or DLL deployment is required.

To keep research evidence outside the source checkout, set `BTD6_AGENT_BRIDGE_LOG_ROOT` in the MCP server environment to an absolute directory path. It overrides the default `btd6-mcp/logs/` location for journals and retained read evidence; the mailbox is configured separately with `BTD6_AGENT_BRIDGE_IPC_ROOT`.

Journals record placements, upgrades, sales, targeting, abilities, collection, UI responses, match lifecycle, round starts/completion waits, and research controls including checkpoint saves/restores. Read-only observation, catalog, placement-search, and round-information polling is omitted by default. Tower results retain identity, position, tiers, and targeting; action responses retain available cash and round outcomes. Cached tower details and round context are labeled as previously observed; logging does not issue extra game requests to obtain missing details.

Each action has a session-local ID linking `action_requested` to `action_returned` or `action_error`. A returned result is not automatically a success: inspect flags such as `placed`, `completed`, and `statusReason`. Errors preserve uncertain-submission information. A request without a terminal event means the adapter did not record its outcome; do not infer success or retry blindly.

Checkpoint restores append new events rather than erasing failed branches. Match starts, restarts, quits, and restores are visible as actions within the session journal. Scheduled abilities log the scheduling request, not an independently verified later activation. The journal records actions through this adapter, not human input or another adapter's actions, and does not capture the model's reasoning.

Files accumulate without automatic deletion; archive or remove old sessions when no longer needed. If journal writing fails, the adapter warns on stderr and disables logging for that session without changing gameplay results or retrying actions.

The bridge DLL separately appends `reports/ui-transitions.jsonl` from its mailbox worker. It records UI action request/native return/failure, `OnMainMenu` entry/return, the first subsequent Unity update, and the current native menu-transition flags. This journal covers bridge-issued UI actions and lifecycle callbacks even when the adapter session journal is unavailable; entries are queued from the game thread so transition callbacks perform no file I/O.

### Optional read-side evidence

Set `BTD6_AGENT_BRIDGE_RETAIN_READS=1` in the MCP process environment and restart the adapter. This adds `<sessionId>.evidence.jsonl` with linked `read_requested`, `read_returned`, and `read_error` events for bridge reads and the actual MCP read-tool output. Arguments, timestamps, previously observed context, responses and structured errors are retained; this includes rendered map images. JSON payloads over 16 KiB are stored atomically under `<sessionId>/artifacts/` with relative path, byte count and SHA-256 references. Keep the evidence file and its artifact directory together.

Retention is opt-in, local and unbounded over the session; images can consume substantial disk space. It does not capture model reasoning. Logging failures warn on stderr without changing tool results or retrying gameplay.

### Internal research branch analysis

The adapter keeps the compact branch-history analyzer as an internal debugging library for journal regression tests and human inspection. It is deliberately not registered as an MCP tool: playing models receive current-round telemetry and bounded checkpoint recovery choices, not a callable summary of older strategy branches.

The analyzer retains purchases, targeting/timing changes, cash and round outcomes, plus repeated failure groups. `triggeringLeak` is reported only when a complete retained leak sequence supports identifying the final health-damaging leak at defeat; remaining bloon/MOAB workload is separate. Durable journal files remain the source of evidence.

Requests without a recorded result remain in bounded per-branch `unresolvedActions`, with `omittedUnresolvedActions` counting entries excluded by retention limits. Top-level `incomplete` remains true even when those branches are outside the requested page. These actions have unknown outcomes, not inferred successes or failures.

## Offline checks

From the repository root, run the pure-managed regressions:

```bash
dotnet run --project AgentBridge.Tests/AgentBridge.Tests.csproj
```

`global.json` selects SDK 10.0.400 with patch roll-forward. Managed checks target .NET 10; the bridge targets net6.0 for the game. These checks do not establish native IL2CPP compatibility.

Run the adapter checks from `btd6-mcp`:

```bash
npm run check
npm run check:test
npm test
```

They cover mailbox correlation and uncertainty, round termination, pre-submission validation, and graph producer bounds. Test TypeScript is checked separately from the production build.

## Mailbox location

The default mailbox is resolved relative to this repository:

```text
<repository>/loader-payload/UserData/AgentBridge/ipc
```

Generated profile snapshots are written separately from the mailbox:

```text
<MelonLoader UserData>/AgentBridge/reports/
```

This ignored directory contains append-only `ui-transitions.jsonl` plus the latest `diagnostics.json`, `benchmark-preflight.json`, and `provisioning.json`; it is local runtime evidence, not repository state. `diagnostics.json` is written only by the explicit **Write research diagnostics** Mod Helper action.

Set the mailbox path to the loaded bridge's `UserData/AgentBridge/ipc` directory. The repository-relative default is a development convention; no loader payload is shipped:

```bash
BTD6_AGENT_BRIDGE_IPC_ROOT=/absolute/path/to/ipc node dist/index.js
```

BTD6 must be running with `AgentBridge` loaded before tools can return game data. Set `BTD6_AGENT_BRIDGE_IPC_ROOT` in the MCP host's server environment as well.

## Verify with MCP Inspector

```bash
cd btd6-mcp
npx --yes @modelcontextprotocol/inspector --cli node dist/index.js --method tools/list --format json
npx --yes @modelcontextprotocol/inspector --cli node dist/index.js --method tools/call --tool-name btd6_status --format json
```

## Register with Claude Code

Build the adapter once, then register the compiled entry point at project scope:

```bash
claude mcp add btd6 --scope project -- \
  node /absolute/path/to/repository/btd6-mcp/dist/index.js
```

Any other MCP-capable host should launch the same `node` command as a local stdio server. The host owns the process lifetime; do not start a persistent adapter daemon.
