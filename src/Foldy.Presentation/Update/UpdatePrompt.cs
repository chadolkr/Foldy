namespace Foldy.Presentation.Update;

/// <summary>
/// 새 버전이 준비되면 창으로 "지금 업데이트할까요?"를 물을지 정한다 (1.16.0, 사용자 의견: 상태 칸 아래 한 줄은 잘 안 보임).
/// 같은 버전은 한 번만 묻고([나중에]를 눌러도 다시 묻지 않음, 상태 칸 단추는 남음), 더 새 버전이 나오면 다시 묻는다.
/// 설정에서 업데이트를 껐거나, 사용자가 다른 창을 보거나 다른 대화 상자를 쓰는 중이면 지금은 묻지 않고 Foldy로 돌아올 때 묻는다.
/// </summary>
public static class UpdatePrompt
{
    public static bool ShouldAsk(string? readyVersion, string? askedVersion, UpdateMode mode, bool userIsHere, bool alreadyScheduled = false) =>
        readyVersion is { Length: > 0 }
        && mode != UpdateMode.Off
        && !alreadyScheduled
        && userIsHere
        && !string.Equals(readyVersion, askedVersion, StringComparison.OrdinalIgnoreCase);
}
