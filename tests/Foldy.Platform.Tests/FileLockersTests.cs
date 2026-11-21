using AwesomeAssertions;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>"누가 이 파일을 쓰고 있나요?": Restart Manager에 묻기만 한다 (프로그램을 끄지 않는다).</summary>
public sealed class FileLockersTests
{
    [Fact]
    public void Finds_the_process_holding_a_file_and_says_it_plainly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "foldy-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "열린 문서.xlsx");
        try
        {
            using (var held = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var who = FileLockers.WhoUses([dir]); // 폴더를 주면 안의 파일을 물어본다
                who.Should().Contain(l => l.ProcessId == Environment.ProcessId);
                FileLockers.Describe(who).Should().Contain("쓰고 있어요").And.Contain("닫고 다시");
            }

            FileLockers.WhoUses([file]).Should().NotContain(l => l.ProcessId == Environment.ProcessId, "닫은 뒤에는 아무도 안 쓴다");
            FileLockers.Describe([]).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
