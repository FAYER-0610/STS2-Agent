using STS2AIAgent.Llm;

namespace STS2AIAgent.Agent;

/// <summary>
/// Local patch: viewer suggestions steer the running decision instead of queueing behind it.
/// </summary>
/// <remarks>
/// The narrator keeps a short board of recent viewer suggestions. It is read at three points: at the
/// start of a decision (shown with the state), before every planned combat step (a new suggestion
/// hands the unplayed plan back to the model to edit), and between tool rounds of a running decision
/// (the new suggestion is appended to the same conversation, so the model carries on from its own
/// reasoning). Text being generated cannot be edited; a suggestion lands at the next of these points.
/// </remarks>
internal sealed partial class AgentLoop
{
    private ViewerGuidance? _guidance;
    private long _guidanceSeen = -1;

    /// <summary>Where suggestions come from; the narrator by default, replaceable in tests.</summary>
    internal Func<CancellationToken, Task<ViewerGuidance?>> GuidanceSource { get; set; } = NarrationGate.ReadGuidanceAsync;

    private async Task<ViewerGuidance?> ReadGuidanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GuidanceSource(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // Advice is optional; a broken narrator never costs a decision.
        }
    }

    private async Task SteerWithNewGuidanceAsync(List<LlmMessage> messages, CancellationToken cancellationToken)
    {
        var board = await ReadGuidanceAsync(cancellationToken);
        if (board is not { Items.Count: > 0 } || board.Version == _guidanceSeen)
        {
            return;
        }

        var fresh = (int)Math.Clamp(board.Version - Math.Max(_guidanceSeen, 0), 1, board.Items.Count);
        messages.Add(LlmMessage.User(
            "A viewer suggestion just arrived while you were thinking (viewer's own words):\n" + board.Lines(fresh)
            + "\n" + ViewerGuidanceRule + " Carry on from where you are."));
        _guidance = board;
        _guidanceSeen = board.Version;
    }
}
