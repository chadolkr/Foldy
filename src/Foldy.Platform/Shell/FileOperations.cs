using System.Runtime.InteropServices;
using Foldy.Platform.Interop;

namespace Foldy.Platform.Shell;

public enum FileOpKind { Copy, Move, Delete, Recycle, Rename, NewFolder }

/// <summary>IFileOperation 요청 하나.</summary>
/// <param name="NoConfirm">확인 창 없이 실행 (테스트·자동화 전용, 화면에서는 쓰지 않는다).</param>
public sealed record FileOpRequest(FileOpKind Kind, IReadOnlyList<string> Sources, string? Destination = null, string? NewName = null, bool NoConfirm = false);

/// <param name="ErrorCode">실패했을 때 Windows 오류 코드(HRESULT, 없으면 0). 쉬운 말을 고르는 데 쓴다 (FileOpErrors).</param>
public sealed record FileOpOutcome(bool Succeeded, bool Aborted, string? Error, string? CreatedPath, int ErrorCode = 0);

/// <summary>진행 보고 (작업 단위 총량/완료량, 현재 항목 이름).</summary>
public delegate void FileOpProgressCallback(long done, long total, string? current);

/// <summary>
/// 탐색기와 같은 파일 작업 (IFileOperation, 설계서 5.4). 탐색기와 같은 확인·충돌·UAC 창을 쓰되
/// 진행 창은 숨기고(FOF_SILENT) 진행은 콜백으로 Foldy 작업 패널에 보낸다.
/// 작업마다 전용 STA 스레드에서 돈다 (PerformOperations는 블로킹하며 메시지 펌프를 돌린다).
/// </summary>
public static class FileOperations
{
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_RENAMEONCOLLISION = 0x0008;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOFX_SHOWELEVATIONPROMPT = 0x00040000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;
    private const int HrCancelled = unchecked((int)0x800704C7);

    // 사용자가 멈춘 것: ERROR_CANCELLED, COPYENGINE_E_USER_CANCELLED, COPYENGINE_E_CANCELLED
    private static bool IsCancel(int hr) => hr is HrCancelled or unchecked((int)0x80270000) or unchecked((int)0x80270001);

