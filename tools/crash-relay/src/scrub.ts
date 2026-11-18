// 서버 측 2차 스크럽. 클라이언트(Foldy.Reporting.ReportScrubber)가 이미 지우지만,
// 구버전 클라이언트나 위조된 요청이 개인 경로를 공개 이슈에 남기지 않도록 한 번 더 지운다.
// 규칙은 클라이언트와 같다: 경로는 확장자 있는 파일 이름만 남기고, 메일 주소는 지운다.

// .NET 예외 메시지는 경로를 '...' 또는 "..."로 감싼다 → 공백이 있어도 통째로 잡는다
const QUOTED_PATH = /(['"])((?:[A-Za-z]:[\\/]|\\\\)[^'"\r\n]*)\1/g;
// 따옴표 없는 경로는 공백에서 끊는다. 드라이브 문자 뒤에는 ':'가 올 수 없으므로 ":line 42"는 남는다
const DRIVE_PATH = /\b[A-Za-z]:[\\/][^\s"'<>|*?:\r\n]*/g;
const UNC_PATH = /\\\\[^\s\\"'<>|:]+\\[^\s"'<>|*?:\r\n]*/g;
const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;

export function scrubText(input: string): string {
  return input
    .replace(QUOTED_PATH, (_, q: string, p: string) => q + keepFileName(p) + q)
    .replace(UNC_PATH, keepFileName)
    .replace(DRIVE_PATH, keepFileName)
    .replace(EMAIL, "<email>");
}

function keepFileName(path: string): string {
  if (path.startsWith("<path>")) return path;
  const trimmed = path.replace(/[\\/.,;)\]]+$/, "");
  const tail = path.slice(trimmed.length);
  const name = trimmed.split(/[\\/]/).pop() ?? "";
  // 확장자가 있는 이름만 남긴다 (폴더 이름은 사용자 정보일 수 있음)
  return /\.[A-Za-z0-9]{1,8}$/.test(name) ? `<path>\\${name}${tail}` : `<path>${tail}`;
}

/** GitHub 마크다운에서 멘션·코드펜스 탈출을 막는다. */
export function neutralizeMarkdown(input: string): string {
  return input.replace(/```/g, "ʼʼʼ").replace(/@(?=[A-Za-z0-9-])/g, "@​");
}
