using System.Text.Json;

namespace STS2AIAgent.Agent;

/// <summary>
/// Local patch: a combat decision may plan the rest of its turn in the act's <c>then</c> list, and
/// the following steps are played without asking the model again.
/// </summary>
/// <remarks>
/// A plan only holds while the turn is the one it was made for. Before every step the live state is
/// checked; the plan is dropped, and the model asked again, as soon as the hand holds a card the plan
/// did not know about (a draw or a generated card), a choice or selection is open, the step's card is
/// gone or unplayable, its target is dead or changed, or ending the turn would kill the player. Every
/// step still goes through <see cref="ExecuteActAsync"/>, so legality checks and narration apply.
///
/// A dropped plan is not thrown away: while the turn is still the same, the next model decision is
/// told what it had planned and what interrupted it (new cards, a dead target, or a viewer's
/// suggestion), so it revises the plan instead of starting the turn over.
/// </remarks>
internal sealed partial class AgentLoop
{
    private const int MaxPlannedSteps = 12;

    private sealed record PlannedStep(string Action, string? CardKey, int? TargetIndex, string? TargetId, string? Reason);

    private sealed class TurnPlan
    {
        public required string? RunId { get; init; }
        public required int Turn { get; init; }
        public required Queue<PlannedStep> Steps { get; init; }
        public required List<string> Hand { get; set; }
        public required int Total { get; init; }
        public required string? Summary { get; init; }
    }

    private TurnPlan? _turnPlan;
    private string? _actStateJson;
    private string? _steer;

    /// <summary>
    /// Drops the plan. While <paramref name="stateJson"/> is still the plan's turn, the next model
    /// decision gets the unplayed steps and <paramref name="why"/>, so it edits the plan, not redoes it.
    /// </summary>
    private void DropTurnPlan(string? stateJson, string why)
    {
        var plan = _turnPlan;
        _turnPlan = null;
        if (plan == null || plan.Steps.Count == 0 || stateJson == null)
        {
            return;
        }

        try
        {
            using var state = JsonDocument.Parse(stateJson);
            var root = state.RootElement;
            if (!IsCombat(root) || ReadTurn(root) != plan.Turn || Text(root, "run_id") != plan.RunId)
            {
                return;
            }
        }
        catch (JsonException)
        {
            return;
        }

        var steps = string.Join("; ", plan.Steps.Select(step => step.Action == "end_turn"
            ? "end_turn"
            : "play " + step.CardKey + (step.TargetIndex is { } target ? " -> enemy " + target : "")));
        _steer = "Your plan for this turn was interrupted: " + why
            + (plan.Summary is { } summary ? "\nWhat you planned: " + summary : "")
            + "\nSteps you had not played yet: " + steps
            + "\nContinue this turn from that plan: keep what still works and change only what the new situation needs.";
    }

