using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Sidebar;
using Foldy.Presentation.Tabs;

namespace Foldy.App;

/// <summary>
/// 문서용 화면 캡처(<c>--doc-screenshots</c>)가 쓰는 가짜 샘플 폴더와 설정을 만든다.
/// 사용자 폴더·이름·회사 정보가 화면에 나오지 않도록, 모든 내용은 여기서 만든 가짜 데이터다.
/// 지우고 다시 만들 폴더는 이름이 FoldyDemo 로 끝나야만 건드린다.
/// </summary>
internal static class DocShotsSandbox
{
    public static string RootOf(AppOptions o) => o.DocRoot is { Length: > 0 } r ? Path.GetFullPath(r) : Path.Combine(Path.GetTempPath(), "FoldyDemo");

    public static string Downloads(string root) => Path.Combine(root, "Downloads");

    public static string Documents(string root) => Path.Combine(root, "Documents");

    public static string Trip(string root) => Path.Combine(root, "Pictures", "제주여행");

    public static string Repo(string root) => Path.Combine(root, "Projects", "todo-app");

    public static string JavaApp(string root) => Path.Combine(root, "Projects", "2026 사업계획");

    public static string Archive(string root) => Path.Combine(root, "Archive");

    /// <summary>대표 화면용: 크기가 다양한 폴더 여러 개 (폴더 분석 트리맵이 여러 색으로 보이게).</summary>
    public static string Space(string root)
    {
        var dir = Path.Combine(root, "공간");
        (string Folder, int[] SizesMb)[] plan =
        [
            ("영상", [60, 25, 12]), ("사진", [9, 8, 7, 6, 5, 4]), ("설치 파일", [38, 14]), ("백업", [30, 10]),
            ("음악", [6, 5, 4, 3]), ("문서", [3, 2, 2, 1, 1]), ("프로젝트", [12, 6, 3]), ("게임", [22]),
        ];
        foreach (var (folder, sizes) in plan)
        {
            var d = Path.Combine(dir, folder);
            Directory.CreateDirectory(d);
            for (var i = 0; i < sizes.Length; i++)
            {
                var f = Path.Combine(d, $"{folder}_{i + 1}.bin");
                if (!File.Exists(f))
                {
                    using var fs = File.Create(f);
                    fs.SetLength(sizes[i] * 1024L * 1024);
                }
            }
        }

        return dir;
    }

    /// <summary>샘플 폴더와 임시 데이터 폴더의 설정 파일을 만든다. 실패하면 false.</summary>
    public static bool Prepare(AppOptions o, string dataDir)
    {
        var root = RootOf(o);
        var realData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy");
        if (!string.Equals(Path.GetFileName(root.TrimEnd('\\')), "FoldyDemo", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(root.TrimEnd('\\')), "FDemo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\'), realData, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("샘플 폴더 이름은 FoldyDemo 또는 FDemo 여야 하고, FOLDY_DATA_DIR 는 실제 Foldy 데이터 폴더가 아니어야 합니다.");
            return false;
        }

        try
        {
            ClearFolder(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(dataDir);
            BuildDownloads(root);
            BuildPictures(root);
            BuildDocuments(root);
            BuildArchive(root);
            BuildRepo(root);
            WriteSettings(root, dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("샘플 폴더를 만들지 못했습니다: " + ex.Message);
            File.WriteAllText(Path.Combine(dataDir, "doc-error.txt"), ex.ToString());
            return false;
        }

        return true;
    }

    private static void ClearFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal); // git 객체는 읽기 전용
        }

        Directory.Delete(path, recursive: true);
    }

