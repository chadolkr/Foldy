using System.Globalization;

namespace Foldy.Core.Diagnostics;

/// <summary>
/// "Foldy가 지금 실행 중" 표시 파일 (데이터 폴더의 running.marker). 정상 종료면 지운다.
/// 다음 실행 때 파일이 남아 있으면 지난번에 갑자기 꺼진 것이다: 네이티브 코드가 프로세스를 끝내면
/// .NET 예외 처리기도 불리지 않아 로그에 아무것도 남지 않았다 (사용자 제보 2026-11-23).
/// 오래 걸리는 위험한 작업(휴지통·삭제)은 <see cref="Note"/>로 지금 하는 일을 적어 두어, 꺼졌을 때 무엇을 하던 중인지 남긴다.
/// </summary>
public sealed class RunMarker
{
    public const string FileName = "running.marker";

    private static readonly object Gate = new();
    private static RunMarker? _current;
    private readonly string _path;
    private readonly string _header;

    private RunMarker(string path, string header)
    {
        _path = path;
        _header = header;
    }

    /// <summary>표시를 만든다. 지난 실행의 표시가 남아 있었으면 그 내용을 <paramref name="previous"/>로 돌려준다.</summary>
    public static RunMarker Start(string dataDir, out string? previous)
    {
        var path = Path.Combine(dataDir, FileName);
        previous = null;
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                previous = text.Length > 0 ? text : "?";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            previous = "?";
        }

        var marker = new RunMarker(path, string.Create(CultureInfo.InvariantCulture, $"started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}, pid {Environment.ProcessId}"));
        lock (Gate)
        {
            _current = marker;
            marker.Write(null);
        }

        return marker;
    }

    /// <summary>지금 하는 일을 적는다 (끝나면 null). 표시가 없으면 아무것도 안 한다.</summary>
    public static void Note(string? activity)
    {
        lock (Gate)
        {
            _current?.Write(activity);
        }
    }

    /// <summary>정상 종료: 표시를 지운다.</summary>
    public void Stop()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }

            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Write(string? activity)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, activity is null ? _header : $"{_header}; {DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {activity}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
