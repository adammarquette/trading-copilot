using MarqSpec.TradingCopilot.Api.Chat;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Chat;

/// <summary>
/// <see cref="ChatTurnScope"/> (gh#1135 of gh#1059, R-7 / gh#471) — which conversation the request is taking a turn
/// in, the one piece of turn identity a chat <b>write</b> tool needs and cannot otherwise reach, since
/// <c>IChatTool.ExecuteAsync</c> receives only the model's own JSON.
/// </summary>
/// <remarks>
/// The property that matters is the <b>null before entry</b>: a tool reads this as a hard dependency and fails closed
/// when it is still null, so a broken wiring refuses to write rather than writing an <i>unattributed</i> rule. A
/// scope that defaulted to some conversation would make that guard unreachable.
/// </remarks>
public class ChatTurnScopeTests
{
    [Fact]
    public void ConversationId_ShouldBeNull_BeforeATurnIsEntered() =>
        new ChatTurnScope().ConversationId.Should().BeNull(
            "a write tool fails closed on a null scope, so a fresh scope must genuinely read null");

    [Fact]
    public void EnterConversation_ShouldExposeTheConversation_ForTheRestOfTheRequest()
    {
        Guid conversation = Guid.NewGuid();
        ChatTurnScope scope = new();

        scope.EnterConversation(conversation);

        scope.ConversationId.Should().Be(conversation);
    }

    [Fact]
    public void EnterConversation_ShouldTakeTheLatestConversation_WhenCalledAgain()
    {
        // Not a supported flow -- the turn endpoint enters once -- but pinned rather than left undefined: the value a
        // tool stamps as provenance must be the conversation the CURRENT turn runs in, never a stale earlier one.
        ChatTurnScope scope = new();
        scope.EnterConversation(Guid.NewGuid());
        Guid second = Guid.NewGuid();

        scope.EnterConversation(second);

        scope.ConversationId.Should().Be(second);
    }
}