    private static void WriteSettings(string root, string dataDir)
    {
        JsonFileStore.Save(Path.Combine(dataDir, "settings.json"), new AppSettings { Theme = "light", Animations = true, ShowGitBar = true });
        JsonFileStore.Save(Path.Combine(dataDir, "quickaccess.json"), new QuickAccessState
        {
            Pinned = [Downloads(root), Repo(root), Trip(root), Documents(root)],
            Recent = [Archive(root), JavaApp(root), Path.Combine(root, "Projects"), Path.Combine(root, "Pictures")],
        });
        JsonFileStore.Save(Path.Combine(dataDir, "session.json"), new SessionState
        {
            Left = new PaneState { Tabs = [new TabState { Kind = TabKind.Folder, Path = Repo(root), Color = 0 }, new TabState { Kind = TabKind.Folder, Path = Documents(root), Color = 1 }], ActiveIndex = 0 },
            Right = new PaneState { Tabs = [new TabState { Kind = TabKind.Folder, Path = Downloads(root), Color = 2 }, new TabState { Kind = TabKind.Folder, Path = Trip(root), Color = 3 }], ActiveIndex = 0 },
            SidebarExpanded = true,
        });
    }

    // ───────────── 일반 파일 ─────────────

    private static readonly Random Rng = new(20261119);

