using Foldy.Core.FileSystem;
using Foldy.Core.Space;
using Foldy.Presentation.Files;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Services;
using Foldy.Presentation.Shell;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Tests;

/// <summary>공간이 모자라면 시작 전에 멈추고, [공간 늘리기]·[다시 시도]로 이어 한다 (사용자 원칙 2026-11-22).</summary>
public class SpaceTests
{
    private static readonly SpaceCheck Lack = new(SpaceProblem.NotEnoughSpace, 12L << 30, 3L << 30, @"D:\");

    [Fact]
    public void Message_says_how_much_is_needed_and_left_on_which_drive()
    {
        var text = SpaceText.Explain(Lack);
        text.Should().Contain("D:").And.Contain(FileItem.FormatBytes(12L << 30)).And.Contain(FileItem.FormatBytes(3L << 30));
        SpaceText.Brief(Lack).Should().Contain(FileItem.FormatBytes(12L << 30));
        SpaceText.Explain(Lack with { Free = -1 }).Should().NotContain(FileItem.FormatBytes(0), "모르는 남은 공간을 0으로 말하지 않는다");
        SpaceText.Explain(new SpaceCheck(SpaceProblem.Fat32TooBig, 5L << 30, 100L << 30, @"E:\")).Should().Contain("FAT32").And.Contain("E:");
        SpaceText.Explain(new SpaceCheck(SpaceProblem.NotEnoughSpace, 10, 1, null, "example.com")).Should().Contain("example.com");
    }

    [Fact]
    public void Failed_job_offers_free_up_and_retry()
    {
        var job = new JobViewModel("x", new InlineDispatcher()) { Retry = () => Task.CompletedTask };
        job.Complete(FileOpResult.NoSpace(Lack));
        job.State.Should().Be(JobState.Failed);
        job.CanFreeUp.Should().BeTrue();
        job.CanRetry.Should().BeTrue();
        job.Detail.Should().Be(SpaceText.Brief(Lack));

        var remote = new JobViewModel("y", new InlineDispatcher()) { Retry = () => Task.CompletedTask };
        remote.Complete(FileOpResult.NoSpace(Lack with { Root = null, Server = "example.com" }));
        remote.CanFreeUp.Should().BeFalse("서버는 폴더 분석을 열 수 없다");
        remote.CanRetry.Should().BeTrue();

        var other = new JobViewModel("z", new InlineDispatcher()) { Retry = () => Task.CompletedTask };
        other.Complete(FileOpResult.Fail("잠김"));
        other.CanRetry.Should().BeFalse("공간 문제가 아닌 실패에는 단추가 없다");
    }

    [Fact]
    public async Task Copy_that_lacks_space_can_free_up_then_retry()
    {
        var ops = new FakeOps();
        var shell = new ShellViewModel(new TabEnvironment(new EmptyProvider(), new InlineDispatcher()), new ShellServices(ops, null!, null!));
        string? opened = null;
        shell.FreeUpSpace = root => opened = root;

        await shell.TransferAsync([@"C:\big"], @"D:\backup", move: false);
        var job = shell.Jobs.Should().ContainSingle().Subject;
        job.State.Should().Be(JobState.Failed);
        shell.Status.Should().EndWith(SpaceText.Explain(Lack));

        shell.FreeUpSpaceFor(job);
        opened.Should().Be(@"D:\");

        ops.HasRoom = true;
        await shell.RetryAsync(job);
        ops.Calls.Should().Be(2);
        shell.Jobs.Should().ContainSingle().Which.State.Should().Be(JobState.Done);
    }

    private sealed class FakeOps : IFileOperationService
    {
        public bool HasRoom { get; set; }

        public int Calls { get; private set; }

        public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(HasRoom ? FileOpResult.Ok() : FileOpResult.NoSpace(Lack));
        }

        public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
            CopyAsync(sources, destinationFolder, progress, ct);

        public Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());

        public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) => Task.FromResult(FileOpResult.Ok());
    }

    private sealed class EmptyProvider : IFileSystemProvider
    {
        public ProviderId Id => ProviderId.Local;

        public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return [];
        }
    }
}
