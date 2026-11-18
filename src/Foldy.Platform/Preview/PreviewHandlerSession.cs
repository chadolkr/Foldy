using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Foldy.Platform.Preview;

/// <summary>처리기가 실패했다. 호출한 쪽은 상태 줄로 "미리 볼 수 없음"을 보여 준다. 세션은 이미 정리됐다.</summary>
public sealed class PreviewHandlerException : Exception
{
    public PreviewHandlerException()
    {
    }

    public PreviewHandlerException(string message)
        : base(message)
    {
    }

    public PreviewHandlerException(string message, Exception? inner)
        : base(message, inner)
    {
    }

    public Guid Clsid { get; init; }

    /// <summary>이번 실패로 이 처리기가 블랙리스트에 올랐는지.</summary>
    public bool NowBlocked { get; init; }
}

/// <summary>
/// 미리보기 처리기(IPreviewHandler) 하나를 부모 창 안에 띄운 상태. UI(STA) 스레드에서 만들고 쓰고 닫는다.
/// 격리: 모든 COM 호출을 try/catch로 감싸 실패하면 Unload·Release 후 <see cref="PreviewHandlerException"/>을 던지고,
/// 연속 실패한 CLSID는 <see cref="PreviewHandlerBlacklist"/>에 올린다. 처리기는 먼저 CLSCTX_LOCAL_SERVER(prevhost.exe
/// 서로게이트)로 만들어, 처리기가 죽어도 Foldy 프로세스는 살아남는다. 호출이 <see cref="Timeout"/>보다 오래 걸리면
/// (UI 스레드의 COM 호출은 중간에 끊을 수 없으므로 사후에) 실패로 치고 닫는다.
/// </summary>
public sealed class PreviewHandlerSession : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly PreviewHandlerBlacklist _blacklist;
    private IPreviewHandler? _handler;
    private bool _previewing;

    private PreviewHandlerSession(IPreviewHandler handler, Guid clsid, PreviewHandlerBlacklist blacklist, TimeSpan timeout)
    {
        _handler = handler;
        Clsid = clsid;
        _blacklist = blacklist;
        Timeout = timeout;
    }

    public Guid Clsid { get; }

    public TimeSpan Timeout { get; }

    public bool IsOpen => _handler is not null;

    /// <summary>
    /// 확장자의 처리기를 만들어 <paramref name="parent"/> 창 안에 파일을 띄운다.
    /// 처리기가 없거나 차단됐으면 <see cref="PreviewHandlerException"/>.
    /// </summary>
    public static PreviewHandlerSession Open(string path, IntPtr parent, int width, int height, PreviewHandlerBlacklist? blacklist = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        blacklist ??= ShellPreviewHandlers.Blacklist;
        var clsid = ShellPreviewHandlers.FindHandler(System.IO.Path.GetExtension(path))
            ?? throw new PreviewHandlerException("미리보기 처리기가 없습니다.");
        if (blacklist.IsBlocked(clsid))
        {
            throw new PreviewHandlerException("처리기가 반복해서 실패해 사용하지 않습니다.") { Clsid = clsid, NowBlocked = true };
        }

        object instance;
        try
        {
            instance = Create(clsid);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            throw Fail(clsid, blacklist, "처리기를 만들지 못했습니다.", ex);
        }

        return Start(instance, clsid, path, parent, width, height, blacklist, timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// 이미 만든 COM 개체로 시작한다 (시험에서 가짜 처리기를 넣는 지점).
    /// 초기화는 IInitializeWithStream을 우선, 없으면 IInitializeWithFile.
    /// </summary>
    internal static PreviewHandlerSession Start(object instance, Guid clsid, string path, IntPtr parent, int width, int height, PreviewHandlerBlacklist blacklist, TimeSpan timeout)
    {
        if (instance is not IPreviewHandler handler)
        {
            Release(instance);
            throw Fail(clsid, blacklist, "IPreviewHandler를 지원하지 않습니다.", null);
        }

        var session = new PreviewHandlerSession(handler, clsid, blacklist, timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            switch (instance)
            {
                case IInitializeWithStream withStream:
                    PreviewNative.SHCreateStreamOnFileEx(path, PreviewNative.STGM_READ | PreviewNative.STGM_SHARE_DENY_NONE, 0, false, null, out var stream);
                    try
                    {
                        withStream.Initialize(stream, PreviewNative.STGM_READ);
                    }
                    finally
                    {
                        // 처리기가 자기 몫의 참조를 AddRef 했으므로 우리 쪽은 놓는다.
                        Marshal.ReleaseComObject(stream);
                    }

                    break;
                case IInitializeWithFile withFile:
                    withFile.Initialize(path, PreviewNative.STGM_READ);
                    break;
                default:
                    throw new PreviewHandlerException("초기화 인터페이스가 없습니다.");
            }

            var rect = new PreviewRect(0, 0, Math.Max(1, width), Math.Max(1, height));
            handler.SetWindow(parent, ref rect);
            // SetWindow가 됐으면 DoPreview가 중간에 실패해도 Unload로 처리기 쪽 자원을 정리해야 한다.
            session._previewing = true;
            handler.DoPreview();
        }
        catch (Exception ex)
        {
            session.Teardown();
            throw Fail(clsid, blacklist, "미리보기 처리기 오류", ex);
        }

        if (sw.Elapsed > timeout)
        {
            // 늦게 끝났다: 화면은 그렸지만 이 처리기는 UI를 붙잡는다 → 실패로 치고 닫는다.
            session.Teardown();
            throw Fail(clsid, blacklist, "처리기 응답이 너무 느립니다.", null);
        }

        blacklist.RecordSuccess(clsid);
        return session;
    }

    /// <summary>호스트 창 크기가 바뀌었을 때.</summary>
    public void SetRect(int width, int height)
    {
        if (_handler is not { } h)
        {
            return;
        }

        try
        {
            var rect = new PreviewRect(0, 0, Math.Max(1, width), Math.Max(1, height));
            h.SetRect(ref rect);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            // 크기 조정 실패는 화면이 조금 어긋날 뿐이다. 반복되면 다음 Open에서 블랙리스트가 걸러 준다.
            _blacklist.RecordFailure(Clsid);
        }
    }

    /// <summary>처리기에 키보드 포커스를 준다 (실패는 무시).</summary>
    public void Focus()
    {
        try
        {
            _handler?.SetFocus();
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
        }
    }

    /// <summary>Unload 후 COM 개체를 놓는다. 여러 번 불러도 안전하다.</summary>
    public void Dispose() => Teardown();

    private void Teardown()
    {
        var h = Interlocked.Exchange(ref _handler, null);
        if (h is null)
        {
            return;
        }

        try
        {
            if (_previewing)
            {
                h.Unload();
            }
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            // 이미 죽은 처리기의 Unload 실패는 정리 중이므로 무시한다.
        }
        finally
        {
            _previewing = false;
            Release(h);
        }
    }

    private static PreviewHandlerException Fail(Guid clsid, PreviewHandlerBlacklist blacklist, string message, Exception? inner)
    {
        var blocked = blacklist.RecordFailure(clsid);
        return new PreviewHandlerException(message, inner) { Clsid = clsid, NowBlocked = blocked };
    }

    private static bool IsComFailure(Exception ex) =>
        ex is COMException or InvalidCastException or NotSupportedException or InvalidOperationException
            or UnauthorizedAccessException or System.IO.IOException or AccessViolationException
            or ArgumentException or NotImplementedException or TimeoutException;

    private static void Release(object o)
    {
        if (Marshal.IsComObject(o))
        {
            Marshal.FinalReleaseComObject(o);
        }
    }

    /// <summary>LOCAL_SERVER(서로게이트) 우선, 등록이 없으면 INPROC.</summary>
    private static object Create(Guid clsid)
    {
        var iid = PreviewNative.IID_IUnknown;
        var hr = PreviewNative.CoCreateInstance(ref clsid, IntPtr.Zero, PreviewNative.CLSCTX_LOCAL_SERVER, ref iid, out var unk);
        if (hr < 0)
        {
            hr = PreviewNative.CoCreateInstance(ref clsid, IntPtr.Zero, PreviewNative.CLSCTX_INPROC_SERVER, ref iid, out unk);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return Marshal.GetObjectForIUnknown(unk);
        }
        finally
        {
            Marshal.Release(unk);
        }
    }
}
