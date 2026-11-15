using System.Text;
using Kazo.Ai.Chat;
using Kazo.Ai.Http;
using Kazo.Ai.Providers;

namespace Kazo.Ai.Race;

/// <summary>
/// Asks the same question to several models at once and keeps the best answer. Every model streams
/// in parallel; when one finishes, its answer is scored (<see cref="IResponseScorer"/>) and becomes
/// the leader when it beats the current leader by at least <see cref="MinLeaderDelta"/> points, so
/// the person sees a first answer quickly and a better one replaces it as it comes.
/// </summary>
public sealed class ModelRace
{
    private readonly Func<string, IChatProvider?> _providers;
    private readonly IResponseScorer _scorer;
    private readonly TimeProvider _time;

    /// <summary>Creates a race.</summary>
    /// <param name="providers">Finds a provider by its id.</param>
    /// <param name="scorer">Scores the answers.</param>
    /// <param name="time">The clock (the tests use a fake).</param>
    public ModelRace(Func<string, IChatProvider?> providers, IResponseScorer scorer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(scorer);
        _providers = providers;
        _scorer = scorer;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets or sets the points a new answer must win by to replace the leader (1 to 50, default 8).</summary>
    public double MinLeaderDelta { get; init; } = 8;

    /// <summary>Gets or sets the time limit of each model (default 2 minutes).</summary>
    public TimeSpan ModelTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Runs the race.</summary>
    /// <param name="models">The models (at least one).</param>
    /// <param name="request">The request; its <see cref="ChatRequest.Model"/> is replaced for each model.</param>
    /// <param name="callbacks">What to tell along the way.</param>
    /// <param name="transform">Applied to each answer before it is scored (the response modules).</param>
    /// <param name="cancellationToken">Stops every model.</param>
    /// <returns>The outcome.</returns>
    public async Task<RaceResult> RunAsync(
        IReadOnlyList<ModelRef> models,
        ChatRequest request,
        RaceCallbacks? callbacks = null,
        Func<string, string>? transform = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(request);
        if (models.Count == 0)
        {
            throw new ArgumentException("A race needs at least one model.", nameof(models));
        }

        callbacks ??= new RaceCallbacks();
        double minDelta = Math.Clamp(MinLeaderDelta, 1, 50);
        long start = _time.GetTimestamp();
        var gate = new object();
        var rankings = new List<RaceRanking>();
        RaceLeader? leader = null;
        int responded = 0;
        callbacks.OnRaceStart?.Invoke(models.Count);

        async Task RunOneAsync(ModelRef model)
        {
            long modelStart = _time.GetTimestamp();
            string name = model.ToString();
            var text = new StringBuilder();
            string? error = null;
            AiErrorKind? kind = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelTimeout);
            try
            {
                IChatProvider provider = _providers(model.ProviderId)
                    ?? throw new AiServiceException(AiErrorKind.NotFound, $"Unknown provider '{model.ProviderId}'.");
                await foreach (ChatDelta delta in provider.StreamAsync(request with { Model = model.ModelId }, timeout.Token))
                {
                    if (delta.Text.Length > 0)
                    {
                        text.Append(delta.Text);
                        callbacks.OnModelText?.Invoke(model, text.ToString());
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = "Timed out";
                kind = AiErrorKind.Timeout;
            }
            catch (AiServiceException exception)
            {
                error = exception.Message;
                kind = exception.Kind;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string content = transform is null ? text.ToString() : transform(text.ToString());
            bool success = error is null && content.Trim().Length > 0;
            if (error is null && !success)
            {
                error = "Empty answer";
                kind = AiErrorKind.Unknown;
            }

            double score = success ? _scorer.Score(content, request.LastUserText) : 0;
            long duration = (long)_time.GetElapsedTime(modelStart).TotalMilliseconds;
            RaceModelResult result;
            RaceLeader? newLeader = null;
            lock (gate)
            {
                responded++;
                rankings.Add(new RaceRanking(name, score, duration, success, error, content.Length, success ? content : null));
                result = new RaceModelResult(name, score, duration, success, error, content.Length, responded, models.Count) { ErrorKind = kind };
                if (success && (leader is null || score >= leader.Score + minDelta))
                {
                    leader = new RaceLeader(name, score, duration, content);
                    newLeader = leader;
                }
            }

            callbacks.OnModelResult?.Invoke(result);
            if (newLeader is not null)
            {
                callbacks.OnLeaderChange?.Invoke(newLeader);
            }
        }

        await Task.WhenAll(models.Select(RunOneAsync));

        var ordered = rankings
            .OrderByDescending(ranking => ranking.Success)
            .ThenByDescending(ranking => ranking.Score)
            .ThenBy(ranking => ranking.DurationMs)
            .ToList();
        RaceRanking? best = ordered.FirstOrDefault(ranking => ranking.Success);
        long total = (long)_time.GetElapsedTime(start).TotalMilliseconds;
        return new RaceResult(
            best?.Content ?? string.Empty,
            best is null ? null : new RaceWinner(best.Model, best.Score, best.DurationMs),
            new RaceSummary(models.Count, ordered.Count(ranking => ranking.Success), total, ordered));
    }

    /// <summary>
    /// Explains why every model of a race failed, from the most telling common cause.
    /// </summary>
    /// <param name="results">The results.</param>
    /// <returns>The kind most of them share, or <see cref="AiErrorKind.Unknown"/>.</returns>
    public static AiErrorKind CommonFailure(IEnumerable<RaceModelResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results
            .Where(result => !result.Success && result.ErrorKind is not null)
            .GroupBy(result => result.ErrorKind!.Value)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault(AiErrorKind.Unknown);
    }
}
