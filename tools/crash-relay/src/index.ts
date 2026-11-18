// Foldy 크래시 릴레이.
// POST /v1/crash  ← 앱이 사용자 동의 후 스크럽된 리포트를 보낸다.
// 같은 서명의 열린 이슈가 있으면 댓글(발생 횟수), 없으면 새 이슈, 닫힌 이슈면 재발 이슈를 새로 만든다.

import { GitHub, type Issue } from "./github";
import { crashSignature, LIMITS, validateReport, type CrashReport } from "./report";
import { neutralizeMarkdown, scrubText } from "./scrub";

export interface Env {
  GITHUB_TOKEN: string;
  GITHUB_REPO: string;
  CRASH_KV: KVNamespace;
  /** IP당 요청 제한 (wrangler.jsonc ratelimits) */
  RATE_LIMITER?: RateLimit;
  /** 하루 새 이슈 상한. 폭주·악용 시 이슈 트래커가 덮이지 않게 한다 */
  MAX_NEW_ISSUES_PER_DAY?: string;
  /** 같은 이슈에 댓글을 다는 발생 횟수 상한. 이후엔 N번째마다만 단다 */
  MAX_COMMENTS_PER_ISSUE?: string;
}

interface SignatureEntry {
  issue: number;
  count: number;
  firstVersion: string;
  lastVersion: string;
}

export interface RelayResult {
  status: "created" | "commented" | "counted";
  signature: string;
  issue: number;
  url: string;
  occurrences: number;
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/health" && request.method === "GET") return json(200, { ok: true });
    if (url.pathname !== "/v1/crash") return json(404, { error: "not found" });
    if (request.method !== "POST") return json(405, { error: "POST only" });

    const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
    if (env.RATE_LIMITER) {
      const { success } = await env.RATE_LIMITER.limit({ key: ip });
      if (!success) return json(429, { error: "요청이 너무 많습니다" });
    }

    const length = Number(request.headers.get("Content-Length") ?? "0");
    if (length > LIMITS.bodyBytes) return json(413, { error: "본문이 너무 큽니다" });
    const raw = await request.text();
    if (raw.length > LIMITS.bodyBytes) return json(413, { error: "본문이 너무 큽니다" });

    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      return json(400, { error: "JSON 파싱 실패" });
    }
    const v = validateReport(parsed);
    if (!v.ok) return json(400, { error: v.error });

    try {
      const result = await relay(v.report, env, new GitHub(env.GITHUB_TOKEN, env.GITHUB_REPO));
      return json(200, result);
    } catch (e) {
      if (e instanceof DailyLimitError) return json(429, { error: e.message });
      console.error("relay 실패", e);
      return json(502, { error: "GitHub 전달 실패" });
    }
  },
} satisfies ExportedHandler<Env>;

class DailyLimitError extends Error {}

export async function relay(input: CrashReport, env: Env, gh: GitHub, now = new Date()): Promise<RelayResult> {
  const report = scrubReport(input);
  const signature = await crashSignature(report);
  const key = `sig:${signature}`;

  let entry = await env.CRASH_KV.get<SignatureEntry>(key, "json");
  let existing: Issue | undefined;
  if (entry) {
    existing = await gh.getIssue(entry.issue).catch(() => undefined);
  } else {
    existing = await gh.findBySignature(signature).catch(() => undefined);
    if (existing) entry = { issue: existing.number, count: 1, firstVersion: "?", lastVersion: "?" };
  }

  if (existing && entry && existing.state === "open") {
    entry = { ...entry, count: entry.count + 1, lastVersion: report.appVersion };
    await env.CRASH_KV.put(key, JSON.stringify(entry));
    const maxComments = Number(env.MAX_COMMENTS_PER_ISSUE ?? "20");
    const shouldComment = entry.count <= maxComments || entry.count % 50 === 0;
    if (shouldComment) await gh.comment(existing.number, occurrenceComment(report, entry.count));
    return {
      status: shouldComment ? "commented" : "counted",
      signature,
      issue: existing.number,
      url: existing.html_url,
      occurrences: entry.count,
    };
  }

  await reserveNewIssue(env, now);
  const regressionOf = existing?.state === "closed" ? existing.number : undefined;
  const issue = await gh.createIssue(issueTitle(report, signature), issueBody(report, signature, regressionOf), [
    "crash",
    "auto-report",
  ]);
  await env.CRASH_KV.put(
    key,
    JSON.stringify({ issue: issue.number, count: 1, firstVersion: report.appVersion, lastVersion: report.appVersion }),
  );
  return { status: "created", signature, issue: issue.number, url: issue.html_url, occurrences: 1 };
}

async function reserveNewIssue(env: Env, now: Date): Promise<void> {
  const day = now.toISOString().slice(0, 10);
  const key = `daily:${day}`;
  const used = Number((await env.CRASH_KV.get(key)) ?? "0");
  const max = Number(env.MAX_NEW_ISSUES_PER_DAY ?? "20");
  if (used >= max) throw new DailyLimitError("오늘 새 크래시 이슈 상한에 도달했습니다");
  await env.CRASH_KV.put(key, String(used + 1), { expirationTtl: 60 * 60 * 48 });
}

function scrubReport(r: CrashReport): CrashReport {
  return {
    ...r,
    message: scrubText(r.message),
    details: scrubText(r.details),
    frames: r.frames.map(scrubText),
    log: r.log === undefined ? undefined : scrubText(r.log),
  };
}

export function issueTitle(r: CrashReport, signature: string): string {
  const shortType = r.exceptionType.split(".").pop() ?? r.exceptionType;
  const msg = r.message.replace(/\s+/g, " ").trim();
  const head = msg.length > 80 ? `${msg.slice(0, 77)}...` : msg;
  return `[crash:${signature}] ${shortType}${head ? `: ${head}` : ""}`;
}

const fence = (s: string) => "```text\n" + neutralizeMarkdown(s) + "\n```";

export function issueBody(r: CrashReport, signature: string, regressionOf?: number): string {
  const lines = [
    "> 사용자 동의를 받아 자동으로 보낸 크래시 리포트입니다. 경로·사용자 이름은 앱과 릴레이에서 지웠습니다.",
    "",
    ...(regressionOf ? [`재발: 닫힌 이슈 #${regressionOf}와 같은 서명입니다.`, ""] : []),
    "| 항목 | 값 |",
    "|---|---|",
    `| 서명 | \`${signature}\` |`,
    `| 앱 버전 | ${neutralizeMarkdown(r.appVersion)} |`,
    `| Windows | ${neutralizeMarkdown(r.osVersion)} |`,
    `| 런타임 | ${neutralizeMarkdown(r.runtime)} |`,
    `| 예외 | \`${r.exceptionType}\` |`,
    "",
    "### 예외",
    fence(r.details),
  ];
  if (r.log) lines.push("", "<details><summary>최근 로그</summary>", "", fence(r.log), "", "</details>");
  lines.push("", "같은 서명의 크래시가 다시 오면 이 이슈에 댓글로 쌓입니다.");
  return lines.join("\n");
}

export function occurrenceComment(r: CrashReport, count: number): string {
  return [
    `**${count}번째 발생** · 앱 ${neutralizeMarkdown(r.appVersion)} · ${neutralizeMarkdown(r.osVersion)}`,
    "",
    "<details><summary>예외 상세</summary>",
    "",
    fence(r.details),
    "",
    "</details>",
  ].join("\n");
}