    private static void Fake(string path, long size, int daysAgo, byte[]? head = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path))
        {
            var buf = head ?? new byte[256];
            if (head is null)
            {
                Rng.NextBytes(buf);
            }

            fs.Write(buf);
            fs.SetLength(Math.Max(size, buf.Length));
        }

        File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysAgo).AddMinutes(-Rng.Next(0, 600)));
    }

    private static void Text(string path, string content, int daysAgo = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysAgo));
    }

    private const long KB = 1024;
    private const long MB = 1024 * 1024;

    private static void BuildDownloads(string root)
    {
        var d = Downloads(root);
        Fake(Path.Combine(d, "invoice-2026-10.pdf"), 184 * KB, 2);
        Fake(Path.Combine(d, "영수증_1107.pdf"), 92 * KB, 12);
        var minutes = new byte[4096];
        Rng.NextBytes(minutes);
        Fake(Path.Combine(d, "회의록_최종_v2.docx"), 1200 * KB, 5, minutes);
        Fake(Path.Combine(d, "회의록_최종_v2 (1).docx"), 1200 * KB, 5, minutes); // 똑같은 내용: 정리할 때 중복으로 잡힌다
        Fake(Path.Combine(d, "보고서_초안.docx"), 340 * KB, 9);
        Fake(Path.Combine(d, "보고서_최종.pdf"), 910 * KB, 8);
        Fake(Path.Combine(d, "견적서_A사.xlsx"), 56 * KB, 18);
        Fake(Path.Combine(d, "2026_사업계획서.pptx"), 8400 * KB, 25);
        Fake(Path.Combine(d, "setup_editor_x64.exe"), 32 * MB, 40);
        Fake(Path.Combine(d, "runtime-22.1-x64.msi"), 28 * MB, 61);
        Fake(Path.Combine(d, "project-backup.zip"), 45 * MB, 33);
        Fake(Path.Combine(d, "old-assets.7z"), 12 * MB, 150);
        Fake(Path.Combine(d, "샘플음악.mp3"), 5 * MB, 77);
        Fake(Path.Combine(d, "강의녹화_10월.mp4"), 70 * MB, 21);
        Fake(Path.Combine(d, "data_export.csv"), 310 * KB, 4);
        Fake(Path.Combine(d, "여행일정.hwp"), 48 * KB, 30);
        Text(Path.Combine(d, "notes.txt"), "장보기: 우유, 계란, 커피\n금요일 회의 자료 확인\n", 1);
        Text(Path.Combine(d, "README.md"), "# 다운로드 폴더\n\n임시로 받은 파일을 모아 두는 곳입니다.\n", 60);
        SaveJpeg(Path.Combine(d, "IMG_4821.jpg"), 4821, 900, 600, 6);
        SaveJpeg(Path.Combine(d, "IMG_4822.jpg"), 4822, 900, 600, 6);
        SavePng(Path.Combine(d, "screenshot_2026-11-03.png"), 303, 1100, 650, 16);
        SaveJpeg(Path.Combine(d, "photos_unsorted", "DSC_0101.jpg"), 101, 900, 600, 45);
        SaveJpeg(Path.Combine(d, "photos_unsorted", "DSC_0102.jpg"), 102, 900, 600, 45);
        Fake(Path.Combine(d, "old_stuff", "이전_자료.zip"), 3 * MB, 300);
        Text(Path.Combine(d, "blog-site", "package.json"), "{\n  \"name\": \"blog-site\",\n  \"version\": \"1.0.0\"\n}\n", 20);
        Text(Path.Combine(d, "blog-site", "index.js"), "console.log('hello');\n", 20);
    }

    private static void BuildArchive(string root)
    {
        var a = Archive(root);
        Fake(Path.Combine(a, "Videos", "강의녹화_2026.mp4"), 62 * MB, 70);
        Fake(Path.Combine(a, "Videos", "여행_브이로그.mp4"), 41 * MB, 90);
        Fake(Path.Combine(a, "Backups", "db-dump-2025.sql"), 26 * MB, 200);
        Fake(Path.Combine(a, "Backups", "보고서_2025.zip"), 6 * MB, 330);
        Fake(Path.Combine(a, "Installers", "setup_tool_1.4.2.exe"), 18 * MB, 110);
        Text(Path.Combine(a, "web-dashboard", "package.json"), "{\n  \"name\": \"web-dashboard\"\n}\n", 150);
        for (var i = 1; i <= 3; i++)
        {
            Fake(Path.Combine(a, "web-dashboard", "node_modules", $"pkg{i}", "dist.bin"), 14 * MB, 150);
        }

        Directory.SetLastWriteTime(Path.Combine(a, "web-dashboard", "node_modules"), DateTime.Now.AddDays(-150));
        Fake(Path.Combine(a, "Photos-2024", "raw-0001.dng"), 24 * MB, 400);
        Fake(Path.Combine(a, "Photos-2024", "raw-0002.dng"), 24 * MB, 400);
    }

    // ───────────── 그림 (WPF로 그린 가짜 풍경) ─────────────

    private static void BuildPictures(string root)
    {
        var names = new[] { "성산일출봉", "한라산", "협재해변", "카페거리", "우도_등대", "동백꽃", "올레길", "흑돼지_저녁", "숙소_창밖", "비자림", "공항_풍경", "기념품" };
        for (var i = 0; i < names.Length; i++)
        {
            SaveJpeg(Path.Combine(Trip(root), names[i] + ".jpg"), 7 + i * 13, 1200, 800, 20 + i * 2);
        }

        Text(Path.Combine(root, "Pictures", "README.md"), "# 사진\n\n여행 사진은 `제주여행` 폴더에 있습니다.\n", 90);
    }

    private static void SaveJpeg(string path, int seed, int w, int h, int daysAgo) => Save(path, Draw(seed, w, h), new JpegBitmapEncoder { QualityLevel = 82 }, daysAgo);

    private static void SavePng(string path, int seed, int w, int h, int daysAgo) => Save(path, Draw(seed, w, h), new PngBitmapEncoder(), daysAgo);

    private static void Save(string path, BitmapSource bmp, BitmapEncoder enc, int daysAgo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path))
        {
            enc.Save(fs);
        }

        File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysAgo).AddMinutes(-Rng.Next(0, 600)));
    }

    private static Color Hsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>씨앗마다 다른 하늘·해·산·바다 그림 (실제 사진이 아니라 문서용 그림).</summary>
    private static RenderTargetBitmap Draw(int seed, int w, int h)
    {
        var r = new Random(seed);
        var hue = r.Next(0, 360);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var sky = new LinearGradientBrush(Hsv(hue, 0.55, 0.95), Hsv((hue + 40) % 360, 0.25, 1.0), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
            var sunX = w * (0.2 + r.NextDouble() * 0.6);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 255, 244, 200)), null, new Point(sunX, h * 0.3), h * 0.09, h * 0.09);
            for (var layer = 0; layer < 3; layer++)
            {
                var baseY = h * (0.5 + layer * 0.12);
                var fig = new StreamGeometry();
                using (var g = fig.Open())
                {
                    g.BeginFigure(new Point(0, h), true, true);
                    g.LineTo(new Point(0, baseY), true, false);
                    var x = 0.0;
                    while (x < w)
                    {
                        x += w * (0.12 + r.NextDouble() * 0.15);
                        g.LineTo(new Point(Math.Min(x, w), baseY - h * (0.05 + r.NextDouble() * 0.16)), true, false);
                    }

                    g.LineTo(new Point(w, h), true, false);
                }

                fig.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Hsv((hue + 120 + layer * 20) % 360, 0.45 + layer * 0.1, 0.62 - layer * 0.14)), null, fig);
            }

            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 40, 90, 140), Color.FromArgb(200, 30, 80, 130), 90), null, new Rect(0, h * 0.82, w, h * 0.18));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // ───────────── 문서 ─────────────

    private static void BuildDocuments(string root)
    {
        var d = Documents(root);
        SavePng(Path.Combine(d, "images", "cover.png"), 55, 900, 300, 8);
        Text(Path.Combine(d, "프로젝트_안내.md"), """
            # 할 일 앱 프로젝트 안내

            ![표지](images/cover.png)

            간단한 **할 일 관리 앱**을 만드는 프로젝트입니다. 이 문서는 팀원이 처음 읽는 안내서입니다.

            ## 목표

            - 할 일을 추가·완료·삭제할 수 있다
            - 마감일과 검색을 지원한다
            - 데이터는 로컬 JSON 파일에 저장한다

            ## 일정

            | 단계 | 기간 | 담당 | 상태 |
            | --- | --- | --- | --- |
            | 설계 | 10/01 ~ 10/07 | 김민지 | 완료 |
            | 구현 | 10/08 ~ 10/31 | 박준 | 진행 중 |
            | 시험 | 11/01 ~ 11/10 | 김민지 | 예정 |

            ## 해야 할 일

            - [x] 저장소 만들기
            - [x] 기본 화면 설계
            - [ ] 검색 기능 마무리
            - [ ] 설치 안내 작성

            ## 실행 방법

            ```python
            from src.app import TodoApp

            app = TodoApp("todos.json")
            app.add("보고서 쓰기")
            print(app.list())
            ```

            > 자세한 내용은 `docs/design.md` 를 보세요.
            """, 2);
        Text(Path.Combine(d, "회의록_2026-11-12.md"), "# 회의록\n\n- 참석: 김민지, 박준\n- 다음 주까지 검색 기능 마무리\n", 7);
        Fake(Path.Combine(d, "분기보고서.docx"), 420 * KB, 14);
        Fake(Path.Combine(d, "보고서_2026_Q1.docx"), 380 * KB, 190);
        Fake(Path.Combine(d, "보고서_2026_Q2.docx"), 395 * KB, 100);
        Fake(Path.Combine(d, "보고서_2026_Q3.docx"), 410 * KB, 30);
        Fake(Path.Combine(d, "예산표.xlsx"), 88 * KB, 22);
        Fake(Path.Combine(d, "발표자료.pptx"), 5 * MB, 35);
    }

    // ───────────── Git 저장소 ─────────────

    private static string Git(string dir, string when, string who, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var (name, mail) = who == "joon" ? ("박준", "joon@example.com") : ("김민지", "minji@example.com");
        psi.Environment["GIT_AUTHOR_NAME"] = name;
        psi.Environment["GIT_AUTHOR_EMAIL"] = mail;
        psi.Environment["GIT_COMMITTER_NAME"] = name;
        psi.Environment["GIT_COMMITTER_EMAIL"] = mail;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (when.Length > 0)
        {
            psi.Environment["GIT_AUTHOR_DATE"] = when;
            psi.Environment["GIT_COMMITTER_DATE"] = when;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git 을 시작하지 못했습니다");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} 실패: {err.Result}");
        }

        return outp;
    }

    private static string Ago(int days, int hour = 14) => DateTime.Now.Date.AddDays(-days).AddHours(hour).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Commit(string dir, int days, string who, string message) => Git(dir, Ago(days, 10 + days % 7), who, "commit", "-q", "-m", message);

    private static void BuildRepo(string root)
    {
        var repo = Repo(root);
        Directory.CreateDirectory(repo);
        string W(string rel, string text) { Text(Path.Combine(repo, rel), text, 30); return rel; }
        Git(repo, "", "minji", "init", "-q", "-b", "main");
        Git(repo, "", "minji", "config", "user.name", "김민지");
        Git(repo, "", "minji", "config", "user.email", "minji@example.com");

        W("README.md", "# 할 일 앱\n\n작은 할 일 관리 앱입니다.\n\n## 실행\n\n```\npython -m src.app\n```\n");
        W(".gitignore", "__pycache__/\n*.pyc\ntodos.json\n");
        W("src/app.py", AppV1);
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 40, "minji", "첫 커밋: 프로젝트 기본 구조");

        W("src/app.py", AppV2);
        W("src/storage.py", StorageV1);
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 34, "minji", "할 일 추가·삭제 기능");

        W("src/utils.py", "def clean(title: str) -> str:\n    return title.strip()\n");
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 30, "minji", "문자열 정리 함수 분리");

        Git(repo, "", "minji", "checkout", "-q", "-b", "feature/search");
        W("src/search.py", "def find(todos, word):\n    return [t for t in todos if word in t['title']]\n");
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 26, "minji", "검색 기능 추가");
        W("tests/test_search.py", "from src.search import find\n\n\ndef test_find():\n    assert find([{'title': '보고서'}], '보고') != []\n");
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 24, "minji", "검색 시험 추가");

        Git(repo, "", "minji", "checkout", "-q", "main");
        W("docs/design.md", "# 설계 메모\n\n- 저장은 JSON 파일\n- 화면은 나중에\n");
        W("docs/주간보고서.md", "# 주간 보고서\n\n이번 주: 검색 기능 구현\n");
        Git(repo, "", "joon", "add", "-A");
        Commit(repo, 25, "joon", "문서: 설계 메모와 주간 보고서");
        Git(repo, Ago(22), "minji", "merge", "-q", "--no-ff", "feature/search", "-m", "검색 기능 합치기");
        W("src/app.py", AppV3);
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 18, "minji", "버그 수정: 빈 제목은 추가하지 않음");

        // 원격(같은 샘플 폴더 안의 빈 저장소)과 맞춘다: 올릴 2개(↑2), 받을 1개(↓1)
        var remote = Path.Combine(root, "_remote", "todo-app.git");
        Directory.CreateDirectory(remote);
        Git(remote, "", "minji", "init", "-q", "--bare", "-b", "main");
        Git(repo, "", "minji", "remote", "add", "origin", remote);
        Git(repo, "", "minji", "push", "-q", "-u", "origin", "main");
        var other = Path.Combine(root, "_remote", "joon-clone");
        Git(root, "", "joon", "clone", "-q", remote, other);
        Text(Path.Combine(other, "README.md"), "# 할 일 앱\n\n작은 할 일 관리 앱입니다.\n\n## 설치\n\npython 3.11 이상이 필요합니다.\n\n## 실행\n\n```\npython -m src.app\n```\n", 5);
        Git(other, "", "joon", "-c", "user.name=박준", "-c", "user.email=joon@example.com", "commit", "-q", "-am", "README: 설치 방법 보강");
        Git(other, "", "joon", "push", "-q", "origin", "main");
        W("src/app.py", AppV3 + "\n    def done(self, index):\n        self.todos[index]['done'] = True\n");
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 3, "minji", "완료 표시 기능");
        W("src/utils.py", "def clean(title: str) -> str:\n    return title.strip()\n\n\ndef short(title: str, n: int = 20) -> str:\n    return title if len(title) <= n else title[:n] + '…'\n");
        Git(repo, "", "minji", "add", "-A");
        Commit(repo, 1, "minji", "긴 제목 줄여서 보여 주기");
        Git(repo, "", "minji", "fetch", "-q", "origin");
        ClearFolder(Path.Combine(root, "_remote", "joon-clone"));

        // 작업 중인 변경: 고침(커밋 안 함) · 올려 둠(스테이지) · 새 파일 · 삭제
        W("src/app.py", AppV3 + "\n    def done(self, index):\n        self.todos[index]['done'] = True\n\n    def due(self, index, date):\n        # 마감일 지정 (작업 중)\n        self.todos[index]['due'] = date\n        self.save()\n\n    def overdue(self, today):\n        return [t for t in self.todos if t.get('due') and t['due'] < today]\n");
        W("README.md", "# 할 일 앱\n\n작은 할 일 관리 앱입니다. 마감일과 검색을 지원합니다.\n\n## 실행\n\n```\npython -m src.app\n```\n");
        W("src/storage.py", StorageV1.Replace("indent=2", "indent=2, ensure_ascii=False", StringComparison.Ordinal));
        Git(repo, "", "minji", "add", "src/storage.py");
        W("src/export.py", "import csv\n\n\ndef to_csv(todos, path):\n    with open(path, 'w', newline='', encoding='utf-8') as f:\n        w = csv.writer(f)\n        for t in todos:\n            w.writerow([t['title'], t.get('done', False)])\n");
        W("메모.txt", "마감일 기능 끝나면 README 정리\n");
        File.Delete(Path.Combine(repo, "src", "utils.py"));
        Directory.CreateDirectory(Path.Combine(repo, "tests"));
        // 파일 시간을 최근으로 (목록의 수정 열이 자연스럽게 보이게)
        foreach (var f in new[] { "src/app.py", "README.md", "src/storage.py", "src/export.py", "메모.txt" })
        {
            File.SetLastWriteTime(Path.Combine(repo, f), DateTime.Now.AddMinutes(-Rng.Next(20, 400)));
        }

        BuildJava(root);
    }

    private static void BuildJava(string root)
    {
        // 최근 폴더에 보이는 일반 문서 프로젝트 (개발 템플릿은 2026-11-21에 뺐다)
        var plan = JavaApp(root);
        foreach (var sub in new[] { "01_기획", "02_자료", "03_작업", "04_보고", "99_보관" })
        {
            Directory.CreateDirectory(Path.Combine(plan, sub));
        }

        File.WriteAllText(Path.Combine(plan, "01_기획", "사업계획_초안.docx"), "Foldy 문서용 예시");
    }

    private const string AppV1 = "class TodoApp:\n    def __init__(self):\n        self.todos = []\n\n    def add(self, title):\n        self.todos.append({'title': title, 'done': False})\n";

    private const string StorageV1 = "import json\n\n\ndef load(path):\n    try:\n        with open(path, encoding='utf-8') as f:\n            return json.load(f)\n    except FileNotFoundError:\n        return []\n\n\ndef save(path, todos):\n    with open(path, 'w', encoding='utf-8') as f:\n        json.dump(todos, f, indent=2)\n";

    private const string AppV2 = "from src import storage\n\n\nclass TodoApp:\n    def __init__(self, path):\n        self.path = path\n        self.todos = storage.load(path)\n\n    def add(self, title):\n        self.todos.append({'title': title, 'done': False})\n        self.save()\n\n    def remove(self, index):\n        del self.todos[index]\n        self.save()\n\n    def save(self):\n        storage.save(self.path, self.todos)\n";

    private const string AppV3 = "from src import storage\n\n\nclass TodoApp:\n    def __init__(self, path):\n        self.path = path\n        self.todos = storage.load(path)\n\n    def add(self, title):\n        if not title.strip():\n            return\n        self.todos.append({'title': title.strip(), 'done': False})\n        self.save()\n\n    def remove(self, index):\n        del self.todos[index]\n        self.save()\n\n    def save(self):\n        storage.save(self.path, self.todos)\n";
}
