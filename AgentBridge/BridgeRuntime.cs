using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int QueueCapacity = 32;
    private const int TransitionJournalCapacity = 256;
    private static readonly ConcurrentQueue<MailboxCommand> commands = new();
    private static readonly ConcurrentQueue<MailboxCompletion> completions = new();
    private static readonly ConcurrentQueue<TransitionJournalEntry> transitionJournal = new();
    private static readonly CancellationTokenSource mailboxStop = new();
    private static Thread? mailboxThread;
    private static int outstandingCommands;
    private static int transitionJournalCount;
    private static int droppedTransitionJournalEntries;
    private static BridgeStateV1? pendingState;
    private static string? mailboxError;
    private static MailboxCommand? activeCommand;
    private static IEnumerator<BridgeResultV1?>? activeJob;
    private static long matchGeneration;
    private static IntPtr progressSimulation;
    private static string? matchId;
    private static BridgeConfiguration configuration = new();

    private sealed record MailboxCommand(BridgeRequestV1 Request, string Path);
    private sealed record MailboxCompletion(MailboxCommand Command, BridgeResultV1 Result);
    internal sealed record TransitionJournalEntry
    {
        public DateTime AtUtc { get; init; }
        public string Event { get; init; } = "";
        public string? BlockerId { get; init; }
        public string? Action { get; init; }
        public string? Screen { get; init; }
        public bool? ActiveGame { get; init; }
        public bool OnMainMenu { get; init; }
        public bool? MenuTransitioning { get; init; }
        public bool? MenuOpeningOrClosing { get; init; }
        public bool? MenuStillLoading { get; init; }
        public bool? MenuAnimatingWithCallback { get; init; }
        public string? Error { get; init; }
        public string? ContextError { get; init; }
    }

    private static void QueueTransitionJournal(TransitionJournalEntry entry)
    {
        if (Interlocked.Increment(ref transitionJournalCount) > TransitionJournalCapacity)
        {
            Interlocked.Decrement(ref transitionJournalCount);
            Interlocked.Increment(ref droppedTransitionJournalEntries);
            return;
        }
        transitionJournal.Enqueue(entry);
    }


    private sealed record BridgeConfiguration
    {
        public string CheckpointMode { get; init; } = "manual";
        public int[] CheckpointRounds { get; init; } = Array.Empty<int>();
        public int MaxRoundCheckpoints { get; init; } = 10;
        public double WorkBudgetMs { get; init; } = 2;
        public int MaxPlacementChecksPerFrame { get; init; } = 8;
    }

    private static void StartMailboxWorker()
    {
        mailboxThread = new Thread(MailboxWorker) { IsBackground = true, Name = "AgentBridge mailbox" };
        mailboxThread.Start();
    }

    public override void OnApplicationQuit()
    {
        activeJob?.Dispose();
        mailboxStop.Cancel();
        mailboxThread?.Join(1000);
    }

    // Only detached managed DTOs cross this boundary. No Unity, IL2CPP, or ModHelper calls here.
    private static bool TryReadBoundedMailboxJson(string path, out string json)
    {
        json = "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            byte[] bytes = new byte[MaxCommandBytes + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                    break;
                total += read;
            }

            if (total > MaxCommandBytes)
                return false;
            json = Encoding.UTF8.GetString(bytes, 0, total);
            return true;
        }
        catch (Exception ex)
        {
            throw new MailboxRecoveryReadException(path, ex);
        }
    }


    private static void MailboxWorker()
    {
        // A crashed command may already have mutated gameplay. Never replay processing files.
        try
        {
            MailboxRecoveryRules.ProcessFiles(
                Directory.EnumerateFiles(IpcProcessingDirectory, "*.json"),
                path =>
                {
                    if (!TryReadBoundedMailboxJson(path, out string json))
                        return null;
                    try
                    {
                        return JsonSerializer.Deserialize<BridgeRequestV1>(json, ProtocolJsonOptions);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                },
                request => MailboxRecoveryRules.IsSafeRequestId(request.RequestId),
                request =>
                {
                    string resultPath = Path.Combine(IpcOutboxDirectory, $"{request.RequestId}.json");
                    if (!File.Exists(resultPath))
                    {
                        WriteJsonAtomically(resultPath, ErrorResult(request, "BRIDGE_RESTARTED",
                            "Bridge restarted during this command; inspect state before retrying a mutation.", false));
                    }
                },
                File.Delete,
                ex => mailboxError = ex.Message);
        }
        catch (Exception ex)
        {
            mailboxError = ex.Message;
        }
        while (!mailboxStop.IsCancellationRequested || !transitionJournal.IsEmpty)
        {
            try
            {
                while (completions.TryPeek(out var completion))
                {
                    WriteJsonAtomically(Path.Combine(IpcOutboxDirectory, $"{completion.Result.RequestId}.json"), completion.Result);
                    File.Delete(completion.Command.Path);
                    completions.TryDequeue(out _);
                    Interlocked.Decrement(ref outstandingCommands);
                }
                while (transitionJournal.TryDequeue(out var entry))
                {
                    try
                    {
                        Directory.CreateDirectory(ReportsDirectory);
                        File.AppendAllText(Path.Combine(ReportsDirectory, "ui-transitions.jsonl"),
                            JsonSerializer.Serialize(entry, ProtocolJsonOptions) + Environment.NewLine);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref transitionJournalCount);
                    }
                }
                var state = Interlocked.Exchange(ref pendingState, null);
                if (state != null) WriteJsonAtomically(IpcStatePath, state);

                // Backpressure leaves excess requests in the inbox instead of dropping results.
                foreach (var path in Directory.EnumerateFiles(IpcInboxDirectory, "*.json"))
                {
                    if (Volatile.Read(ref outstandingCommands) >= QueueCapacity || mailboxStop.IsCancellationRequested) break;
                    var processing = Path.Combine(IpcProcessingDirectory, Path.GetFileName(path));
                    try { File.Move(path, processing); }
                    catch (IOException) { continue; }
                    try
                    {
                        long readStart = Stopwatch.GetTimestamp();
                        if (new FileInfo(processing).Length > MaxCommandBytes) throw new InvalidDataException("Oversized mailbox command.");
                        var request = JsonSerializer.Deserialize<BridgeRequestV1>(File.ReadAllText(processing), ProtocolJsonOptions);
                        requestReadTimes.Add(ElapsedMs(readStart));
                        if (request == null || !IsSafeRequestId(request.RequestId)) throw new InvalidDataException("Invalid mailbox request ID.");
                        Interlocked.Increment(ref outstandingCommands);
                        commands.Enqueue(new MailboxCommand(request, processing));
                    }
                    catch (Exception ex)
                    {
                        mailboxError = ex.Message;
                        File.Delete(processing);
                    }
                }
            }
            catch (Exception ex) { mailboxError = ex.Message; }
            mailboxStop.Token.WaitHandle.WaitOne(25);
        }
    }

    private static void PumpMailbox()
    {
        var start = Stopwatch.GetTimestamp();
        int steps = 0;
        while (ElapsedMs(start) < configuration.WorkBudgetMs && steps++ < configuration.MaxPlacementChecksPerFrame)
        {
            if (activeCommand == null && !commands.TryDequeue(out activeCommand)) break;
            var command = activeCommand;
            var request = command.Request;
            using var timing = Measure(request.Kind);
            try
            {
                BridgeResultV1? result;
                if (request.ProtocolVersion != ProtocolVersion || request.DeadlineAtUtc == default || request.DeadlineAtUtc <= DateTime.UtcNow)
                {
                    result = activeJob != null && request.Kind is "upgrade_tower" or "export_checkpoints"
                        ? ErrorResult(request, "SUBMISSION_OUTCOME_UNKNOWN", "The command deadline expired after execution began; inspect state before repeating the operation.", false)
                        : HandleRequest(request);
                }
                else if (request.Kind == "find_placement_spots")
                {
                    activeJob ??= RunPlacementSearch(request);
                    if (!activeJob.MoveNext()) throw new InvalidOperationException("Placement search ended without a result.");
                    result = activeJob.Current;
                    if (result == null) continue;
                }
                else if (request.Kind == "upgrade_tower")
                {
                    activeJob ??= RunUnifiedUpgradeTower(request);
                    if (!activeJob.MoveNext()) throw new InvalidOperationException("Upgrade sequence ended without a result.");
                    result = activeJob.Current;
                    if (result == null) break;
                }
                else if (request.Kind is "export_checkpoints" or "import_checkpoints")
                {
                    activeJob ??= request.Kind == "export_checkpoints"
                        ? RunExportCheckpoints(request)
                        : RunImportCheckpoints(request);
                    if (!activeJob.MoveNext()) throw new InvalidOperationException("Checkpoint transfer ended without a result.");
                    result = activeJob.Current;
                    if (result == null) break;
                }
                else result = HandleRequest(request);
                FinishCommand(command, result);
            }
            catch (Exception ex)
            {
                FinishCommand(command, ErrorResult(request, "COMMAND_FAILED", $"{ex.GetType().Name}: {ex.Message}", false));
            }
        }
    }

    private static void FinishCommand(MailboxCommand command, BridgeResultV1 result)
    {
        activeJob?.Dispose();
        activeJob = null;
        activeCommand = null;
        completions.Enqueue(new MailboxCompletion(command, result));
    }

    private static BridgeProgressV1 BuildRoundProgress()
    {
        UpdateUiState(false);
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame() || inGame.bridge?.Simulation == null)
        {
            progressSimulation = IntPtr.Zero;
            matchId = null;
            return new BridgeProgressV1 { ObservedAtUtc = DateTime.UtcNow, Ui = uiState };
        }
        var bridge = inGame.bridge;
        if (progressSimulation != bridge.Simulation.Pointer || matchId == null)
        {
            progressSimulation = bridge.Simulation.Pointer;
            matchId = Guid.NewGuid().ToString("N");
        }
        bool active = bridge.AreRoundsActive();
        bool paused = isMatchPaused || TimeManager.gamePaused || UnityEngine.Time.timeScale == 0f;
        return new BridgeProgressV1
        {
            ObservedAtUtc = DateTime.UtcNow,
            MatchId = matchId,
            ActiveGame = true,
            Ui = uiState,
            GameStatus = inGame.MatchLost ? "defeat" : inGame.WaitingForVictoryScreen ? "victory" : paused ? "paused" : active ? "round_in_progress" : "in_game",
            Round = bridge.GetCurrentRound() + 1,
            RoundActive = active,
            CanStartRound = bridge.CanSendNextRound(),
            Cash = inGame.GetCash(),
            Lives = inGame.GetHealth()
        };
    }

    private static bool ShouldCheckpoint(int round) => configuration.CheckpointMode is "every_round" or "assisted" ||
        (configuration.CheckpointMode == "selected_rounds" && Array.IndexOf(configuration.CheckpointRounds, round) >= 0);


    private static bool IsAssistedAnchor(int nextRound) => nextRound > 1 && (nextRound - 1) % 5 == 0;
    private static void TrimRoundCheckpoints()
    {
        if (configuration.CheckpointMode == "assisted" && roundJsonCheckpoints.Count > 1)
        {
            int latest = roundJsonCheckpoints.Keys.Max();
            foreach (int round in roundJsonCheckpoints.Keys.Where(round => round != latest && !IsAssistedAnchor(round)).ToArray())
            {
                roundJsonCheckpoints.Remove(round);
                roundCheckpointTimestamps.Remove(round);
                roundCheckpointFidelity.Remove(round);
            }
        }
        while (roundJsonCheckpoints.Count > configuration.MaxRoundCheckpoints)
        {
            int oldest = roundCheckpointTimestamps.OrderBy(pair => pair.Value).First().Key;
            roundJsonCheckpoints.Remove(oldest);
            roundCheckpointTimestamps.Remove(oldest);
            roundCheckpointFidelity.Remove(oldest);
        }
    }

    private static void TrimCustomCheckpoints()
    {
        while (customJsonCheckpoints.Count > MaxCustomCheckpoints)
        {
            string oldest = customCheckpointTimestamps.OrderBy(pair => pair.Value).First().Key;
            customJsonCheckpoints.Remove(oldest);
            customCheckpointTimestamps.Remove(oldest);
            customCheckpointFidelity.Remove(oldest);
        }
    }

    private static BridgeResultV1 HandleConfigureBridge(BridgeRequestV1 request)
    {
        try
        {
            var payload = request.Payload;
            if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a configuration object.");
            foreach (var property in payload.EnumerateObject())
                if (property.Name is not ("checkpointMode" or "checkpointRounds" or "maxRoundCheckpoints" or "workBudgetMs" or "maxPlacementChecksPerFrame"))
                    throw new ArgumentException($"Unknown setting: {property.Name}");
            string mode = payload.TryGetProperty("checkpointMode", out var m) ? m.GetString()! : configuration.CheckpointMode;
            int[] rounds = payload.TryGetProperty("checkpointRounds", out var r) ? r.EnumerateArray().Select(x => x.GetInt32()).Distinct().OrderBy(x => x).ToArray() : configuration.CheckpointRounds;
            int retained = payload.TryGetProperty("maxRoundCheckpoints", out var c) ? c.GetInt32() : configuration.MaxRoundCheckpoints;
            double budget = payload.TryGetProperty("workBudgetMs", out var b) ? b.GetDouble() : configuration.WorkBudgetMs;
            int checks = payload.TryGetProperty("maxPlacementChecksPerFrame", out var p) ? p.GetInt32() : configuration.MaxPlacementChecksPerFrame;
            if (mode is not ("manual" or "every_round" or "selected_rounds" or "assisted") || rounds.Length > 100 || rounds.Any(x => x < 1 || x > 10000) || retained < 1 || retained > 100 || !double.IsFinite(budget) || budget < 0.25 || budget > 8 || checks < 1 || checks > 64)
                throw new ArgumentException("Invalid configuration range.");
            configuration = new BridgeConfiguration { CheckpointMode = mode, CheckpointRounds = rounds, MaxRoundCheckpoints = retained, WorkBudgetMs = budget, MaxPlacementChecksPerFrame = checks };
            TrimRoundCheckpoints();
            ModHelper.Msg<AgentBridgeMod>($"Research bridge configuration: {mode}, retain {retained}, budget {budget}ms, placement checks {checks}.");
            return SuccessResult(request, configuration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return ErrorResult(request, "INVALID_ARGUMENT", ex.Message, false);
        }
    }

    private sealed class Samples
    {
        private readonly double[] values;
        private int next;
        private int count;
        public Samples(int capacity) { values = new double[capacity]; }
        public void Add(double value)
        {
            lock (values)
            {
                values[next] = value;
                next = (next + 1) % values.Length;
                count = Math.Min(count + 1, values.Length);
            }
        }
        public object Snapshot()
        {
            double[] sorted;
            lock (values)
            {
                sorted = new double[count];
                Array.Copy(values, sorted, count);
            }
            Array.Sort(sorted);
            int n = sorted.Length;
            double Percentile(double fraction) => n == 0 ? 0 : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(n * fraction) - 1, 0, n - 1)], 3);
            return new { Count = n, P50Ms = Percentile(.5), P95Ms = Percentile(.95), P99Ms = Percentile(.99), MaxMs = Percentile(1) };
        }
    }

    private static readonly Samples frameTimes = new(2048);
    private static readonly Dictionary<string, Samples> phaseTimes = new(StringComparer.Ordinal);
    private static readonly List<PhaseDuration> framePhases = new(16);
    private static readonly Queue<object> frameSpikes = new();
    private static long lastFrameTimestamp;
    private static long frameNumber;
    private readonly record struct PhaseDuration(string Operation, double Milliseconds);
    private readonly struct Measurement : IDisposable
    {
        private readonly string name;
        private readonly long start;
        public Measurement(string name) { this.name = name; start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            double ms = ElapsedMs(start);
            if (!phaseTimes.TryGetValue(name, out var samples))
            {
                if (phaseTimes.Count >= 64) return;
                phaseTimes[name] = samples = new Samples(512);
            }
            samples.Add(ms);
            if (framePhases.Count < 32) framePhases.Add(new PhaseDuration(name, Math.Round(ms, 3)));
        }
    }
    private static Measurement Measure(string name) => new(name);
    private static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    private static void RecordFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (lastFrameTimestamp != 0)
        {
            double elapsed = (now - lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            frameTimes.Add(elapsed);
            if (elapsed >= 32)
            {
                if (frameSpikes.Count == 64) frameSpikes.Dequeue();
                frameSpikes.Enqueue(new { Frame = frameNumber, ObservedAtUtc = DateTime.UtcNow, IntervalMs = Math.Round(elapsed, 3), PreviousUpdateOperations = framePhases.ToArray() });
            }
        }
        framePhases.Clear();
        lastFrameTimestamp = now;
        frameNumber++;
    }
    private static readonly Samples requestReadTimes = new(512);
    private static readonly Samples serializationTimes = new(512);
    private static readonly Samples atomicWriteTimes = new(512);
    private static object BuildPerformance() => new
    {
        CapturedAtUtc = DateTime.UtcNow,
        Frames = frameTimes.Snapshot(),
        Operations = phaseTimes.Select(pair => new { Name = pair.Key, Timing = pair.Value.Snapshot() }).ToArray(),
        WorkerOperations = new[] {
            new { Name = "request_read_parse", Timing = requestReadTimes.Snapshot() },
            new { Name = "json_serialize", Timing = serializationTimes.Snapshot() },
            new { Name = "atomic_write", Timing = atomicWriteTimes.Snapshot() }
        },
        RecentSpikes = frameSpikes.ToArray(),
        Configuration = configuration,
        PendingCommands = Volatile.Read(ref outstandingCommands),
        MailboxError = mailboxError,
        DroppedTransitionJournalEntries = Volatile.Read(ref droppedTransitionJournalEntries),
        Notes = "Rolling samples: 2048 frame intervals, 512 per operation, 64 spikes. Intervals include all game/OS work, not just bridge work. Command budget is cooperative, not preemption of indivisible game APIs."
    };
}
