using Foldy.Presentation.Update;

namespace Foldy.Presentation.Tests;

public class UpdatePromptTests
{
    [Fact]
    public void A_new_version_is_asked_once()
    {
        UpdatePrompt.ShouldAsk("1.16.0", null, UpdateMode.Auto, userIsHere: true).Should().BeTrue();
        UpdatePrompt.ShouldAsk("1.16.0", "1.16.0", UpdateMode.Auto, userIsHere: true).Should().BeFalse("[나중에]를 눌렀으면 같은 버전은 다시 묻지 않는다");
    }

    [Fact]
    public void A_newer_version_than_the_one_asked_is_asked_again()
    {
        UpdatePrompt.ShouldAsk("1.16.1", "1.16.0", UpdateMode.Auto, userIsHere: true).Should().BeTrue();
        UpdatePrompt.ShouldAsk("1.16.1", "1.16.0", UpdateMode.NotifyOnly, userIsHere: true).Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_asked_without_a_ready_update_or_when_updates_are_off()
    {
        UpdatePrompt.ShouldAsk(null, null, UpdateMode.Auto, userIsHere: true).Should().BeFalse();
        UpdatePrompt.ShouldAsk("", null, UpdateMode.Auto, userIsHere: true).Should().BeFalse();
        UpdatePrompt.ShouldAsk("1.16.0", null, UpdateMode.Off, userIsHere: true).Should().BeFalse();
    }

    [Fact]
    public void It_waits_until_the_user_is_back_and_not_already_scheduled()
    {
        UpdatePrompt.ShouldAsk("1.16.0", null, UpdateMode.Auto, userIsHere: false).Should().BeFalse("다른 프로그램을 쓰는 중이면 Foldy로 돌아올 때 묻는다");
        UpdatePrompt.ShouldAsk("1.16.0", null, UpdateMode.Auto, userIsHere: true, alreadyScheduled: true).Should().BeFalse();
    }
}