    public static Task<FileOpOutcome> RunAsync(FileOpRequest req, IntPtr owner, FileOpProgressCallback? progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<FileOpOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(Run(req, owner, progress, ct));
            }
            catch (COMException ex) when (IsCancel(ex.HResult))
            {
                tcs.TrySetResult(new FileOpOutcome(false, true, null, null));
            }
            catch (COMException ex)
            {
                tcs.TrySetResult(new FileOpOutcome(false, false, ex.Message, null, ex.HResult));
            }
            catch (Exception ex)
            {
                // 이 스레드에서 새는 예외는 Foldy 전체를 끝낸다 (전역 처리기는 화면 스레드만 살린다).
                // 어떤 예외든 실패 결과로 돌려 작업 패널에 알린다 (사용자 제보 2026-11-23)
                tcs.TrySetResult(new FileOpOutcome(false, false, ex.Message, null));
            }
        })
        {
            IsBackground = true,
            Name = $"FileOperation {req.Kind}",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static FileOpOutcome Run(FileOpRequest req, IntPtr owner, FileOpProgressCallback? progress, CancellationToken ct)
    {
        var op = (IFileOperation)new FileOperationClass();
        try
        {
            var flags = FOF_SILENT | FOF_NOCONFIRMMKDIR | FOFX_SHOWELEVATIONPROMPT | FOFX_ADDUNDORECORD;
            flags |= req.Kind switch
            {
                // 지우기는 Windows 오류 창("폴더 액세스가 거부됨" 등) 대신 Foldy가 쉬운 말로 알린다. UAC 창은 FOFX_SHOWELEVATIONPROMPT로 그대로 뜬다
                FileOpKind.Recycle => FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOERRORUI,
                FileOpKind.Delete => FOF_NOERRORUI,
                FileOpKind.NewFolder => FOF_RENAMEONCOLLISION | FOF_ALLOWUNDO,
                _ => FOF_ALLOWUNDO,
            };
            if (req.NoConfirm)
            {
                flags |= FOF_NOCONFIRMATION;
            }

            op.SetOperationFlags(flags);
            if (owner != IntPtr.Zero)
            {
                op.SetOwnerWindow(owner);
            }

            var sink = new Sink(progress, ct);
            var cookie = op.Advise(sink);
            try
            {
                switch (req.Kind)
                {
                    case FileOpKind.Copy:
                    case FileOpKind.Move:
                    {
                        var dest = Native.ShellItem(req.Destination ?? throw new ArgumentException("대상 폴더가 없습니다"));
                        foreach (var s in req.Sources)
                        {
                            var item = Native.ShellItem(s);
                            if (req.Kind == FileOpKind.Copy)
                            {
                                op.CopyItem(item, dest, null, null);
                            }
                            else
                            {
                                op.MoveItem(item, dest, null, null);
                            }
                        }

                        break;
                    }

                    case FileOpKind.Delete:
                    case FileOpKind.Recycle:
                        foreach (var s in req.Sources)
                        {
                            op.DeleteItem(Native.ShellItem(s), null);
                        }

                        break;

                    case FileOpKind.Rename:
                        op.RenameItem(Native.ShellItem(req.Sources[0]), req.NewName ?? throw new ArgumentException("새 이름이 없습니다"), null);
                        break;

                    case FileOpKind.NewFolder:
                        op.NewItem(Native.ShellItem(req.Destination ?? req.Sources[0]), (uint)FileAttributes.Directory, req.NewName ?? "새 폴더", null, null);
                        break;
                }

                try
                {
                    op.PerformOperations();
                }
                catch (COMException ex) when (!IsCancel(ex.HResult))
                {
                    // 항목 하나가 실패하면 전체가 실패 코드로 끝나기도 한다: 그 항목의 코드가 더 알맞다
                    var code = sink.FirstError is { } first && !IsCancel(first) ? first : ex.HResult;
                    return new FileOpOutcome(false, false, Marshal.GetExceptionForHR(code)?.Message ?? ex.Message, sink.CreatedPath, code);
                }

                var aborted = op.GetAnyOperationsAborted();
                if (sink.FirstError is { } hr && !IsCancel(hr))
                {
                    // 오류 창 없이 실패한 것은 취소가 아니라 실패로 알린다 (GetAnyOperationsAborted가 참이어도)
                    return new FileOpOutcome(false, false, Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}", sink.CreatedPath, hr);
                }

                return new FileOpOutcome(!aborted, aborted, null, sink.CreatedPath);
            }
            finally
            {
                op.Unadvise(cookie);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(op);
        }
    }

    [ComVisible(true)]
    private sealed class Sink(FileOpProgressCallback? progress, CancellationToken ct) : IFileOperationProgressSink
    {
        private string? _current;
        private long _total;
        private long _done;

        public int? FirstError { get; private set; }

        public string? CreatedPath { get; private set; }

        private int Check() => ct.IsCancellationRequested ? HrCancelled : 0;

        private int Pre(IShellItem item)
        {
            _current = Native.DisplayName(item);
            progress?.Invoke(_done, _total, _current);
            return Check();
        }

        private int Post(int hr, IShellItem? created)
        {
            if (hr < 0 && FirstError is null)
            {
                FirstError = hr;
            }

            if (created is not null)
            {
                CreatedPath ??= Native.FileSysPath(created);
            }

            return Check();
        }

        public int StartOperations() => Check();

        public int FinishOperations(int hrResult) => 0;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) => Pre(psiItem);

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) => Post(hrRename, psiNewlyCreated);

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => Pre(psiItem);

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => Post(hrMove, psiNewlyCreated);

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => Pre(psiItem);

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => Post(hrCopy, psiNewlyCreated);

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem) => Pre(psiItem);

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated) => Post(hrDelete, null);

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) => Check();

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => Post(hrNew, psiNewItem);

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
            _total = iWorkTotal;
            _done = iWorkSoFar;
            progress?.Invoke(_done, _total, _current);
            return Check();
        }

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }
}
