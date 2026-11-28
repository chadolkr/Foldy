using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Foldy.Core.Search;

/// <summary>
/// NTFS 변경 기록(USN 저널)에서 "어느 폴더 안이 바뀌었나"를 관리자 권한 없이 읽는다 (Windows 10 1709+, 2026-11-28).
/// 색인 서비스가 없는 Store 판이 Foldy가 꺼져 있던 동안 바뀐 것을 폴더를 다 돌지 않고 따라잡는 데 쓴다.
/// <list type="bullet">
/// <item>드라이브 맨 위 폴더 같은 보통 핸들로 <c>FSCTL_QUERY_USN_JOURNAL</c>·<c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c>을 부른다.
/// 디스크 목록표(MFT)를 통째로 읽는 <c>FSCTL_ENUM_USN_DATA</c>는 관리자만 된다(노트북에서 "권한 없음" 확인).</item>
/// <item>기록마다 바뀐 것이 든 폴더의 번호(부모 파일 번호)가 있다. 번호를 <c>OpenFileById</c>로 열어 위치를 얻는다.
/// 2026-11-28 노트북: 기록 37만 개, 바뀐 폴더 5천 개를 1초 안에.</item>
/// <item>그사이 지워진 폴더는 열리지 않는다. 그 폴더를 지운 일은 그 위 폴더의 기록으로 남아 위 폴더를 다시 읽으므로 괜찮다.</item>
/// </list>
/// 잘 모를 때(저널이 새로 만들어졌거나 오래된 기록이 지워졌거나, 바뀐 폴더가 너무 많거나, 오류)는 null을 돌려준다: 그때는 평소처럼 모은다.
/// </summary>
internal static class UsnChanges
{
    /// <summary>저널의 이름표와 다음 기록 위치. 다음에 이 위치부터 읽으면 그 뒤로 바뀐 것만 나온다.</summary>
    internal readonly record struct Mark(ulong JournalId, long NextUsn);

    /// <summary>바뀐 폴더가 이보다 많으면 따로 열지 않고 평소처럼 모은다 (그만큼이면 폴더를 다 도는 것과 큰 차이가 없다).</summary>
    internal const int MostFolders = 20000;

    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const int ErrorHandleEof = 38;

    /// <summary>지금 저널의 위치. 저널이 없거나(NTFS가 아님 등) 물을 수 없으면 null.</summary>
    public static Mark? Now(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var h = OpenRoot(root);
        return h is null ? null : Query(h);
    }

    /// <summary>
    /// <paramref name="since"/> 뒤로 안이 바뀐 폴더들 (<paramref name="root"/> 아래만, 대소문자 무시). 모르면 null.
    /// <paramref name="now"/>는 다 읽은 뒤의 위치 (다음에 여기서부터).
    /// </summary>
    public static HashSet<string>? ChangedFolders(string root, Mark since, out Mark now, int most = MostFolders)
    {
        now = since;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var h = OpenRoot(root);
        if (h is null || Query(h, out var first) is not { } q || q.JournalId != since.JournalId || since.NextUsn < first || since.NextUsn > q.NextUsn)
        {
            return null; // 저널이 새로 만들어졌거나, 그 뒤 기록이 이미 지워졌다
        }

        var parents = new HashSet<long>();
        var input = new byte[40]; // READ_USN_JOURNAL_DATA_V0
        var buffer = new byte[64 * 1024];
        var start = since.NextUsn;
        while (start < q.NextUsn)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0), start);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF); // 모든 까닭
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), since.JournalId);
            if (!DeviceIoControl(h, FsctlReadUnprivilegedUsnJournal, input, input.Length, buffer, buffer.Length, out var got, 0))
            {
                if (Marshal.GetLastPInvokeError() == ErrorHandleEof)
                {
                    break;
                }

                return null;
            }

            if (got < 8)
            {
                break;
            }

            var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            for (var off = 8; off + 24 <= got;)
            {
                var len = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(off));
                if (len <= 0 || off + len > got)
                {
                    break;
                }

                if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(off + 4)) != 2)
                {
                    return null; // 128비트 파일 번호(ReFS 등): 이 방법은 쓰지 않는다
                }

                parents.Add(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(off + 16)));
                if (parents.Count > most)
                {
                    return null;
                }

                off += len;
            }

            if (next <= start)
            {
                break;
            }

            start = next;
        }

        // 맨 위 위치를 Windows가 돌려주는 모양(긴 이름)으로 맞춘다: 임시 폴더는 짧은 이름(RUNNER~1)으로 올 수 있다
        var given = root.TrimEnd('\\'); // "C:\" → "C:", "C:\a\" → "C:\a"
        var top = (FinalPath(h) ?? root).TrimEnd('\\');
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in parents)
        {
            if (PathOf(h, id) is { } p && Under(p, top))
            {
                p = p.TrimEnd('\\');
                folders.Add(p.Length == top.Length ? root : given + p[top.Length..]);
            }
        }

        now = new Mark(q.JournalId, Math.Max(start, since.NextUsn));
        return folders;
    }

    private static bool Under(string path, string top) =>
        path.StartsWith(top, StringComparison.OrdinalIgnoreCase) && (path.Length == top.Length || path[top.Length] == '\\');

    private static SafeFileHandle? OpenRoot(string root)
    {
        var h = CreateFileW(WinDirList.Extended(root), 0, ShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        return h;
    }

    private static Mark? Query(SafeFileHandle h) => Query(h, out _);

    private static Mark? Query(SafeFileHandle h, out long firstUsn)
    {
        firstUsn = 0;
        var data = new byte[80]; // USN_JOURNAL_DATA_V2까지 들어가게
        if (!DeviceIoControl(h, FsctlQueryUsnJournal, null, 0, data, data.Length, out var got, 0) || got < 24)
        {
            return null;
        }

        firstUsn = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        return new Mark(BinaryPrimitives.ReadUInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
    }

    /// <summary>파일 번호로 폴더를 열어 위치를 얻는다 (\\?\ 없이). 지워졌거나 열 수 없으면 null.</summary>
    private static string? PathOf(SafeFileHandle volume, long id)
    {
        var d = new FileIdDescriptor { Size = Marshal.SizeOf<FileIdDescriptor>(), Type = 0, FileId = id };
        using var h = OpenFileById(volume, ref d, 0, ShareAll, 0, FileFlagBackupSemantics);
        return h.IsInvalid ? null : FinalPath(h);
    }

    private static string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        var n = GetFinalPathNameByHandleW(h, buf, buf.Length, 0);
        if (n <= 0 || n >= buf.Length)
        {
            return null;
        }

        var p = new string(buf, 0, n);
        return p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null
            : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public int Size;
        public int Type;
        public long FileId;
        public long Rest; // 128비트 번호·GUID 자리 (쓰지 않음)
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FileIdDescriptor id, uint access, uint share, nint security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buffer, int size, int flags);
}
