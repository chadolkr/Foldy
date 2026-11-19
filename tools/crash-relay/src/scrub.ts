// 서버 측 2차 스크럽. 클라이언트(Foldy.Reporting.ReportScrubber)가 이미 지우지만,
// 구버전 클라이언트나 위조된 요청이 개인 경로를 공개 이슈에 남기지 않도록 한 번 더 지운다.
// 규칙은 클라이언트와 같다: 경로는 지우고 Foldy 소스·실행 파일 이름(.cs .xaml .rs .dll .exe)만 남긴다(문서는 <file>.확장자),
// 메일 주소·URL·IP·user@서버·서버 쪽 경로·사내 호스트 이름도 지운다. 공개 이슈에 회사 정보가 남으면 안 된다.

// .NET 예외 메시지는 경로를 '...' 또는 "..."로 감싼다 → 공백이 있어도 통째로 잡는다
const QUOTED_PATH = /(['"])((?:[A-Za-z]:[\\/]|\\\\)[^'"\r\n]*)\1/g;
// 따옴표 없는 경로는 공백에서 끊는다. 드라이브 문자 뒤에는 ':'가 올 수 없으므로 ":line 42"는 남는다
const DRIVE_PATH = /\b[A-Za-z]:[\\/][^\s"'<>|*?:\r\n]*/g;
const UNC_PATH = /\\\\[^\s\\"'<>|:]+\\[^\s"'<>|*?:\r\n]*/g;
const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
const URL = /\b(?:https?|s?ftp|ssh|file|smb|ldaps?|wss?):\/\/[^\s"'<>]*/gi;
const ACCOUNT = /(?<![\w.%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9][A-Za-z0-9.-]*/g;
const OCTET = "(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)";
// 버전 번호("Version=10.0.0.0", "Foldy 0.3.18.0")는 남긴다. 0.x.x.x는 실제 주소가 아니므로 버전으로 본다
const IPV4 = new RegExp(`(?<![\\w.=])(?<!Foldy )(?!0\\.)(?:${OCTET}\\.){3}${OCTET}(?::\\d{1,5})?(?![\\w.])`, "g");
const IPV6 = /(?<![\w:])(?=[0-9A-Fa-f:]*\d)(?=[0-9A-Fa-f:]*(?:::|(?:[0-9A-Fa-f]*:){3}))[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![\w:])/g;
const PRIVATE_HOST = /\b[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*\.(?:local|lan|corp|internal|intranet|intra|localdomain|home\.arpa)\b/gi;
const POSIX_PATH = /(?<![\w.:/<>\\])\/(?:[^\s/"'<>|]+\/)+[^\s"'<>|]*/g;
const KEPT_EXTENSIONS = new Set([".cs", ".xaml", ".rs", ".dll", ".exe"]);

export function scrubText(input: string): string {
  return input
    .replace(URL, "<url>")
    .replace(QUOTED_PATH, (_, q: string, p: string) => q + keepFileName(p) + q)
    .replace(UNC_PATH, keepFileName)
    .replace(DRIVE_PATH, keepFileName)
    .replace(EMAIL, "<email>")
    .replace(ACCOUNT, "<account>")
    .replace(IPV4, "<ip>")
    .replace(IPV6, "<ip>")
    .replace(PRIVATE_HOST, "<host>")
    .replace(POSIX_PATH, "<path>");
}

function keepFileName(path: string): string {
  if (path.startsWith("<path>")) return path;
  const trimmed = path.replace(/[\\/.,;)\]]+$/, "");
  const tail = path.slice(trimmed.length);
  const name = trimmed.split(/[\\/]/).pop() ?? "";
  // 확장자가 있는 이름만 남긴다 (폴더 이름은 사용자 정보일 수 있음)
  if (!/\.[A-Za-z0-9]{1,8}$/.test(name)) return `<path>${tail}`;
  // Foldy 소스·실행 파일 이름은 디버깅에 필요. 그 밖의 이름(사용자 문서)은 확장자만 남긴다.
  const ext = name.slice(name.lastIndexOf("."));
  return KEPT_EXTENSIONS.has(ext.toLowerCase()) ? `<path>\\${name}${tail}` : `<path>\\<file>${ext}${tail}`;
}

/** GitHub 마크다운에서 멘션·코드펜스 탈출을 막는다. */
export function neutralizeMarkdown(input: string): string {
  return input.replace(/```/g, "ʼʼʼ").replace(/@(?=[A-Za-z0-9-])/g, "@​");
}