    /// <summary>Called after every model act: keeps its <c>then</c> list as the plan, or clears the old one.</summary>
    private void RememberTurnPlan(string argumentsJson)
    {
        _turnPlan = null;
        var before = _actStateJson;
        if (before == null)
        {
            return;
        }

        try
        {
            using var args = ParseArgs(argumentsJson);
            if (!args.RootElement.TryGetProperty("then", out var then)
                || then.ValueKind != JsonValueKind.Array || then.GetArrayLength() == 0)
            {
                return;
            }

            using var state = JsonDocument.Parse(before);
            var root = state.RootElement;
            var action = ReadString(args, "action")?.Trim().ToLowerInvariant();
            if (!IsCombat(root) || ReadTurn(root) is not { } turn || action is not ("play_card" or "use_potion"))
            {
                return;
            }

            var hand = HandCards(root);
            if (action == "play_card")
            {
                var played = hand.FindIndex(card => card.Index == ReadInt(args, "card_index"));
                if (played < 0)
                {
                    return;
                }

                hand.RemoveAt(played);
            }

            var enemies = EnemyIds(root);
            var steps = new Queue<PlannedStep>();
            foreach (var item in then.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || steps.Count >= MaxPlannedSteps)
                {
                    break;
                }

                var stepAction = Text(item, "action")?.Trim().ToLowerInvariant();
                var reason = Text(item, "reason")?.Trim();
                if (stepAction == "end_turn")
                {
                    steps.Enqueue(new PlannedStep("end_turn", null, null, null, reason));
                    break;
                }

                var cardId = Text(item, "card_id")?.Trim();
                if (stepAction != "play_card" || string.IsNullOrEmpty(cardId))
                {
                    break; // The plan is only good up to the first step it cannot express.
                }

                var upgraded = item.TryGetProperty("upgraded", out var up) && up.ValueKind == JsonValueKind.True;
                int? target = item.TryGetProperty("target_index", out var t) && t.TryGetInt32(out var ti) ? ti : null;
                steps.Enqueue(new PlannedStep("play_card", CardKey(cardId, upgraded), target,
                    target is { } index && enemies.TryGetValue(index, out var id) ? id : null,
                    string.IsNullOrEmpty(reason) ? null : reason));
            }

            if (steps.Count > 0)
            {
                var summary = ReadString(args, "reason")?.Trim();
                _turnPlan = new TurnPlan
                {
                    RunId = Text(root, "run_id"), Turn = turn, Steps = steps,
                    Hand = hand.Select(card => card.Key).ToList(), Total = steps.Count + 1,
                    Summary = string.IsNullOrEmpty(summary) ? null : summary.Length > 300 ? summary[..300] : summary
                };
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Plays the plan's next step when it still holds; null hands the decision to the model.</summary>
    private async Task<AgentTurnResult?> TryPlannedStepAsync(
        string stateJson, CancellationToken cancellationToken, Action<string>? checkState, Action<string>? reportPhase)
    {
        var plan = _turnPlan;
        if (plan == null || plan.Steps.Count == 0)
        {
            _turnPlan = null;
            return null;
        }

        var step = plan.Steps.Peek();
        var hand = new List<HandCard>();
        string? argumentsJson = null;
        try
        {
            using var state = JsonDocument.Parse(stateJson);
            var root = state.RootElement;
            hand = HandCards(root);
            argumentsJson = StillHolds(plan, root, hand) ? StepArguments(step, root, hand) : null;
        }
        catch (JsonException)
        {
        }

        if (argumentsJson == null)
        {
            DropTurnPlan(stateJson, "new cards are in your hand, a choice is open, or a planned card or target is no longer available.");
            return null;
        }

        plan.Steps.Dequeue();
        var number = plan.Total - plan.Steps.Count;
        var reasoning = $"按本回合计划（{number}/{plan.Total}）" + (step.Reason is { } why ? "：" + why : "");
        reportPhase?.Invoke(PlayPhases.ExecutingAction);
        string? accepted = null;
        string? response = null;
        try
        {
            var outcome = await ExecuteActAsync(argumentsJson, cancellationToken, checkState,
                (action, result) => { accepted = action; response = result; });
            if (outcome.Error != null)
            {
                plan.Steps.Clear();
                _turnPlan = null;
                return null;
            }

            if (step.CardKey != null)
            {
                hand.RemoveAt(hand.FindIndex(card => card.Key == step.CardKey && card.Playable));
                plan.Hand = hand.Select(card => card.Key).ToList();
            }

            if (plan.Steps.Count == 0 || step.Action == "end_turn")
            {
                _turnPlan = null;
            }

            return new AgentTurnResult
            {
                Acted = outcome.Action, ActResultJson = outcome.ResultJson, Reasoning = reasoning,
                StateFingerprint = outcome.Fingerprint, ExecutedUnsettled = outcome.Unsettled,
                ToolRounds = 0, RequestsSpent = 0
            };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _turnPlan = null;
            throw new AgentTurnCanceledException(new AgentTurnResult
            {
                Acted = accepted, ActResultJson = response, ExecutedUnsettled = accepted != null,
                Reasoning = reasoning, RequestsSpent = 0
            }, ex, cancellationToken);
        }
        catch (AutoPlayStoppedException ex)
        {
            _turnPlan = null;
            ex.Receipt = new AgentTurnResult
            {
                Acted = accepted, ActResultJson = response, ExecutedUnsettled = accepted != null,
                Reasoning = reasoning, RequestsSpent = 0
            };
            throw;
        }
    }

    private static bool StillHolds(TurnPlan plan, JsonElement root, List<HandCard> hand)
    {
        if (!IsCombat(root) || ReadTurn(root) != plan.Turn || Text(root, "run_id") != plan.RunId)
        {
            return false;
        }

        if (root.TryGetProperty("selection", out var selection) && selection.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return false;
        }

        if (root.TryGetProperty("combat", out var combat) && combat.ValueKind == JsonValueKind.Object
            && combat.TryGetProperty("action_readiness", out var ready) && ready.ValueKind == JsonValueKind.Object)
        {
            if (ready.TryGetProperty("hand_in_card_selection", out var picking) && picking.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            if (Text(ready, "hand_mode") is { } mode && !string.Equals(mode, "Play", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Every card in hand must be one the plan already knew about; anything else was drawn or
        // made after the plan, so the model has to see it. Cards that left the hand are fine.
        var known = plan.Hand.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var card in hand)
        {
            if (!known.TryGetValue(card.Key, out var count) || count == 0)
            {
                return false;
            }

            known[card.Key] = count - 1;
        }

        return true;
    }

    private static string? StepArguments(PlannedStep step, JsonElement root, List<HandCard> hand)
    {
        if (!ReadActionNames(root).Contains(step.Action, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (step.Action == "end_turn")
        {
            var combat = root.GetProperty("combat");
            if (combat.TryGetProperty("end_turn_will_kill_player", out var fatal) && fatal.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            return JsonSerializer.Serialize(new { action = "end_turn", reason = step.Reason });
        }

        var card = hand.FirstOrDefault(item => item.Key == step.CardKey && item.Playable);
        if (card == null)
        {
            return null;
        }

        int? target = null;
        if (card.Targets != null)
        {
            if (step.TargetIndex is not { } index || !card.Targets.Contains(index))
            {
                return null;
            }

            if (step.TargetId != null && (!EnemyIds(root).TryGetValue(index, out var id) || id != step.TargetId))
            {
                return null;
            }

            target = index;
        }

        return JsonSerializer.Serialize(new { action = "play_card", card_index = card.Index, target_index = target, reason = step.Reason });
    }

    private sealed record HandCard(int Index, string Key, bool Playable, int[]? Targets);

    private static List<HandCard> HandCards(JsonElement root)
    {
        var cards = new List<HandCard>();
        if (!root.TryGetProperty("combat", out var combat) || combat.ValueKind != JsonValueKind.Object
            || !combat.TryGetProperty("hand", out var hand) || hand.ValueKind != JsonValueKind.Array)
        {
            return cards;
        }

        foreach (var card in hand.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("i", out var i) || !i.TryGetInt32(out var index))
            {
                continue;
            }

            var key = CardKey(Text(card, "card_id") ?? "?",
                card.TryGetProperty("upgraded", out var up) && up.ValueKind == JsonValueKind.True);
            var playable = !card.TryGetProperty("playable", out var p) || p.ValueKind != JsonValueKind.False;
            int[]? targets = null;
            if (Text(card, "target") != null && card.TryGetProperty("targets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                targets = list.EnumerateArray().Where(item => item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToArray();
            }

            cards.Add(new HandCard(index, key, playable, targets));
        }

        return cards;
    }

    /// <summary>Living, hittable enemies by index; a dead or untargetable one is simply absent.</summary>
    private static Dictionary<int, string> EnemyIds(JsonElement root)
    {
        var ids = new Dictionary<int, string>();
        if (!root.TryGetProperty("combat", out var combat) || combat.ValueKind != JsonValueKind.Object
            || !combat.TryGetProperty("enemies", out var enemies) || enemies.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var enemy in enemies.EnumerateArray())
        {
            if (enemy.ValueKind == JsonValueKind.Object && enemy.TryGetProperty("i", out var i) && i.TryGetInt32(out var index)
                && !(enemy.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.False)
                && !(enemy.TryGetProperty("hittable", out var hit) && hit.ValueKind == JsonValueKind.False))
            {
                ids[index] = Text(enemy, "enemy_id") ?? "?";
            }
        }

        return ids;
    }

    private static bool IsCombat(JsonElement root) =>
        string.Equals(Text(root, "screen"), "COMBAT", StringComparison.OrdinalIgnoreCase)
        && root.TryGetProperty("combat", out var combat) && combat.ValueKind == JsonValueKind.Object;

    private static int? ReadTurn(JsonElement root) =>
        root.TryGetProperty("turn", out var turn) && turn.TryGetInt32(out var value) ? value : null;

    private static string CardKey(string cardId, bool upgraded) => upgraded ? cardId + "+" : cardId;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
