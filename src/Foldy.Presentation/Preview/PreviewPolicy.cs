namespace Foldy.Presentation.Preview;

/// <summary>미리보기 가능 여부 판정 결과.</summary>
/// <param name="Provider">내용을 준비할 공급자. Status가 Ready일 때만 있다.</param>
/// <param name="CanForce">[미리보기]를 눌러 보호를 풀 수 있는지 (네트워크 드라이브).</param>
public sealed record PreviewDecision(PreviewStatus Status, string StatusText, IPreviewProvider? Provider, bool CanForce = false)
{
    public bool IsReady => Status == PreviewStatus.Ready && Provider is not null;
}

/// <summary>
/// 선택 규칙 (DESIGN.md 느린 드라이브 규칙 + 설계서 9장). 판정 순서:
/// 폴더 → 클라우드 전용(이미지는 셸 썸네일만) → 네트워크 드라이브(사용자가 누르기 전까지) → 공급자 선택 → 크기 제한.
/// 디스크·네트워크에 접근하지 않는 순수 함수라 UI 스레드에서 불러도 된다.
/// </summary>
public static class PreviewPolicy
{
    public static PreviewDecision Evaluate(PreviewRequest request, PreviewProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);

        if (request.IsFolder)
        {
            return new PreviewDecision(PreviewStatus.NotPreviewable, PreviewMessages.Folder, null);
        }

        var provider = registry.Select(request);
        var cloud = PreviewGuards.IsCloudOnly(request.Attributes);

        // 클라우드 전용: 내용을 읽으면 다운로드가 시작된다. 이미지만 셸이 가진 썸네일로 보여 준다.
        if (cloud && provider?.Kind != PreviewKind.Image)
        {
            return new PreviewDecision(PreviewStatus.CloudOnly, PreviewMessages.CloudOnly, null);
        }

        if (request.IsRemote && !request.UserForced)
        {
            return new PreviewDecision(PreviewStatus.NetworkDrive, PreviewMessages.NetworkDrive, null, CanForce: true);
        }

        if (provider is null)
        {
            return new PreviewDecision(PreviewStatus.NotPreviewable, PreviewMessages.NotPreviewable, null);
        }

        if (provider.MaxBytes > 0 && request.Length > provider.MaxBytes && !cloud)
        {
            return new PreviewDecision(PreviewStatus.TooLarge, PreviewMessages.TooLarge(request.Length, provider.MaxBytes), null);
        }

        return new PreviewDecision(PreviewStatus.Ready, "", provider);
    }
}
