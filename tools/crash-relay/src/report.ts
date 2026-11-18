// 크래시 리포트 스키마 v1 검증과 서명(중복 판정 키) 계산.
// 클라이언트 쪽 정의: src/Foldy.Reporting/CrashReport.cs

export interface CrashReport {
  schema: 1;
  appVersion: string;
  osVersion: string;
  runtime: string;
  exceptionType: string;
  message: string;
  /** 스크럽된 Exception.ToString() 전체 (내부 예외 포함) */
  details: string;
  /** 정규화된 스택 프레임 ("Namespace.Type.Method"). 서명 계산용 */
  frames: string[];
  /** 최근 로그 몇 줄 (선택) */
  log?: string;
}

export const LIMITS = {
  bodyBytes: 64 * 1024,
  short: 200,
  message: 2000,
  details: 30000,
  frames: 64,
  frame: 300,
  log: 8000,
} as const;

export type ValidationResult = { ok: true; report: CrashReport } | { ok: false; error: string };

export function validateReport(input: unknown): ValidationResult {
  if (typeof input !== "object" || input === null) return { ok: false, error: "본문이 JSON 객체가 아님" };
  const o = input as Record<string, unknown>;
  if (o.schema !== 1) return { ok: false, error: "지원하지 않는 schema" };

  const str = (key: string, max: number, required = true): string | undefined | Error => {
    const v = o[key];
    if (v === undefined || v === null) return required ? new Error(`${key} 누락`) : undefined;
    if (typeof v !== "string") return new Error(`${key}는 문자열이어야 함`);
    if (required && v.trim() === "") return new Error(`${key}가 비어 있음`);
    return v.length > max ? v.slice(0, max) : v;
  };

  const fields = {
    appVersion: str("appVersion", LIMITS.short),
    osVersion: str("osVersion", LIMITS.short),
    runtime: str("runtime", LIMITS.short),
    exceptionType: str("exceptionType", LIMITS.short),
    message: str("message", LIMITS.message, false),
    details: str("details", LIMITS.details),
    log: str("log", LIMITS.log, false),
  };
  for (const v of Object.values(fields)) if (v instanceof Error) return { ok: false, error: v.message };

  if (!/^\d+\.\d+\.\d+([.+-][0-9A-Za-z.+-]*)?$/.test(fields.appVersion as string))
    return { ok: false, error: "appVersion 형식 오류" };
  if (!/^[A-Za-z_][\w.+`<>,\[\]]*$/.test(fields.exceptionType as string))
    return { ok: false, error: "exceptionType 형식 오류" };

  if (!Array.isArray(o.frames)) return { ok: false, error: "frames는 배열이어야 함" };
  const frames = o.frames
    .slice(0, LIMITS.frames)
    .filter((f): f is string => typeof f === "string")
    .map((f) => f.slice(0, LIMITS.frame));

  return {
    ok: true,
    report: {
      schema: 1,
      appVersion: fields.appVersion as string,
      osVersion: fields.osVersion as string,
      runtime: fields.runtime as string,
      exceptionType: fields.exceptionType as string,
      message: (fields.message as string | undefined) ?? "",
      details: fields.details as string,
      frames,
      log: fields.log as string | undefined,
    },
  };
}

/** 서명에 쓰는 프레임 수. 너무 많으면 호출 경로만 다른 같은 버그가 갈라진다. */
export const SIGNATURE_FRAMES = 5;

/**
 * 같은 버그 = 예외 타입 + 상위 프레임(Foldy 코드 우선)이 같은 것.
 * 줄 번호·오프셋은 빌드마다 바뀌므로 넣지 않는다. 프레임이 없으면 숫자를 지운 메시지로 대신한다.
 */
export async function crashSignature(report: Pick<CrashReport, "exceptionType" | "frames" | "message">): Promise<string> {
  const normalized = report.frames.map(normalizeFrame).filter((f) => f !== "");
  const own = normalized.filter((f) => f.startsWith("Foldy."));
  const chosen = (own.length > 0 ? own : normalized).slice(0, SIGNATURE_FRAMES);
  const basis = chosen.length > 0 ? chosen.join("\n") : report.message.replace(/\d+/g, "#").slice(0, 200);
  const data = new TextEncoder().encode(`${report.exceptionType}\n${basis}`);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)]
    .slice(0, 6)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

export function normalizeFrame(frame: string): string {
  return frame
    .trim()
    .replace(/^at\s+/, "")
    .replace(/\s+in\s+.*$/, "") // " in <path>\File.cs:line 42"
    .replace(/\+<>c__DisplayClass\d+_\d+/g, "")
    .replace(/<(\w+)>b__\d+_\d+/g, "<$1>lambda")
    .replace(/<(\w+)>d__\d+/g, "<$1>async");
}
